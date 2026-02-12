#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveBackpacks
{
    public class BagTierHarmonyGateSystem : ModSystem
    {
        private Harmony? harmony;
        public const string HarmonyId = "immersivebackpacks.bagtier.gate";

        public override void Start(ICoreAPI api)
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(BagTierHarmonyGateSystem).Assembly);
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;

            BagTierEquipSlotRegistry.ClearAll();
        }
    }

    /// <summary>
    /// Self-sufficient equip-slot rule registry.
    /// Built lazily from the inventory itself (client+server) and then cached O(1).
    /// </summary>
    public static class BagTierEquipSlotRegistry
    {
        // Final desired equip tiers: [Pouch, Pouch, Satchel, Backpack]
        private static readonly int[] RequiredTierByEquipIndex = { 1, 1, 2, 3 };

        public sealed class Rule
        {
            public int EquipIndex;
            public int RequiredTier;
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new();
            public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class SlotMaps
        {
            public readonly Dictionary<ItemSlot, Rule> BySlotRef;
            public readonly int BackpackCountSnapshot;

            public SlotMaps(Dictionary<ItemSlot, Rule> bySlotRef, int backpackCountSnapshot)
            {
                BySlotRef = bySlotRef;
                BackpackCountSnapshot = backpackCountSnapshot;
            }
        }

        // InventoryID -> cached maps (inventoryID is stable; client has it too)
        private static readonly Dictionary<string, SlotMaps> invIdToMaps = new(StringComparer.Ordinal);

        public static void ClearAll()
        {
            lock (invIdToMaps) invIdToMaps.Clear();
        }

        public static void UnregisterInventory(string inventoryId)
        {
            lock (invIdToMaps) invIdToMaps.Remove(inventoryId);
        }

        public static bool TryGetRule(ItemSlot slot, out Rule rule)
        {
            rule = null!;

            if (slot.Inventory is not InventoryBase invBase) return false;

            string invId = invBase.InventoryID;

            // Fast path: cached lookup
            SlotMaps? maps;
            lock (invIdToMaps)
            {
                invIdToMaps.TryGetValue(invId, out maps);
            }

            // If missing or inventory resized, rebuild.
            if (maps == null || maps.BackpackCountSnapshot != invBase.Count)
            {
                maps = BuildMaps(invBase);
                lock (invIdToMaps)
                {
                    invIdToMaps[invId] = maps;
                }
            }

            // Primary: reference lookup
            if (maps.BySlotRef.TryGetValue(slot, out rule))
            {
                return true;
            }

            // If the slot ref isn't in the map (rare), rebuild once more and retry.
            maps = BuildMaps(invBase);
            lock (invIdToMaps)
            {
                invIdToMaps[invId] = maps;
            }

            return maps.BySlotRef.TryGetValue(slot, out rule);
        }

        private static SlotMaps BuildMaps(InventoryBase backpackInv)
        {
            var byRef = new Dictionary<ItemSlot, Rule>(4, ReferenceEqualityComparer<ItemSlot>.Instance);

            int equipIndex = 0;

            for (int slotId = 0; slotId < backpackInv.Count; slotId++)
            {
                ItemSlot s = backpackInv[slotId];
                if (s is ItemSlotBackpack)
                {
                    if (equipIndex < RequiredTierByEquipIndex.Length)
                    {
                        var r = new Rule
                        {
                            EquipIndex = equipIndex,
                            RequiredTier = RequiredTierByEquipIndex[equipIndex]
                        };
                        byRef[s] = r;
                    }

                    equipIndex++;
                }
            }

            return new SlotMaps(byRef, backpackInv.Count);
        }
    }

    public static class TierUtil
    {
        /// <summary>
        /// STRICT: must have Collectible.Attributes["iboBagTier"] in range 1..3, else returns 0.
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

        public static IPlayer? TryGetOwnerFromBackpackInventory(InventoryBase invBase)
        {
            string id = invBase.InventoryID;
            string prefix = GlobalConstants.backpackInvClassName + "-"; // "backpack-"

            if (!id.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string uid = id.Substring(prefix.Length);

            return invBase.Api?.World?.PlayerByUid(uid);
        }
    }

    // ============================================================
    // HARD GATE: ItemSlot.CanHold
    // ============================================================
    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.CanHold))]
    public static class Patch_ItemSlot_CanHold
    {
        public static bool Prefix(ItemSlot __instance, ItemSlot sourceSlot, ref bool __result)
        {
            if (__instance is not ItemSlotBackpack) return true;

            // IMPORTANT: If we can't resolve the rule, FAIL CLOSED for ItemSlotBackpack.
            // This prevents manual insertion bypass and keeps mod behavior consistent.
            if (!BagTierEquipSlotRegistry.TryGetRule(__instance, out var rule))
            {
                __result = false;
                return false;
            }

            // Tier gating (STRICT)
            int actualTier = TierUtil.GetTierStrictOrZero(sourceSlot?.Itemstack);
            if (actualTier == 0 || actualTier != rule.RequiredTier)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    // ============================================================
    // HARD GATE: ItemSlot.CanTakeFrom
    // ============================================================
    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.CanTakeFrom))]
    public static class Patch_ItemSlot_CanTakeFrom
    {
        public static bool Prefix(ItemSlot __instance, ItemSlot sourceSlot, EnumMergePriority priority, ref bool __result)
        {
            if (__instance is not ItemSlotBackpack) return true;

            // Same fail-closed behavior.
            if (!BagTierEquipSlotRegistry.TryGetRule(__instance, out var rule))
            {
                __result = false;
                return false;
            }

            int actualTier = TierUtil.GetTierStrictOrZero(sourceSlot?.Itemstack);
            if (actualTier == 0 || actualTier != rule.RequiredTier)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }
}
