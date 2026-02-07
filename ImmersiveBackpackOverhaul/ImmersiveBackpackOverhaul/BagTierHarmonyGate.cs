#nullable enable

using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Client;

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
    /// Also enforces: pouch slots (0,1) require waist slot occupied.
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

            // Waist gating for pouch slots:
            if ((equipIndex == 0 || equipIndex == 1) && !IsWaistOccupiedForThisBackpackInventory(invBase))
            {
                __result = false;
                return false;
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

        private static bool IsWaistOccupiedForThisBackpackInventory(InventoryBase backpackInv)
        {
            // backpackInv.InventoryID typically looks like "backpack-<playeruid>"
            string invId = backpackInv.InventoryID ?? "";
            const string prefix = "backpack-";
            string? uid = invId.StartsWith(prefix) ? invId.Substring(prefix.Length) : null;

            // Client: only the local player matters.
            if (backpackInv.Api is ICoreClientAPI capi)
            {
                var plr = capi.World.Player;
                return IsWaistOccupied(plr?.InventoryManager);
            }

            // Server: resolve by UID (returns IPlayer, not IServerPlayer)
            if (backpackInv.Api is ICoreServerAPI sapi)
            {
                if (uid == null) return true; // fail-open

                IPlayer? plr = sapi.World.PlayerByUid(uid);
                return IsWaistOccupied(plr?.InventoryManager);
            }

            return true; // fail-open
        }

        private static bool IsWaistOccupied(IPlayerInventoryManager? invMan)
        {
            if (invMan == null) return true;

            IInventory? charInv = invMan.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (charInv == null) return true;

            foreach (var slot in charInv)
            {
                if (slot is ItemSlotCharacter ch && ch.Type == EnumCharacterDressType.Waist)
                {
                    return !ch.Empty;
                }
            }

            return true; // fail-open if waist slot not found
        }
    }
}
