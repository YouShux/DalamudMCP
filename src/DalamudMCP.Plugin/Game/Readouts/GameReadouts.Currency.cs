using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using NativePlayer = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameReadouts
{
    internal const string CURRENCY_TOOL = "inspect_currency_balances";
    private (long Until, uint[] IDs)? tomestoneIndex;

    internal unsafe object CurrencyBalances(ToolArguments input)
    {
        var offset = input.Integer("offset", 0, 0, 10000);
        var limit = input.Integer("limit", 40, 1, 100);
        var includeZero = input.Boolean("include_zero");
        var sampledAt = DateTimeOffset.UtcNow;
        var sdk = typeof(InventoryManager).Assembly;
        var supported = GameDiagnostics.CurrentCnLayoutKnown;
        var loggedIn = services.ClientState.IsLoggedIn && services.PlayerState.IsLoaded;
        var manager = supported && loggedIn && InventoryManager.Addresses.Instance.Value != 0 ? InventoryManager.Instance() : null;
        var itemSheet = services.DataManager.GetExcelSheet<Item>();
        var rows = new List<CurrencyRow>();
        var currencyLoaded = Loaded(InventoryType.Currency);
        var amountByItem = new Dictionary<uint, long>();
        if (currencyLoaded)
            foreach (var item in services.GameInventory.GetInventoryItems(GameInventoryType.Currency))
                if (!item.IsEmpty && item.BaseItemId != 0)
                    amountByItem[item.BaseItemId] = amountByItem.GetValueOrDefault(item.BaseItemId) + item.Quantity;

        Add("gil", 1, currencyLoaded ? amountByItem.GetValueOrDefault(1u) : null, "inventory_currency");
        var player = manager != null && NativePlayer.Addresses.Instance.Value != 0 ? NativePlayer.Instance() : null;
        var companyID = player != null && player->IsLoaded ? player->GrandCompany : (byte?)null;
        for (byte id = 1; id <= 3; id++)
        {
            var itemID = (uint)(19 + id);
            var amount = currencyLoaded && InventoryManager.MemberFunctionPointers.GetCompanySeals != null
                ? (long?)manager->GetCompanySeals(id) : null;
            Add("company_seals_" + id, itemID, amount, "InventoryManager.GetCompanySeals");
        }

        Add("gold_saucer_coin", 29, currencyLoaded && InventoryManager.MemberFunctionPointers.GetGoldSaucerCoin != null ?
            (long?)manager->GetGoldSaucerCoin() : null, "InventoryManager.GetGoldSaucerCoin");
        Add("wolf_marks", 25, currencyLoaded && InventoryManager.MemberFunctionPointers.GetWolfMarks != null ?
            (long?)manager->GetWolfMarks() : null, "InventoryManager.GetWolfMarks");
        Add("allied_seals", 27, currencyLoaded && InventoryManager.MemberFunctionPointers.GetAlliedSeals != null ?
            (long?)manager->GetAlliedSeals() : null, "InventoryManager.GetAlliedSeals");

        var fc = manager != null ? InfoProxyFreeCompany.Instance() : null;
        var fcAmount = fc != null && fc->Id != 0 && Loaded(InventoryType.FreeCompanyGil) &&
            InventoryManager.MemberFunctionPointers.GetFreeCompanyGil != null ? (long?)manager->GetFreeCompanyGil() : null;
        Add("free_company_gil", 1, fcAmount, "InventoryManager.GetFreeCompanyGil",
            fc != null && fc->Id == 0 ? "membership_unavailable" : "free_company_chest_unavailable");
        var retainer = manager != null && RetainerManager.Addresses.Instance.Value != 0 ? RetainerManager.Instance() : null;
        var activeRetainer = retainer != null && retainer->IsReady && RetainerManager.MemberFunctionPointers.GetActiveRetainer != null
            ? retainer->GetActiveRetainer() : null;
        var retainerAmount = activeRetainer != null && activeRetainer->RetainerId != 0 && Loaded(InventoryType.RetainerGil) &&
            InventoryManager.MemberFunctionPointers.GetRetainerGil != null ? (long?)manager->GetRetainerGil() : null;
        Add("retainer_gil", 1, retainerAmount, "InventoryManager.GetRetainerGil", "retainer_inventory_unavailable");
        if (tomestoneIndex is not { } index || index.Until <= Environment.TickCount64)
        {
            index = (Environment.TickCount64 + 60000, services.DataManager.GetExcelSheet<TomestonesItem>()
                .Where(row => row.CurrencyInventorySlot >= 0 && row.Item.RowId != 0).Select(row => row.Item.RowId).Distinct().Order().ToArray());
            tomestoneIndex = index;
        }

        var limitedItem = currencyLoaded && InventoryManager.MemberFunctionPointers.GetSpecialItemId != null
            ? InventoryManager.MemberFunctionPointers.GetSpecialItemId(9) : 0;
        foreach (var id in index.IDs)
        {
            var amount = currencyLoaded && InventoryManager.MemberFunctionPointers.GetTomestoneCount != null
                ? (long?)manager->GetTomestoneCount(id) : null;
            var limited = limitedItem > 0 && id == limitedItem;
            var earned = limited && InventoryManager.MemberFunctionPointers.GetLimitedTomestoneCount != null
                ? manager->GetWeeklyAcquiredTomestoneCount() : (int?)null;
            var cap = limited && InventoryManager.MemberFunctionPointers.GetLimitedTomestoneWeeklyLimit != null
                ? InventoryManager.GetLimitedTomestoneWeeklyLimit() : (int?)null;
            Add("tomestone_" + id, id, amount, "InventoryManager.GetTomestoneCount",
                weeklyEarned: earned >= 0 ? earned : null, weeklyLimit: cap > 0 ? cap : null,
                weeklyAvailability: limitedItem <= 0 ? "unknown" : !limited ? "not_applicable" :
                    earned >= 0 && cap > 0 ? "available" : "unknown",
                weeklyReason: limitedItem <= 0 ? "limited_item_unavailable" :
                    limited && !(earned >= 0 && cap > 0) ? "weekly_counter_or_cap_unavailable" : null);
        }

        foreach (var (id, amount) in amountByItem.OrderBy(row => row.Key))
            if (id != 1 && id is not (20 or 21 or 22 or 25 or 27 or 29) && !index.IDs.Contains(id))
                Add("item_" + id, id, amount, "inventory_currency");

        var entries = rows.Where(row => includeZero || row.Amount is not 0 || row.WeeklyEarned > 0 || row.Availability != "available").ToArray();
        var page = entries.Skip(offset).Take(limit).ToArray();
        return new
        {
            status = !loggedIn ? "unavailable" : !supported ? "unsupported_sdk" : rows.Any(row => row.Availability != "available") ? "partial" : "ok",
            sampledAt, clientLanguage = services.ClientState.ClientLanguage.ToString(),
            territoryID = services.ClientState.TerritoryType, sdkMvid = sdk.ManifestModule.ModuleVersionId,
            sdkVersion = sdk.GetName().Version?.ToString(), clientLayout = supported ? "current_cn" : "unknown",
            grandCompanyID = companyID, entries = page, total = entries.Length, currencyContainerAvailable = currencyLoaded,
            nextOffset = page.Length > 0 && offset + page.Length < entries.Length ? (int?)offset + page.Length : null,
        };

        bool Loaded(InventoryType type)
        {
            if (manager == null || InventoryManager.MemberFunctionPointers.GetInventoryContainer == null) return false;
            var container = manager->GetInventoryContainer(type);
            return container != null && container->IsLoaded;
        }

        void Add(string key, uint id, long? amount, string source, string? reason = null,
            int? weeklyEarned = null, int? weeklyLimit = null, string? weeklyAvailability = null, string? weeklyReason = null)
        {
            var name = itemSheet.TryGetRow(id, out var item) ? item.Name.ToString() : null;
            rows.Add(new(key, id, name, amount, weeklyEarned, weeklyLimit, weeklyAvailability, weeklyReason,
                amount.HasValue ? "available" : "unknown",
                amount.HasValue ? null : !loggedIn ? "not_logged_in" : !supported ? "unsupported_sdk" :
                    reason ?? (!currencyLoaded ? "currency_container_unavailable" : "sdk_entry_unavailable"), source));
        }
    }

    private sealed record CurrencyRow(string Key, uint ItemID, string? Name, long? Amount, int? WeeklyEarned, int? WeeklyLimit,
        string? WeeklyAvailability, string? WeeklyReason, string Availability, string? Reason, string Source);
}
