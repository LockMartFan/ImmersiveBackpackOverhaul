#nullable enable

using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace ImmersiveBackpacks
{
    /// <summary>
    /// SERVER AUTHORITATIVE "BOUNCER"
    ///
    /// Goal:
    ///  - Stage 1 enforcement only: the 4 backpack equip slots must be [T1, T1, T2, T3]
    ///  - If anything invalid ends up there (old saves, admin, glitches), eject it safely.
    ///
    /// Performance:
    ///  - No ticking. Uses InventoryBase.SlotModified.
    /// </summary>
    public sealed class BackpackSlotBouncerSystem : ModSystem
    {
        private ICoreServerAPI sapi = null!;

        // Equip plan: [Pouch, Pouch, Satchel, Backpack]
        // Tier plan:  [  T1 ,  T1 ,   T2  ,   T3   ]
        private static readonly int[] RequiredTierByEquipIndex = { 1, 1, 2, 3 };

        // PlayerUID -> equip slots (the 4 ItemSlotBackpack slots in that player's backpack inventory)
        private readonly Dictionary<string, List<ItemSlot>> equipSlotsByPlayerUid = new();

        // Prevent recursion (our own modifications cause SlotModified too)
        private readonly HashSet<string> enforcing = new();

        // Prevent double subscription
        private readonly HashSet<string> subscribed = new();

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            equipSlotsByPlayerUid.Remove(player.PlayerUID);
            enforcing.Remove(player.PlayerUID);
            subscribed.Remove(player.PlayerUID);

            // Clean registry entry for this player's backpack inventory
            var backpackInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpackInv is InventoryBase invBase)
            {
                BagTierRegistry.UnregisterInventory(invBase.InventoryID);
            }
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            var backpackInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpackInv is not InventoryBase invBase) return;

            // Register tier requirements for the Harmony gate (slotId -> tier)
            BagTierRegistry.RegisterBackpackEquipSlots(invBase, RequiredTierByEquipIndex);

            // Cache equip slots
            equipSlotsByPlayerUid[player.PlayerUID] = FindEquipSlots(invBase);

            // Subscribe once to modifications in this inventory
            if (subscribed.Add(player.PlayerUID))
            {
                invBase.SlotModified += slotId =>
                {
                    if (enforcing.Contains(player.PlayerUID)) return;
                    if (slotId < 0 || slotId >= invBase.Count) return;

                    var changedSlot = invBase[slotId];
                    EnforceIfEquipSlot(player, changedSlot);
                };
            }

            // One-time cleanup
            EnforceAll(player);
        }

        private List<ItemSlot> FindEquipSlots(InventoryBase backpackInv)
        {
            var list = new List<ItemSlot>(4);

            foreach (var slot in backpackInv)
            {
                if (slot is ItemSlotBackpack) list.Add(slot);
            }

            if (list.Count != 4)
            {
                sapi.Logger.Warning($"[ImmersiveBackpacks] Expected 4 ItemSlotBackpack equip slots, found {list.Count}.");
            }

            return list;
        }

        private void EnforceAll(IServerPlayer player)
        {
            if (!equipSlotsByPlayerUid.TryGetValue(player.PlayerUID, out var equipSlots)) return;

            for (int equipIndex = 0; equipIndex < equipSlots.Count; equipIndex++)
            {
                EnforceOne(player, equipSlots[equipIndex], equipIndex);
            }
        }

        private void EnforceIfEquipSlot(IServerPlayer player, ItemSlot changedSlot)
        {
            if (!equipSlotsByPlayerUid.TryGetValue(player.PlayerUID, out var equipSlots)) return;

            for (int equipIndex = 0; equipIndex < equipSlots.Count; equipIndex++)
            {
                if (ReferenceEquals(equipSlots[equipIndex], changedSlot))
                {
                    EnforceOne(player, changedSlot, equipIndex);
                    return;
                }
            }
        }

        private void EnforceOne(IServerPlayer player, ItemSlot equipSlot, int equipIndex)
        {
            if (equipSlot.Empty) return;

            if (equipIndex < 0 || equipIndex >= RequiredTierByEquipIndex.Length)
            {
                Eject(player, equipSlot, $"Equip index {equipIndex} has no rule.");
                return;
            }

            int requiredTier = RequiredTierByEquipIndex[equipIndex];

            ItemStack? stack = equipSlot.Itemstack;
            int actualTier = TierUtil.GetTierStrictOrZero(stack);

            // STRICT: no tier tag => invalid
            if (actualTier == 0)
            {
                Eject(player, equipSlot, "Missing/invalid attributes.iboBagTier tag (compat patch required).");
                return;
            }

            if (actualTier != requiredTier)
            {
                Eject(player, equipSlot, $"Tier {actualTier} not allowed here (requires Tier {requiredTier}).");
            }
        }

        private void Eject(IServerPlayer player, ItemSlot equipSlot, string reason)
        {
            enforcing.Add(player.PlayerUID);

            try
            {
                ItemStack? badStack = equipSlot.Itemstack?.Clone();
                string code = badStack?.Collectible?.Code?.ToString() ?? "<unknown>";

                equipSlot.Itemstack = null;
                equipSlot.MarkDirty();

                if (badStack != null)
                {
                    bool placed = TryGiveToNonEquipPlayerSlots(player, badStack);
                    if (!placed)
                    {
                        sapi.World.SpawnItemEntity(badStack, player.Entity.Pos.XYZ);
                    }
                }

                sapi.Logger.Warning($"[ImmersiveBackpacks] Ejected '{code}' from bag equip slot. Reason: {reason}");
            }
            finally
            {
                enforcing.Remove(player.PlayerUID);
            }
        }

        /// <summary>
        /// Put into normal player slots only (avoid backpack equip slots).
        /// </summary>
        private bool TryGiveToNonEquipPlayerSlots(IServerPlayer player, ItemStack stack)
        {
            var invMan = player.InventoryManager;

            var hotbar = invMan.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            TryPutIntoInventorySkippingEquip(hotbar, stack);
            if (stack.StackSize <= 0) return true;

            var character = invMan.GetOwnInventory(GlobalConstants.characterInvClassName);
            TryPutIntoInventorySkippingEquip(character, stack);
            if (stack.StackSize <= 0) return true;

            return false;
        }

        private void TryPutIntoInventorySkippingEquip(IInventory? inv, ItemStack stack)
        {
            if (inv == null) return;
            if (stack.StackSize <= 0) return;

            var src = new DummySourceSlot(stack);

            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot target = inv[i];
                if (target is ItemSlotBackpack) continue;
                if (!target.CanHold(src)) continue;

                int moved = src.TryPutInto(sapi.World, target, src.StackSize);
                if (moved > 0 && src.StackSize <= 0) return;
            }
        }

        private sealed class DummySourceSlot : ItemSlot
        {
            public DummySourceSlot(ItemStack stack) : base(null!)
            {
                Itemstack = stack;
            }
        }
    }
}
