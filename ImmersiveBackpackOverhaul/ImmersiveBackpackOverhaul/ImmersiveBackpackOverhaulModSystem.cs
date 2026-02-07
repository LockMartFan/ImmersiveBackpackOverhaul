#nullable enable

using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Config;

namespace ImmersiveBackpacks
{
    public class BackpackSlotBouncerSystem : ModSystem
    {
        private ICoreServerAPI sapi = null!;

        // Final desired equip tiers: [Pouch, Pouch, Satchel, Backpack]
        private readonly int[] requiredTierByEquipIndex = { 1, 1, 2, 3 };

        private readonly Dictionary<string, List<ItemSlot>> equipSlotsByPlayerUid = new();
        private readonly HashSet<string> enforcingPlayerUids = new();
        private readonly HashSet<string> subscribedBackpackInvUids = new();
        private readonly HashSet<string> subscribedCharacterInvUids = new();

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            equipSlotsByPlayerUid.Remove(player.PlayerUID);
            enforcingPlayerUids.Remove(player.PlayerUID);

            subscribedBackpackInvUids.Remove(player.PlayerUID);
            subscribedCharacterInvUids.Remove(player.PlayerUID);

            // Unregister Harmony map entry for this player's backpack inventory (if present)
            IInventory? backpackInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpackInv is InventoryBase invBase)
            {
                BagTierEquipSlotRegistry.UnregisterInventory(invBase.InventoryID);
            }
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            IInventory? backpackInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpackInv is not InventoryBase backpackInvBase)
            {
                sapi.Logger.Warning("[ImmersiveBackpacks] Backpack inventory missing or not InventoryBase; cannot enforce bag slots.");
                return;
            }

            // Register equip slot ids for Harmony gate (server authoritative)
            BagTierEquipSlotRegistry.RegisterBackpackEquipSlots(backpackInvBase, requiredTierByEquipIndex);

            // Cache equip slots (object refs) for bouncer safety net
            equipSlotsByPlayerUid[player.PlayerUID] = FindEquipSlots(backpackInvBase);

            // Subscribe once to backpack inventory slot changes
            if (subscribedBackpackInvUids.Add(player.PlayerUID))
            {
                backpackInvBase.SlotModified += slotId =>
                {
                    if (enforcingPlayerUids.Contains(player.PlayerUID)) return;

                    ItemSlot changedSlot = backpackInvBase[slotId];
                    if (changedSlot == null) return;

                    EnforceIfEquipSlot(player, changedSlot);
                };
            }

            // Subscribe once to character inventory slot changes (waist gating cleanup)
            IInventory? charInv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (charInv is InventoryBase charInvBase)
            {
                if (subscribedCharacterInvUids.Add(player.PlayerUID))
                {
                    charInvBase.SlotModified += _ =>
                    {
                        if (enforcingPlayerUids.Contains(player.PlayerUID)) return;

                        // If waist is empty now, force-eject pouch slots once (0 and 1)
                        if (!TierUtil.HasWaistEquipped(player))
                        {
                            EjectPouchSlotsIfAny(player);
                        }
                    };
                }
            }

            // Clean invalid pre-existing states once (e.g., from older saves/mod changes)
            EnforceAllEquipSlots(player);

            // Also apply waist gating cleanup once on join (in case save already has pouches equipped)
            if (!TierUtil.HasWaistEquipped(player))
            {
                EjectPouchSlotsIfAny(player);
            }
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

        private void EnforceAllEquipSlots(IServerPlayer player)
        {
            if (!equipSlotsByPlayerUid.TryGetValue(player.PlayerUID, out var equipSlots)) return;

            for (int equipIndex = 0; equipIndex < equipSlots.Count; equipIndex++)
            {
                EnforceOneEquipSlot(player, equipSlots[equipIndex], equipIndex);
            }
        }

        private void EnforceIfEquipSlot(IServerPlayer player, ItemSlot changedSlot)
        {
            if (!equipSlotsByPlayerUid.TryGetValue(player.PlayerUID, out var equipSlots)) return;

            for (int equipIndex = 0; equipIndex < equipSlots.Count; equipIndex++)
            {
                if (ReferenceEquals(equipSlots[equipIndex], changedSlot))
                {
                    EnforceOneEquipSlot(player, changedSlot, equipIndex);
                    return;
                }
            }
        }

        private void EnforceOneEquipSlot(IServerPlayer player, ItemSlot equipSlot, int equipIndex)
        {
            if (equipSlot.Empty) return;

            if (equipIndex < 0 || equipIndex >= requiredTierByEquipIndex.Length)
            {
                Eject(player, equipSlot, $"Equip index {equipIndex} has no rule.");
                return;
            }

            int requiredTier = requiredTierByEquipIndex[equipIndex];

            ItemStack? stack = equipSlot.Itemstack;
            int actualTier = TierUtil.GetTierStrictOrZero(stack);

            // Strict: missing tag => invalid (forces you to patch)
            if (actualTier == 0)
            {
                Eject(player, equipSlot, "Missing attributes.iboBagTier tag (compat patch required).");
                return;
            }

            if (actualTier != requiredTier)
            {
                Eject(player, equipSlot, $"Tier {actualTier} not allowed here (requires Tier {requiredTier}).");
            }
        }

        private void EjectPouchSlotsIfAny(IServerPlayer player)
        {
            if (!equipSlotsByPlayerUid.TryGetValue(player.PlayerUID, out var equipSlots)) return;
            if (equipSlots.Count < 2) return;

            // Only slots 0 and 1 are pouch slots
            for (int equipIndex = 0; equipIndex <= 1; equipIndex++)
            {
                ItemSlot slot = equipSlots[equipIndex];
                if (!slot.Empty)
                {
                    Eject(player, slot, "Pouch slots require waist slot occupied.");
                }
            }
        }

        private void Eject(IServerPlayer player, ItemSlot equipSlot, string reason)
        {
            enforcingPlayerUids.Add(player.PlayerUID);

            try
            {
                ItemStack? badStack = equipSlot.Itemstack?.Clone();
                string code = badStack?.Collectible?.Code?.ToString() ?? "<unknown>";

                equipSlot.Itemstack = null;
                equipSlot.MarkDirty();

                if (badStack != null)
                {
                    // Safety net return: avoid re-equip into ItemSlotBackpack
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
                enforcingPlayerUids.Remove(player.PlayerUID);
            }
        }

        private bool TryGiveToNonEquipPlayerSlots(IServerPlayer player, ItemStack stack)
        {
            var invMan = player.InventoryManager;

            IInventory? hotbar = invMan.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            TryPutIntoInventorySkippingEquip(hotbar, stack);
            if (stack.StackSize <= 0) return true;

            IInventory? character = invMan.GetOwnInventory(GlobalConstants.characterInvClassName);
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
                if (target == null) continue;

                // Never re-equip into backpack equip slots
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
