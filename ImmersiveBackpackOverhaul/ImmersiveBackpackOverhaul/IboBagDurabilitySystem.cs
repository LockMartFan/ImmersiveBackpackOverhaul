#nullable enable

using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace ImmersiveBackpackOverhaul
{
    /// <summary>
    /// Bag durability degradation when adding items into bags.
    /// 
    /// PERFORMANCE CRITICAL:
    /// - No full inventory scans on SlotModified.
    /// - We track per-slot stack sizes and per-bag totals incrementally.
    /// - Degradation is decided by comparing per-bag total at start/end of a debounce window
    ///   (prevents false positives when rearranging items inside the same bag).
    /// </summary>
    public sealed class IboBagDurabilitySystem : ModSystem
    {
        private ICoreServerAPI sapi = null!;

        // No external config file for bag durability by design.
        // Defaults only, and this whole system is gated by the master switch in ibo-weights.json.
        private BagDurabilityConfig cfg = BagDurabilityConfig.Default();

        // invId -> state
        private readonly Dictionary<string, BackpackState> statesByInvId = new(StringComparer.Ordinal);

        // playerUid -> handler (so we can unsubscribe cleanly)
        private readonly Dictionary<string, Action<int>> handlersByPlayerUid = new(StringComparer.Ordinal);

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            // Master switch lives in config/ibo-weights.json
            // If disabled, bag durability must not run or hook anything.
            if (!IsWeightSystemEnabledFromConfig(api))
            {
                sapi.Logger.Notification("[IBO] Weight system disabled in config/ibo-weights.json (Enabled=false). Bag durability hooks not loaded.");
                return;
            }

            // Intentionally do NOT load any bag-durability config file. Defaults only.

            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            api.Event.PlayerDisconnect += OnPlayerDisconnect;
        }

        public override void Dispose()
        {
            if (sapi != null)
            {
                sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
                sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
            }

            // Best effort: unregister callbacks
            foreach (var st in statesByInvId.Values)
            {
                st.UnregisterCallbacksSafe(sapi);
            }

            handlersByPlayerUid.Clear();
            statesByInvId.Clear();
        }

        private static bool IsWeightSystemEnabledFromConfig(ICoreAPI api)
        {
            try
            {
                var asset = api.Assets.TryGet(new AssetLocation("immersivebackpackoverhaul", "config/ibo-weights.json"));
                if (asset == null) return true; // fail-open

                var wcfg = asset.ToObject<IboWeightSystem.WeightConfig>();
                return wcfg?.Enabled ?? true;
            }
            catch
            {
                return true; // fail-open
            }
        }

        private void OnPlayerNowPlaying(IServerPlayer plr)
        {
            var invMan = plr.InventoryManager;
            if (invMan == null) return;

            if (invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) is not InventoryBase backpack) return;

            // Build/refresh state for this backpack inventory.
            // This is a one-time O(N) scan on join, acceptable.
            var st = new BackpackState(backpack);
            statesByInvId[backpack.InventoryID] = st;

            if (!handlersByPlayerUid.ContainsKey(plr.PlayerUID))
            {
                Action<int> h = slotId => OnBackpackSlotModified(plr, backpack, slotId);
                handlersByPlayerUid[plr.PlayerUID] = h;
                backpack.SlotModified += h;
            }
        }

        private void OnPlayerDisconnect(IServerPlayer plr)
        {
            var invMan = plr.InventoryManager;
            if (invMan == null) return;

            if (invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) is InventoryBase backpack
                && handlersByPlayerUid.TryGetValue(plr.PlayerUID, out var h))
            {
                backpack.SlotModified -= h;
                handlersByPlayerUid.Remove(plr.PlayerUID);
            }

            if (invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) is InventoryBase bp2)
            {
                if (statesByInvId.TryGetValue(bp2.InventoryID, out var st))
                {
                    st.UnregisterCallbacksSafe(sapi);
                    statesByInvId.Remove(bp2.InventoryID);
                }
            }
        }

        private void OnBackpackSlotModified(IServerPlayer plr, InventoryBase backpack, int slotId)
        {
            if (slotId < 0 || slotId >= backpack.Count) return;

            // Only bag content slots matter.
            if (backpack[slotId] is not ItemSlotBagContent bc) return;

            if (!statesByInvId.TryGetValue(backpack.InventoryID, out var st))
            {
                st = new BackpackState(backpack);
                statesByInvId[backpack.InventoryID] = st;
            }
            else
            {
                // If inventory size changed (rare), rebuild state.
                if (st.BackpackCount != backpack.Count)
                {
                    st.UnregisterCallbacksSafe(sapi);
                    st = new BackpackState(backpack);
                    statesByInvId[backpack.InventoryID] = st;
                }
            }

            int bagIndex = bc.BagIndex;
            if (bagIndex < 0 || bagIndex >= st.BagSlots.Length) return;

            // Update incremental totals.
            int prevSlotSize = st.SlotLastSize[slotId];
            int currSlotSize = backpack[slotId].StackSize;
            if (currSlotSize == prevSlotSize) return;

            int prevBagTotal = st.BagTotals[bagIndex];
            int delta = currSlotSize - prevSlotSize;

            st.SlotLastSize[slotId] = currSlotSize;
            st.BagTotals[bagIndex] = prevBagTotal + delta;

            // Start (or continue) a debounce window for this bag.
            // We decide degradation at the END of the window by comparing totals,
            // which avoids false positives from rearranging items inside the same bag.
            if (!st.PendingByBag[bagIndex])
            {
                st.PendingByBag[bagIndex] = true;
                st.WindowStartTotals[bagIndex] = prevBagTotal;

                // (Re)register callback for this bag index.
                // Only one callback per bag index at a time.
                st.UnregisterCallbackSafe(sapi, bagIndex);
                st.CallbackIds[bagIndex] = sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        st.CallbackIds[bagIndex] = 0;

                        if (!st.PendingByBag[bagIndex]) return;
                        st.PendingByBag[bagIndex] = false;

                        int startTotal = st.WindowStartTotals[bagIndex];
                        int endTotal = st.BagTotals[bagIndex];

                        // Only degrade on net increase.
                        if (endTotal <= startTotal) return;

                        // Resolve bag slot + validate exclusions.
                        ItemSlot bagSlot = st.BagSlots[bagIndex];
                        ItemStack? bag = bagSlot.Itemstack;
                        if (bag?.Collectible == null) return;

                        if (cfg.ExcludeTier1 && IsTier1Bag(bag)) return;
                        if (cfg.ExcludeBaskets && IsBasket(bag)) return;

                        int max = bag.Collectible.GetMaxDurability(bag);
                        if (max <= 0) return;

                        int dmg = cfg.DamagePerMove;
                        dmg = Math.Clamp(dmg, cfg.MinDamage, cfg.MaxDamage);
                        if (dmg <= 0) return;

                        bag.Collectible.DamageItem(sapi.World, plr.Entity, bagSlot, dmg);
                    }
                    catch
                    {
                        // ignore
                    }
                }, cfg.DebounceWindowMs);
            }
        }

        private sealed class BackpackState
        {
            public readonly int BackpackCount;
            public readonly ItemSlot[] BagSlots;

            public readonly int[] SlotLastSize;         // per backpack slot
            public readonly int[] BagTotals;            // per bag index

            public readonly bool[] PendingByBag;
            public readonly int[] WindowStartTotals;
            public readonly long[] CallbackIds;

            public BackpackState(InventoryBase backpack)
            {
                BackpackCount = backpack.Count;
                BagSlots = CollectBagSlots(backpack);

                SlotLastSize = new int[BackpackCount];
                BagTotals = new int[BagSlots.Length];

                PendingByBag = new bool[BagSlots.Length];
                WindowStartTotals = new int[BagSlots.Length];
                CallbackIds = new long[BagSlots.Length];

                // Initialize slot sizes + bag totals once.
                for (int i = 0; i < BackpackCount; i++)
                {
                    ItemSlot slot = backpack[i];
                    SlotLastSize[i] = slot.StackSize;

                    if (slot is ItemSlotBagContent bc)
                    {
                        int bi = bc.BagIndex;
                        if (bi >= 0 && bi < BagTotals.Length)
                        {
                            BagTotals[bi] += slot.StackSize;
                        }
                    }
                }
            }

            public void UnregisterCallbackSafe(ICoreServerAPI sapi, int bagIndex)
            {
                try
                {
                    long id = CallbackIds[bagIndex];
                    if (id != 0)
                    {
                        sapi.Event.UnregisterCallback(id);
                        CallbackIds[bagIndex] = 0;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            public void UnregisterCallbacksSafe(ICoreServerAPI sapi)
            {
                for (int i = 0; i < CallbackIds.Length; i++)
                {
                    UnregisterCallbackSafe(sapi, i);
                }
            }
        }

        private static ItemSlot[] CollectBagSlots(InventoryBase backpack)
        {
            var list = new List<ItemSlot>(4);
            for (int i = 0; i < backpack.Count; i++)
            {
                if (backpack[i] is ItemSlotBackpack) list.Add(backpack[i]);
            }
            return list.ToArray();
        }

        private static bool IsBasket(ItemStack bag)
        {
            string code = bag.Collectible?.Code?.ToString() ?? "";
            return code.Contains("basket", StringComparison.OrdinalIgnoreCase)
                || code.Contains("handbasket", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTier1Bag(ItemStack bag)
        {
            JsonObject? attrs = bag.Collectible?.Attributes;
            if (attrs != null && attrs.KeyExists("iboBagTier"))
            {
                return attrs["iboBagTier"].AsInt(0) <= 1;
            }
            return false;
        }

        public sealed class BagDurabilityConfig
        {
            public bool ExcludeTier1 = true;
            public bool ExcludeBaskets = true;

            // One durability hit per put-in-window, regardless of number of items added.
            public int DamagePerMove = 1;

            // Debounce window (ms). Multiple add operations inside this window count as ONE durability hit per bag.
            public int DebounceWindowMs = 150;

            public int MinDamage = 0;
            public int MaxDamage = 1;

            public static BagDurabilityConfig Default() => new BagDurabilityConfig();
        }
    }
}
