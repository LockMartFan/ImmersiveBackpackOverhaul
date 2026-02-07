#nullable enable

using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace ImmersiveBackpacks
{
    /// <summary>
    /// Client-only UI helper:
    /// - Copy waist slot background icon onto pouch slots (equip 0/1).
    /// - Gray out pouch slots when waist is empty.
    /// No ticking: updates on load and on relevant inventory slot modifications.
    /// </summary>
    public class PouchWaistUiClientSystem : ModSystem
    {
        private ICoreClientAPI capi = null!;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // Run once after world is ready.
            capi.Event.LevelFinalize += () => Refresh();

            // Also update whenever character/backpack inventory changes.
            // InventoryBase.SlotModified is cheap and event-driven.
            capi.Event.LevelFinalize += SubscribeInventoryEvents;
        }

        private void SubscribeInventoryEvents()
        {
            var player = capi.World.Player;
            if (player == null) return;

            var invMan = player.InventoryManager;
            if (invMan == null) return;

            if (invMan.GetOwnInventory(GlobalConstants.characterInvClassName) is InventoryBase charInv)
            {
                charInv.SlotModified += _ => Refresh();
            }

            if (invMan.GetOwnInventory(GlobalConstants.backpackInvClassName) is InventoryBase backpackInv)
            {
                backpackInv.SlotModified += _ => Refresh();
            }

            // Ensure initial state after subscriptions.
            Refresh();
        }

        private void Refresh()
        {
            var player = capi.World.Player;
            if (player == null) return;

            var invMan = player.InventoryManager;
            if (invMan == null) return;

            IInventory? backpackInv = invMan.GetOwnInventory(GlobalConstants.backpackInvClassName);
            IInventory? charInv = invMan.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (backpackInv == null || charInv == null) return;

            // Find waist slot icon + occupancy
            string? waistIcon = null;
            bool waistOccupied = true;

            foreach (var slot in charInv)
            {
                if (slot is ItemSlotCharacter ch && ch.Type == EnumCharacterDressType.Waist)
                {
                    waistIcon = ch.BackgroundIcon;
                    waistOccupied = !ch.Empty;
                    break;
                }
            }

            // Apply to pouch slots (equipIndex 0,1). We identify them by scanning ItemSlotBackpack order.
            int equipIndex = 0;
            foreach (var slot in backpackInv)
            {
                if (slot is not ItemSlotBackpack) continue;

                if (equipIndex == 0 || equipIndex == 1)
                {
                    if (waistIcon != null) slot.BackgroundIcon = waistIcon;
                    slot.DrawUnavailable = !waistOccupied;
                }

                equipIndex++;
                if (equipIndex >= 4) break;
            }
        }
    }
}
