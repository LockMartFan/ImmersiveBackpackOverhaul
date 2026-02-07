#nullable enable

using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Config;

namespace ImmersiveBackpacks
{
    /// <summary>
    /// Server-authoritative "bouncer".
    /// If anything invalid ends up in the 4 backpack equip slots, it is removed and safely returned.
    /// </summary>
    public class BackpackSlotBouncerSystem : ModSystem
    {
        private ICoreServerAPI sapi = null!;

        // Policy: [Pouch, Pouch, Satchel, Backpack]
        private static readonly int[] RequiredTierByEquipIndex = { 1, 1, 2, 3 };

        private readonly Dictionary<string, List<ItemSlot>> equipSlotsByPlayerUid = new();
        private readonly HashSet<string> enforcingPlayerUids = new();
        private readonly HashSet<string> subscribedPlayerUids = new();

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
            subscribedPlayerUids.Remove(player.PlayerUID);
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            IInventory? backpackInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpackInv is not InventoryBase invBase)
            {
                sapi.Logger.Warning("[ImmersiveBackpacks] Backpack inventory missing or not InventoryBase; cannot enforce.");
                return;
            }

            equipSlotsByPlayerUid[player.PlayerUID] = FindEquipSlots(invBase);

            if (subscribedPlayerUids.Add(player.PlayerUID))
            {
                invBase.SlotModified += slotId =>
                {
                    if (enforcingPlayerUids.Contains(player.PlayerUID)) return;
                    if (slotId < 0 || slotId >= invBase.Count) return;

                    ItemSlot changedSlot = invBase[slotId];
                    EnforceIfEquipSlot(player, changedSlot);
                };
            }

            EnforceAllEquipSlots(player);
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
                sapi.Logger.Warning($"[ImmersiveBackpacks] Expected 4 backpack equip slots, found {list.Count}.");
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

            if (equipIndex < 0 || equipIndex >= RequiredTierByEquipIndex.Length)
            {
                Eject(player, equipSlot, $"Equip index {equipIndex} has no rule.");
                return;
            }

            int requiredTier = RequiredTierByEquipIndex[equipIndex];

            int actualTier = TierUtil.GetTierStrictOrZero(equipSlot.Itemstack);

            // STRICT: missing/invalid tag => invalid
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
            enforcingPlayerUids.Add(player.PlayerUID);

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
                enforcingPlayerUids.Remove(player.PlayerUID);
            }
        }

        private bool TryGiveToNonEquipPlayerSlots(IServerPlayer player, ItemStack stack)
        {
            var invMan = player.InventoryManager;

            IInventory? hotbar = invMan.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            TryPutIntoInventory(hotbar, stack);
            if (stack.StackSize <= 0) return true;

            IInventory? character = invMan.GetOwnInventory(GlobalConstants.characterInvClassName);
            TryPutIntoInventory(character, stack);
            if (stack.StackSize <= 0) return true;

            return false;
        }

        private void TryPutIntoInventory(IInventory? inv, ItemStack stack)
        {
            if (inv == null) return;
            if (stack.StackSize <= 0) return;

            var srcInv = new InventoryGeneric(1, "ibo-src", sapi);
            ItemSlot src = srcInv[0];
            src.Itemstack = stack;

            for (int i = 0; i < inv.Count && src.Itemstack != null && src.Itemstack.StackSize > 0; i++)
            {
                ItemSlot target = inv[i];

                // Never allow re-equip into backpack equip slots
                if (target is ItemSlotBackpack) continue;

                if (!target.CanHold(src)) continue;

                src.TryPutInto(sapi.World, target, src.StackSize);
            }

            stack.StackSize = src.Itemstack?.StackSize ?? 0;
        }
    }

    /// <summary>
    /// One-and-only TierUtil. Do NOT duplicate this in other files.
    /// </summary>
    public static class TierUtil
    {
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
