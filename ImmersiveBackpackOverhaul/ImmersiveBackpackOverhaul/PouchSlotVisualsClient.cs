#nullable enable

using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveBackpacks
{
    /// <summary>
    /// Client-only visuals:
    /// - Pouch equip slots (equipIndex 0 and 1) show the waist icon
    /// - Pouch equip slots gray out when waist is empty (DrawUnavailable = true)
    ///
    /// Event-driven: no ticking.
    /// Safe against recursion: no MarkDirty(), guarded refresh, deferred execution.
    /// </summary>
    public class PouchSlotVisualsClient : ModSystem
    {
        private ICoreClientAPI capi = null!;
        private bool subscribed;

        // Guards against recursive refresh
        private bool inRefresh;

        // Coalesces bursts of SlotModified events into one Refresh next frame
        private bool refreshQueued;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            capi.Event.LevelFinalize += () =>
            {
                SubscribeOnce();
                QueueRefresh();
            };
        }

        private void SubscribeOnce()
        {
            if (subscribed) return;
            subscribed = true;

            var player = capi.World.Player;
            if (player?.InventoryManager == null) return;

            if (player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName) is InventoryBase charInv)
            {
                charInv.SlotModified += _ => QueueRefresh();
            }

            if (player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName) is InventoryBase backpackInv)
            {
                backpackInv.SlotModified += _ => QueueRefresh();
            }
        }

        private void QueueRefresh()
        {
            if (refreshQueued) return;
            refreshQueued = true;

            // Defer to avoid running inside SlotModified call stack
            capi.Event.EnqueueMainThreadTask(() =>
            {
                refreshQueued = false;
                Refresh();
            }, "ibo-pouchslot-visual-refresh");
        }

        private void Refresh()
        {
            if (inRefresh) return;
            inRefresh = true;

            try
            {
                var player = capi.World.Player;
                if (player?.InventoryManager == null) return;

                IInventory? charInv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
                IInventory? backpackInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
                if (charInv == null || backpackInv == null) return;

                // Determine waist status + grab waist background icon
                bool waistOccupied = true;
                string? waistIcon = null;

                foreach (var slot in charInv)
                {
                    if (slot is ItemSlotCharacter ch && ch.Type == EnumCharacterDressType.Waist)
                    {
                        waistOccupied = !ch.Empty;
                        waistIcon = ch.BackgroundIcon;
                        break;
                    }
                }

                // Apply visuals to pouch slots: equipIndex 0 and 1 (first two ItemSlotBackpack slots)
                int equipIndex = 0;

                foreach (var slot in backpackInv)
                {
                    if (slot is not ItemSlotBackpack) continue;

                    if (equipIndex == 0 || equipIndex == 1)
                    {
                        bool wantUnavailable = !waistOccupied;

                        // Only write if changed to avoid triggering extra internal updates
                        if (slot.DrawUnavailable != wantUnavailable)
                        {
                            slot.DrawUnavailable = wantUnavailable;
                        }

                        // Only change icon if we have an icon and it differs
                        if (!string.IsNullOrEmpty(waistIcon) && slot.BackgroundIcon != waistIcon)
                        {
                            slot.BackgroundIcon = waistIcon!;
                        }
                    }

                    equipIndex++;
                    if (equipIndex >= 4) break;
                }
            }
            finally
            {
                inRefresh = false;
            }
        }
    }
}
