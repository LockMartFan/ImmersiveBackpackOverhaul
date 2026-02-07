#nullable enable

using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;

namespace ImmersiveBackpacks
{
    /// <summary>
    /// ONE-file Harmony gate (strict).
    ///
    /// Enforces the bouncer's tier rules at the slot acceptance layer:
    ///  - ItemSlot.CanHold(...)
    ///  - ItemSlot.CanTakeFrom(...)
    ///
    /// This is the safest place to enforce "under no circumstances" rules,
    /// because it affects shift-click, RMB pickup, auto-pickup, drag-drop, etc.
    /// </summary>
    public sealed class BagTierHarmonyGate : ModSystem
    {
        private Harmony? harmony;
        private const string HarmonyId = "immersivebackpacks.bagtier.gate";

        public override void Start(ICoreAPI api)
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(BagTierHarmonyGate).Assembly);
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }
    }

    // ============================================================
    //  A) HARD GATE: ItemSlot.CanHold
    // ============================================================

    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.CanHold))]
    public static class Patch_ItemSlot_CanHold
    {
        public static bool Prefix(ItemSlot __instance, ItemSlot sourceSlot, ref bool __result)
        {
            if (!IsTargetedBackpackEquipSlot(__instance, out var invBase, out int slotId, out int requiredTier))
                return true;

            int actualTier = TierUtil.GetTierStrictOrZero(sourceSlot?.Itemstack);
            if (actualTier == 0 || actualTier != requiredTier)
            {
                __result = false;
                return false;
            }

            return true; // tier ok -> allow vanilla checks too
        }

        private static bool IsTargetedBackpackEquipSlot(ItemSlot inst, out InventoryBase? invBase, out int slotId, out int requiredTier)
        {
            invBase = null;
            slotId = -1;
            requiredTier = 0;

            if (inst is not ItemSlotBackpack) return false;

            invBase = inst.Inventory as InventoryBase;
            if (invBase == null) return false;

            slotId = FindSlotId(invBase, inst);
            if (slotId < 0) return false;

            return BagTierRegistry.TryGetRequiredTier(invBase, slotId, out requiredTier);
        }

        private static int FindSlotId(InventoryBase inv, ItemSlot target)
        {
            for (int i = 0; i < inv.Count; i++)
            {
                if (ReferenceEquals(inv[i], target)) return i;
            }
            return -1;
        }
    }

    // ============================================================
    //  B) HARD GATE: ItemSlot.CanTakeFrom
    // ============================================================

    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.CanTakeFrom))]
    public static class Patch_ItemSlot_CanTakeFrom
    {
        public static bool Prefix(ItemSlot __instance, ItemSlot sourceSlot, EnumMergePriority priority, ref bool __result)
        {
            if (!IsTargetedBackpackEquipSlot(__instance, out var invBase, out int slotId, out int requiredTier))
                return true;

            int actualTier = TierUtil.GetTierStrictOrZero(sourceSlot?.Itemstack);
            if (actualTier == 0 || actualTier != requiredTier)
            {
                __result = false;
                return false;
            }

            return true; // tier ok -> allow vanilla checks too
        }

        private static bool IsTargetedBackpackEquipSlot(ItemSlot inst, out InventoryBase? invBase, out int slotId, out int requiredTier)
        {
            invBase = null;
            slotId = -1;
            requiredTier = 0;

            if (inst is not ItemSlotBackpack) return false;

            invBase = inst.Inventory as InventoryBase;
            if (invBase == null) return false;

            slotId = FindSlotId(invBase, inst);
            if (slotId < 0) return false;

            return BagTierRegistry.TryGetRequiredTier(invBase, slotId, out requiredTier);
        }

        private static int FindSlotId(InventoryBase inv, ItemSlot target)
        {
            for (int i = 0; i < inv.Count; i++)
            {
                if (ReferenceEquals(inv[i], target)) return i;
            }
            return -1;
        }
    }

    // ============================================================
    //  Shared Registry + Tier Util (strict)
    // ============================================================

    /// <summary>
    /// Server fills this on PlayerNowPlaying (via BackpackSlotBouncerSystem).
    /// Map: backpack InventoryID -> (slotId -> requiredTier)
    /// </summary>
    public static class BagTierRegistry
    {
        private static readonly Dictionary<string, Dictionary<int, int>> invIdToSlotTier = new();

        public static void RegisterBackpackEquipSlots(InventoryBase backpackInv, int[] requiredTierByEquipIndex)
        {
            string invId = backpackInv.InventoryID;
            var map = new Dictionary<int, int>(4);

            int equipIndex = 0;

            for (int slotId = 0; slotId < backpackInv.Count; slotId++)
            {
                ItemSlot slot = backpackInv[slotId];
                if (slot is ItemSlotBackpack)
                {
                    if (equipIndex < requiredTierByEquipIndex.Length)
                    {
                        map[slotId] = requiredTierByEquipIndex[equipIndex];
                    }
                    equipIndex++;
                }
            }

            lock (invIdToSlotTier)
            {
                invIdToSlotTier[invId] = map;
            }
        }

        public static void UnregisterInventory(string inventoryId)
        {
            lock (invIdToSlotTier)
            {
                invIdToSlotTier.Remove(inventoryId);
            }
        }

        public static bool TryGetRequiredTier(InventoryBase inv, int slotId, out int requiredTier)
        {
            requiredTier = 0;

            Dictionary<int, int>? map;
            lock (invIdToSlotTier)
            {
                if (!invIdToSlotTier.TryGetValue(inv.InventoryID, out map)) return false;
            }

            return map.TryGetValue(slotId, out requiredTier);
        }
    }

    public static class TierUtil
    {
        /// <summary>
        /// STRICT:
        ///  - requires Collectible.Attributes["iboBagTier"] to exist and be 1..3
        ///  - otherwise returns 0 (invalid / not supported)
        /// </summary>
        public static int GetTierStrictOrZero(ItemStack? stack)
        {
            if (stack?.Collectible == null) return 0;

            var attrs = stack.Collectible.Attributes;
            if (attrs == null) return 0;
            if (!attrs.KeyExists("iboBagTier")) return 0;

            int t = attrs["iboBagTier"].AsInt(0);
            return (t >= 1 && t <= 3) ? t : 0;
        }
    }
}
