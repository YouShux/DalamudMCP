using System.Diagnostics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

public sealed partial class GameTools : IDisposable
{
    private readonly GameServices services;
    private readonly GameState state;
    private readonly AddonTools addons;
    private readonly AddonEventObserver addonEvents;
    private readonly PluginIntegration integration;
    private readonly DevPluginBuildTools devBuilds;
    private readonly PluginDiscovery discovery;
    private readonly ChatHistory chat;
    private readonly DalamudLog log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly RepositoryPackages packages;
    private readonly GameDiagnostics diagnostics;
    private readonly OpcodeCatalogCheck opcodeCatalog;
    private readonly SettingsStore settings;
    private readonly RuntimeInspector runtime;
    private readonly RuntimeObservations runtimeObservations;
    private readonly RuntimeMemorySearch memorySearch = new();
    private readonly RuntimeCallJournal runtimeCalls = new();
    private readonly PacketLayoutCatalog packetLayouts;
    private readonly PacketSourceUpdates packetSources;
    private readonly GameReadouts readouts;
    private readonly ClientStateJournal stateJournal = new(2000);
    private readonly GameWindowState gameWindow = new();
    private long nextStateJournalSample;
    private int namedQueries;
    private int memoryMapQueries;

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public GameTools(GameServices services, SettingsStore settings)
    {
        this.services = services;
        this.settings = settings;
        runtime = new(services);
        runtimeObservations = new(services.Interop, runtime);
        packetLayouts = new(services.PluginInterface.GetPluginConfigDirectory());
        packetSources = new(settings.Current.CheckPacketSourcesOnLoad, message => services.Log.Information(message));
        opcodeCatalog = new(services.PluginInterface.GetPluginConfigDirectory(), settings.Current.CheckOpcodesOnLoad);
        lifetimeToken = lifetime.Token;
        state = new(services, settings.Current);
        addons = new(services);
        addonEvents = new(services.AddonLifecycle);
        devBuilds = new(services.Framework);
        discovery = new(services, devBuilds.GetLoadedPluginAssembly);
        integration = new(services, settings, discovery);
        integration.StartMessages();
        chat = new(services);
        log = new();
        packages = new(services, lifetime.Token);
        readouts = new(services);
        diagnostics = new(services, readouts, settings.Current);
        diagnostics.StartDecisionPublisher();
        Tools = CreateTools();
        addons.ObserveDutyLifecycle(true);
        services.Framework.Update += ObserveDiagnostics;
    }

