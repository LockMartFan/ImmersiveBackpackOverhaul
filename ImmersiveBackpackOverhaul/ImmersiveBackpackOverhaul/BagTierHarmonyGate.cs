#nullable enable

using HarmonyLib;
using Vintagestory.API.Common;

namespace ImmersiveBackpacks
{
    /// <summary>
    /// Harmony loader. Runs on both client and server.
    /// </summary>
    public class BagTierHarmonyGate : ModSystem
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

    /// <summary>
    /// HARD GATE: blocks invalid insertion into ItemSlotBackpack equip slots on both client and server.
    /// No registry, no timing: compute equipIndex on-demand.
    /// </summary>
    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.CanHold))]
    public static class Patch_ItemSlot_CanHold
    {
        private static readonly int[] RequiredTierByEquipIndex = { 1, 1, 2, 3 };

        public static bool Prefix(ItemSlot __instance, ItemSlot sourceSlot, ref bool __result)
        {
            if (__instance is not ItemSlotBackpack) return true;
            if (__instance.Inventory is not InventoryBase invBase) return true;

            int slotId = invBase.GetSlotId(__instance);
            if (slotId < 0) return true;

            if (!TryGetEquipIndexForSlot(invBase, slotId, out int equipIndex))
            {
                return true;
            }

            if (equipIndex < 0 || equipIndex >= RequiredTierByEquipIndex.Length)
            {
                __result = false;
                return false;
            }

            int requiredTier = RequiredTierByEquipIndex[equipIndex];
            int actualTier = TierUtil.GetTierStrictOrZero(sourceSlot?.Itemstack);

            // STRICT: missing/invalid tag => reject
            if (actualTier == 0 || actualTier != requiredTier)
            {
                __result = false;
                return false;
            }

            return true;
        }

        private static bool TryGetEquipIndexForSlot(InventoryBase invBase, int targetSlotId, out int equipIndex)
        {
            equipIndex = -1;
            int found = 0;

            for (int id = 0; id < invBase.Count; id++)
            {
                if (invBase[id] is ItemSlotBackpack)
                {
                    if (id == targetSlotId)
                    {
                        equipIndex = found;
                        return true;
                    }
                    found++;
                }
            }

            return false;
        }
    }
}
