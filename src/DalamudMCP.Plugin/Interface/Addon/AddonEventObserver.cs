using System.Diagnostics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Plugin.Tools;

internal sealed class AddonEventObserver(IAddonLifecycle lifecycle) : IDisposable
{
    private readonly object sync = new();
    private Observation? active;
    private bool disposed;

    public async Task<object> ObserveAsync(ToolArguments input, CancellationToken token)
    {
        var name = input.Text("addon_name");
        if (name.Length > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("addon_name 必须是明确的原生窗口名，最多 32 个字母、数字或下划线。");
        var duration = input.Integer("duration_ms", 3000, 250, 10000);
        var limit = input.Integer("limit", 64, 1, 128);
        var phase = input.Text("phase", "after");
        if (phase is not ("before" or "after")) throw new ArgumentException("phase 必须为 before 或 after。");
        var type = phase == "before" ? AddonEvent.PreReceiveEvent : AddonEvent.PostReceiveEvent;
        var nodeID = input.Has("node_id") ? input.Element("node_id").GetUInt32() : (uint?)null;
        var readNode = nodeID.HasValue || input.Boolean("include_node");
        if (readNode && !GameDiagnostics.ReadoutLayoutKnown) return new { status = "unsupported_sdk" };
        Observation observation;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (active is not null) return new { status = "busy" };
            observation = new(name, type, duration, limit, nodeID, readNode, token);
            lifecycle.RegisterListener(type, name, observation.OnEvent);
            active = observation;
        }

        try
        {
            await Task.Delay(duration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return observation.Snapshot();
        }
        finally
        {
            lock (sync)
                if (ReferenceEquals(active, observation)) End();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            End();
        }
    }

    private void End()
    {
        if (active is not { } observation) return;
        active = null;
        lifecycle.UnregisterListener(observation.Type, observation.Name, observation.OnEvent);
    }

    private sealed class Observation(string name, AddonEvent type, int duration, int limit, uint? nodeID, bool readNode, CancellationToken token)
    {
        private readonly object sync = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<EventSample> events = new(limit);
        private bool truncated;
        private int missingNodeEvents;

        public string Name => name;
        public AddonEvent Type => type;

        public unsafe void OnEvent(AddonEvent observedType, AddonArgs args)
        {
            if (observedType != type || args is not AddonReceiveEventArgs received ||
                !string.Equals(args.AddonName, name, StringComparison.Ordinal)) return;
            lock (sync)
            {
                var elapsed = clock.ElapsedMilliseconds;
                if (elapsed >= duration || token.IsCancellationRequested) return;
                uint? observedNode = null;
                if (readNode && NativeMemoryReader.TryRead((AtkEvent*)received.AtkEvent, out var data) && data.Node != null &&
                    NativeMemoryReader.TryRead(&data.Node->NodeId, out var id)) observedNode = id;
                if (readNode && !observedNode.HasValue) missingNodeEvents++;
                if (nodeID.HasValue && observedNode != nodeID) return;
                if (events.Count == limit) { truncated = true; return; }
                events.Add(new(elapsed, (int)received.AtkEventType, received.AtkEventType.ToString(), received.EventParam, observedNode));
            }
        }

        public object Snapshot()
        {
            lock (sync) return new
            {
                status = events.Count == 0 ? "no_events" : "observed", addonName = name, durationMs = duration,
                source = type == AddonEvent.PreReceiveEvent ? "addon_pre_receive_event" : "addon_post_receive_event", events = events.ToArray(), truncated,
                nodeFilter = nodeID, missingNodeEvents = readNode ? (int?)missingNodeEvents : null,
            };
        }
    }

    private sealed record EventSample(long ElapsedMs, int EventType, string EventName, int EventParam, uint? NodeID);
}