    internal async ValueTask<JsonElement> CallAsync(ToolDefinition definition, JsonElement arguments, bool structuredOnly, Func<bool> isAllowed, CancellationToken cancellationToken)
    {
        var name = definition.Name;
        var tracked = name is "inspect_runtime_catalog" or "access_runtime_object" or "invoke_sdk_member" or "access_sdk_memory"
            or "read_process_memory" or "write_process_memory" or "scan_process_memory" or "search_memory_values"
            or "inspect_address_regions" or "follow_memory_offsets" or "inspect_process_module"
            or "start_runtime_observation" or "stop_runtime_observation" or "watch_runtime_value";
        var started = Environment.TickCount64;
        var status = "returned";
        try { return await CallCoreAsync(definition, arguments, structuredOnly, isAllowed, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { status = "error:" + error.GetType().Name; throw; }
        finally { if (tracked) runtimeCalls.Record(name, started, status); }
    }

    private async ValueTask<JsonElement> CallCoreAsync(ToolDefinition definition, JsonElement arguments, bool structuredOnly, Func<bool> isAllowed, CancellationToken cancellationToken)
    {
        JsonElement Success<T>(T value) => ToolJson.Success(value, structuredOnly);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        var name = definition.Name;
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("工具参数必须为对象。");
        if (arguments.EnumerateObject().MoveNext())
        {
            var properties = definition.InputSchema.GetProperty("properties");
            var validNames = properties.EnumerateObject().Select(property => ToolArguments.Normalize(property.Name)).ToHashSet();
            foreach (var argument in arguments.EnumerateObject())
                if (!validNames.Remove(ToolArguments.Normalize(argument.Name)))
                    throw new ArgumentException($"参数未知或重复：{argument.Name}。");
        }

        var input = new ToolArguments(arguments);
        foreach (var required in definition.InputSchema.GetProperty("required").EnumerateArray())
            if (!input.Has(required.GetString() ?? string.Empty))
                throw new ArgumentException($"缺少参数 {required.GetString()}。");

        if (name is GameReadouts.CURRENCY_TOOL or AddonTools.DUTY_BROWSER_TOOL or AddonTools.DUTY_ENTRIES_TOOL or AddonTools.DUTY_SELECT_TOOL)
            return Success(await ReadDutyOrCurrencyAsync(name, input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == ARRIVAL_TOOL)
            return Success(await WaitGameArrivalAsync(input, linked.Token).ConfigureAwait(false));
        if (name == CHARACTER_REQUEST_TOOL)
            return Success(await RequestCharacterControlAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == BUTTON_WORKFLOW_TOOL)
            return Success(await RequestAddonButtonAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == GameReadouts.APPLY_GEARSET_TOOL)
            return Success(await ApplyGearsetAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == NPC_INTERACTION_TOOL)
            return Success(await RequestNPCInteractionAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == BATTLE_CHECKPOINT_TOOL)
            return Success(await services.Framework.RunOnTick(() => CreateBattleCheckpoint(input), cancellationToken: linked.Token).WaitAsync(linked.Token).ConfigureAwait(false));
        if (name == BATTLE_RESULT_TOOL)
            return Success(await WaitBattleResultAsync(input, linked.Token).ConfigureAwait(false));
        if (name == GameDiagnostics.WAIT_COMBAT_TOOL)
            return Success(await diagnostics.WaitCombatRecordingAsync(input, linked.Token).ConfigureAwait(false));
        if (name == PluginIntegration.MESSAGES_TOOL)
            return Success(integration.ReadMessages(input));
        if (name == DevelopmentWorkflow.TOOL)
            return Success(DevelopmentWorkflow.Describe(input.Boolean("include_skill"), input.Boolean("include_bundle"),
                input.Has("known_revision") ? input.Text("known_revision") : null));
        if (name is GameReadouts.NAMED_FIELDS_TOOL or GameReadouts.NAMED_ROW_TOOL or GameReadouts.SEARCH_FIELDS_TOOL)
        {
            if (Interlocked.Increment(ref namedQueries) > 2)
            {
                Interlocked.Decrement(ref namedQueries);
                return Success(new { status = "busy" });
            }
            try { return Success(await Task.Run(() => readouts.NamedData(name, input, linked.Token), linked.Token).ConfigureAwait(false)); }
            finally { Interlocked.Decrement(ref namedQueries); }
        }
        if (name == GameDiagnostics.DECODE_TOOL)
            return Success(GameDiagnostics.DecodeSample(input));
        if (name == GameDiagnostics.COMPARE_SAMPLE_TOOL)
            return Success(GameDiagnostics.CompareSamples(input));
        if (name == "inspect_opcode_catalog") return Success(await opcodeCatalog.InspectAsync(input, linked.Token).ConfigureAwait(false));
        if (name == "refresh_opcode_catalog") return Success(await opcodeCatalog.CheckAsync(input, linked.Token, persist: isAllowed).ConfigureAwait(false));
        if (name is "check_cn_opcode_catalog" or "find_cn_zone_opcode")
            return Success(await opcodeCatalog.CheckAsync(input, linked.Token, name == "find_cn_zone_opcode").ConfigureAwait(false));

        if (name == "inspect_protocol_updates") return Success(packetSources.Inspect());
        if (name == "check_packet_sources") return Success(await packetSources.CheckAsync(input.Boolean("force"), linked.Token).ConfigureAwait(false));
        if (name == "inspect_packet_aliases") return Success(BundledPacketLayouts.ReadAliases(input));
        if (name == "inspect_runtime_catalog") return Success(await Task.Run(() => RuntimeInspector.Inspect(input), linked.Token).ConfigureAwait(false));
        if (name is "inspect_packet_layouts" or "save_packet_layout")
            return Success(await Task.Run(() => name == "inspect_packet_layouts" ? packetLayouts.Read(input) : packetLayouts.Save(input, isAllowed), linked.Token).ConfigureAwait(false));
        if (name == "parse_zone_packet")
        {
            if (!isAllowed()) throw new InvalidOperationException("原始包读取权限已关闭。");
            return Success(await Task.Run(() => diagnostics.InspectPacket(name, input, packetLayouts), linked.Token).ConfigureAwait(false));
        }
        if (name == "search_memory_values") return Success(await Task.Run(() => memorySearch.Call(input, isAllowed, linked.Token), linked.Token).ConfigureAwait(false));
        if (name is "inspect_address_regions" or "follow_memory_offsets" or "inspect_process_module" or "scan_process_memory")
        {
            if (Interlocked.Increment(ref memoryMapQueries) > 2)
            {
                Interlocked.Decrement(ref memoryMapQueries);
                return Success(new { status = "busy" });
            }
            var until = Environment.TickCount64 + 1000;
            var memoryToken = linked.Token;
            var operation = Task.Run(() =>
            {
                try { return RuntimeInspector.MemoryMap(name, input, isAllowed, memoryToken, until); }
                finally { Interlocked.Decrement(ref memoryMapQueries); }
            });
            try { return Success(await operation.WaitAsync(TimeSpan.FromSeconds(1), memoryToken).ConfigureAwait(false)); }
            catch (TimeoutException) when (name == "inspect_process_module")
            {
                _ = operation.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return Success(new { status = "timeout" });
            }
            catch (TimeoutException) { return Success(await operation.ConfigureAwait(false)); }
            catch (OperationCanceledException) { return Success(new { status = "cancelled" }); }
        }
        if (name == "capture_game_screenshot")
            return await GameScreenshot.CaptureAsync(services, ReadScreenshotViewport(), ReadScreenshotViewport, input, structuredOnly, linked.Token).ConfigureAwait(false);
        if (name == AddonTools.SAMPLE_NODES_TOOL)
            return Success(await SampleAddonNodeAsync(input, linked.Token).ConfigureAwait(false));
        if (name == "observe_addon_events")
            return Success(await addonEvents.ObserveAsync(input, linked.Token).ConfigureAwait(false));
        if (name is "observe_addon_callbacks" or "observe_addon_node_listener" or "observe_ui_callbacks")
            return Success(await ObserveAddonCallbacksAsync(input, isAllowed, linked.Token, name == "observe_addon_node_listener", name == "observe_ui_callbacks").ConfigureAwait(false));
        if (name == GameWindowState.READ_TOOL)
            return Success(GameWindowState.Read());
        if (name == GameWindowState.SET_TOOL)
            return Success(await gameWindow.SetAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == WindowInputScheduler.QUEUE_SEQUENCE_TOOL)
            return Success(QueueSequence(input, isAllowed));
        if (name is WindowInputScheduler.READ_SEQUENCE_TOOL or WindowInputScheduler.CANCEL_SEQUENCE_TOOL)
            return Success(windowInput.SequenceState(RepositoryPackages.ParseID(input.Text("sequence_id")),
                name == WindowInputScheduler.CANCEL_SEQUENCE_TOOL, isAllowed));
        if (name == GameWindowState.KEY_TOOL)
        {
            var territory = input.Element("expected_territory_id").GetUInt32();
            if (!windowInput.BeginLegacyGameKey()) return Success(new { status = "busy", pulses = 0 });
            try
            {
                return Success(await gameWindow.RequestKeyAsync(input, isAllowed, () =>
                    services.Framework.RunOnTick(() => !services.Framework.IsFrameworkUnloading && services.ClientState.IsLoggedIn &&
                        !services.ClientState.IsPvP && services.ClientState.TerritoryType == territory &&
                        !services.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51),
                        cancellationToken: linked.Token).WaitAsync(linked.Token), linked.Token).ConfigureAwait(false));
            }
            finally { windowInput.EndLegacyGameKey(); }
        }
        if (name == "get_dalamud_log")
            return Success(log.Read(input));
        if (name is "install_repository_plugin" or "remove_repository_plugin")
            return Success(packages.Start(name == "install_repository_plugin", input, isAllowed));
        if (name == "change_repository_source")
            return Success(packages.StartRepository(input, isAllowed));
        if (name == "get_repository_job")
            return Success(packages.GetOperation(input));
        if (name is "queue_imgui_text" or "queue_imgui_gesture" or "probe_imgui_point" or "queue_window_chord" or "drag_window_control" or "hold_window_key")
            return Success(await QueueImGuiInputAsync(input, name, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == "watch_runtime_value") return Success(await WatchRuntimeAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == "dispatch_when_node_ready") return Success(await DispatchWhenReadyAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == MechanicGrid.TOOL)
            return Success(MechanicGrid.Solve(input));
        if (name is GameDiagnostics.MECHANIC_REPLAY or MechanicAssignments.TOOL or MechanicRoute.ROUTE or MechanicRoute.KNOCKBACK)
        {
            if (Interlocked.Increment(ref mechanicSolves) > 2)
            {
                Interlocked.Decrement(ref mechanicSolves);
                return Success(new { status = "busy" });
            }
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(4));
                return Success(await Task.Run(() => name switch
                {
                    GameDiagnostics.MECHANIC_REPLAY => diagnostics.ReplayMechanics(input, budget.Token),
                    MechanicAssignments.TOOL => MechanicAssignments.Solve(input, budget.Token),
                    MechanicRoute.ROUTE => MechanicRoute.Plan(input, budget.Token),
                    _ => MechanicRoute.Knockback(input),
                }, budget.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            {
                return Success(new { status = "timeout" });
            }
            finally { Interlocked.Decrement(ref mechanicSolves); }
        }
        if (name == MECHANICS_TOOL)
        {
            if (input.Has("archive_id"))
                return Success(await Task.Run(() => InspectArchivedMechanics(input), linked.Token).ConfigureAwait(false));
            return Success(await services.Framework.RunOnFrameworkThread(() => InspectLocalMechanics(input)).WaitAsync(linked.Token).ConfigureAwait(false));
        }
        if (name == "get_window_input_queue") return Success(windowInput.State());
        if (name == "cancel_window_input") return Success(windowInput.Cancel());
        if (name == "sample_imgui_hit_points")
            return Success(await ProbeImGuiPointsAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == "adjust_dropdown_panel")
            return Success(await AdjustDropdownAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name is "sample_combat_window" or "archive_combat_window")
            return Success(await diagnostics.SampleAsync(name == "archive_combat_window", input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name is GameDiagnostics.ANALYZE_COMBAT_TOOL or GameDiagnostics.EXPORT_COMBAT_TOOL)
            return Success(await diagnostics.AnalyzeArchiveAsync(input, name == GameDiagnostics.EXPORT_COMBAT_TOOL, isAllowed, linked.Token).ConfigureAwait(false));
        if (name is "read_combat_capture" or "summarize_combat_capture" or "read_combat_activity" or GameDiagnostics.COMBAT_TIMELINE_TOOL
            or GameDiagnostics.READ_ACR or GameDiagnostics.READ_MECHANICS)
            return Success(await Task.Run(() => diagnostics.ReadArchive(input, name), linked.Token).ConfigureAwait(false));
        if (name == "sample_client_connection")
            return Success(await diagnostics.SampleConnectionAsync(input, linked.Token).ConfigureAwait(false));
        if (name == "watch_addon_state")
            return Success(await WaitForNativeControlAsync(input, linked.Token).ConfigureAwait(false));
        if (name == AddonTools.SURFACE_TOOL)
            return Success(await RequestNativeSurfaceAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == ClientStateJournal.TOOL)
            return Success(await stateJournal.ReadAsync(input, linked.Token).ConfigureAwait(false));
        if (name == "reload_plugin")
            return Success(await integration.ReloadAsync(input.Text("plugin_name"), linked.Token));
        if (name is DevPluginBuildTools.STATE_TOOL or DevPluginBuildTools.WAIT_TOOL)
            return Success(await devBuilds.CallAsync(name == DevPluginBuildTools.WAIT_TOOL, input, linked.Token));
        if (name == DevPluginBuildTools.LOAD_TOOL)
            return Success(await devBuilds.LoadAsync(input, services.PluginInterface.InternalName, isAllowed, linked.Token));
        if (name == PluginWindowRequest.TOOL)
            return Success(await discovery.ShowWindowAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name is WindowFrameObserver.SNAPSHOT_TOOL or WindowFrameObserver.WAIT_TOOL)
            return Success(await ObserveWindowAsync(name == WindowFrameObserver.WAIT_TOOL, input, linked.Token).ConfigureAwait(false));
        if (name == "send_addon_event" && input.Has("observe"))
            return Success(await CallObservedAddonAsync(input, isAllowed, linked.Token).ConfigureAwait(false));
        if (name == "send_addon_event" && input.Has("observe_timeout_ms"))
            throw new ArgumentException("observe_timeout_ms 需要 observe。");
        if (name == "unsafe_invoke_plugin_ipc" && !input.Boolean("run_on_framework_thread", true))
            return Success(integration.Call(name, input));

        GameDiagnostics.RawZoneBuild? rawZone = null;
        if (name == GameDiagnostics.ZONE_TRACE_STATUS_TOOL || name == GameDiagnostics.ZONE_TRACE_CONTROL_TOOL &&
            input.Boolean("enabled") && input.Boolean("raw_payload"))
            rawZone = await GameDiagnostics.VerifyRawZoneBuildAsync(linked.Token).ConfigureAwait(false);

        object Execute()
        {
            linked.Token.ThrowIfCancellationRequested();
            return name switch
            {
                "inspect_runtime_health" => runtimeCalls.Health(),
                "read_runtime_calls" => runtimeCalls.Read(input),
                "start_runtime_observation" => runtimeObservations.Start(input, isAllowed),
                "read_runtime_observation" when isAllowed() => runtimeObservations.Read(input),
                "stop_runtime_observation" when isAllowed() => runtimeObservations.Read(input, stop: true, remove: input.Boolean("remove")),
                "access_runtime_object" => runtime.Access(input, isAllowed),
                "invoke_sdk_member" => runtime.CallSdk(input, isAllowed),
                "access_sdk_memory" => RuntimeInspector.TypedMemory(input, isAllowed),
                "read_process_memory" => RuntimeInspector.Memory(input, false, isAllowed),
                "write_process_memory" => RuntimeInspector.Memory(input, true, isAllowed),
                "inspect_opcode_refresh" => opcodeCatalog.Startup,
                "read_zone_packet_slice" or "decode_zone_packet" or "compare_zone_packets" when isAllowed() => diagnostics.InspectPacket(name, input),
                "get_chat_log" => chat.Read(input),
                CHARACTER_STATE_TOOL => InspectCharacterControls(),
                "find_repository_plugins" => packages.Catalog(input),
                "inspect_repository_sources" => packages.Sources(),
                "read_addon_control" or "edit_addon_control" => addons.Control(input, name == "edit_addon_control", isAllowed, settings.Current.AllowExtendedControlEditing),
                "activate_addon_row" => addons.ActivateRow(input, isAllowed),
                AddonTools.READ_LIST_ROW_TOOL or AddonTools.ACTIVATE_LIST_ROW_TOOL =>
                    addons.ListRenderer(input, name == AddonTools.ACTIVATE_LIST_ROW_TOOL, isAllowed),
                "locate_inventory_item" => diagnostics.Holdings(input),
                GameDiagnostics.INVENTORY_CONTEXT_TOOL => diagnostics.InventoryContext(),
                AddonTools.REQUEST_BAG_MENU_TOOL => addons.RequestBagMenu(input, isAllowed),
                GameDiagnostics.ZONE_CAPTURE_TOOL => diagnostics.SetZoneCapture(input, isAllowed),
                GameDiagnostics.ZONE_READ_TOOL => diagnostics.ReadZoneCapture(input),
                GameDiagnostics.ZONE_TRACE_STATUS_TOOL => diagnostics.ZoneTrafficSupport(rawZone),
                GameDiagnostics.ZONE_TRACE_CONTROL_TOOL => diagnostics.SetZoneTrafficTrace(input, isAllowed, rawZone, opcodeCatalog.Snapshot),
                GameDiagnostics.ZONE_TRACE_READ_TOOL => diagnostics.ReadZoneTrafficTrace(input),
                GameDiagnostics.ZONE_BYTES_TOOL when isAllowed() => diagnostics.ReadZoneTrafficTrace(input, includeBytes: true),
                GameDiagnostics.ZONE_BYTES_TOOL => throw new InvalidOperationException("原始包读取权限已关闭。"),
                "configure_combat_history" => ConfigureCombatHistory(input, isAllowed),
                "set_combat_recorder" => SetCombatSession(input, isAllowed, false),
                GameDiagnostics.DISPATCH_RECORDER_TOOL => SetCombatSession(input, isAllowed, true),
                "get_combat_recorder" => diagnostics.RecorderStatus(),
                "inspect_client_connection" => diagnostics.Connection(),
                GameState.NAVIGATION_TOOL or GameState.ROUTE_TOOL or GameState.STOP_TOOL => state.Navigation(name, input, isAllowed),
                GameState.FOCUS_TOOL => state.SetFocus(input, isAllowed),
                GameState.HOTBAR_READ_TOOL or GameState.HOTBAR_REQUEST_TOOL => state.Hotbar(input, name == GameState.HOTBAR_REQUEST_TOOL, isAllowed),
                GameState.RECHARGES_TOOL => state.HotbarRecharges(input),
                GameState.CHAT_TOOL => state.SendChatLine(input, isAllowed),
                GameReadouts.ACTION_CHECK_TOOL or GameReadouts.JOB_ACTIONS_TOOL or GameReadouts.ACTION_REQUEST_TOOL =>
                    readouts.ActionReadiness(name, input, isAllowed),
                GameReadouts.SHEET_LIST_TOOL or GameReadouts.SHEET_FIELDS_TOOL or GameReadouts.SHEET_CELLS_TOOL =>
                    readouts.BrowseData(name, input),
                PluginIntegration.ENDPOINTS_TOOL or PluginIntegration.EDIT_ENDPOINT_TOOL or PluginIntegration.CALL_ENDPOINT_TOOL =>
                    integration.CallDeclared(name, input, isAllowed),
                GameReadouts.CHARACTER_TOOL or GameReadouts.PROGRESS_TOOL or GameReadouts.PARTY_TOOL or
                    GameReadouts.TARGETS_TOOL or GameReadouts.EFFECTS_TOOL or GameReadouts.TRAVEL_TOOL or
                    GameReadouts.EQUIPMENT_TOOL or GameReadouts.GEARSETS_TOOL or
                    GameReadouts.BUDDIES_TOOL or GameReadouts.GAUGE_TOOL or GameReadouts.RECAST_TOOL or
                    GameReadouts.UNLOCK_TOOL or GameReadouts.SHEETS_TOOL or GameReadouts.MAP_TOOL or
                    GameReadouts.SOCIETIES_TOOL or GameReadouts.NEARBY_BATTLE_TOOL => readouts.Call(name, input),
                "get_session_status" => new
                {
                    loggedIn = services.ClientState.IsLoggedIn,
                    territoryID = services.ClientState.TerritoryType,
                    mapID = services.ClientState.MapId,
                    instance = services.ClientState.Instance,
                    clientLanguage = services.ClientState.ClientLanguage.ToString(),
                    isPvP = services.ClientState.IsPvP,
                    isGPosing = services.ClientState.IsGPosing,
                    localPlayerAvailable = services.ObjectTable.LocalPlayer is not null,
                    pluginBuildMvid = typeof(GameTools).Assembly.ManifestModule.ModuleVersionId,
                    pluginPath = services.PluginInterface.AssemblyLocation.FullName,
                    tools = Tools.Count,
                },
                "get_addon_list" or "get_addon_tree" or "get_addon_strings" or "get_addon_nodes" or AddonTools.SNAPSHOT_TOOL or "inspect_menu_choice" or "send_addon_input" or
                    "send_addon_event" or "send_addon_callback_values" or "select_addon_menu_item" => addons.Call(name, input),
                PluginDiscovery.LIST_TOOL or PluginDiscovery.COMMANDS_TOOL or PluginDiscovery.IPC_TOOL or
                    PluginDiscovery.STATE_TOOL => discovery.Call(name, input),
                "unsafe_invoke_plugin_ipc" or "invoke_plugin_ipc" or "slash_command" or
                    "plugin_data_subscribe" or "plugin_data_unsubscribe" or "plugin_data_poll" => integration.Call(name, input),
                _ => state.Call(name, input, structuredOnly),
            };
        }

        var result = services.Framework.IsInFrameworkUpdateThread
            ? Execute()
            : await services.Framework.RunOnFrameworkThread(Execute).WaitAsync(linked.Token).ConfigureAwait(false);
        if (name == "teleport_to_aetheryte" && result is Task<bool> teleportRequest)
        {
            var accepted = await teleportRequest.WaitAsync(linked.Token).ConfigureAwait(false);
            result = new
            {
                succeeded = accepted,
                requestAccepted = accepted,
                resultObserved = false,
                destination = input.Text("query"),
                provider = "OmniToolbox",
                message = accepted ? "传送请求已接受，请查询区域和坐标确认到达。" : "Omni 未接受传送请求，请检查目的地、模块开关、验证状态或当前传送任务。"
            };
        }
        return name == "get_player_context" && result is JsonElement compactPlayer ? compactPlayer : Success(result);
    }

    private async Task<object> CallObservedAddonAsync(ToolArguments input, Func<bool> isAllowed, CancellationToken cancellationToken)
    {
        var observation = AddonObservation.Parse(input);
        var timeoutMs = input.Integer("observe_timeout_ms", 2_000, 250, 5_000);
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        AddonTools.ObservationSample? before = null;
        AddonTools.ObservationSample? after = null;
        AddonTools.EventResult? action = null;
        var dispatchStarted = 0;
        try
        {
            (before, action) = await services.Framework.RunOnTick(() =>
            {
                CheckFramework();
                var baseline = addons.ReadObservation(observation);
                if (Matches(baseline) != false)
                    return (baseline, (AddonTools.EventResult?)null);
                CheckFramework();
                if (!isAllowed()) throw new InvalidOperationException("窗口事件权限已关闭。");
                Volatile.Write(ref dispatchStarted, 1);
                return (baseline, (AddonTools.EventResult)addons.Call("send_addon_event", input));
            }, cancellationToken: token).WaitAsync(token).ConfigureAwait(false);

            if (Matches(before) is null) return Result("baseline_unavailable");
            if (action is null) return Result("already_matched");

            do
            {
                after = await services.Framework.RunOnTick(() =>
                {
                    CheckFramework();
                    return addons.ReadObservation(observation);
                }, delayTicks: 1, cancellationToken: token).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (Matches(after) == true)
                    return Result(action.Succeeded ? "observed" : "dispatch_unconfirmed");
                await Task.Delay(50, token).ConfigureAwait(false);
            } while (clock.ElapsedMilliseconds < timeoutMs);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        return Result("timeout");

        bool? Matches(AddonTools.ObservationSample sample) => observation.IsMatch(sample.Visible, sample.Node?.Checked);

        void CheckFramework()
        {
            token.ThrowIfCancellationRequested();
            if (!services.Framework.IsInFrameworkUpdateThread || services.Framework.IsFrameworkUnloading)
                throw new InvalidOperationException("卫月框架线程当前不可用。");
        }

        object Result(string status) => new
        {
            status,
            succeeded = action?.Succeeded ?? false,
            requestDispatched = action?.RequestDispatched ?? (Volatile.Read(ref dispatchStarted) == 0 ? false : (bool?)null),
            dispatchMayHaveStarted = Volatile.Read(ref dispatchStarted) != 0,
            dispatched = action?.Dispatched,
            nodeID = action?.NodeID,
            resultObserved = status == "observed",
            addonName = observation.AddonName,
            expectedVisible = observation.ExpectedChecked.HasValue ? (bool?)null : observation.ExpectedVisible,
            expectedChecked = observation.ExpectedChecked,
            before,
            after,
            elapsedMs = clock.ElapsedMilliseconds,
        };
    }

    public void Dispose()
    {
        services.Framework.Update -= ObserveDiagnostics;
        addons.ObserveDutyLifecycle(false);
        lifetime.Cancel();
        addonEvents.Dispose();
        opcodeCatalog.Dispose();
        packetSources.Dispose();
        try { runtimeObservations.Dispose(); }
        catch (Exception error) { services.Log.Error(error, "无法解除运行时观察。"); }
        try { StopCallbackObservation(); }
        catch (Exception error) { services.Log.Debug(error, "无法释放窗口回调观察。"); }
        try { diagnostics.EndZoneTrafficTrace("unloaded"); }
        catch (Exception error) { services.Log.Debug(error, "无法释放收发观察。"); }
        try { diagnostics.EndZoneCapture("unloaded"); }
        catch (Exception error) { services.Log.Debug(error, "无法释放服务端回调采集。"); }
        try { diagnostics.StopCombatRecorder(); }
        catch (Exception error) { services.Log.Debug(error, "无法释放战斗归档。"); }
        windowInput.Dispose();
        windowFrames.Dispose();
        log.Dispose();
        state.Dispose();
        chat.Dispose();
        discovery.Dispose();
        diagnostics.StopDecisionPublisher();
        integration.Dispose();
        lifetime.Dispose();
    }

    private void ObserveDiagnostics(IFramework framework)
    {
        runtimeCalls.ObserveFrameworkThread();
        if (!framework.IsFrameworkUnloading)
        {
            ObserveAddonNodeFrame();
            runtimeObservations.Tick();
            state.MaintainNavigation();
        }
        if (!framework.IsFrameworkUnloading && !lifetimeToken.IsCancellationRequested)
            diagnostics.MaintainContinuousRecorder(settings.Current, () => settings.Snapshot.AllowUnsafe &&
                !settings.Snapshot.DisabledTools.Contains("configure_combat_history"));
        diagnostics.ObserveFrame(framework, lifetimeToken);
        var now = Environment.TickCount64;
        if (now < nextStateJournalSample || lifetimeToken.IsCancellationRequested || framework.IsFrameworkUnloading) return;
        nextStateJournalSample = now + 250;
        var loggedIn = services.ClientState.IsLoggedIn;
        var player = loggedIn ? services.ObjectTable.LocalPlayer : null;
        if (loggedIn && player is null) return;
        var target = loggedIn ? services.TargetManager.Target : null;
        stateJournal.Observe(new(loggedIn, loggedIn ? services.ClientState.TerritoryType : 0u,
            loggedIn && services.Condition[ConditionFlag.InCombat], player?.ClassJob.RowId ?? 0, target?.GameObjectId,
            player?.CurrentHp, player?.CurrentMp, player?.CurrentGp,
            player is null ? null : FormattableString.Invariant($"{player.Position.X:F1},{player.Position.Y:F1},{player.Position.Z:F1}"),
            services.TargetManager.FocusTarget?.GameObjectId,
            target is Dalamud.Game.ClientState.Objects.Types.IBattleChara battle ? battle.CurrentHp : null,
            player?.Level, services.DutyState.IsDutyStarted,
            loggedIn ? string.Join(",", services.FateTable.Where(fate => fate is not null).Select(fate => $"{fate.FateId}:{fate.Progress}").Order(StringComparer.Ordinal)) : null,
            player?.IsCasting == true ? player.CastActionId : null,
            player is null ? null : string.Join(",", player.StatusList.Where(effect => effect.StatusId != 0).Select(effect => $"{effect.StatusId}:{effect.SourceId}:{effect.Param}").Order(StringComparer.Ordinal))));
    }

    private object SetCombatSession(ToolArguments input, Func<bool> allowed, bool dispatched)
    {
        var result = diagnostics.SetRecorder(input, allowed, dispatched);
        if (!input.Boolean("enabled") && settings.Current.RecordCombatContinuously)
        {
            settings.Current.RecordCombatContinuously = false;
            settings.Save();
            diagnostics.ResetContinuousRecorder();
        }
        return result;
    }

    private object ConfigureCombatHistory(ToolArguments input, Func<bool> allowed)
    {
        if (!allowed()) throw new InvalidOperationException("战斗采集权限已关闭。");
        var scope = input.Text("scope", settings.Current.CombatRecordingScope);
        if (scope is not ("self" or "party" or "nearby")) throw new ArgumentException("未知战斗采集范围。");
        var capacity = input.Integer("max_events", settings.Current.CombatEventLimit, 100, 50000);
        var enabled = input.Boolean("enabled");
        if (settings.Current.RecordCombatContinuously != enabled || settings.Current.CombatRecordingScope != scope ||
            settings.Current.CombatEventLimit != capacity)
        {
            settings.Current.RecordCombatContinuously = enabled;
            settings.Current.CombatRecordingScope = scope;
            settings.Current.CombatEventLimit = capacity;
            settings.Save();
            diagnostics.ResetContinuousRecorder();
        }
        return new { status = "configured", enabled, scope, maxEvents = capacity, recorder = diagnostics.RecorderStatus() };
    }

    private static ToolDefinition[] CreateTools()
    {
        var text = new { type = "string", minLength = 1, maxLength = 4096 };
        var windowSelector = new Dictionary<string, object>
        {
            ["window_name"] = new { type = "string", minLength = 1, maxLength = 512 },
            ["window_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
        };
        var flag = new { type = "boolean" };
        var distance = new { type = "number", minimum = 1, maximum = 1000 };
        var count = new { type = "integer", minimum = 1, maximum = 1000 };
        var pluginName = new { type = "string", minLength = 1, maxLength = 128 };
        var pluginPage = new Dictionary<string, object>
        {
            ["query"] = pluginName,
            ["offset"] = new { type = "integer", minimum = 0, maximum = 4096 },
            ["limit"] = new { type = "integer", minimum = 1, maximum = 100, @default = 30 },
        };
        var pluginDetails = new Dictionary<string, object>(pluginPage) { ["plugin_name"] = pluginName };
        var addon = new Dictionary<string, object> { ["addon_name"] = text };
        var nodeID = new { type = "integer", minimum = 0, maximum = uint.MaxValue };
        var nodeSelector = new Dictionary<string, object>
        {
            ["node_id"] = nodeID,
            ["label"] = text,
            ["component_type"] = text,
            ["component_path"] = new { type = "array", maxItems = AddonNodeQuery.MAX_COMPONENT_DEPTH, items = nodeID },
        };
        var nearby = new Dictionary<string, object> { ["max_distance"] = distance, ["name_contains"] = text, ["include_players"] = flag };
        var unsafeIPC = new Dictionary<string, object>
        {
            ["callgate"] = text,
            ["result_kind"] = new { type = "string", @enum = new[] { "bool", "int", "uint", "long", "ulong", "float", "double", "string" } },
            ["argument_kinds"] = text,
            ["arguments_json"] = text,
            ["run_on_framework_thread"] = flag,
        };
        return
        [
            .. ExtraTools(nodeSelector),
            .. DiagnosticExpansionTools(),
            .. ReadoutTools(),
            .. InputSequenceTools(),
            Define("get_player_context", "玩家状态", "读取角色状态。只查区域、职业和世界坐标时传 compact=true。", new() { ["compact"] = flag }),
            Define("get_duty_context", "副本状态", "读取当前区域及真实副本完成状态。"),
            Define("get_inventory_summary", "背包概览", "读取已加载的背包、装备、兵装库及金币。"),
            Define("get_addon_list", "游戏窗口", "列出游戏当前加载的原生窗口。"),
            Define(WindowFrameObserver.SNAPSHOT_TOOL, "插件窗口快照", "查询 ImGui 窗口的当前帧状态；可按完整名称或 ID 匹配。", windowSelector),
            Define(WindowFrameObserver.WAIT_TOOL, "等待插件窗口状态", "按完整名称或 ID 等待 ImGui 窗口在连续两帧满足指定状态。", new(windowSelector)
            {
                ["condition"] = new { type = "string", @enum = new[] { "visible", "collapsed", "not_visible" } },
                ["timeout_ms"] = new { type = "integer", minimum = 250, maximum = 5000, @default = 2000 },
            }, ["condition"]),
            Define("get_addon_tree", "窗口节点", "读取指定原生窗口的节点树，最多 4096 个节点、16 层。", addon, ["addon_name"]),
            Define(AddonTools.SNAPSHOT_TOOL, "读取精确窗口节点快照", "用节点 ID 或完整标签定位唯一原生节点，一次读取几何、可见性、注册事件、动画和图像分片。", new(nodeSelector)
            {
                ["addon_name"] = text, ["expected_addon_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
            }, ["addon_name"]),
            Define(AddonTools.SAMPLE_NODES_TOOL, "连续采样窗口节点", "跨框架帧观察同一原生节点的位置、可见性和动画；节点换实例时停止。", new(nodeSelector)
            {
                ["addon_name"] = text, ["expected_addon_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["frames"] = new { type = "integer", minimum = 2, maximum = 60 },
                ["interval_frames"] = new { type = "integer", minimum = 1, maximum = 10 },
                ["timeout_ms"] = new { type = "integer", minimum = 250, maximum = 5000 },
            }, ["addon_name"]),
            Define("get_addon_strings", "窗口文本", "读取窗口文本节点及 AtkValue 字符串。", addon, ["addon_name"]),
            Define("get_addon_nodes", "查找窗口节点", "按标签、节点 ID、组件类型或组件路径查询原生窗口节点；复选框同时返回勾选状态。", new(nodeSelector)
            {
                ["addon_name"] = text, ["contains_match"] = flag, ["visible_only"] = flag,
                ["max_results"] = new { type = "integer", minimum = 1, maximum = 100, @default = 50 },
            }, ["addon_name"]),
            Define("get_fate_context", "危命任务", "读取附近危命任务、进度及剩余时间。", new() { ["max_distance"] = distance, ["name_contains"] = text }),
            Define("get_nearby_interactables", "附近对象", "按距离读取可选中对象。", nearby),
            Define("get_quest_status", "任务进度", "按任务 ID 或名称查询接受、完成及阶段。", new() { ["quest_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue }, ["query"] = text, ["max_results"] = count }),
            Define("get_available_quests", "可接任务", "读取当前地图实际显示的可接任务标记。", new() { ["name_contains"] = text, ["max_results"] = count }),
            Define("get_current_quest_objective", "追踪任务目标", "读取追踪任务阶段、地图目标及地图跳转信息。"),
            Define("get_chat_log", "聊天记录", "读取插件加载后保留的最近 1000 条聊天记录。", new() { ["channels"] = new { type = "array", maxItems = 100, items = text }, ["since"] = new { type = "string", format = "date-time" }, ["max_count"] = count }),
            Define("get_dalamud_log", "卫月运行日志", "读取本插件加载后保留的卫月运行日志。可按来源或文本筛选；增量读取使用上次返回的 streamId 和 nextAfterId。", new()
            {
                ["source"] = text,
                ["contains"] = text,
                ["stream_id"] = text,
                ["after_id"] = new { type = "integer", minimum = 0 },
                ["max_count"] = new { type = "integer", minimum = 1, maximum = 100, @default = 50 },
            }),
            Define("capture_game_screenshot", "游戏渲染截图", "读取游戏渲染帧或用 screenshot_id 对同一帧裁剪，返回 PNG。内存缓存最多 4 帧、60 秒，不写文件。", new()
            {
                ["screenshot_id"] = new { type = "string", format = "uuid" },
                ["include_dalamud_ui"] = flag, ["max_dimension"] = new { type = "integer", minimum = 256, maximum = 2560 },
                ["timeout_ms"] = new { type = "integer", minimum = 250, maximum = 5000 },
                ["crop"] = new { type = "object", properties = new
                    {
                        x = new { type = "integer", minimum = 0 }, y = new { type = "integer", minimum = 0 },
                        width = new { type = "integer", minimum = 1 }, height = new { type = "integer", minimum = 1 },
                    }, required = new[] { "x", "y", "width", "height" }, additionalProperties = false },
            }),
            Define("get_session_status", "游戏连接状态", "读取登录状态、游戏工具数量及当前运行的 DalamudMCP 构建标识。"),
            Define(PluginDiscovery.LIST_TOOL, "已安装插件", "分页列出插件内部名、版本、加载状态和公开界面入口。", new(pluginPage) { ["loaded_only"] = flag }),
            Define(PluginDiscovery.COMMANDS_TOOL, "公开插件命令", "列出公开斜杠命令及帮助，可按插件处理器程序集筛选。", pluginDetails),
            Define(PluginDiscovery.IPC_TOOL, "已注册 IPC", "列出已注册的 IPC 函数或动作及签名。不会调用端点，也不会从名称推断其是否只读。", pluginDetails),
            Define(PluginDiscovery.STATE_TOOL, "插件状态", "读取插件加载状态及其主动发布的状态快照。不会调用目标插件的函数。", new()
            {
                ["plugin_name"] = pluginName,
                ["state_name"] = new { type = "string", minLength = 1, maxLength = 64 },
                ["development_only"] = flag,
            }, ["plugin_name"]),
            Define(PluginWindowRequest.TOOL, "打开插件窗口", "调用已加载插件公开的主界面或设置页入口一次。返回 requested 表示回调已返回，请用只读窗口工具确认是否可见。", new()
            {
                ["plugin_name"] = pluginName,
                ["surface"] = new { type = "string", @enum = new[] { "main", "settings" } },
                ["development_only"] = flag,
            }, ["plugin_name", "surface"], ToolAccess.Action),
            Define(DevPluginBuildTools.STATE_TOOL, "开发插件状态", "按注册的 DLL 绝对路径查询开发插件、自动重载和构建标识。单次匹配不表示新构建已就绪。", new() { ["file_path"] = text }, ["file_path"]),
            Define(DevPluginBuildTools.WAIT_TOOL, "验证开发插件构建", "只读等待已注册的开发插件加载指定构建。编译成功后传入产物的 MVID，仅在 status 为 ready 且 buildVerified 为 true 时继续验收。", new()
            {
                ["file_path"] = text,
                ["expected_build_mvid"] = new { type = "string", format = "uuid" },
                ["timeout_ms"] = new { type = "integer", minimum = DevPluginBuildTools.MIN_WAIT_MS, maximum = DevPluginBuildTools.MAX_WAIT_MS, @default = DevPluginBuildTools.DEFAULT_WAIT_MS },
            }, ["file_path", "expected_build_mvid"]),
            Define(DevPluginBuildTools.LOAD_TOOL, "加载开发插件", "按绝对路径注册并加载指定构建的本地开发插件，并验证运行 MVID。只应加载自己信任的 DLL。", new()
            {
                ["file_path"] = text,
                ["expected_build_mvid"] = new { type = "string", format = "uuid" },
                ["auto_reload"] = flag,
                ["timeout_ms"] = new { type = "integer", minimum = DevPluginBuildTools.MIN_WAIT_MS, maximum = DevPluginBuildTools.MAX_WAIT_MS, @default = DevPluginBuildTools.DEFAULT_WAIT_MS },
            }, ["file_path", "expected_build_mvid"], ToolAccess.Unsafe),
            Define("target_object", "选中对象", "通过对象 ID 选中当前区域对象。", new() { ["game_object_id"] = text }, ["game_object_id"], ToolAccess.Action),
            Define("interact_with_target", "交互目标", "与当前目标交互，可校验预期目标 ID。", new() { ["expected_game_object_id"] = text, ["check_line_of_sight"] = flag }, access: ToolAccess.Action),
            ButtonWorkflowTool(),
            CharacterStateTool(),
            CharacterRequestTool(),
            ArrivalTool(),
            BattleCheckpointTool(),
            BattleResultTool(),
            Define(NPC_INTERACTION_TOOL, "交互指定 NPC 并核对对话", "核对区域与事件 NPC；可选择短距离接近。交互一次后核对窗口，可按精确标签选择一次并核对下一界面。", new()
            {
                ["game_object_id"] = text,
                ["expected_base_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["expected_territory_id"] = new { type = "integer", minimum = 1, maximum = uint.MaxValue },
                ["addon_name"] = new { type = "string", @enum = NPC_RESULT_ADDONS },
                ["expected_text"] = new { type = "string", minLength = 1, maxLength = 256 },
                ["approach"] = flag, ["menu_label"] = text, ["result_addon"] = text, ["result_text"] = text,
                ["timeout_ms"] = new { type = "integer", minimum = 250, maximum = 20000 },
            }, ["game_object_id", "expected_base_id", "expected_territory_id", "addon_name"], ToolAccess.Action),
            Define("move_to_entity", "移动到对象", "通过 vnavmesh 寻路移动到指定对象，不模拟按键。", new() { ["game_object_id"] = text, ["allow_flight"] = flag }, ["game_object_id"], ToolAccess.Action),
            Define("move_to_nearby_interactable", "移动到附近对象", "按名称找到最近可交互对象，通过 vnavmesh 移动。", new(nearby) { ["allow_flight"] = flag }, ["name_contains"], ToolAccess.Action),
            Define("teleport_to_aetheryte", "传送水晶", "按当前语言名称或 ID 传送已解锁的大水晶；城内以太网复用 Omni，需开启“更好的传送管理”。Omni 返回成功表示请求已接受，到达情况请查询区域和坐标。", new() { ["query"] = text }, ["query"], ToolAccess.Action),
            Define("use_duty_action", "使用任务技能", "使用当前副本任务技能的第 1 或 2 个槽位。", new() { ["slot"] = new { type = "integer", minimum = 1, maximum = 2 } }, ["slot"], ToolAccess.Action),
            Define("send_addon_input", "窗口输入", "向白名单窗口发送游戏内输入。", new(addon) { ["input_type"] = new { type = "string", @enum = new[] { "gamepad", "dpad", "back" } }, ["input_id"] = new { type = "integer", minimum = 0, maximum = 65535 }, ["auxiliary_state"] = flag, ["input_state"] = new { type = "string", @enum = new[] { "down", "up", "held" } } }, ["addon_name", "input_type", "input_id"], ToolAccess.Action),
            Define("send_addon_event", "窗口事件", "向白名单窗口节点发送事件，可按唯一标签或组件路径定位，并等待指定可见性或复选框勾选状态。", new(nodeSelector)
            {
                ["addon_name"] = text, ["event_type"] = text, ["event_param"] = new { type = "integer" },
                ["collision_index"] = new { type = "integer", minimum = 0, maximum = 4095 },
                ["observe"] = new
                {
                    type = "object", additionalProperties = false,
                    properties = new Dictionary<string, object>(nodeSelector) { ["addon_name"] = text, ["visible"] = flag, ["checked"] = flag },
                },
                ["observe_timeout_ms"] = new { type = "integer", minimum = 250, maximum = 5000, @default = 2000 },
            }, ["addon_name", "event_type"], ToolAccess.Action),
            Define("send_addon_callback_values", "窗口回调", "向白名单窗口发送 1 至 32 个整数回调参数。", new(addon) { ["values"] = new { type = "array", minItems = 1, maxItems = 32, items = new { type = "integer" } } }, ["addon_name", "values"], ToolAccess.Action),
            Define("select_addon_menu_item", "选择菜单", "按唯一文本选择 SelectString、SelectIconString 或 TelepotTown 条目。", new(addon) { ["label"] = text, ["contains_match"] = flag }, ["addon_name", "label"], ToolAccess.Action),
            Define("inspect_menu_choice", "核对菜单选项", "只读匹配已显示的菜单条目，返回选项索引、文本和歧义，不派发选择。", new(addon) { ["label"] = text, ["contains_match"] = flag }, ["addon_name", "label"]),
            Define("observe_addon_events", "限时观察指定窗口的事件", "观察窗口接收事件之前或之后的类型和参数；可筛选事件明确携带的节点。不触发输入。", new()
            {
                ["addon_name"] = new { type = "string", minLength = 1, maxLength = 32, pattern = "^[A-Za-z0-9_]+$" },
                ["duration_ms"] = new { type = "integer", minimum = 250, maximum = 10000 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 128 },
                ["phase"] = new { type = "string", @enum = new[] { "before", "after" } },
                ["include_node"] = flag, ["node_id"] = nodeID,
            }, ["addon_name"]),
            Define("observe_addon_callbacks", "观察指定窗口的回调结果", "核对窗口 ID 后限时记录回调参数和返回值；文本默认关闭。不触发任何回调。", new()
            {
                ["addon_name"] = new { type = "string", minLength = 1, maxLength = 32, pattern = "^[A-Za-z0-9_]+$" },
                ["expected_addon_id"] = new { type = "integer", minimum = 1, maximum = ushort.MaxValue },
                ["duration_ms"] = new { type = "integer", minimum = 250, maximum = 10000 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 64 }, ["include_text"] = flag,
            }, ["addon_name", "expected_addon_id"], ToolAccess.Unsafe),
            Define("observe_addon_node_listener", "观察单个节点的已注册监听器", "按节点与已注册的事件定位一个监听器，限时记录匹配事件，可显式读取 SDK 定义的原始事件结构。", new(nodeSelector)
            {
                ["include_raw_event"] = flag,
                ["addon_name"] = new { type = "string", minLength = 1, maxLength = 32, pattern = "^[A-Za-z0-9_]+$" },
                ["expected_addon_id"] = new { type = "integer", minimum = 1, maximum = ushort.MaxValue },
                ["event_type"] = new { type = "integer", minimum = 0, maximum = 255 }, ["event_param"] = nodeID,
                ["duration_ms"] = new { type = "integer", minimum = 250, maximum = 10000 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 64 },
            }, ["addon_name", "expected_addon_id"], ToolAccess.Unsafe),
            Define("unsafe_invoke_plugin_ipc", "调用任意 IPC", "按明确参数类型调用任意已注册插件 IPC。", unsafeIPC, ["callgate", "result_kind"], ToolAccess.Unsafe),
            Define("reload_plugin", "重载开发插件", "重载指定开发插件；禁止重载本插件。", new() { ["plugin_name"] = text }, ["plugin_name"], ToolAccess.Unsafe),
            Define("slash_command", "执行插件命令", "执行已注册的 Dalamud 斜杠命令，不向服务器发送聊天命令。", new() { ["command"] = new { type = "string", minLength = 1, maxLength = 256 } }, ["command"], ToolAccess.Unsafe),
            Define("invoke_plugin_ipc", "调用插件 MCP 接口", "调用 PluginName.MCP.Method 插件约定 IPC。", new() { ["plugin_name"] = text, ["method"] = text, ["arguments_json"] = text }, ["plugin_name", "method"], ToolAccess.Unsafe),
            Define("plugin_data_subscribe", "订阅插件数据", "创建有界回传通道，最多 32 个通道，每通道 1000 条。", new() { ["plugin_name"] = text, ["channel"] = text }, ["plugin_name", "channel"], ToolAccess.Unsafe),
            Define("plugin_data_unsubscribe", "取消数据订阅", "注销插件数据回传通道。", new() { ["channel"] = text }, ["channel"], ToolAccess.Unsafe),
            Define("plugin_data_poll", "读取订阅数据", "取出并消费通道中排队的数据。", new() { ["channel"] = text, ["max_items"] = count }, ["channel"], ToolAccess.Unsafe),
        ];
    }

    private static ToolDefinition Define(string name, string title, string description, Dictionary<string, object>? properties = null, string[]? required = null, ToolAccess access = ToolAccess.Read)
        => new(name, title, description, ToolJson.Element(new { type = "object", properties = properties ?? [], required = required ?? [], additionalProperties = false }), access);
}
