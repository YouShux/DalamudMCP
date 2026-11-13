using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    private unsafe void CacheRetainer(ulong owner)
    {
        var inventory = InventoryForRead();
        if (inventory == null || InventoryManager.MemberFunctionPointers.GetInventoryContainer == null ||
            !retainerCache.Keys.Any(key => key.Owner == owner) && retainerCache.Keys.Select(key => key.Owner).Distinct().Count() >= 10)
            return;
        var now = DateTimeOffset.UtcNow;
        var id = owner.ToString("X");
        foreach (var (type, scope) in Containers)
        {
            if (scope != "retainer") continue;
            var container = inventory->GetInventoryContainer((InventoryType)(uint)type);
            if (container == null || !container->IsLoaded || container->Size is < 1 or > 1024) continue;
            var slots = services.GameInventory.GetInventoryItems(type);
            if (slots.Length != container->Size) continue;
            var items = new List<Holding>();
            foreach (var slot in slots)
                if (!slot.IsEmpty && slot.Quantity > 0)
                    items.Add(new(type.ToString(), scope, slot.InventorySlot, slot.Quantity,
                        slot.IsCollectable ? "collectible" : slot.IsHq ? "high_quality" : "normal", id, slot.BaseItemId, null));
            retainerCache[(owner, type)] = new(now, [.. items]);
        }
    }

    private unsafe object ReadRetainerCoverage(ulong owner, bool includeCached)
    {
        var manager = RetainerManager.Addresses.Instance.Value != 0 ? RetainerManager.Instance() : null;
        if (manager == null || !manager->IsReady || RetainerManager.MemberFunctionPointers.GetRetainerCount == null ||
            RetainerManager.MemberFunctionPointers.GetRetainerBySortedIndex == null) return new { available = false };
        var count = Math.Min(10, (int)manager->GetRetainerCount());
        var retainers = new List<object>(count);
        for (uint i = 0; i < count; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0) continue;
            var id = retainer->RetainerId;
            var cachedContainers = includeCached && id != owner ? retainerCache.Keys.Count(key => key.Owner == id) : 0;
            retainers.Add(new { retainerID = id.ToString("X"), liveOwner = id == owner, cachedContainers,
                source = id == owner ? "live_owner" : cachedContainers != 0 ? "cached" : "unavailable" });
        }
        return new { available = retainers.Count == count, retainers };
    }

    private sealed record RetainerSnapshot(DateTimeOffset ObservedAt, Holding[] Items);
}
