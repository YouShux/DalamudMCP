using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed unsafe partial class AddonTools
{
    public object Control(ToolArguments input, bool write, Func<bool> isAllowed, bool allowExtended = false)
    {
        if (!(write ? GameDiagnostics.NativeLayoutKnown || allowExtended && GameDiagnostics.CurrentCnLayoutKnown : GameDiagnostics.ReadoutLayoutKnown))
            return new { status = "unsupported_sdk" };
        var name = input.Text("addon_name");
        var query = AddonNodeQuery.Parse(input, false);
        if (query.NodeID is null && query.Label is null) throw new ArgumentException("请提供 node_id 或完整 label。");
        var offset = input.Integer("offset", 0, 0, 4096);
        var limit = input.Integer("limit", 20, 1, 100);
        var before = ResolveControl(name, query);
        if (before is null) return new { status = "target_unavailable" };
        var target = before.Value;
        var component = (AtkComponentBase*)target.Component;
        var data = ReadControl(target, offset, limit);
        if (data is null) return new { status = "unsupported_or_unreadable" };
        if (!write) return new { status = "ok", addonName = name, target.AddonID, node = target.Info, control = data };
        var allowedSearchField = target.Type == ComponentType.TextInput && name is ("ItemSearch" or "RecipeNote" or "MiragePrismPrismBox");
        var extended = !GameDiagnostics.NativeLayoutKnown || !(ActionAddons.Contains(name) || allowedSearchField);
        if (!services.ClientState.IsLoggedIn || !target.Info.Enabled || extended && !allowExtended)
            throw new InvalidOperationException("需要角色已登录、控件已启用，并允许编辑该窗口。");
        if (extended && (query.ComponentPath is not { Length: > 0 } || !VerifiedEditableType(target)))
            return new { status = "unsupported_sdk", reason = "component_identity_unverified", requestDispatched = false };
        if (input.Element("expected_addon_id").GetUInt32() != target.AddonID)
            return new { status = "target_changed", requestDispatched = false };
        if (extended)
        {
            var refreshed = ResolveControl(name, query);
            if (refreshed is not { } current || current.Addon != target.Addon || current.AddonID != target.AddonID ||
                current.Component != target.Component || current.Node != target.Node || !VerifiedEditableType(current))
                return new { status = "target_changed", requestDispatched = false };
        }
        var desired = input.Element("value");
        var expected = input.Element("expected_value");
        var rowLabel = input.Has("row_label") ? PlainText(input.Element("row_label")) : null;
        if (rowLabel is not null && target.Type is not (ComponentType.DropDownList or ComponentType.List or ComponentType.TreeList))
            throw new ArgumentException("只有下拉框和列表接受 row_label。");
        if (!Matches(data, expected)) return new { status = "value_changed", requestDispatched = false, before = data };

        switch (target.Type)
        {
            case ComponentType.TextInput:
                var text = PlainText(desired);
                if (data.Text == text) return AlreadyMatched();
                var field = (AtkComponentTextInput*)component;
                if (!field->Enabled || AtkComponentTextInput.MemberFunctionPointers.SetText == null)
                    throw new InvalidOperationException("文本控件或当前 SDK 的设置入口不可用。");
                DemandAccess();
                field->SetText(text);
                break;
            case ComponentType.NumericInput:
                var number = desired.GetInt32();
                if (data.Number == number) return AlreadyMatched();
                if (number < data.Minimum || number > data.Maximum)
                    throw new ArgumentOutOfRangeException("value", $"数值必须在 {data.Minimum}–{data.Maximum} 之间。");
                if (AtkComponentNumericInput.MemberFunctionPointers.SetValue == null)
                    throw new InvalidOperationException("当前 SDK 的数字设置入口不可用。");
                DemandAccess();
                ((AtkComponentNumericInput*)component)->SetValue(number);
                break;
            case ComponentType.CheckBox:
                if (desired.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("复选框的 value 必须为布尔值。");
                var checkedValue = desired.GetBoolean();
                if (data.Checked == checkedValue) return AlreadyMatched();
                var checkbox = (AtkComponentCheckBox*)component;
                if ((AtkResNode*)checkbox->OwnerNode != (AtkResNode*)target.Node ||
                    AtkComponentButton.MemberFunctionPointers.SetChecked == null)
                    throw new InvalidOperationException("复选框归属或当前 SDK 的设置入口无法确认。");
                DemandAccess();
                checkbox->SetChecked(checkedValue);
                break;
            case ComponentType.DropDownList:
            case ComponentType.List:
            case ComponentType.TreeList:
                var index = desired.GetInt32();
                if (target.Type == ComponentType.DropDownList && (data.IsOpen != true || data.OpenPending == true))
                    throw new InvalidOperationException("请先打开下拉框，等状态稳定后再选项。");
                var list = ListFor(target);
                if (list == null || index < 0 || index >= list->ListLength || rowLabel is null ||
                    !string.Equals(RowLabel(list, index), rowLabel, StringComparison.Ordinal) ||
                    list->VirtualTable == null || list->VirtualTable->GetItemDisabledState == null ||
                    list->GetItemDisabledState(index))
                    throw new InvalidOperationException("目标行的索引、完整标签或可用状态不匹配。");
                if (extended && !StableChoiceRow(list, index, rowLabel))
                    return new { status = "unavailable", reason = "row_identity_unverified", requestDispatched = false };
                if (data.SelectedIndex == index) return AlreadyMatched();
                if (target.Type == ComponentType.DropDownList)
                {
                    if (AtkComponentDropDownList.MemberFunctionPointers.SelectItem == null)
                        throw new InvalidOperationException("当前 SDK 的下拉设置入口不可用。");
                    DemandAccess();
                    ((AtkComponentDropDownList*)component)->SelectItem(index);
                }
                else
                {
                    if (list->VirtualTable->SelectItem == null)
                        throw new InvalidOperationException("当前 SDK 的列表设置入口不可用。");
                    DemandAccess();
                    list->SelectItem(index, false);
                }
                break;
            default: throw new ArgumentException("此控件只支持读取。");
        }

        var after = ResolveControl(name, query);
        if (after is not { } actual || actual.Addon != target.Addon || actual.AddonID != target.AddonID ||
            actual.Node != target.Node || actual.Component != target.Component || actual.Type != target.Type)
            return new { status = "target_changed", requestDispatched = true, resultObserved = false, before = data };
        var updated = ReadControl(actual, offset, limit);
        var verified = updated is not null && Matches(updated, desired) && (rowLabel is null ||
            (ListFor(actual) != null && RowLabel(ListFor(actual), desired.GetInt32()) == rowLabel &&
                (!extended || StableChoiceRow(ListFor(actual), desired.GetInt32(), rowLabel))));
        return new { status = verified ? "observed" : "not_observed", requestDispatched = true, resultObserved = verified,
            before = data, after = updated };

        object AlreadyMatched() => new { status = "already_matched", requestDispatched = false, resultObserved = true, before = data };
        void DemandAccess()
        {
            if (!isAllowed()) throw new InvalidOperationException("窗口编辑权限已关闭。");
        }
    }

    internal bool MatchInputReadback(string addonName, uint addonID, AddonNodeQuery query, JsonElement expected) =>
        GameDiagnostics.ReadoutLayoutKnown && ResolveControl(addonName, query) is { } target && target.AddonID == addonID &&
        ReadControl(target, 0, 0) is { } data && Matches(data, expected);

    private static bool VerifiedEditableType(ControlTarget target)
    {
        var component = (AtkComponentBase*)target.Component;
        if (component->VirtualTable == null || component->OwnerNode != (AtkComponentNode*)target.Node) return false;
        var actual = (nint)component->VirtualTable;
        return target.Type switch
        {
            ComponentType.TextInput => actual == (nint)AtkComponentTextInput.StaticVirtualTablePointer,
            ComponentType.CheckBox => actual == (nint)AtkComponentCheckBox.StaticVirtualTablePointer,
            ComponentType.DropDownList => actual == (nint)AtkComponentDropDownList.StaticVirtualTablePointer,
            ComponentType.NumericInput or ComponentType.List => AtkComponentBase.MemberFunctionPointers.GetComponentType != null &&
                component->GetComponentType() == target.Type,
            _ => false,
        };
    }

    private ControlTarget? ResolveControl(string name, AddonNodeQuery query)
    {
        var wrapper = services.GameGui.GetAddonByName(name, 1);
        if (wrapper.IsNull || !wrapper.IsVisible || !wrapper.IsReady) return null;
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
        if (addon == null || addon->NameString != name) return null;
        var scan = AddonNodeScanner.Scan(addon, query, true, true, 1);
        if (scan.Incomplete || scan.ScopeStatus == "ambiguous" || scan.MatchCount != 1) return null;
        var match = scan.Matches[0];
        var component = AddonNodeScanner.GetComponent((AtkResNode*)match.Address);
        var type = AddonNodeScanner.GetComponentType(component);
        return type is ComponentType actual
            ? new((nint)addon, addon->Id, match.Address, (nint)component, actual, match.Info) : null;
    }

    private static ControlValue? ReadControl(ControlTarget target, int offset, int limit)
    {
        var component = (AtkComponentBase*)target.Component;
        switch (target.Type)
        {
            case ComponentType.TextInput:
                var input = (AtkComponentTextInput*)component;
                if (input->ContainingAddon2 != (AtkUnitBase*)target.Addon ||
                    (input->ComponentTextData.Flags1 & TextInputFlags1.Mask) != 0 ||
                    input->EvaluatedString.BufUsed is < 1 or > 4097 || !input->EvaluatedString.StringPtr.HasValue) return null;
                return new(Text: input->EvaluatedString.ToString());
            case ComponentType.NumericInput:
                var number = (AtkComponentNumericInput*)component;
                return number->Data.Min <= number->Data.Max
                    ? new(Number: number->Value, Minimum: number->Data.Min, Maximum: number->Data.Max) : null;
            case ComponentType.CheckBox:
                return new(Checked: ((AtkComponentCheckBox*)component)->IsChecked);
            case ComponentType.DropDownList:
            case ComponentType.List:
            case ComponentType.TreeList:
                var list = ListFor(target);
                if (list == null || list->ListLength is < 0 or > 4096 || list->SelectedItemIndex < -1 ||
                    list->SelectedItemIndex >= list->ListLength) return null;
                var end = Math.Min(list->ListLength, offset + limit);
                var rows = new List<ControlRow>();
                for (var index = offset; index < end; index++)
                    rows.Add(new(index, RowLabel(list, index)));
                var dropdown = target.Type == ComponentType.DropDownList ? (AtkComponentDropDownList*)component : null;
                return new(SelectedIndex: list->SelectedItemIndex, IsOpen: dropdown == null ? null : dropdown->IsOpen,
                    OpenPending: dropdown == null ? null : dropdown->OpenStateChangePending, Count: list->ListLength,
                    Rows: [.. rows], NextOffset: end < list->ListLength ? end : null);
            default: return null;
        }
    }

    private static AtkComponentList* ListFor(ControlTarget target)
    {
        if (target.Type is ComponentType.List or ComponentType.TreeList) return (AtkComponentList*)target.Component;
        if (target.Type != ComponentType.DropDownList) return null;
        var list = ((AtkComponentDropDownList*)target.Component)->List;
        return list != null && list->OwnerNode != null && list->OwnerNode->Component == (AtkComponentBase*)list ? list : null;
    }

    private static string? RowLabel(AtkComponentList* list, int index)
    {
        if (index < 0 || index >= list->ListLength) return null;
        var bytes = list->ItemLabels != null ? (byte*)list->ItemLabels[index] : null;
        if (bytes == null && AtkComponentList.MemberFunctionPointers.GetItemLabel != null)
            bytes = (byte*)list->GetItemLabel(index);
        if (bytes == null) return null;
        var length = 0;
        while (length < 1024 && bytes[length] != 0) length++;
        return length < 1024 ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes, length)) : null;
    }

    internal static string PlainText(JsonElement value)
    {
        var text = value.GetString() ?? throw new ArgumentException("文本不能为 null。");
        return text.Length <= 512 && Encoding.UTF8.GetByteCount(text) <= 1024 && !text.Any(char.IsControl)
            ? text : throw new ArgumentException("仅支持不含控制字符、最多 512 字符和 1024 UTF-8 字节的纯文本。");
    }

    private static bool Matches(ControlValue value, JsonElement token) =>
        value.Text is { } text ? token.ValueKind == JsonValueKind.String && token.GetString() == text :
        value.Checked is bool state ? token.ValueKind is JsonValueKind.True or JsonValueKind.False && token.GetBoolean() == state :
        (value.Number ?? value.SelectedIndex) is int number &&
        token.ValueKind == JsonValueKind.Number && token.TryGetInt32(out var actual) && number == actual;

    private readonly record struct ControlTarget(nint Addon, uint AddonID, nint Node, nint Component, ComponentType Type,
        AddonNodeScanner.NodeInfo Info);
    private sealed record ControlRow(int Index, string? Label);
    private sealed record ControlValue(string? Text = null, int? Number = null, int? Minimum = null, int? Maximum = null,
        bool? Checked = null, int? SelectedIndex = null, bool? IsOpen = null, bool? OpenPending = null, int? Count = null,
        ControlRow[]? Rows = null, int? NextOffset = null);
}
