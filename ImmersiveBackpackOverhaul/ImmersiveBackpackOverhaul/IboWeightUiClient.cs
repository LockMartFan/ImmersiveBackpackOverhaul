#nullable enable

using System;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ImmersiveBackpackOverhaul
{
    public sealed class IboWeightUiClient : ModSystem
    {
        private ICoreClientAPI capi = null!;
        private GuiDialogIboWeight? dialog;

        private const string HotkeyCode = "ibo-toggleweightwindow";
        private const string HotkeyName = "Toggle Weight Window";

        internal const string SettingsKeyX = "iboWeightWindowX";
        internal const string SettingsKeyY = "iboWeightWindowY";

        private float lastRaw = float.NaN;
        private float lastEff = float.NaN;
        private float lastStart = float.NaN;
        private float lastMax = float.NaN;
        private float lastEnc = float.NaN;

        private long refreshCallbackId;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        private static bool IsWeightSystemEnabledFromConfig(ICoreClientAPI capi)
        {
            try
            {
                var asset = capi.Assets.TryGet(new AssetLocation("immersivebackpackoverhaul", "config/ibo-weights.json"));
                if (asset == null) return true; // fail-open

                var cfg = asset.ToObject<IboWeightSystem.WeightConfig>();
                return cfg?.Enabled ?? true;
            }
            catch
            {
                return true; // fail-open
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // Master switch: if disabled, do not register hotkeys/UI/tooltips.
            if (!IsWeightSystemEnabledFromConfig(capi))
            {
                capi.Logger.Notification("[IBO] Weight system disabled in config/ibo-weights.json (Enabled=false). Client UI/tooltips not loaded.");
                return;
            }

            // Default binding: K (rebindable in Controls)
            capi.Input.RegisterHotKey(HotkeyCode, HotkeyName, GlKeys.K, HotkeyType.GUIOrOtherControls, false, false, false);
            capi.Input.SetHotKeyHandler(HotkeyCode, _ =>
            {
                ToggleWindow();
                return true;
            });

            dialog = new GuiDialogIboWeight(capi);

            TryHookAttributeEvents();

            // Show item weights in tooltips (includes liquid contents)
            IboWeightTooltipInjector.InjectIntoAllCollectibles(capi);

            // Show bag effectiveness as LOAD REDUCTION in tooltips for any bag that has attributes.iboBagEffectiveness
            IboBagTooltipInjector.InjectIntoAllCollectibles(capi);
        }

        public override void Dispose()
        {
            StopRefresh();
            dialog?.TryClose();
            dialog = null;
        }

        private void ToggleWindow()
        {
            if (dialog == null) return;

            if (dialog.IsOpened())
            {
                dialog.TryClose();
                StopRefresh();
            }
            else
            {
                dialog.TryOpen();
                ForceRefresh();
                StartRefreshWhileOpen();
            }
        }

        private void ForceRefresh()
        {
            lastRaw = float.NaN;
            lastEff = float.NaN;
            lastStart = float.NaN;
            lastMax = float.NaN;
            lastEnc = float.NaN;
            RefreshIfNeeded();
        }

        private void RefreshIfNeeded()
        {
            var ent = capi.World.Player?.Entity;
            if (ent == null || dialog == null) return;

            float raw = ent.WatchedAttributes.GetFloat("iboCarriedKg", float.NaN);
            float eff = ent.WatchedAttributes.GetFloat("iboEffectiveKg", float.NaN);
            float start = ent.WatchedAttributes.GetFloat("iboStartKg", float.NaN);
            float max = ent.WatchedAttributes.GetFloat("iboMaxKg", float.NaN);
            float enc = ent.WatchedAttributes.GetFloat("iboEncPct", float.NaN);

            if (float.IsNaN(raw)) raw = 0;
            if (float.IsNaN(eff)) eff = 0;
            if (float.IsNaN(start)) start = 0;
            if (float.IsNaN(max)) max = 0;
            if (float.IsNaN(enc)) enc = 0;

            if (NearlyEqual(raw, lastRaw) && NearlyEqual(eff, lastEff) &&
                NearlyEqual(start, lastStart) && NearlyEqual(max, lastMax) && NearlyEqual(enc, lastEnc))
                return;

            lastRaw = raw;
            lastEff = eff;
            lastStart = start;
            lastMax = max;
            lastEnc = enc;

            dialog.SetValues(raw, eff, start, max, enc);
        }

        private static bool NearlyEqual(float a, float b)
        {
            if (float.IsNaN(a) || float.IsNaN(b)) return false;
            return Math.Abs(a - b) < 0.01f;
        }

        private void StartRefreshWhileOpen()
        {
            if (refreshCallbackId != 0) return;

            // No tick calls: only while open, low frequency
            refreshCallbackId = capi.Event.RegisterCallback(_ =>
            {
                refreshCallbackId = 0;

                if (dialog != null && dialog.IsOpened())
                {
                    RefreshIfNeeded();
                    StartRefreshWhileOpen();
                }
            }, 250);
        }

        private void StopRefresh()
        {
            if (refreshCallbackId != 0)
            {
                capi.Event.UnregisterCallback(refreshCallbackId);
                refreshCallbackId = 0;
            }
        }

        private void TryHookAttributeEvents()
        {
            try
            {
                var ent = capi.World.Player?.Entity;
                if (ent == null) return;

                var wa = ent.WatchedAttributes;
                if (wa == null) return;

                var evt = wa.GetType().GetEvent("OnModified");
                if (evt == null) return;

                var handlerType = evt.EventHandlerType;
                if (handlerType == null) return;

                if (handlerType == typeof(Action))
                {
                    Action handler = () =>
                    {
                        if (dialog != null && dialog.IsOpened()) RefreshIfNeeded();
                    };
                    evt.AddEventHandler(wa, handler);
                }
                else if (handlerType == typeof(Action<string>))
                {
                    Action<string> handler = key =>
                    {
                        if (dialog == null || !dialog.IsOpened()) return;

                        if (key == "iboCarriedKg" || key == "iboEffectiveKg" || key == "iboStartKg" || key == "iboMaxKg" || key == "iboEncPct")
                        {
                            RefreshIfNeeded();
                        }
                    };
                    evt.AddEventHandler(wa, handler);
                }
            }
            catch
            {
                // refresh loop while open covers it
            }
        }
    }

    internal sealed class GuiDialogIboWeight : GuiDialog
    {
        private const string DialogCode = "iboWeightDialog";

        private const string ElemRaw = "iboRaw";
        private const string ElemEff = "iboEff";
        private const string ElemEnc = "iboEnc";

        private float rawKg;
        private float effKg;
        private float startKg;
        private float maxKg;
        private float encPct;

        private ElementBounds? dialogBounds;

        public GuiDialogIboWeight(ICoreClientAPI capi) : base(capi)
        {
            Compose();
        }

        public void SetValues(float raw, float eff, float start, float max, float enc)
        {
            rawKg = raw;
            effKg = eff;
            startKg = start;
            maxKg = max;
            encPct = enc;

            if (!IsOpened()) return;

            SingleComposer?.GetDynamicText(ElemRaw)?.SetNewText($"Raw weight: {rawKg:0.##} kg");

            // Display requested: EFFECTIVE / MAXBEFOREDEBUFF (startKg)
            if (startKg > 0)
            {
                SingleComposer?.GetDynamicText(ElemEff)?.SetNewText($"Carried weight: {effKg:0.##} / {startKg:0.##} kg");
            }
            else
            {
                SingleComposer?.GetDynamicText(ElemEff)?.SetNewText($"Carried weight: {effKg:0.##} kg");
            }

            SingleComposer?.GetDynamicText(ElemEnc)?.SetNewText($"Slowdown: {encPct * 100f:0}%");
        }

        public override string ToggleKeyCombinationCode => null!;
        public override bool PrefersUngrabbedMouse => true;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            SetValues(rawKg, effKg, startKg, maxKg, encPct);
        }

        public override void OnGuiClosed()
        {
            PersistPosition();
            base.OnGuiClosed();
        }

        private void Compose()
        {
            dialogBounds = ElementBounds.Fixed(0, 0, 330, 100);

            int offX = 20;
            int offY = 20;

            try
            {
                if (capi.Settings != null)
                {
                    offX = capi.Settings.Int.Get(IboWeightUiClient.SettingsKeyX, 20);
                    offY = capi.Settings.Int.Get(IboWeightUiClient.SettingsKeyY, 20);
                }
            }
            catch { }

            dialogBounds
                .WithAlignment(EnumDialogArea.LeftTop)
                .WithFixedOffset(offX, offY);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(12);

            var font = CairoFont.WhiteSmallText();
            font.UnscaledFontsize = 18;

            const int startY = 20;
            const int lineH = 22;
            const int gap = 2;

            SingleComposer = capi.Gui
                .CreateCompo(DialogCode, dialogBounds)
                .AddDialogBG(bgBounds, withTitleBar: true)
                .AddDialogTitleBar("Weight", () => TryClose())
                .BeginChildElements(bgBounds)

                .AddDynamicText("Raw weight: 0 kg", font, ElementBounds.Fixed(0, startY + (lineH + gap) * 0, 400, lineH), key: ElemRaw)
                .AddDynamicText("Carried weight: 0 / 0 kg", font, ElementBounds.Fixed(0, startY + (lineH + gap) * 1, 400, lineH), key: ElemEff)
                .AddDynamicText("Slowdown: 0%", font, ElementBounds.Fixed(0, startY + (lineH + gap) * 2, 400, lineH), key: ElemEnc)

                .EndChildElements()
                .Compose();
        }

        private void PersistPosition()
        {
            try
            {
                if (dialogBounds == null || capi.Settings == null) return;

                int x = (int)Math.Round(dialogBounds.fixedX);
                int y = (int)Math.Round(dialogBounds.fixedY);

                capi.Settings.Int.Set(IboWeightUiClient.SettingsKeyX, x, false);
                capi.Settings.Int.Set(IboWeightUiClient.SettingsKeyY, y, false);
            }
            catch { }
        }
    }


    // ------------------------
    // Weight tooltip injector
    // ------------------------

    internal static class IboWeightTooltipInjector
    {
        private static bool injected;

        private static IboWeightSystem.WeightConfig cfg = IboWeightSystem.WeightConfig.Default();

        // Per-session caches (tiny + fast)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> BaseKgPerUnitByCollectibleCode = new(StringComparer.Ordinal);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> ItemsPerLitreByLiquidCode = new(StringComparer.Ordinal);

        public static void InjectIntoAllCollectibles(ICoreClientAPI capi)
        {
            if (injected) return;
            injected = true;

            LoadConfig(capi);

            foreach (var col in capi.World.Collectibles)
            {
                if (col?.Code == null) continue;
                if (HasBehavior<IboWeightTooltipBehavior>(col)) continue;

                var beh = new IboWeightTooltipBehavior(col, () => cfg, BaseKgPerUnitByCollectibleCode, ItemsPerLitreByLiquidCode);
                beh.Initialize(new JsonObject(new JObject()));
                beh.OnLoaded(capi);

                var arr = col.CollectibleBehaviors;
                var newArr = new CollectibleBehavior[arr.Length + 1];
                Array.Copy(arr, newArr, arr.Length);
                newArr[arr.Length] = beh;
                col.CollectibleBehaviors = newArr;
            }
        }

        private static void LoadConfig(ICoreClientAPI capi)
        {
            try
            {
                var asset = capi.Assets.TryGet(new AssetLocation("immersivebackpackoverhaul", "config/ibo-weights.json"));
                if (asset == null) return;

                var parsed = asset.ToObject<IboWeightSystem.WeightConfig>();
                if (parsed != null) cfg = parsed;
            }
            catch
            {
                // keep defaults
            }
        }

        private static bool HasBehavior<T>(CollectibleObject col) where T : CollectibleBehavior
        {
            var arr = col.CollectibleBehaviors;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] is T) return true;
            }
            return false;
        }
    }

    internal sealed class IboWeightTooltipBehavior : CollectibleBehavior
    {
        private readonly Func<IboWeightSystem.WeightConfig> getCfg;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> baseKgPerUnitByCollectibleCode;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> itemsPerLitreByLiquidCode;

        public IboWeightTooltipBehavior(
            CollectibleObject collObj,
            Func<IboWeightSystem.WeightConfig> getCfg,
            System.Collections.Concurrent.ConcurrentDictionary<string, float> baseKgPerUnitByCollectibleCode,
            System.Collections.Concurrent.ConcurrentDictionary<string, float> itemsPerLitreByLiquidCode
        ) : base(collObj)
        {
            this.getCfg = getCfg;
            this.baseKgPerUnitByCollectibleCode = baseKgPerUnitByCollectibleCode;
            this.itemsPerLitreByLiquidCode = itemsPerLitreByLiquidCode;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            var cfg = getCfg();
            if (cfg == null || !cfg.Enabled) return;

            ItemStack? stack = inSlot?.Itemstack;
            if (stack?.Collectible == null) return;

            float totalKg = ResolveStackKg(stack, world, cfg);
            if (totalKg <= 0.0001f) return;

            dsc.AppendLine($"Weight: {Math.Round(totalKg, 2):0.##} kg");
        }

        private float ResolveStackKg(ItemStack stack, IWorldAccessor world, IboWeightSystem.WeightConfig cfg)
        {
            float perUnitBaseKg = ResolveBaseKgPerUnit(stack, cfg);
            float baseKg = perUnitBaseKg * stack.StackSize;

            float liquidKg = ResolveLiquidContentsKg(stack, world);
            return baseKg + liquidKg;
        }

        private float ResolveBaseKgPerUnit(ItemStack stack, IboWeightSystem.WeightConfig cfg)
        {
            var col = stack.Collectible!;
            string code = col.Code?.ToString() ?? "";

            if (!string.IsNullOrEmpty(code))
            {
            if (baseKgPerUnitByCollectibleCode.TryGetValue(code, out float cached))
            {
                return cached;
            }
}

            float resolved;
            var attrs = col.Attributes;

            if (attrs != null && attrs.KeyExists("iboBaseWeightKg"))
            {
                resolved = Math.Max(0f, attrs["iboBaseWeightKg"].AsFloat(0f));
            }
            else
            {
                resolved = cfg.Rules.ResolveKg(stack, cfg.DefaultBlockKg);
            }

            if (!string.IsNullOrEmpty(code))
            {
            baseKgPerUnitByCollectibleCode[code] = resolved;
}

            return resolved;
        }

        /// <summary>
        /// Survival-style: containerStack.Attributes["contents"] holds ItemstackAttribute entries.
        /// For liquids: litres = content.StackSize / itemsPerLitre. We intentionally do 1L == 1kg for all liquids.
        /// </summary>
        private float ResolveLiquidContentsKg(ItemStack containerStack, IWorldAccessor world)
        {
            ITreeAttribute? contents = containerStack.Attributes?.GetTreeAttribute("contents");
            if (contents == null || contents.Count == 0) return 0f;

            float kg = 0f;

            foreach (var entry in contents)
            {
                if (entry.Value is not ItemstackAttribute isa) continue;

                ItemStack? content = isa.value;
                if (content == null || content.StackSize <= 0) continue;

                // IMPORTANT (perf): Do NOT call ResolveBlockOrItem() here.
                // Tooltips can be queried very frequently (hover/held item/inventory open),
                // and resolving nested stacks here can cause frame hitches.
                // If the nested stack isn't resolved yet, just skip it for now.
                if (content.Collectible == null) continue;

                if (content.Collectible.MatterState != EnumMatterState.Liquid) continue;

                JsonObject? wt = content.Collectible.Attributes?["waterTightContainerProps"];
                if (wt == null || !wt.Exists) continue;

                string liquidCode = content.Collectible.Code?.ToString() ?? "";
                float itemsPerLitre;
                if (!itemsPerLitreByLiquidCode.TryGetValue(liquidCode, out itemsPerLitre))
                {
                    itemsPerLitre = wt["itemsPerLitre"].AsFloat(1f);
                    if (itemsPerLitre <= 0f) itemsPerLitre = 1f;

                    if (!string.IsNullOrEmpty(liquidCode))
                    {
                        // ConcurrentDictionary ignores duplicates safely
                        itemsPerLitreByLiquidCode[liquidCode] = itemsPerLitre;
                    }
                }
float litres = content.StackSize / itemsPerLitre;
                if (litres > 0f) kg += litres;
            }

            return kg;
        }
    }


    // --------------------------------------------------------------------
    // Tooltip injection for bags (same file, no extra mod files needed)
    // --------------------------------------------------------------------

    internal static class IboBagTooltipInjector
    {
        // Defaults match server config defaults (and we try to load from ibo-weights.json)
        private static float BrokenEffectiveness = 1.0f;
        private static float CurvePower = 1.25f;

        private static bool injected;

        public static void InjectIntoAllCollectibles(ICoreClientAPI capi)
        {
            if (injected) return;
            injected = true;

            LoadDegradeConfig(capi);

            foreach (var col in capi.World.Collectibles)
            {
                if (col?.Code == null) continue;

                var attrs = col.Attributes;
                if (attrs == null) continue;

                if (!attrs.KeyExists("iboBagEffectiveness")) continue;

                if (HasBehavior<IboBagEffectivenessTooltipBehavior>(col)) continue;

                var beh = new IboBagEffectivenessTooltipBehavior(col, () => BrokenEffectiveness, () => CurvePower);
                beh.Initialize(new JsonObject(new JObject()));
                beh.OnLoaded(capi);

                var arr = col.CollectibleBehaviors;
                var newArr = new CollectibleBehavior[arr.Length + 1];
                Array.Copy(arr, newArr, arr.Length);
                newArr[arr.Length] = beh;
                col.CollectibleBehaviors = newArr;
            }
        }

        private static void LoadDegradeConfig(ICoreClientAPI capi)
        {
            try
            {
                var asset = capi.Assets.TryGet(new AssetLocation("immersivebackpackoverhaul", "config/ibo-weights.json"));
                if (asset == null) return;

                // WeightConfig is declared in your server ModSystem file but lives in the same mod assembly.
                // This is safe to reference client-side.
                var cfg = asset.ToObject<IboWeightSystem.WeightConfig>();
                if (cfg?.BagDegrade != null)
                {
                    BrokenEffectiveness = cfg.BagDegrade.BrokenEffectiveness;
                    CurvePower = cfg.BagDegrade.CurvePower;
                }
            }
            catch
            {
                // keep defaults
            }
        }

        private static bool HasBehavior<T>(CollectibleObject col) where T : CollectibleBehavior
        {
            var arr = col.CollectibleBehaviors;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] is T) return true;
            }
            return false;
        }
    }

    internal sealed class IboBagEffectivenessTooltipBehavior : CollectibleBehavior
    {
        private readonly Func<float> getBrokenEff;
        private readonly Func<float> getCurvePower;

        public IboBagEffectivenessTooltipBehavior(CollectibleObject collObj, Func<float> getBrokenEff, Func<float> getCurvePower) : base(collObj)
        {
            this.getBrokenEff = getBrokenEff;
            this.getCurvePower = getCurvePower;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            ItemStack? stack = inSlot?.Itemstack;
            if (stack?.Collectible?.Attributes == null) return;

            var attrs = stack.Collectible.Attributes;
            if (!attrs.KeyExists("iboBagEffectiveness")) return;

            // Base effectiveness is per-bag (different backpacks have different values)
            float baseEff = Math.Clamp(attrs["iboBagEffectiveness"].AsFloat(1f), 0f, 10f);

            // Current effectiveness includes durability degradation (mirrors server logic)
            float effNow = ResolveEffWithCondition(stack, baseEff);

            // Convert effectiveness (multiplier) -> load reduction (1 - multiplier)
            // Example: BE=0.35 => load reduction = 65%
            float baseRed = Math.Clamp(1f - baseEff, 0f, 1f);
            float currRed = Math.Clamp(1f - effNow, 0f, 1f);

            int basePct = (int)Math.Round(baseRed * 100f);
            int currPct = (int)Math.Round(currRed * 100f);

            dsc.AppendLine($"Load reduction: {currPct}% / {basePct}%");
        }

        private float ResolveEffWithCondition(ItemStack bag, float baseEff)
        {
            var col = bag.Collectible;
            if (col == null) return baseEff;

            int max = col.GetMaxDurability(bag);
            if (max <= 0) return baseEff;

            int rem = col.GetRemainingDurability(bag);
            float cond = rem / (float)max;
            cond = Math.Clamp(cond, 0f, 1f);

            float wear = 1f - cond;
            float penalty = (float)Math.Pow(wear, getCurvePower());

            float brokenEff = getBrokenEff();
            float eff = baseEff + (brokenEff - baseEff) * penalty;

            float lo = Math.Min(baseEff, brokenEff);
            float hi = Math.Max(baseEff, brokenEff);
            return Math.Clamp(eff, lo, hi);
        }
    }
}
