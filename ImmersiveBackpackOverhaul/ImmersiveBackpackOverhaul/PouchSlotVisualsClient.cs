#nullable enable

using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveBackpacks
{
    /// <summary>
    /// Client-only visuals:
    /// - Pouch equip slots (equipIndex 0 and 1) show the waist-slot background icon as a clear "pouch" hint.
    /// - No locking/gray-out behavior: waist gating was removed.
    ///
    /// Event-driven: no ticking.
    /// Safe against recursion: no MarkDirty(), guarded refresh, deferred execution.
    /// </summary>
    public class PouchSlotVisualsClient : ModSystem
    {
        private ICoreClientAPI capi = null!;
        private bool subscribed;

        private InventoryBase? subscribedCharInv;
        private InventoryBase? subscribedBackpackInv;

        private Action<int>? charSlotHandler;
        private Action<int>? backpackSlotHandler;

        // Guards against recursive refresh
        private bool inRefresh;

        // Coalesces bursts of SlotModified events into one Refresh next frame
        private bool refreshQueued;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            capi.Event.LevelFinalize += () =>
            {
                // In case inventories were recreated (reconnect, relog, world switch),
                // rebind event handlers safely.
                SubscribeOnce();
                QueueRefresh();
            };
        }

        public override void Dispose()
        {
            UnsubscribeSafe();
            subscribed = false;

            subscribedCharInv = null;
            subscribedBackpackInv = null;
            charSlotHandler = null;
            backpackSlotHandler = null;

            base.Dispose();
        }

        private void SubscribeOnce()
        {
            var player = capi.World.Player;
            if (player?.InventoryManager == null) return;

            var charInv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName) as InventoryBase;
            var backpackInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName) as InventoryBase;

            // If we've already subscribed but inventories changed, rebind.
            if (subscribed)
            {
                if (!ReferenceEquals(charInv, subscribedCharInv) || !ReferenceEquals(backpackInv, subscribedBackpackInv))
                {
                    UnsubscribeSafe();
                    subscribed = false;
                }
                else
                {
                    return;
                }
            }

            subscribed = true;

            subscribedCharInv = charInv;
            subscribedBackpackInv = backpackInv;

            // Store delegate refs so we can unsubscribe cleanly (prevents handler accumulation).
            charSlotHandler ??= _ => QueueRefresh();
            backpackSlotHandler ??= _ => QueueRefresh();

            if (subscribedCharInv != null)
            {
                subscribedCharInv.SlotModified += charSlotHandler;
            }

            if (subscribedBackpackInv != null)
            {
                subscribedBackpackInv.SlotModified += backpackSlotHandler;
            }
        }

        private void UnsubscribeSafe()
        {
            try
            {
                if (subscribedCharInv != null && charSlotHandler != null)
                {
                    subscribedCharInv.SlotModified -= charSlotHandler;
                }
            }
            catch { }

            try
            {
                if (subscribedBackpackInv != null && backpackSlotHandler != null)
                {
                    subscribedBackpackInv.SlotModified -= backpackSlotHandler;
                }
            }
            catch { }

            subscribedCharInv = null;
            subscribedBackpackInv = null;
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

                // Grab the waist slot background icon (used as a clear "pouch" hint),
                // but DO NOT gray out or lock anything anymore. Waist gating was removed.
                string? waistIcon = null;

                foreach (var slot in charInv)
                {
                    if (slot is ItemSlotCharacter ch && ch.Type == EnumCharacterDressType.Waist)
                    {
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
                        // Always available now
                        if (slot.DrawUnavailable)
                        {
                            slot.DrawUnavailable = false;
                        }

                        // Keep the pouch hint icon if available
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
