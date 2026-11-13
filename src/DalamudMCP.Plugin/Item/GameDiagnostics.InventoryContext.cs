using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string INVENTORY_CONTEXT_TOOL = "inspect_open_bag_menu";

    public unsafe object InventoryContext()
    {
        if (!CurrentCnLayoutKnown) return new { status = "unsupported_sdk" };
        if (!services.ClientState.IsLoggedIn) return new { status = "not_logged_in" };
        var window = services.GameGui.GetAddonByName("ContextMenu");
        if (window.IsNull || !window.IsVisible || !window.IsReady) return new { status = "menu_not_visible" };
        var menu = (AtkUnitBase*)window.Address;
        var context = AgentInventoryContext.Instance();
        if (context == null || context->AddonId != menu->Id || context->ContextItemCount <= 0 ||
            context->TargetInventorySlot == null || context->TargetInventorySlotId is < 0 or > 255)
            return new { status = "not_inventory_menu" };

        var type = context->TargetInventoryId;
        if (type is not (InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4))
            return new { status = "unsupported_container" };
        var ownerID = context->OwnerAddonId;
        var bagVisible = false;
        foreach (var name in AddonTools.BagWindows)
        {
            var bag = services.GameGui.GetAddonByName(name);
            if (!bag.IsNull && bag.IsVisible && bag.IsReady && ((AtkUnitBase*)bag.Address)->Id == ownerID)
            {
                bagVisible = true;
                break;
            }
        }
        if (!bagVisible) return new { status = "owner_not_visible" };

        var slot = context->TargetInventorySlotId;
        var manager = InventoryForRead();
        if (manager == null || InventoryManager.MemberFunctionPointers.GetInventorySlot == null ||
            InventoryManager.MemberFunctionPointers.GetInventoryContainer == null)
            return new { status = "inventory_unavailable" };
        var container = manager->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded || slot >= container->Size)
            return new { status = "slot_not_verified" };
        var item = manager->GetInventorySlot(type, slot);
        if (item == null || item != context->TargetInventorySlot || item == &context->TargetDummyItem)
            return new { status = "slot_not_verified" };
        foreach (var entry in services.GameInventory.GetInventoryItems((GameInventoryType)type))
        {
            if (entry.InventorySlot != (uint)slot || entry.IsEmpty) continue;
            return new { status = "observed", menuID = menu->Id, ownerAddonID = ownerID, container = type.ToString(),
                slot, itemID = entry.BaseItemId, entry.Quantity, highQuality = entry.IsHq };
        }
        return new { status = "slot_not_verified" };
    }

}
