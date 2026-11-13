using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameReadouts
{
    internal const string APPLY_GEARSET_TOOL = "apply_saved_loadout";

    internal unsafe int[] FindGearsetSlots(string name)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return [];
        var slots = new List<int>();
        for (var slot = 0; slot < module->Entries.Length; slot++)
        {
            ref var entry = ref module->Entries[slot];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) != 0 && entry.NameString.Equals(name, StringComparison.OrdinalIgnoreCase))
                slots.Add(slot);
        }
        return [.. slots];
    }

    internal unsafe GearsetState? GearsetStateForApply(int slot)
    {
        if (!GameDiagnostics.CurrentCnLayoutKnown || !services.ClientState.IsLoggedIn || !services.PlayerState.IsLoaded ||
            services.PlayerState.ContentId == 0 || services.ObjectTable.LocalPlayer is not { IsDead: false } player ||
            services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51)) return null;
        var module = RaptureGearsetModule.Instance();
        if (module == null || slot < 0 || slot >= module->Entries.Length) return null;
        ref var entry = ref module->Entries[slot];
        if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0 || entry.ClassJob == 0) return null;
        var saved = new uint[entry.Items.Length];
        for (var i = 0; i < saved.Length; i++) saved[i] = entry.Items[i].ItemId;
        var manager = InventoryManager.Addresses.Instance.Value == 0 ? null : InventoryManager.Instance();
        var container = manager == null || InventoryManager.MemberFunctionPointers.GetInventoryContainer == null
            ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);
        var items = services.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems);
        bool? matches = null;
        if (container != null && container->IsLoaded && items.Length == container->Size && items.Length >= saved.Length)
        {
            matches = true;
            foreach (var item in items)
            {
                var index = item.InventorySlot;
                if (index >= saved.Length || index == (uint)RaptureGearsetModule.GearsetItemIndex.Belt) continue;
                if ((item.IsEmpty ? 0u : item.ItemId) != saved[index]) { matches = false; break; }
            }
        }
        return new(services.PlayerState.ContentId, services.ClientState.TerritoryType, module->CurrentGearsetIndex,
            player.ClassJob.RowId, entry.ClassJob, entry.NameString, saved, matches,
            (entry.Flags & RaptureGearsetModule.GearsetFlag.MainHandMissing) != 0);
    }

    internal unsafe int? EquipGearset(int slot)
    {
        var module = RaptureGearsetModule.Instance();
        return module == null || RaptureGearsetModule.MemberFunctionPointers.EquipGearset == null ? null : module->EquipGearset(slot);
    }

    internal sealed record GearsetState(ulong Character, uint TerritoryID, int ActiveSlot, uint JobID,
        uint SavedJobID, string Name, uint[] Items, bool? EquipmentMatches, bool MainHandMissing);
}
