namespace DalamudMCP.Plugin.Tools;

internal static class PluginWindowRequest
{
    internal const string TOOL = "show_plugin_window";
    internal const int DISPATCH_TIMEOUT_MS = 3_000;

    internal static async Task<Result> RunAsync<T>(
        string name, Surface surface, bool developmentOnly, int timeoutMs,
        Func<Func<Result>, CancellationToken, Task<Result>> schedule,
        Func<IEnumerable<T>> getPlugins, Func<T, (string Name, bool IsDev, bool IsLoaded)> describe,
        Func<T, Surface, Action?> getCallback, Func<bool> isAllowed, CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        const int CLAIMED = 1;
        const int EXPIRED = 2;
        var dispatch = 0;
        try
        {
            return await schedule(Request, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Interlocked.CompareExchange(ref dispatch, EXPIRED, 0) == CLAIMED
                ? new("dispatch_unconfirmed", null)
                : new("timeout", false);
        }

        Result Request()
        {
            token.ThrowIfCancellationRequested();
            T? target = null;
            var found = false;
            var scanned = 0;
            foreach (var plugin in getPlugins())
            {
                if (++scanned > 4096) return new("scan_incomplete", false);
                var candidate = describe(plugin);
                if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || developmentOnly && !candidate.IsDev)
                    continue;
                found = true;
                if (!candidate.IsLoaded) continue;
                if (target is not null) return new("ambiguous", false);
                target = plugin;
            }

            if (target is null) return new(found ? "not_loaded" : "not_found", false);
            if (!describe(target).IsLoaded) return new("not_loaded", false);
            var callback = getCallback(target, surface);
            if (callback is null) return new("no_public_entry", false);
            if (!isAllowed()) throw new InvalidOperationException("打开插件窗口的权限已关闭。");
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref dispatch, CLAIMED, 0) != 0)
                throw new OperationCanceledException(token);

            callback();
            return new("requested", true);
        }
    }

    internal enum Surface { Main, Settings }
    internal sealed record Result(string Status, bool? Requested);
}
