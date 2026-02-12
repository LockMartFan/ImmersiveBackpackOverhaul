#nullable enable

using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace ImmersiveBackpackOverhaul
{
    public sealed class IboWeightSystem : ModSystem
    {
        private ICoreServerAPI sapi = null!;
        private WeightConfig cfg = WeightConfig.Default();

        private const string WalkspeedStat = "walkspeed";
        private const string StatKey = "ibo-weight";

        // Debounce for weight recomputation after inventory changes.
        // Slightly higher value reduces recalcs during rapid shift-click/drag bursts,
        // while still feeling responsive to players.
        private const int RecalcDebounceMs = 125; // internal debounce, not user config

        // Debounce without Unregister/Register churn (stutter source during rapid slot ops)
        private readonly Dictionary<string, long> pendingCallbackId = new();
        private readonly Dictionary<string, long> pendingDueMs = new();

        private readonly Dictionary<string, Action<int>> backpackHandlers = new();
        private readonly Dictionary<string, Action<int>> hotbarHandlers = new();

        // Per-player cached weights to avoid full inventory rescans and repeated allocations
        // on every SlotModified (which can stutter in singleplayer where server+client share a process).
        private readonly Dictionary<string, PlayerWeightState> states = new();
        // Debug / perf counters (enabled only when cfg.DebugMode=true)
        private long dbgSlotModifiedTotal;
        private long dbgSlotModifiedRelevant;
        private long dbgQueueRecalcRequests;
        private long dbgRecalcApplied;
        private long dbgFullRebuilds;
        private long dbgDirtySlotRecomputes;
        private long dbgTickListenerId;


        private bool debugEnabled;
        private int dbgWindowIndex;

        private enum InvKind
        {
            Backpack,
            Hotbar
        }

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = (ICoreServerAPI)api;
            LoadConfig(api);

            // Master switch: if disabled, do not register any events/callbacks.
            if (!cfg.Enabled)
            {
                sapi.Logger.Notification("[IBO] Weight system disabled in config/ibo-weights.json (Enabled=false).");
                return;
            }

            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            debugEnabled = cfg.DebugMode;
            if (debugEnabled)
            {
                dbgTickListenerId = sapi.Event.RegisterGameTickListener(OnDebugTick, 30000);
                sapi.Logger.Warning("[IBO] DebugMode enabled: perf counters will log every 30s.");
            }
        }

        public override void Dispose()
        {
            if (sapi != null)
            {
                sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
                sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
            }

            foreach (var kvp in pendingCallbackId)
            {
                try { sapi.Event.UnregisterCallback(kvp.Value); } catch { }
            }

            pendingCallbackId.Clear();
            pendingDueMs.Clear();

            backpackHandlers.Clear();
            hotbarHandlers.Clear();
            states.Clear();
            debugEnabled = false;

            if (dbgTickListenerId != 0 && sapi != null)
            {
                try { sapi.Event.UnregisterGameTickListener(dbgTickListenerId); } catch { }
                dbgTickListenerId = 0;
            }
        }

        private void LoadConfig(ICoreAPI api)
        {
            var asset = api.Assets.TryGet(new AssetLocation("immersivebackpackoverhaul", "config/ibo-weights.json"));
            if (asset == null) return;

            try
            {
                var parsed = asset.ToObject<WeightConfig>();
                if (parsed != null) cfg = parsed;
            }
            catch
            {
                // keep defaults
            }
        }

        private void OnDebugTick(float dt)
        {
            // Debug-only periodic report. This is the ONLY ticking code path and only runs when cfg.DebugMode=true.
            int win = System.Threading.Interlocked.Increment(ref dbgWindowIndex);
            long slotModTotal = System.Threading.Interlocked.Exchange(ref dbgSlotModifiedTotal, 0);
            long slotModRelevant = System.Threading.Interlocked.Exchange(ref dbgSlotModifiedRelevant, 0);
            long queueReq = System.Threading.Interlocked.Exchange(ref dbgQueueRecalcRequests, 0);
            long recalc = System.Threading.Interlocked.Exchange(ref dbgRecalcApplied, 0);
            long fullRebuilds = System.Threading.Interlocked.Exchange(ref dbgFullRebuilds, 0);
            long slotRecomp = System.Threading.Interlocked.Exchange(ref dbgDirtySlotRecomputes, 0);

            // Always report, even if idle (requested).


            sapi.Logger.Warning(
                $"[IBO][Debug] 30s: SlotModified={slotModTotal} (relevant={slotModRelevant}), QueueRecalc={queueReq}, RecalcApplied={recalc}, FullRebuilds={fullRebuilds}, SlotRecomputes={slotRecomp}"
            );

            if (win > 1 && recalc > 0 && slotModTotal == 0 && slotModRelevant == 0 && queueReq == 0)
            {
                sapi.Logger.Warning("[IBO][Debug] RecalcApplied occurred with 0 SlotModified and 0 QueueRecalc in the last window. This suggests a non-inventory-driven trigger path.");
            }
        }


        private void OnPlayerNowPlaying(IServerPlayer plr)
        {
            var invMan = plr.InventoryManager;
            if (invMan == null) return;

            // Ensure we have a state object early so handler closures remain tiny.
            GetOrCreateState(plr);

            if (invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) is InventoryBase backpack)
            {
                if (!backpackHandlers.ContainsKey(plr.PlayerUID))
                {
                    Action<int> bh = slotId =>
                    {
                        if (debugEnabled) System.Threading.Interlocked.Increment(ref dbgSlotModifiedTotal);

                        // Only react to changes that can actually affect weight.
                        if (TryMarkDirtyIfRelevant(plr, InvKind.Backpack, slotId))
                        {
                            if (debugEnabled) System.Threading.Interlocked.Increment(ref dbgSlotModifiedRelevant);
                            QueueRecalc(plr);
                        }
                    };
                    backpackHandlers[plr.PlayerUID] = bh;
                    backpack.SlotModified += bh;
                }
            }

            // Hotbar changes affect carried weight too.
            if (invMan.GetHotbarInventory() is InventoryBase hotbarInvBase)
            {
                if (!hotbarHandlers.ContainsKey(plr.PlayerUID))
                {
                    Action<int> hh = slotId =>
                    {
                        if (debugEnabled) System.Threading.Interlocked.Increment(ref dbgSlotModifiedTotal);

                        // Only react to changes that can actually affect weight.
                        if (TryMarkDirtyIfRelevant(plr, InvKind.Hotbar, slotId))
                        {
                            if (debugEnabled) System.Threading.Interlocked.Increment(ref dbgSlotModifiedRelevant);
                            QueueRecalc(plr);
                        }
                    };
                    hotbarHandlers[plr.PlayerUID] = hh;
                    hotbarInvBase.SlotModified += hh;
                }
            }

            QueueRecalc(plr);
        }

        private void OnPlayerDisconnect(IServerPlayer plr)
        {
            var invMan = plr.InventoryManager;
            if (invMan == null) return;

            if (invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) is InventoryBase backpack
                && backpackHandlers.TryGetValue(plr.PlayerUID, out var bh))
            {
                backpack.SlotModified -= bh;
                backpackHandlers.Remove(plr.PlayerUID);
            }

            if (invMan.GetHotbarInventory() is InventoryBase hotbarInvBase
                && hotbarHandlers.TryGetValue(plr.PlayerUID, out var hh))
            {
                hotbarInvBase.SlotModified -= hh;
                hotbarHandlers.Remove(plr.PlayerUID);
            }

            lock (pendingCallbackId)
            {
                if (pendingCallbackId.TryGetValue(plr.PlayerUID, out long id))
                {
                    try { sapi.Event.UnregisterCallback(id); } catch { }
                    pendingCallbackId.Remove(plr.PlayerUID);
                    pendingDueMs.Remove(plr.PlayerUID);
                }
            }

            states.Remove(plr.PlayerUID);
        }

        private void QueueRecalc(IServerPlayer plr)
        {
            // IMPORTANT: avoid Unregister/Register churn on every SlotModified.
            if (debugEnabled) System.Threading.Interlocked.Increment(ref dbgQueueRecalcRequests);
            // We keep at most one pending callback per player. Each SlotModified just pushes the due time forward.
            string uid = plr.PlayerUID;
            long now = sapi.World.ElapsedMilliseconds;
            long due = now + RecalcDebounceMs;

            lock (pendingCallbackId)
            {
                pendingDueMs[uid] = due;

                // Already scheduled - nothing else to do.
                if (pendingCallbackId.ContainsKey(uid)) return;

                long id = sapi.Event.RegisterCallback(_ => PendingRecalcCallback(plr), RecalcDebounceMs);
                pendingCallbackId[uid] = id;
            }
        }

        private void PendingRecalcCallback(IServerPlayer plr)
        {
            string uid = plr.PlayerUID;

            long due;
            lock (pendingCallbackId)
            {
                // This callback is now "consumed"
                pendingCallbackId.Remove(uid);

                if (!pendingDueMs.TryGetValue(uid, out due))
                {
                    // Nothing to do
                    return;
                }
            }

            long now = sapi.World.ElapsedMilliseconds;
            if (now < due)
            {
                long waitMs = due - now;
                if (waitMs < 1) waitMs = 1;
                if (waitMs > 5000) waitMs = 5000;

                lock (pendingCallbackId)
                {
                    // Another callback might have been scheduled meanwhile.
                    pendingDueMs[uid] = due;
                    if (pendingCallbackId.ContainsKey(uid)) return;

                    long id = sapi.Event.RegisterCallback(_ => PendingRecalcCallback(plr), (int)waitMs);
                    pendingCallbackId[uid] = id;
                }

                return;
            }

            lock (pendingCallbackId)
            {
                pendingDueMs.Remove(uid);
            }

            if (debugEnabled) System.Threading.Interlocked.Increment(ref dbgRecalcApplied);
            RecalcAndApply(plr);
        }

        private void RecalcAndApply(IServerPlayer plr)
        {
            // Incremental cached totals (rebuilds automatically if inventory layout changed).
            var st = GetOrCreateState(plr);
            st.EnsureUpToDate(plr);
            st.GetTotals(out float carriedRawKg, out float effectiveKg);

            var ent = plr.Entity;
            long nowMs = sapi.World.ElapsedMilliseconds;

            // Encumbrance start/cap values (incl. trait adds) change rarely.
            // Cache them and only recompute/sync when the underlying inputs change.
            EncumbranceConfig enc = st.GetOrUpdateEncumbrance(ent, cfg.Encumbrance, out int appliedTraitMods, out bool encChanged);

            float mult = ComputeWalkspeedMultiplierPiecewise(effectiveKg, enc);
            float encPct = Math.Clamp(1f - mult, 0f, 1f);

            if (ent?.Stats != null)
            {
                ent.Stats.Set(WalkspeedStat, StatKey, mult - 1f, false);
            }

            // Sync "static" UI values only when they change (traits/config): start/cap + trait count.
            if (encChanged)
            {
                st.SyncEncumbranceWatched(ent, enc.StartKg, enc.CapKg, appliedTraitMods, nowMs);
            }

            // Sync "dynamic" UI values (carried/effective/pct/mult) with throttling + epsilon checks.
            st.SyncDynamicWatched(ent, carriedRawKg, effectiveKg, encPct, mult, nowMs);
        }

        // ---------------- Incremental cached weights ----------------

        private PlayerWeightState GetOrCreateState(IServerPlayer plr)
        {
            if (!states.TryGetValue(plr.PlayerUID, out var st))
            {
                st = new PlayerWeightState(this);
                states[plr.PlayerUID] = st;
            }
            return st;
        }

        private void MarkDirty(IServerPlayer plr, InvKind kind, int slotId)
        {
            var st = GetOrCreateState(plr);
            st.MarkDirty(plr, kind, slotId);
        }

        /// <summary>
        /// Marks a slot dirty only if the observable slot signature changed in a way that can affect weight.
        /// This is critical to avoid "pseudo ticking" when VS fires SlotModified due to attribute churn
        /// (perishables, temperature, syncing) that does not change mass.
        /// </summary>
        private bool TryMarkDirtyIfRelevant(IServerPlayer plr, InvKind kind, int slotId)
        {
            var st = GetOrCreateState(plr);
            return st.TryMarkDirtyIfRelevant(plr, kind, slotId);
        }

        private sealed class PlayerWeightState
        {
            private readonly IboWeightSystem sys;

            private int hotbarCount;
            private int backpackCount;

            private float hotbarKg;
            private float backpackRawKg;
            private float backpackEffectiveKg;

            // Per-slot cached contributions (already multiplied by StackSize and any bag effectiveness where applicable)
            private float[] hotbarSlotKg = Array.Empty<float>();
            private float[] backpackSlotKgRaw = Array.Empty<float>();
            private float[] backpackSlotKgEff = Array.Empty<float>();

            // Per-slot lightweight signatures used to ignore SlotModified events that do not affect weight.
            // Updated on full rebuild and on each recomputed dirty slot.
            private long[] hotbarSlotSig = Array.Empty<long>();
            private long[] backpackSlotSig = Array.Empty<long>();

            // Backpack bag model
            private int[] bagIndexBySlot = Array.Empty<int>();              // slot -> bagIndex for ItemSlotBagContent, else -1
            private int[] bagIndexByBagSlotIndex = Array.Empty<int>();      // slot -> bagIndex for ItemSlotBackpack, else -1
            private List<int>[] contentSlotsByBagIndex = Array.Empty<List<int>>();
            private ItemSlot[] bagSlots = Array.Empty<ItemSlot>();
            private float[] bagEffByIndex = Array.Empty<float>();

            // Dirtiness tracking (deduped)
            private readonly List<int> dirtyHotbarSlots = new(8);
            private readonly List<int> dirtyBackpackSlots = new(16);
            private readonly List<int> dirtyBagIndices = new(4);
            private bool dirtyAll;

            // Reusable caches (avoid per-recalc allocations)
            private readonly Dictionary<string, float> baseKgCache = new(StringComparer.Ordinal);
            private readonly Dictionary<string, LiquidCachedProps> liquidCache = new(StringComparer.Ordinal);

            // ---------------- Encumbrance caching (traits + config) ----------------

            private int encBaseHash = 0;
            private float cachedStartAddKg = float.NaN;
            private float cachedCapAddKg = float.NaN;
            private EncumbranceConfig cachedEnc = EncumbranceConfig.DefaultBase();
            private int cachedTraitModsApplied = 0;
            private bool encInitialized = false;

            /// <summary>
            /// Returns an encumbrance config that already includes any trait-based additive StartKg/CapKg.
            /// Recomputes only when the base config or the underlying trait adds change.
            /// </summary>
            public EncumbranceConfig GetOrUpdateEncumbrance(Entity? ent, EncumbranceConfig baseEnc, out int traitModsApplied, out bool changed)
            {
                int baseHash = HashEncBase(baseEnc);

                float startAdd = GetTraitAdditiveKg(ent, "iboEncStartKgAdd");
                float capAdd = GetTraitAdditiveKg(ent, "iboEncCapKgAdd");

                bool startChanged = !NearlyEqual(cachedStartAddKg, startAdd, 0.0001f);
                bool capChanged = !NearlyEqual(cachedCapAddKg, capAdd, 0.0001f);

                changed = !encInitialized || baseHash != encBaseHash || startChanged || capChanged;

                if (changed)
                {
                    cachedStartAddKg = startAdd;
                    cachedCapAddKg = capAdd;
                    encBaseHash = baseHash;

                    cachedTraitModsApplied = 0;
                    if (startAdd != 0) cachedTraitModsApplied++;
                    if (capAdd != 0) cachedTraitModsApplied++;

                    cachedEnc = EncumbranceConfig.CloneOf(baseEnc);
                    cachedEnc.StartKg += startAdd;
                    cachedEnc.CapKg += capAdd;
                    cachedEnc.Sanitize();

                    encInitialized = true;
                }

                traitModsApplied = cachedTraitModsApplied;
                return cachedEnc;
            }

            private static int HashEncBase(EncumbranceConfig e)
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + (e.Enabled ? 1 : 0);
                    h = h * 31 + BitConverter.SingleToInt32Bits(e.StartKg);
                    h = h * 31 + BitConverter.SingleToInt32Bits(e.CapKg);
                    h = h * 31 + BitConverter.SingleToInt32Bits(e.StartDebuff);
                    h = h * 31 + BitConverter.SingleToInt32Bits(e.MaxDebuff);
                    return h;
                }
            }

            // ---------------- WatchedAttributes syncing ----------------

            // Updating WatchedAttributes too frequently (or writing many paths at once) can cause noticeable stutter/rubber-banding
            // in singleplayer during bursty inventory ops. We therefore:
            //  - throttle dynamic values to at most ~5 updates/sec
            //  - write only the keys that actually changed (epsilon)
            // Static encumbrance values (start/cap/traitMods) are synced only when they change.
            private const int WatchedMinIntervalMs = 200;

            private long lastDynamicSyncMs = -1;

            private float lastCarriedKg = float.NaN;
            private float lastEffectiveKg = float.NaN;
            private float lastEncPct = float.NaN;
            private float lastWalkspeedMult = float.NaN;

            private float lastEncStartKg = float.NaN;
            private float lastEncCapKg = float.NaN;
            private int lastTraitModsApplied = int.MinValue;

            public void SyncEncumbranceWatched(Entity? ent, float startKg, float capKg, int traitMods, long nowMs)
            {
                var wa = ent?.WatchedAttributes;
                if (wa == null) return;

                bool wroteAny = false;

                if (!NearlyEqual(lastEncStartKg, startKg, 0.01f)) { wa.SetFloat("iboStartKg", startKg); lastEncStartKg = startKg; wroteAny = true; }
                if (!NearlyEqual(lastEncCapKg, capKg, 0.01f)) { wa.SetFloat("iboMaxKg", capKg); lastEncCapKg = capKg; wroteAny = true; }

                if (lastTraitModsApplied != traitMods) { wa.SetInt("iboTraitModsApplied", traitMods); lastTraitModsApplied = traitMods; wroteAny = true; }

                // No throttle needed here: this only runs on actual changes.
                if (wroteAny)
                {
                    // Make sure a static update doesn't get immediately overwritten by a dynamic update
                    // that is still using old UI values. (This is mostly defensive.)
                    lastDynamicSyncMs = nowMs;
                }
            }

            public void SyncDynamicWatched(Entity? ent, float carriedKg, float effectiveKg, float encPct, float walkspeedMult, long nowMs)
            {
                var wa = ent?.WatchedAttributes;
                if (wa == null) return;

                if (lastDynamicSyncMs >= 0 && nowMs - lastDynamicSyncMs < WatchedMinIntervalMs)
                {
                    if (!NeedsDynamicUpdate(carriedKg, effectiveKg, encPct, walkspeedMult))
                    {
                        return;
                    }
                }

                bool wroteAny = false;

                // Kg values: 10L buckets are large steps, so 0.01kg precision is plenty.
                if (!NearlyEqual(lastCarriedKg, carriedKg, 0.01f)) { wa.SetFloat("iboCarriedKg", carriedKg); lastCarriedKg = carriedKg; wroteAny = true; }
                if (!NearlyEqual(lastEffectiveKg, effectiveKg, 0.01f)) { wa.SetFloat("iboEffectiveKg", effectiveKg); lastEffectiveKg = effectiveKg; wroteAny = true; }

                // Encumbrance percent / mult: tight epsilon avoids churn.
                if (!NearlyEqual(lastEncPct, encPct, 0.001f)) { wa.SetFloat("iboEncPct", encPct); lastEncPct = encPct; wroteAny = true; }
                if (!NearlyEqual(lastWalkspeedMult, walkspeedMult, 0.001f)) { wa.SetFloat("iboWalkspeedMult", walkspeedMult); lastWalkspeedMult = walkspeedMult; wroteAny = true; }

                if (wroteAny)
                {
                    lastDynamicSyncMs = nowMs;
                }
            }

            private bool NeedsDynamicUpdate(float carriedKg, float effectiveKg, float encPct, float walkspeedMult)
            {
                return !NearlyEqual(lastCarriedKg, carriedKg, 0.01f)
                    || !NearlyEqual(lastEffectiveKg, effectiveKg, 0.01f)
                    || !NearlyEqual(lastEncPct, encPct, 0.001f)
                    || !NearlyEqual(lastWalkspeedMult, walkspeedMult, 0.001f);
            }


            private static bool NearlyEqual(float a, float b, float eps)
            {
                if (float.IsNaN(a)) return false;
                return Math.Abs(a - b) <= eps;
            }

            private enum InvKindLocal { Backpack, Hotbar }

            public PlayerWeightState(IboWeightSystem sys)
            {
                this.sys = sys;
            }

            public void MarkDirty(IServerPlayer plr, InvKind kind, int slotId)
            {
                if (slotId < 0)
                {
                    dirtyAll = true;
                    return;
                }

                if (kind == InvKind.Hotbar)
                {
                    AddUnique(dirtyHotbarSlots, slotId);
                    return;
                }

                AddUnique(dirtyBackpackSlots, slotId);

                var invMan = plr.InventoryManager;
                if (invMan?.GetOwnInventory(GlobalConstants.backpackInvClassName) is not InventoryBase bp) return;
                if (slotId >= 0 && slotId < bp.Count)
                {
                    if (bp[slotId] is ItemSlotBackpack && slotId < bagIndexByBagSlotIndex.Length)
                    {
                        int bi = bagIndexByBagSlotIndex[slotId];
                        if (bi >= 0) AddUnique(dirtyBagIndices, bi);
                    }
                }
            }

            public void EnsureUpToDate(IServerPlayer plr)
            {
                var invMan = plr.InventoryManager;
                if (invMan == null)
                {
                    dirtyAll = true;
                    return;
                }

                var hotbar = invMan.GetHotbarInventory();
                var backpack = invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) as InventoryBase;

                int newHotbarCount = hotbar?.Count ?? 0;
                int newBackpackCount = backpack?.Count ?? 0;

                if (hotbarCount != newHotbarCount || backpackCount != newBackpackCount
                    || (newHotbarCount > 0 && hotbarSlotKg.Length != newHotbarCount)
                    || (newBackpackCount > 0 && backpackSlotKgRaw.Length != newBackpackCount)
                    || (newHotbarCount > 0 && hotbarSlotSig.Length != newHotbarCount)
                    || (newBackpackCount > 0 && backpackSlotSig.Length != newBackpackCount))
                {
                    dirtyAll = true;
                }

                if (dirtyAll)
                {
                    RebuildAll(hotbar, backpack);
                    ClearDirty();
                    return;
                }

                if (hotbar != null) ApplyHotbarDirty(hotbar);
                if (backpack != null) ApplyBackpackDirty(backpack);

                ClearDirty();
            }

            public void GetTotals(out float carriedRawKg, out float effectiveKg)
            {
                carriedRawKg = hotbarKg + backpackRawKg;
                effectiveKg = hotbarKg + backpackEffectiveKg;
            }

            private void RebuildAll(IInventory? hotbar, InventoryBase? backpack)
            {
                if (sys.debugEnabled) System.Threading.Interlocked.Increment(ref sys.dbgFullRebuilds);
                baseKgCache.Clear();
                liquidCache.Clear();

                hotbarCount = hotbar?.Count ?? 0;
                backpackCount = backpack?.Count ?? 0;

                if (hotbarCount <= 0)
                {
                    hotbarSlotKg = Array.Empty<float>();
                    hotbarSlotSig = Array.Empty<long>();
                    hotbarKg = 0f;
                }
                else
                {
                    hotbarSlotKg = EnsureArraySize(hotbarSlotKg, hotbarCount);
                    hotbarSlotSig = EnsureArraySize(hotbarSlotSig, hotbarCount);
                    hotbarKg = 0f;
                    for (int i = 0; i < hotbarCount; i++)
                    {
                        var slot = hotbar![i];
                        hotbarSlotSig[i] = ComputeSlotSig(slot);
                        float kg = ComputeSlotKg(slot, bagEff: 1f);
                        hotbarSlotKg[i] = kg;
                        hotbarKg += kg;
                    }
                }

                if (backpackCount <= 0)
                {
                    backpackSlotKgRaw = Array.Empty<float>();
                    backpackSlotKgEff = Array.Empty<float>();
                    backpackSlotSig = Array.Empty<long>();
                    bagIndexBySlot = Array.Empty<int>();
                    bagIndexByBagSlotIndex = Array.Empty<int>();
                    contentSlotsByBagIndex = Array.Empty<List<int>>();
                    bagSlots = Array.Empty<ItemSlot>();
                    bagEffByIndex = Array.Empty<float>();
                    backpackRawKg = 0f;
                    backpackEffectiveKg = 0f;
                    return;
                }

                backpackSlotKgRaw = EnsureArraySize(backpackSlotKgRaw, backpackCount);
                backpackSlotKgEff = EnsureArraySize(backpackSlotKgEff, backpackCount);
                backpackSlotSig = EnsureArraySize(backpackSlotSig, backpackCount);
                bagIndexBySlot = EnsureArraySize(bagIndexBySlot, backpackCount, fill: -1);
                bagIndexByBagSlotIndex = EnsureArraySize(bagIndexByBagSlotIndex, backpackCount, fill: -1);

                var bags = new List<ItemSlot>(8);
                for (int i = 0; i < backpackCount; i++)
                {
                    if (backpack![i] is ItemSlotBackpack)
                    {
                        int bi = bags.Count;
                        bags.Add(backpack[i]);
                        bagIndexByBagSlotIndex[i] = bi;
                    }
                }

                bagSlots = bags.ToArray();
                bagEffByIndex = EnsureArraySize(bagEffByIndex, bagSlots.Length);
                contentSlotsByBagIndex = EnsureArraySize(contentSlotsByBagIndex, bagSlots.Length);
                for (int bi = 0; bi < contentSlotsByBagIndex.Length; bi++)
                {
                    contentSlotsByBagIndex[bi] ??= new List<int>(32);
                    contentSlotsByBagIndex[bi].Clear();
                }

                for (int i = 0; i < backpackCount; i++)
                {
                    if (backpack![i] is ItemSlotBagContent bc)
                    {
                        int bi = bc.BagIndex;
                        if (bi >= 0 && bi < contentSlotsByBagIndex.Length)
                        {
                            bagIndexBySlot[i] = bi;
                            contentSlotsByBagIndex[bi].Add(i);
                        }
                        else
                        {
                            bagIndexBySlot[i] = -1;
                        }
                    }
                    else
                    {
                        bagIndexBySlot[i] = -1;
                    }
                }

                for (int bi = 0; bi < bagSlots.Length; bi++)
                {
                    bagEffByIndex[bi] = sys.ResolveBagEffWithCondition(bagSlots, bi);
                }

                backpackRawKg = 0f;
                backpackEffectiveKg = 0f;
                for (int i = 0; i < backpackCount; i++)
                {
                    var slot = backpack![i];
                    backpackSlotSig[i] = ComputeSlotSig(slot);
                    float raw = ComputeSlotKg(slot, bagEff: 1f);
                    backpackSlotKgRaw[i] = raw;
                    backpackRawKg += raw;

                    float eff = raw;
                    int bi = bagIndexBySlot[i];
                    if (bi >= 0 && bi < bagEffByIndex.Length)
                    {
                        eff = ComputeSlotKg(slot, bagEff: bagEffByIndex[bi]);
                    }
                    backpackSlotKgEff[i] = eff;
                    backpackEffectiveKg += eff;
                }
            }

            private void ApplyHotbarDirty(IInventory hotbar)
            {
                if (hotbarCount <= 0 || hotbarSlotKg.Length != hotbarCount)
                {
                    dirtyAll = true;
                    return;
                }

                for (int di = 0; di < dirtyHotbarSlots.Count; di++)
                {
                    int idx = dirtyHotbarSlots[di];
                    if (sys.debugEnabled) System.Threading.Interlocked.Increment(ref sys.dbgDirtySlotRecomputes);
                    if ((uint)idx >= (uint)hotbarCount) { dirtyAll = true; return; }

                    float old = hotbarSlotKg[idx];
                    var slot = hotbar[idx];
                    hotbarSlotSig[idx] = ComputeSlotSig(slot);
                    float now = ComputeSlotKg(slot, bagEff: 1f);

                    hotbarSlotKg[idx] = now;
                    hotbarKg += (now - old);
                }
            }

            private void ApplyBackpackDirty(InventoryBase backpack)
            {
                if (backpackCount <= 0 || backpackSlotKgRaw.Length != backpackCount || backpackSlotKgEff.Length != backpackCount)
                {
                    dirtyAll = true;
                    return;
                }

                for (int i = 0; i < dirtyBagIndices.Count; i++)
                {
                    int bi = dirtyBagIndices[i];
                    if ((uint)bi >= (uint)bagEffByIndex.Length) { dirtyAll = true; return; }
                    bagEffByIndex[bi] = sys.ResolveBagEffWithCondition(bagSlots, bi);

                    var lst = (bi < contentSlotsByBagIndex.Length) ? contentSlotsByBagIndex[bi] : null;
                    if (lst == null) continue;
                    for (int j = 0; j < lst.Count; j++)
                    {
                        int slotIdx = lst[j];
                        AddUnique(dirtyBackpackSlots, slotIdx);
                    }
                }

                for (int di = 0; di < dirtyBackpackSlots.Count; di++)
                {
                    int idx = dirtyBackpackSlots[di];
                    if (sys.debugEnabled) System.Threading.Interlocked.Increment(ref sys.dbgDirtySlotRecomputes);
                    if ((uint)idx >= (uint)backpackCount) { dirtyAll = true; return; }

                    var slot = backpack[idx];

                    backpackSlotSig[idx] = ComputeSlotSig(slot);

                    float oldRaw = backpackSlotKgRaw[idx];
                    float nowRaw = ComputeSlotKg(slot, bagEff: 1f);
                    backpackSlotKgRaw[idx] = nowRaw;
                    backpackRawKg += (nowRaw - oldRaw);

                    float oldEff = backpackSlotKgEff[idx];
                    float effMult = 1f;
                    int bi = (idx < bagIndexBySlot.Length) ? bagIndexBySlot[idx] : -1;
                    if (bi >= 0 && bi < bagEffByIndex.Length) effMult = bagEffByIndex[bi];
                    float nowEff = (effMult == 1f) ? nowRaw : ComputeSlotKg(slot, bagEff: effMult);
                    backpackSlotKgEff[idx] = nowEff;
                    backpackEffectiveKg += (nowEff - oldEff);

                    if (idx < bagIndexByBagSlotIndex.Length)
                    {
                        int bagIdx = bagIndexByBagSlotIndex[idx];
                        if (bagIdx >= 0) AddUnique(dirtyBagIndices, bagIdx);
                    }
                }
            }

            private float ComputeSlotKg(ItemSlot slot, float bagEff)
            {
                ItemStack? stack = slot.Itemstack;
                if (stack?.Collectible == null) return 0f;

                float perUnitKg = sys.ResolveItemKg(stack, baseKgCache, liquidCache);
                float kg = perUnitKg * stack.StackSize;
                if (bagEff != 1f) kg *= bagEff;
                return kg;
            }

            /// <summary>
            /// Lightweight signature for deciding whether a SlotModified event can affect weight.
            /// Designed to be cheap enough to run on every SlotModified.
            /// Includes stack size, collectible code, durability (if any), and liquid contents (if present).
            /// </summary>
            private long ComputeSlotSig(ItemSlot slot)
            {
                ItemStack? stack = slot.Itemstack;
                if (stack?.Collectible == null) return 0;

                // Base: code hash + stacksize
                string code = stack.Collectible.Code?.ToString() ?? "";
                unchecked
                {
                    long h = 1469598103934665603L; // FNV-1a 64 offset
                    // string hash (stable across session not required; just needs to detect changes)
                    for (int i = 0; i < code.Length; i++)
                    {
                        h ^= code[i];
                        h *= 1099511628211L;
                    }

                    h ^= (uint)stack.StackSize;
                    h *= 1099511628211L;

                    // Durability (affects bag effectiveness and thus effective kg)
                    int maxDur = stack.Collectible.GetMaxDurability(stack);
                    if (maxDur > 0)
                    {
                        int rem = stack.Collectible.GetRemainingDurability(stack);
                        h ^= (uint)rem;
                        h *= 1099511628211L;
                    }

                    // Liquid contents signature (only if contents tree exists)
                    ITreeAttribute? contents = stack.Attributes?.GetTreeAttribute("contents");
                    if (contents != null && contents.Count > 0)
                    {
                        foreach (var entry in contents)
                        {
                            if (entry.Value is not ItemstackAttribute isa) continue;
                            ItemStack? content = isa.value;
                            if (content?.Collectible == null) continue;
                            string lcode = content.Collectible.Code?.ToString() ?? "";
                            for (int i = 0; i < lcode.Length; i++)
                            {
                                h ^= lcode[i];
                                h *= 1099511628211L;
                            }
                            h ^= (uint)content.StackSize;
                            h *= 1099511628211L;
                        }
                    }

                    return h;
                }
            }

            public bool TryMarkDirtyIfRelevant(IServerPlayer plr, InvKind kind, int slotId)
            {
                if (slotId < 0)
                {
                    dirtyAll = true;
                    return true;
                }

                var invMan = plr.InventoryManager;
                if (invMan == null) { dirtyAll = true; return true; }

                if (kind == InvKind.Hotbar)
                {
                    if (invMan.GetHotbarInventory() is not IInventory hb) { dirtyAll = true; return true; }
                    int count = hb.Count;
                    if ((uint)slotId >= (uint)count) { dirtyAll = true; return true; }

                    // If signatures aren't aligned yet, force a full rebuild (rare).
                    if (hotbarSlotSig.Length != count || hotbarSlotKg.Length != count)
                    {
                        dirtyAll = true;
                        return true;
                    }

                    long sig = ComputeSlotSig(hb[slotId]);
                    if (hotbarSlotSig[slotId] == sig) return false;

                    // Update signature immediately to suppress repeated SlotModified churn.
                    hotbarSlotSig[slotId] = sig;
                    AddUnique(dirtyHotbarSlots, slotId);
                    return true;
                }

                // Backpack
                if (invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) is not InventoryBase bp)
                {
                    dirtyAll = true;
                    return true;
                }

                int bpCount = bp.Count;
                if ((uint)slotId >= (uint)bpCount) { dirtyAll = true; return true; }
                if (backpackSlotSig.Length != bpCount || backpackSlotKgRaw.Length != bpCount || backpackSlotKgEff.Length != bpCount)
                {
                    dirtyAll = true;
                    return true;
                }

                long bSig = ComputeSlotSig(bp[slotId]);
                if (backpackSlotSig[slotId] == bSig) return false;
                backpackSlotSig[slotId] = bSig;

                AddUnique(dirtyBackpackSlots, slotId);

                // If a bag slot changed, its effectiveness can change -> affects all its content slots.
                if (bp[slotId] is ItemSlotBackpack && slotId < bagIndexByBagSlotIndex.Length)
                {
                    int bi = bagIndexByBagSlotIndex[slotId];
                    if (bi >= 0) AddUnique(dirtyBagIndices, bi);
                }

                return true;
            }

            private void ClearDirty()
            {
                dirtyAll = false;
                dirtyHotbarSlots.Clear();
                dirtyBackpackSlots.Clear();
                dirtyBagIndices.Clear();
            }

            private static void AddUnique(List<int> list, int value)
            {
                for (int i = 0; i < list.Count; i++) if (list[i] == value) return;
                list.Add(value);
            }

            private static T[] EnsureArraySize<T>(T[] arr, int size)
            {
                if (arr.Length == size) return arr;
                return new T[size];
            }

            private static int[] EnsureArraySize(int[] arr, int size, int fill)
            {
                if (arr.Length != size)
                {
                    arr = new int[size];
                }
                for (int i = 0; i < arr.Length; i++) arr[i] = fill;
                return arr;
            }

            private static List<int>[] EnsureArraySize(List<int>[] arr, int size)
            {
                if (arr.Length == size) return arr;
                return new List<int>[size];
            }
        }

        /// <summary>
        /// Returns kg *per single stack unit* (i.e. for StackSize==1). Caller multiplies by StackSize.
        /// Includes liquid contents weight for liquid containers.
        /// </summary>
        private float ResolveItemKg(
            ItemStack stack,
            Dictionary<string, float> baseKgCache,
            Dictionary<string, LiquidCachedProps> liquidCache
        )
        {
            var col = stack.Collectible!;
            string code = col.Code?.ToString() ?? "";

            if (!string.IsNullOrEmpty(code) && baseKgCache.TryGetValue(code, out float cached))
            {
                return cached + ResolveLiquidContentsKg(stack, liquidCache);
            }

            JsonObject? attrs = col.Attributes;

            float resolved;
            if (attrs != null && attrs.KeyExists("iboBaseWeightKg"))
            {
                float v = attrs["iboBaseWeightKg"].AsFloat(0);
                resolved = Math.Max(0, v);
            }
            else
            {
                resolved = cfg.Rules.ResolveKg(stack, cfg.DefaultBlockKg);
            }

            if (!string.IsNullOrEmpty(code))
            {
                baseKgCache[code] = resolved;
            }

            return resolved + ResolveLiquidContentsKg(stack, liquidCache);
        }

        private readonly struct LiquidCachedProps
        {
            public readonly float ItemsPerLitre;

            public LiquidCachedProps(float itemsPerLitre)
            {
                ItemsPerLitre = itemsPerLitre <= 0 ? 1f : itemsPerLitre;
            }
        }

        /// <summary>
        /// Returns extra kg for any liquid contents stored in this itemstack (e.g., buckets, jugs).
        /// 1 litre == 1 kg for all liquids (intentionally simple).
        /// </summary>
        private float ResolveLiquidContentsKg(
            ItemStack containerStack,
            Dictionary<string, LiquidCachedProps> liquidCache
        )
        {
            ITreeAttribute? contents = containerStack.Attributes?.GetTreeAttribute("contents");
            if (contents == null || contents.Count == 0)
            {
                return 0f;
            }

            float extraKg = 0f;

            foreach (var entry in contents)
            {
                if (entry.Value is not ItemstackAttribute isa) continue;

                ItemStack? content = isa.value;
                if (content == null || content.StackSize <= 0) continue;

                // IMPORTANT (perf): Do NOT call ResolveBlockOrItem() here.
                // This method runs during inventory bursts (pickup/throw/shift-click...) and
                // resolving nested stacks can hitch the game. If the nested stack isn't
                // resolved yet, skip it for now.
                if (content.Collectible == null) continue;

                JsonObject? wt = content.Collectible.Attributes?["waterTightContainerProps"];
                if (wt == null || !wt.Exists) continue;

                if (content.Collectible.MatterState != EnumMatterState.Liquid) continue;

                string liquidCode = content.Collectible.Code?.ToString() ?? "";
                if (!liquidCache.TryGetValue(liquidCode, out LiquidCachedProps props))
                {
                    float itemsPerLitre = wt["itemsPerLitre"].AsFloat(1f);
                    props = new LiquidCachedProps(itemsPerLitre);
                    if (!string.IsNullOrEmpty(liquidCode)) liquidCache[liquidCode] = props;
                }

                float litres = content.StackSize / props.ItemsPerLitre;
                if (litres <= 0f) continue;

                extraKg += litres;
            }

            return extraKg;
        }

        private float ResolveBagEffWithCondition(ItemSlot[] bagSlots, int bagIndex)
        {
            if (bagIndex < 0 || bagIndex >= bagSlots.Length) return 1f;

            ItemStack? bag = bagSlots[bagIndex].Itemstack;
            if (bag?.Collectible == null) return 1f;

            float baseEff = ResolveBagBaseEff(bag);

            int max = bag.Collectible.GetMaxDurability(bag);
            if (max > 0)
            {
                int rem = bag.Collectible.GetRemainingDurability(bag);
                float cond = rem / (float)max;
                cond = Math.Clamp(cond, 0f, 1f);

                float wear = 1f - cond;
                float penalty = (float)Math.Pow(wear, cfg.BagDegrade.CurvePower);

                float brokenEff = cfg.BagDegrade.BrokenEffectiveness;
                float eff = baseEff + (brokenEff - baseEff) * penalty;

                float lo = Math.Min(baseEff, brokenEff);
                float hi = Math.Max(baseEff, brokenEff);
                return Math.Clamp(eff, lo, hi);
            }

            return baseEff;
        }

        private float ResolveBagBaseEff(ItemStack bag)
        {
            JsonObject? attrs = bag.Collectible!.Attributes;

            if (attrs != null && attrs.KeyExists("iboBagEffectiveness"))
            {
                return Math.Clamp(attrs["iboBagEffectiveness"].AsFloat(1f), 0f, 10f);
            }

            return 1f;
        }

        private static float GetTraitAdditiveKg(Entity? ent, string statKey)
        {
            if (ent?.Stats == null) return 0f;

            float blended = ent.Stats.GetBlended(statKey);
            float delta = blended - 1f;

            if (Math.Abs(delta) < 0.0001f) return 0f;
            return delta;
        }

        private static float ComputeWalkspeedMultiplierPiecewise(float kg, EncumbranceConfig e)
        {
            if (!e.Enabled) return 1f;

            float startKg = e.StartKg;
            float capKg = Math.Max(e.StartKg + 0.001f, e.CapKg);

            float startDebuff = Math.Clamp(e.StartDebuff, 0f, 1f);
            float maxDebuff = Math.Clamp(e.MaxDebuff, 0f, 1f);
            if (maxDebuff < startDebuff) maxDebuff = startDebuff;

            if (kg <= startKg) return 1f;

            float t = (kg - startKg) / (capKg - startKg);
            t = Math.Clamp(t, 0f, 1f);

            float debuff = startDebuff + t * (maxDebuff - startDebuff);
            return Math.Clamp(1f - debuff, 1f - maxDebuff, 1f);
        }

        // ---------- Config ----------

        public sealed class WeightConfig
        {
            public bool Enabled = true;

            // Debug-only: logs perf counters every 10 seconds. Adds a small tick listener ONLY when true.
            public bool DebugMode = false;

            public float DefaultBlockKg = 0.1f;

            public BagDegradeConfig BagDegrade = new();

            public EncumbranceConfig Encumbrance = EncumbranceConfig.DefaultBase();

            public WeightRuleSet Rules = WeightRuleSet.Default();

            public static WeightConfig Default() => new WeightConfig();
        }

        public sealed class BagDegradeConfig
        {
            public float BrokenEffectiveness = 1f;
            public float CurvePower = 1.25f;
        }

        public sealed class EncumbranceConfig
        {
            public bool Enabled = true;

            // Debug-only: logs perf counters every 10 seconds. Adds a small tick listener ONLY when true.
            public bool DebugMode = false;

            public float StartKg = 30f;
            public float CapKg = 90f;

            public float StartDebuff = 0.05f;
            public float MaxDebuff = 0.35f;

            public static EncumbranceConfig DefaultBase() => new EncumbranceConfig();

            public static EncumbranceConfig CloneOf(EncumbranceConfig other) => new EncumbranceConfig
            {
                Enabled = other.Enabled,
                StartKg = other.StartKg,
                CapKg = other.CapKg,
                StartDebuff = other.StartDebuff,
                MaxDebuff = other.MaxDebuff
            };

            public static EncumbranceConfig DefaultBaseNoClamp() => new EncumbranceConfig();

            public void Sanitize()
            {
                if (StartKg < 0) StartKg = 0;
                if (CapKg < StartKg + 0.001f) CapKg = StartKg + 0.001f;

                StartDebuff = Math.Clamp(StartDebuff, 0f, 1f);
                MaxDebuff = Math.Clamp(MaxDebuff, 0f, 1f);
                if (MaxDebuff < StartDebuff) MaxDebuff = StartDebuff;
            }
        }

        public sealed class WeightRuleSet
        {
            public Dictionary<string, float> ByClass = new();
            public Dictionary<string, float> ByCode = new();
            public List<WildcardRule> Wildcards = new();

            public static WeightRuleSet Default() => new WeightRuleSet();

            public float ResolveKg(ItemStack stack, float defaultKg)
            {
                var col = stack.Collectible;
                if (col == null) return 0f;

                // Config may omit these sections or set them to null. Be defensive to avoid NREs.
                ByCode ??= new Dictionary<string, float>();
                ByClass ??= new Dictionary<string, float>();
                Wildcards ??= new List<WildcardRule>();

                string code = col.Code?.ToString() ?? "";
                if (!string.IsNullOrEmpty(code) && ByCode.TryGetValue(code, out float exact))
                {
                    return Math.Max(0f, exact);
                }

                // Wildcards are still supported by config, but you’ve been minimizing/eliminating them.
                // If your config has none, this is essentially free.
                for (int i = 0; i < Wildcards.Count; i++)
                {
                    var w = Wildcards[i];
                    if (w == null) continue;
                    if (string.IsNullOrEmpty(w.Contains)) continue;
                    if (!string.IsNullOrEmpty(code) && code.Contains(w.Contains, StringComparison.OrdinalIgnoreCase))
                    {
                        return Math.Max(0f, w.Kg);
                    }
                }

                // Class fallback
                // IMPORTANT: Use the collectible's concrete type name (e.g. ItemOre, ItemWearable...)
                // NOT EnumItemClass. Config expects C# class names.
                string cls = col.GetType().Name;
                if (!string.IsNullOrEmpty(cls) && ByClass.TryGetValue(cls, out float byCls))
                {
                    return Math.Max(0f, byCls);
                }

                // IMPORTANT: blocks stay at DefaultBlockKg; items without rules will also use default.
                return Math.Max(0f, defaultKg);
            }
        }

        public sealed class WildcardRule
        {
            public string Contains = "";
            public float Kg = 0f;
        }
    }
}
