#nullable enable

using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

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
        }
    }

    /// <summary>
    /// Registry: backpack InventoryID -> (slotId -> rule).
    /// Filled server-side on PlayerNowPlaying in BackpackSlotBouncerSystem.
    /// Harmony uses it to know which slotIds correspond to equipIndex 0..3 and what tier they require.
    /// </summary>
    public static class BagTierEquipSlotRegistry
    {
        public sealed class Rule
        {
            public int EquipIndex;
            public int RequiredTier;
        }

        private static readonly Dictionary<string, Dictionary<int, Rule>> invIdToSlotRules = new();

        public static void RegisterBackpackEquipSlots(InventoryBase backpackInv, int[] requiredTierByEquipIndex)
        {
            string invId = backpackInv.InventoryID;

            var map = new Dictionary<int, Rule>(4);
            int equipIndex = 0;

            for (int slotId = 0; slotId < backpackInv.Count; slotId++)
            {
                ItemSlot slot = backpackInv[slotId];
                if (slot is ItemSlotBackpack)
                {
                    if (equipIndex < requiredTierByEquipIndex.Length)
                    {
                        map[slotId] = new Rule
                        {
                            EquipIndex = equipIndex,
                            RequiredTier = requiredTierByEquipIndex[equipIndex]
                        };
                    }
                    equipIndex++;
                }
            }

            lock (invIdToSlotRules)
            {
                invIdToSlotRules[invId] = map;
            }
        }

        public static void UnregisterInventory(string inventoryId)
        {
            lock (invIdToSlotRules)
            {
                invIdToSlotRules.Remove(inventoryId);
            }
        }

        public static bool TryGetRule(InventoryBase inv, int slotId, out Rule rule)
        {
            rule = null!;

            Dictionary<int, Rule>? map;
            lock (invIdToSlotRules)
            {
                if (!invIdToSlotRules.TryGetValue(inv.InventoryID, out map)) return false;
            }

            return map.TryGetValue(slotId, out rule);
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

        /// <summary>
        /// Waist occupied check (server authoritative when possible).
        /// We consider "waist occupied" if the character inventory contains any item whose dress type is Waist.
        /// </summary>
        public static bool HasWaistEquipped(IPlayer player)
        {
            var invMan = player.InventoryManager;
            if (invMan == null) return false;

            IInventory? charInv = invMan.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (charInv is not InventoryBase charInvBase) return false;

            for (int i = 0; i < charInvBase.Count; i++)
            {
                ItemSlot slot = charInvBase[i];
                if (slot?.Itemstack == null) continue;

                // If any equipped item is a waist dress type, we treat waist slot as occupied.
                if (ItemSlotCharacter.IsDressType(slot.Itemstack, EnumCharacterDressType.Waist))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolve the owning player from a backpack inventory using InventoryID format: "backpack-{playerUid}".
        /// Returns null if not resolvable.
        /// </summary>
        public static IPlayer? TryGetOwnerFromBackpackInventory(InventoryBase invBase)
        {
            // Example from audit logs: "backpack-vvsH+9emBJnLZG4n9ebInF2Q"
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
            if (__instance.Inventory is not InventoryBase invBase) return true;

            int slotId = FindSlotId(invBase, __instance);
            if (slotId < 0) return true;

            if (!BagTierEquipSlotRegistry.TryGetRule(invBase, slotId, out var rule))
            {
                // If we don't know the rule, don't block (but bouncer will still enforce server-side)
                return true;
            }

            // Waist gating only for pouch slots (equipIndex 0 and 1)
            if (rule.EquipIndex <= 1)
            {
                IPlayer? owner = TierUtil.TryGetOwnerFromBackpackInventory(invBase);
                if (owner != null && !TierUtil.HasWaistEquipped(owner))
                {
                    __result = false;
                    return false;
                }
            }

            // Tier gating (STRICT)
            int actualTier = TierUtil.GetTierStrictOrZero(sourceSlot?.Itemstack);
            if (actualTier == 0 || actualTier != rule.RequiredTier)
            {
                __result = false;
                return false;
            }

            // Let vanilla run additional checks too
            return true;
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
    // HARD GATE: ItemSlot.CanTakeFrom
    // (Covers some move-operation paths where CanHold isn't consulted first)
    // ============================================================
    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.CanTakeFrom))]
    public static class Patch_ItemSlot_CanTakeFrom
    {
        public static bool Prefix(ItemSlot __instance, ItemSlot sourceSlot, EnumMergePriority priority, ref bool __result)
        {
            if (__instance is not ItemSlotBackpack) return true;
            if (__instance.Inventory is not InventoryBase invBase) return true;

            int slotId = FindSlotId(invBase, __instance);
            if (slotId < 0) return true;

            if (!BagTierEquipSlotRegistry.TryGetRule(invBase, slotId, out var rule))
            {
                return true;
            }

            // Waist gating for pouch slots
            if (rule.EquipIndex <= 1)
            {
                IPlayer? owner = TierUtil.TryGetOwnerFromBackpackInventory(invBase);
                if (owner != null && !TierUtil.HasWaistEquipped(owner))
                {
                    __result = false;
                    return false;
                }
            }

            int actualTier = TierUtil.GetTierStrictOrZero(sourceSlot?.Itemstack);
            if (actualTier == 0 || actualTier != rule.RequiredTier)
            {
                __result = false;
                return false;
            }

            return true;
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
}
