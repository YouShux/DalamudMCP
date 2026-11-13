using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class PluginIntegration
{
    internal const string ENDPOINTS_TOOL = "list_saved_plugin_endpoints";
    internal const string EDIT_ENDPOINT_TOOL = "edit_saved_plugin_endpoint";
    internal const string CALL_ENDPOINT_TOOL = "call_saved_plugin_endpoint";
    private const int MAX_ENDPOINTS = 48;

    public object CallDeclared(string name, ToolArguments input, Func<bool> isAllowed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var entries = settings.Current.SavedEndpoints ?? [];
        if (entries.Count > MAX_ENDPOINTS || entries.Any(item => item is null))
            throw new InvalidOperationException("已保存 IPC 配置超出限制或含空条目。");
        if (name == ENDPOINTS_TOOL)
            return new { status = "ok", endpoints = entries.OrderBy(item => item.Alias, StringComparer.OrdinalIgnoreCase).ToArray() };
        var alias = input.Text("alias");
        DeclaredPluginEndpoint.ValidateName(alias, 48, "alias");
        var matches = entries.Where(entry => string.Equals(entry.Alias, alias, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("IPC 别名不唯一。");
        var existing = matches.SingleOrDefault();

        if (name == EDIT_ENDPOINT_TOOL)
        {
            var operation = input.Text("operation");
            var next = new List<DeclaredPluginEndpoint>(entries);
            if (operation == "remove")
            {
                if (input.Has("plugin_name") || input.Has("callgate") || input.Has("development_only") ||
                    input.Has("argument_kinds") || input.Has("return_kind"))
                    throw new ArgumentException("删除时只传别名和 expected_callgate。");
                var expected = input.Text("expected_callgate");
                if (existing is null) return new { status = "not_found", alias };
                if (!string.Equals(existing.Callgate, expected, StringComparison.Ordinal)) return new { status = "endpoint_changed", alias };
                next.Remove(existing);
            }
            else if (operation == "add")
            {
                if (input.Has("expected_callgate")) throw new ArgumentException("新增不接受 expected_callgate。");
                if (existing is not null || entries.Count == MAX_ENDPOINTS)
                    return new { status = existing is null ? "limit_reached" : "alias_in_use", alias };
                var values = input.Element("argument_kinds");
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > DeclaredPluginEndpoint.MAX_ARGUMENTS)
                    throw new ArgumentException("argument_kinds 必须是最多四项的数组。");
                var endpoint = new DeclaredPluginEndpoint(alias, input.Text("plugin_name"), input.Text("callgate"),
                    input.Boolean("development_only"), values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray(),
                    input.Text("return_kind"));
                endpoint.Validate();
                Check(endpoint);
                next.Add(endpoint);
            }
            else throw new ArgumentException("operation 必须为 add 或 remove。");
            DemandAccess();
            settings.Current.SavedEndpoints = next;
            try { settings.Save(); }
            catch
            {
                settings.Current.SavedEndpoints = entries;
                throw;
            }
            return new { status = operation == "add" ? "saved" : "removed", alias };
        }

        if (existing is null) return new { status = "not_found", alias };
        existing.Validate();
        var (kinds, result) = Check(existing);
        var arguments = input.Element("arguments");
        if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() != kinds.Length)
            throw new ArgumentException("实参数量与保存的 IPC 声明不符。");
        var valuesToCall = arguments.EnumerateArray().Select((value, i) =>
            DeclaredPluginEndpoint.ParseArgument(existing.ArgumentKinds[i], value)).ToArray();
        if (!SubscriberMethods.TryGetValue(kinds.Length + 1, out var factory))
            throw new InvalidOperationException("当前卫月不支持该 IPC 参数数量。");
        var isAction = result == typeof(void);
        var subscriber = factory.MakeGenericMethod([.. kinds, isAction ? typeof(object) : result])
            .Invoke(services.PluginInterface, [existing.Callgate]) ?? throw new InvalidOperationException("无法绑定 IPC。");
        var invoke = subscriber.GetType().GetMethod(isAction ? "InvokeAction" : "InvokeFunc", kinds)
            ?? throw new InvalidOperationException("IPC 调用入口与声明不匹配。");
        DemandAccess();
        try
        {
            var output = invoke.Invoke(subscriber, valuesToCall);
            if (output is string text && (text.Length > 4096 || Encoding.UTF8.GetByteCount(text) > 8192))
                return new { status = "result_too_large", alias, requestDispatched = true };
            return new { status = "returned", alias, requestDispatched = true, result = output };
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException("保存的 IPC 在执行时失败，可能已产生部分效果。", error.InnerException);
        }

        void DemandAccess()
        {
            if (!isAllowed()) throw new InvalidOperationException("IPC 变更权限已关闭。");
        }

        (Type[] Kinds, Type Result) Check(DeclaredPluginEndpoint endpoint)
        {
            var arguments = endpoint.ArgumentKinds.Select(DeclaredPluginEndpoint.KindType).ToArray();
            var result = DeclaredPluginEndpoint.KindType(endpoint.ReturnKind);
            discovery.VerifyDeclaredEndpoint(endpoint, arguments, result);
            return (arguments, result);
        }
    }
}
