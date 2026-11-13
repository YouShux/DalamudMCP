using DalamudMCP.Shared;

namespace DalamudMCP.Plugin;

internal static class ToolPurposes
{
    internal enum Purpose
    {
        All,
        Daily,
        Travel,
        Interface,
        Combat,
        Development,
        Diagnostics,
        Other,
    }

    internal static readonly string[] Labels =
        ["全部用途", "日常查询", "任务与导航", "界面与输入", "战斗与机制", "插件开发", "底层诊断", "其他工具"];

    internal static Purpose Get(ToolDefinition tool) =>
        tool.Source != "game" ? Purpose.Diagnostics : tool.Name switch
        {
            "get_session_status" or "poll_client_changes" or "get_chat_log" or "get_player_context" or
            "inspect_character_stats" or "inspect_job_progress" or "inspect_character_controls" or
            "request_character_control" or "inspect_party_roster" or "inspect_companion_roster" or
            "inspect_nearby_battle_actors" or "inspect_target_slots" or "inspect_actor_effects" or
            "get_inventory_summary" or "locate_inventory_item" or "inspect_equipped_items" or
            "inspect_saved_loadouts" or "apply_saved_loadout" or "inspect_currency_balances" or
            "submit_game_chat_line" => Purpose.Daily,

            "get_quest_status" or "get_available_quests" or "get_current_quest_objective" or "get_fate_context" or
            "inspect_area_map" or "inspect_society_dailies" or "inspect_character_unlock" or "get_duty_context" or
            "get_nearby_interactables" or "target_object" or "set_focus_actor" or "interact_with_target" or
            "request_npc_interaction" or "inspect_unlocked_travel" or "teleport_to_aetheryte" or
            "wait_game_arrival" or "move_to_entity" or "move_to_nearby_interactable" or "inspect_navigation_path" or
            "route_to_coordinate" or "stop_navigation_path" => Purpose.Travel,

            "get_addon_list" or "get_addon_strings" or "get_addon_tree" or "get_addon_nodes" or
            "inspect_addon_node" or "sample_addon_node_frames" or "read_addon_control" or "watch_addon_state" or
            "inspect_menu_choice" or "inspect_addon_list_row" or "request_known_addon" or "select_addon_menu_item" or
            "edit_addon_control" or "adjust_dropdown_panel" or "request_addon_list_row" or "activate_addon_row" or
            "request_addon_button" or "dispatch_when_node_ready" or "send_addon_input" or "send_addon_event" or
            "send_addon_callback_values" or "capture_game_screenshot" or "inspect_game_window" or
            "request_game_window_state" or "request_foreground_game_key" or "get_window_snapshot" or
            "wait_for_window_visibility" or "probe_imgui_point" or "sample_imgui_hit_points" or "queue_imgui_text" or
            "queue_imgui_gesture" or "queue_window_chord" or "drag_window_control" or "hold_window_key" or
            "get_window_input_queue" or "cancel_window_input" or "queue_input_sequence" or "get_input_sequence" or
            "cancel_input_sequence" or "inspect_duty_browser" or "list_duty_entries" or "request_duty_selection" or
            "inspect_open_bag_menu" or "request_bag_slot_menu" => Purpose.Interface,

            "inspect_job_meter" or "inspect_action_recasts" or "inspect_action_readiness" or
            "list_current_job_actions" or "request_checked_action" or "inspect_hotbar_slot" or
            "list_hotbar_recharges" or "request_hotbar_slot" or "sample_combat_window" or "archive_combat_window" or
            "set_combat_recorder" or "set_combat_dispatch_recorder" or "configure_combat_history" or
            "get_combat_recorder" or "wait_combat_recording" or "read_combat_capture" or "read_combat_timeline" or
            "read_combat_activity" or "read_combat_mechanics" or "read_combat_acr_decisions" or
            "analyze_combat_capture" or "summarize_combat_capture" or "export_combat_capture" or
            "create_battle_checkpoint" or "wait_battle_result" or "inspect_combat_mechanics" or
            "solve_mechanic_grid" or "solve_mechanic_assignments" or "plan_mechanic_route" or
            "evaluate_mechanic_knockback" or "replay_combat_mechanics" or
            "use_duty_action" => Purpose.Combat,

            "get_dalamud_log" or "lookup_game_rows" or "browse_client_sheets" or "describe_client_sheet" or
            "read_client_sheet_cells" or "list_sheet_properties" or "read_sheet_record" or "find_sheet_text" or
            "list_plugins" or "list_plugin_commands" or "list_plugin_ipc" or "get_plugin_state" or
            "show_plugin_window" or "get_dev_build" or "wait_for_dev_build" or "load_dev_dll" or "reload_plugin" or
            "read_development_workflow" or "find_repository_plugins" or "inspect_repository_sources" or
            "change_repository_source" or "install_repository_plugin" or "remove_repository_plugin" or
            "get_repository_job" or "slash_command" or "list_saved_plugin_endpoints" or
            "edit_saved_plugin_endpoint" or "call_saved_plugin_endpoint" or "unsafe_invoke_plugin_ipc" or
            "invoke_plugin_ipc" or "read_plugin_messages" or "plugin_data_subscribe" or "plugin_data_poll" or
            "plugin_data_unsubscribe" or "observe_addon_events" or "observe_addon_callbacks" or
            "observe_addon_node_listener" or "observe_ui_callbacks" => Purpose.Development,

            "inspect_runtime_health" or "read_runtime_calls" or "inspect_runtime_catalog" or
            "start_runtime_observation" or "read_runtime_observation" or "stop_runtime_observation" or
            "watch_runtime_value" or "access_runtime_object" or "invoke_sdk_member" or "access_sdk_memory" or
            "search_memory_values" or "read_process_memory" or "write_process_memory" or "inspect_address_regions" or
            "follow_memory_offsets" or "inspect_process_module" or "scan_process_memory" or
            "inspect_client_connection" or "sample_client_connection" or "inspect_zone_traffic_support" or
            "set_zone_traffic_trace" or "read_zone_traffic_trace" or "read_zone_packet_bytes" or
            "read_zone_packet_slice" or "set_zone_callback_capture" or "read_zone_callback_capture" or
            "inspect_opcode_catalog" or "inspect_opcode_refresh" or "check_cn_opcode_catalog" or
            "find_cn_zone_opcode" or "refresh_opcode_catalog" or "inspect_packet_layouts" or
            "inspect_packet_aliases" or "inspect_protocol_updates" or "check_packet_sources" or
            "save_packet_layout" or "parse_zone_packet" or "decode_zone_packet" or "compare_zone_packets" or
            "decode_ipc_sample" or "compare_ipc_samples" => Purpose.Diagnostics,
            _ => Purpose.Other,
        };
}
