#pragma once

#include "movie_frame_resize_policy.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace deadlock_mvm {

constexpr std::uint32_t kProtocolMagic = 0x4D564D43; // "CMVM" little-endian
constexpr std::uint16_t kProtocolVersion = 24;
constexpr std::size_t kMaxCampathKeyframes = 128;
constexpr double kMinFov = 5.0;
constexpr double kMaxFov = 170.0;
constexpr double kMaxWorldCoordinate = 200000.0;

enum class MessageType : std::uint16_t {
    hello = 1,
    heartbeat = 2,
    enable_override = 3,
    disable_override = 4,
    set_camera_sample = 5,
    get_status = 6,
    shutdown = 7,
    set_campath = 8,
    clear_campath = 9,
    prepare_camera_observation = 10,
    update_smvm_snapshot = 11,
    set_editor_campath = 12,
    clear_editor_campath = 13,
    set_roll_override = 14,
    enable_manual_camera = 15,
    disable_manual_camera = 16,
    set_campath_documents = 17,
    status = 100,
};

enum class BackendState : std::uint32_t {
    unavailable = 0,
    loading = 1,
    connected = 2,
    ready = 3,
    failed = 4,
};

enum class ErrorCode : std::uint32_t {
    none = 0,
    wrong_process = 1,
    replay_launch_required = 2,
    client_module_missing = 3,
    signature_missing = 4,
    signature_ambiguous = 5,
    camera_unavailable = 6,
    hook_target_mismatch = 7,
    hook_install_failed = 8,
    protocol_error = 9,
    replay_gate_closed = 10,
    invalid_sample = 11,
    observer_not_roaming = 12,
    heartbeat_stale = 13,
    hook_runtime_invalid = 14,
    replay_clock_unavailable = 15,
};

enum StatusFlags : std::uint32_t {
    status_resolved = 1u << 0,
    status_hook_installed = 1u << 1,
    status_pipe_connected = 1u << 2,
    status_replay_gate = 1u << 3,
    status_override_requested = 1u << 4,
    status_override_active = 1u << 5,
    status_has_sample = 1u << 6,
    status_command_line_replay = 1u << 7,
    status_campath_active = 1u << 8,
    status_camera_observed = 1u << 9,
    status_campath_completed = 1u << 10,
    status_roll_override_active = 1u << 11,
    status_manual_camera_requested = 1u << 12,
    status_manual_camera_active = 1u << 13,
};

enum class CameraAvailability : std::uint32_t {
    initializing = 0,
    ready = 1,
    replay_unavailable = 2,
    replay_seeking = 3,
    not_in_free_roam = 4,
    observer_target_active = 5,
    camera_manager_unavailable = 6,
    camera_object_unavailable = 7,
    camera_readback_unavailable = 8,
    native_backend_disconnected = 9,
    signature_unavailable = 10,
    managed_host_disconnected = 11,
    ownership_held_by_campath = 12,
    ownership_rejected = 13,
    snapshot_stale = 14,
    protocol_mismatch = 15,
};

enum class CameraOwnership : std::uint32_t {
    none = 0,
    deadlock_spectator = 1,
    smvm_manual_camera = 2,
    smvm_restore = 3,
    smvm_campath = 4,
};

enum SmvmCapabilities : std::uint32_t {
    smvm_capability_manual_camera = 1u << 0,
    smvm_capability_rendered_roll = 1u << 1,
    smvm_capability_path_visualization = 1u << 2,
    smvm_capability_camera_self_test = 1u << 3,
    smvm_capability_campath_self_test = 1u << 4,
};

enum class DeadlockUiMode : std::uint32_t {
    deadlock_ui = 0,
    smvm_replay_ui = 1,
    clean_footage = 2,
    death_notices_only = 3,
};

enum DeadlockUiCapabilities : std::uint32_t {
    deadlock_ui_capability_hide_panorama = 1u << 0,
    deadlock_ui_capability_restore_panorama = 1u << 1,
    deadlock_ui_capability_smvm_replay_ui = 1u << 2,
    deadlock_ui_capability_clean_footage = 1u << 3,
};

enum class DeadlockUiError : std::uint32_t {
    none = 0,
    replay_unavailable = 1,
    command_channel_unavailable = 2,
    unsupported_mode = 3,
    apply_failed = 4,
    restore_failed = 5,
};

enum class SmvmReplayBarAnchor : std::uint32_t {
    bottom = 0,
    top = 1,
};

enum class SmvmMenuAnchor : std::uint32_t {
    left = 0,
    right = 1,
};

enum class SmvmNotificationAnchor : std::uint32_t {
    top_left = 0,
    top_right = 1,
    bottom_left = 2,
    bottom_right = 3,
};

enum class CampathInterpolation : std::uint32_t {
    linear = 0,
    smooth = 1,
};

enum class CampathEasing : std::uint32_t {
    linear = 0,
    ease_in = 1,
    ease_out = 2,
    ease_in_out = 3,
};

enum class CampathEndBehavior : std::uint32_t {
    stop_and_release = 0,
    hold_final_camera = 1,
    loop = 2,
};

enum class SmvmRendererBackend : std::uint32_t {
    none = 0,
    d3d11 = 1,
    unsupported = 2,
};

enum class SmvmRendererError : std::uint32_t {
    none = 0,
    renderer_not_loaded = 1,
    unsupported_renderer = 2,
    swapchain_probe_failed = 3,
    hook_install_failed = 4,
    present_not_observed = 5,
    device_unavailable = 6,
    resource_creation_failed = 7,
    window_hook_failed = 8,
    device_reset = 9,
};

enum SmvmOverlayFlags : std::uint32_t {
    smvm_overlay_hook_installed = 1u << 0,
    smvm_overlay_present_observed = 1u << 1,
    smvm_overlay_ready = 1u << 2,
    smvm_overlay_menu_open = 1u << 3,
    smvm_overlay_clean_view = 1u << 4,
    smvm_overlay_manual_pointer_active = 1u << 5,
    smvm_overlay_manual_mouse_observed = 1u << 6,
    smvm_overlay_manual_pointer_requested = 1u << 7,
    smvm_overlay_keyboard_ready = 1u << 8,
    smvm_overlay_relative_mouse_ready = 1u << 9,
    smvm_overlay_raw_input_ready = 1u << 10,
    smvm_overlay_cursor_ready = 1u << 11,
    smvm_overlay_foreground_ready = 1u << 12,
    smvm_overlay_window_procedure_ready = 1u << 13,
    smvm_overlay_engine_input_ready = 1u << 14,
    smvm_overlay_fallback_mouse_observed = 1u << 15,
    smvm_overlay_creep_healthbar_hook_installed = 1u << 16,
    smvm_overlay_creep_healthbar_suppression_observed = 1u << 17,
    smvm_overlay_tower_outline_hooks_installed = 1u << 18,
    smvm_overlay_tower_outline_suppression_observed = 1u << 19,
    smvm_overlay_tower_fade_override_installed = 1u << 20,
    smvm_overlay_tower_fade_override_enforced = 1u << 21,
    smvm_overlay_creep_healthbar_hook_retrying = 1u << 22,
    smvm_overlay_tower_outline_hooks_retrying = 1u << 23,
    smvm_overlay_tower_fade_override_retrying = 1u << 24,
    smvm_overlay_movie_recording_active = 1u << 25,
    smvm_overlay_world_depth_available = 1u << 26,
    smvm_overlay_world_depth_failed = 1u << 27,
    smvm_overlay_movie_avi_available = 1u << 28,
    smvm_overlay_world_depth_incomplete = 1u << 29,
    smvm_overlay_movie_frame_ready = 1u << 30,
};

enum SmvmSnapshotFlags : std::uint32_t {
    smvm_snapshot_replay_active = 1u << 0,
    smvm_snapshot_pause_known = 1u << 1,
    smvm_snapshot_paused = 1u << 2,
    smvm_snapshot_camera_readable = 1u << 3,
    smvm_snapshot_fov_writable = 1u << 4,
    smvm_snapshot_roll_writable = 1u << 5,
    smvm_snapshot_campath_playing = 1u << 6,
    smvm_snapshot_camera_owned = 1u << 7,
    smvm_snapshot_editor_path = 1u << 8,
    smvm_snapshot_internal_enabled = 1u << 9,
    smvm_snapshot_show_toolbar = 1u << 10,
    smvm_snapshot_show_path = 1u << 11,
    smvm_snapshot_show_cameras = 1u << 12,
    smvm_snapshot_show_labels = 1u << 13,
    smvm_snapshot_fov_inverted = 1u << 14,
    smvm_snapshot_manual_camera_requested = 1u << 15,
    smvm_snapshot_manual_camera_active = 1u << 16,
    smvm_snapshot_input_takeover = 1u << 17,
    smvm_snapshot_invert_y = 1u << 18,
    smvm_snapshot_show_minimal_pill = 1u << 19,
    smvm_snapshot_notifications = 1u << 20,
    smvm_snapshot_hide_path_while_playing = 1u << 21,
    smvm_snapshot_campath_unsaved = 1u << 22,
    smvm_snapshot_campath_recovery_available = 1u << 23,
    smvm_snapshot_restore_workspace = 1u << 24,
    smvm_snapshot_capture_diagnostics = 1u << 25,
    smvm_snapshot_show_status_hud = 1u << 26,
    smvm_snapshot_replay_seek_in_progress = 1u << 27,
    smvm_snapshot_recording_profile_restore_pending = 1u << 28,
    smvm_snapshot_recording_profile_transaction_in_progress = 1u << 29,
};

enum MovieRecordingFlags : std::uint32_t {
    movie_recording_none = 0,
    movie_recording_disable_post_processing = 1u << 0,
    movie_recording_mute_dialogue = 1u << 1,
    movie_recording_world_depth = 1u << 3,
    movie_recording_active = 1u << 4,
    movie_recording_armed = 1u << 5,
    movie_recording_finalizing = 1u << 6,
};

constexpr std::uint32_t kKnownMovieRecordingFlags =
    movie_recording_disable_post_processing |
    movie_recording_mute_dialogue |
    movie_recording_world_depth |
    movie_recording_active |
    movie_recording_armed |
    movie_recording_finalizing;

enum class MovieRecordingOption : std::int32_t {
    disable_post_processing = 0,
    mute_dialogue = 1,
};

enum class MovieRecordingPreset : std::uint32_t {
    edit_sequence = 0,
    fast_avi = 1,
    compositing = 2,
    custom = 3,
    greenscreen = 4,
};

enum class MovieOutputMode : std::uint32_t {
    image_sequence = 0,
    avi = 1,
    both = 2,
};

enum class MovieCompositingStage : std::uint32_t {
    none = 0,
    world = 1,
    chroma = 2,
};

enum MovieCapturePassFlags : std::uint32_t {
    movie_capture_pass_none = 0,
    movie_capture_pass_beauty = 1u << 0,
    movie_capture_pass_world_depth_pfm = 1u << 1,
    movie_capture_pass_world_depth_avi = 1u << 2,
    movie_capture_pass_greenscreen_free_camera = 1u << 3,
};

constexpr std::uint32_t kKnownMovieCapturePassFlags =
    movie_capture_pass_beauty |
    movie_capture_pass_world_depth_pfm |
    movie_capture_pass_world_depth_avi |
    movie_capture_pass_greenscreen_free_camera;

enum MovieToolFlags : std::uint32_t {
    movie_tool_none = 0,
    movie_tool_rule_of_thirds = 1u << 0,
    movie_tool_custom_fog = 1u << 1,
};

constexpr std::uint32_t kKnownMovieToolFlags =
    movie_tool_rule_of_thirds | movie_tool_custom_fog;

enum class GreenscreenMode : std::uint32_t {
    off = 0,
    free_camera = 1,
};

enum class SmvmCampathSession : std::uint32_t {
    no_path = 0,
    draft_path = 1,
    saved_path = 2,
};

enum class SmvmInputCode : std::uint32_t {
    none = 0,
    mouse_middle = 0x1001,
    mouse_x1 = 0x1002,
    mouse_x2 = 0x1003,
    wheel_up = 0x1004,
    wheel_down = 0x1005,
};

enum class SmvmCaptureStage : std::uint32_t {
    none = 0,
    input_observed = 1,
    binding_matched = 2,
    capture_requested = 3,
    native_frame_awaited = 4,
    native_frame_captured = 5,
    managed_action_returned = 6,
    draft_created = 7,
    keyframe_added = 8,
    capture_rejected = 9,
};

enum class SmvmCaptureRejection : std::uint32_t {
    none = 0,
    replay_unavailable = 1,
    not_in_free_roam = 2,
    camera_unreadable = 3,
    native_backend_unavailable = 4,
    campath_owns_camera = 5,
    snapshot_stale = 6,
    hook_frame_stale = 7,
    capture_already_pending = 8,
    connection_epoch_changed = 9,
    invalid_sample = 10,
    unknown = 11,
};

struct SmvmCaptureAvailability final {
    bool native_backend_available;
    bool replay_available;
    bool free_roam;
    bool manual_camera_requested;
    bool manual_camera_active;
    bool snapshot_fresh;
    bool camera_readable;
    bool hook_frame_fresh;
    bool campath_owns_camera;
    bool capture_pending;
};

[[nodiscard]] constexpr SmvmCaptureRejection CaptureRejectionFor(
    const SmvmCaptureAvailability& availability) noexcept {
    if (!availability.native_backend_available)
        return SmvmCaptureRejection::native_backend_unavailable;
    if (!availability.replay_available)
        return SmvmCaptureRejection::replay_unavailable;
    if (!availability.free_roam)
        return SmvmCaptureRejection::not_in_free_roam;
    if (!availability.manual_camera_requested || !availability.manual_camera_active)
        return SmvmCaptureRejection::not_in_free_roam;
    if (!availability.snapshot_fresh)
        return SmvmCaptureRejection::snapshot_stale;
    if (!availability.camera_readable)
        return SmvmCaptureRejection::camera_unreadable;
    if (!availability.hook_frame_fresh)
        return SmvmCaptureRejection::hook_frame_stale;
    if (availability.campath_owns_camera)
        return SmvmCaptureRejection::campath_owns_camera;
    if (availability.capture_pending)
        return SmvmCaptureRejection::capture_already_pending;
    return SmvmCaptureRejection::none;
}

constexpr std::uint32_t kSmvmInputBaseMask = 0x0000FFFFu;
constexpr std::uint32_t kSmvmInputModifierMask = 0x000F0000u;

enum class SmvmActionType : std::uint32_t {
    none = 0,
    toggle_replay_pause = 1,
    set_timescale = 2,
    seek_tick = 3,
    step_back = 4,
    step_forward = 5,
    free_roam = 6,
    previous_player = 7,
    next_player = 8,
    in_eye = 9,
    chase = 10,
    set_fov = 11,
    set_roll = 12,
    save_camera = 13,
    restore_camera = 14,
    add_keyframe = 15,
    delete_keyframe = 16,
    select_keyframe = 17,
    go_to_keyframe = 18,
    update_keyframe = 19,
    clear_path = 20,
    set_interpolation = 21,
    set_easing = 22,
    play_from_start = 23,
    play_from_current = 24,
    stop_campath = 25,
    set_end_behavior = 26,
    undo_edit = 27,
    redo_edit = 28,
    toggle_toolbar = 29,
    toggle_show_path = 30,
    toggle_show_cameras = 31,
    toggle_show_labels = 32,
    set_binding = 33,
    set_path_name = 34,
    save_path = 35,
    load_next_path = 36,
    toggle_manual_camera = 37,
    reacquire_camera = 38,
    camera_self_test = 39,
    campath_self_test = 40,
    toggle_notifications = 41,
    toggle_input_takeover = 42,
    set_ui_scale = 43,
    set_menu_opacity = 44,
    set_menu_anchor = 45,
    set_movement_speed = 46,
    set_mouse_sensitivity = 47,
    set_smoothing = 48,
    toggle_invert_y = 49,
    reset_bindings = 50,
    toggle_minimal_pill = 51,
    toggle_hide_path_while_playing = 52,
    new_path = 53,
    save_path_as = 54,
    load_path = 55,
    close_path = 56,
    recover_draft = 57,
    discard_draft = 58,
    request_path_list = 59,
    toggle_restore_workspace = 60,
    set_path_label_scale = 61,
    set_notification_anchor = 62,
    capture_diagnostic = 63,
    set_deadlock_ui_mode = 64,
    restore_deadlock_ui = 65,
    set_replay_bar_scale = 66,
    set_replay_bar_opacity = 67,
    set_replay_bar_anchor = 68,
    cycle_replay_interface = 69,
    apply_movie_maker_defaults = 70,
    toggle_status_hud = 71,
    set_status_hud_anchor = 72,
    set_status_hud_scale = 73,
    set_status_hud_opacity = 74,
    set_movie_recording_option = 75,
    start_movie_recording = 76,
    stop_movie_recording = 77,
    set_movie_recording_fps = 78,
    set_movie_recording_preset = 79,
    set_movie_output_mode = 80,
    set_movie_capture_pass = 81,
    open_movie_capture_folder = 82,
    set_rule_of_thirds = 83,
    set_custom_fog_enabled = 84,
    set_custom_fog_value = 85,
    set_custom_fog_color = 86,
    reset_custom_fog = 87,
    set_greenscreen_mode = 88,
    retired_camera_attachment = 89,
    retired_camera_attachment_offset = 90,
    choose_movie_capture_folder = 91,
    set_movie_output_resolution = 92,
};

#pragma pack(push, 1)
struct MessageHeader final {
    std::uint32_t magic;
    std::uint16_t version;
    MessageType type;
    std::uint32_t payload_size;
    std::uint64_t sequence;
    std::uint64_t replay_session_generation;
};

struct HelloPayload final {
    std::uint32_t expected_process_id;
    std::uint32_t client_build;
};

struct HeartbeatPayload final {
    std::uint8_t replay_active;
    std::uint8_t free_roam;
    std::array<std::uint8_t, 6> reserved;
    std::int64_t replay_tick;
    std::int64_t game_tick_offset;
};

struct CameraSample final {
    double x;
    double y;
    double z;
    double pitch;
    double yaw;
    double roll;
    double fov;
};

struct RollPayload final {
    double roll;
};

struct CampathKeyframe final {
    std::int64_t demo_tick;
    CameraSample camera;
};

struct CampathPayloadHeader final {
    std::uint32_t keyframe_count;
    CampathInterpolation interpolation;
    CampathEasing easing;
    CampathEndBehavior end_behavior;
};

struct SmvmSnapshotPayload final {
    std::uint32_t snapshot_version;
    std::uint32_t flags;
    std::int64_t current_tick;
    std::int64_t total_ticks;
    double timescale;
    CameraSample camera;
    std::uint32_t observer_mode;
    std::int32_t selected_keyframe;
    std::uint32_t keyframe_count;
    CampathInterpolation interpolation;
    CampathEasing easing;
    CampathEndBehavior end_behavior;
    std::uint32_t playback_state;
    std::uint32_t start_failure;
    std::uint32_t menu_key;
    std::uint32_t add_key;
    std::uint32_t delete_key;
    std::uint32_t clean_view_key;
    double fov_step;
    std::uint32_t roll_left_key;
    std::uint32_t roll_right_key;
    std::uint32_t roll_reset_key;
    CameraAvailability camera_availability;
    std::array<char, 64> replay_name;
    std::array<char, 64> path_name;
    std::array<char, 128> status;
    CameraOwnership camera_ownership;
    std::uint32_t capabilities;
    std::uint32_t forward_key;
    std::uint32_t backward_key;
    std::uint32_t left_key;
    std::uint32_t right_key;
    std::uint32_t up_key;
    std::uint32_t down_key;
    std::uint32_t fast_key;
    std::uint32_t precision_key;
    std::uint32_t play_start_key;
    std::uint32_t play_current_key;
    std::uint32_t stop_key;
    std::uint32_t undo_key;
    std::uint32_t redo_key;
    std::uint32_t show_path_key;
    std::uint32_t show_cameras_key;
    std::uint32_t show_labels_key;
    double movement_speed;
    double boost_multiplier;
    double precision_multiplier;
    double mouse_sensitivity;
    double smoothing;
    double ui_scale;
    double menu_opacity;
    double path_label_scale;
    SmvmMenuAnchor menu_anchor;
    SmvmNotificationAnchor notification_anchor;
    std::array<char, 80> camera_status;
    SmvmCampathSession campath_session;
    std::uint32_t saved_document_count;
    std::uint32_t restore_ui_key;
    DeadlockUiMode deadlock_ui_mode;
    std::uint32_t deadlock_ui_capabilities;
    DeadlockUiError deadlock_ui_error;
    std::uint32_t vconsole_port;
    double replay_bar_scale;
    double replay_bar_opacity;
    SmvmReplayBarAnchor replay_bar_anchor;
    std::uint32_t cycle_ui_key;
    std::uint32_t toggle_free_camera_key;
    std::uint32_t replay_pause_key;
    std::uint32_t step_back_key;
    std::uint32_t step_forward_key;
    SmvmNotificationAnchor status_hud_anchor;
    double status_hud_scale;
    double status_hud_opacity;
    std::uint64_t recording_profile_ack_generation;
    std::uint64_t replay_session_generation;
    std::uint32_t movie_recording_flags;
    std::array<char, 64> movie_recording_name;
    std::uint32_t movie_recording_reserved;
    std::uint32_t movie_recording_fps;
    MovieRecordingPreset movie_recording_preset;
    MovieOutputMode movie_output_mode;
    std::uint32_t movie_capture_pass_flags;
    std::uint32_t movie_tool_flags;
    GreenscreenMode greenscreen_mode;
    double custom_fog_start;
    double custom_fog_end;
    double custom_fog_max_density;
    double custom_fog_exponent;
    std::uint32_t custom_fog_color_rgb;
    std::uint32_t greenscreen_color_rgb;
    std::uint32_t retired_camera_attachment_enabled;
    std::uint32_t retired_camera_attachment_bone;
    double retired_camera_attachment_offset_x;
    double retired_camera_attachment_offset_y;
    double retired_camera_attachment_offset_z;
    std::array<char, 192> movie_capture_path;
    MovieOutputResolution movie_output_resolution;
    std::uint32_t movie_active_pass_flags;
    MovieCompositingStage movie_compositing_stage;
    std::uint32_t movie_capture_audio;
    std::uint64_t movie_expected_frame_count;
};

constexpr std::size_t kMaxCampathDocuments = 32;
constexpr std::uint32_t kCampathDocumentDraft = 1u << 0;
constexpr std::uint32_t kCampathDocumentMatchesReplay = 1u << 1;

#pragma pack(push, 1)
struct CampathDocumentEntry final {
    std::array<char, 64> name;
    std::array<char, 64> replay_name;
    std::uint32_t keyframe_count;
    std::int64_t modified_utc_ticks;
    std::uint32_t flags;
};

struct CampathDocumentsPayload final {
    std::uint32_t count;
    std::uint32_t reserved;
    std::array<CampathDocumentEntry, kMaxCampathDocuments> entries;
};
#pragma pack(pop)

struct SmvmActionPayload final {
    SmvmActionType type;
    std::int32_t index;
    std::int64_t tick;
    double value;
    CameraSample camera;
    std::array<char, 64> text;
    std::uint64_t replay_session_generation;
};

struct StatusPayload final {
    BackendState state;
    ErrorCode error;
    std::uint32_t flags;
    std::uint32_t process_id;
    std::uint64_t accepted_sequence;
    std::uint64_t applied_sequence;
    std::uint64_t hook_calls;
    std::int64_t replay_tick;
    CameraSample camera;
    SmvmRendererBackend renderer_backend;
    SmvmRendererError renderer_error;
    std::uint32_t overlay_flags;
    std::uint32_t overlay_frame_microseconds;
    SmvmActionPayload action;
};
#pragma pack(pop)

static_assert(sizeof(MessageHeader) == 28);
static_assert(sizeof(HeartbeatPayload) == 24);
static_assert(sizeof(CameraSample) == 56);
static_assert(sizeof(RollPayload) == 8);
static_assert(sizeof(CampathKeyframe) == 64);
static_assert(sizeof(CampathPayloadHeader) == 16);
static_assert(sizeof(SmvmSnapshotPayload) == 1128);
static_assert(sizeof(SmvmActionPayload) == 152);
static_assert(sizeof(StatusPayload) == 272);
static_assert(sizeof(CampathDocumentEntry) == 144);

constexpr std::size_t kMaxMessageBytes =
    sizeof(CampathPayloadHeader) + (sizeof(CampathKeyframe) * kMaxCampathKeyframes);

[[nodiscard]] inline bool IsKnownMessageType(const MessageType type) noexcept {
    switch (type) {
        case MessageType::hello:
        case MessageType::heartbeat:
        case MessageType::enable_override:
        case MessageType::disable_override:
        case MessageType::set_camera_sample:
        case MessageType::get_status:
        case MessageType::shutdown:
        case MessageType::set_campath:
        case MessageType::clear_campath:
        case MessageType::prepare_camera_observation:
        case MessageType::update_smvm_snapshot:
        case MessageType::set_editor_campath:
        case MessageType::clear_editor_campath:
        case MessageType::set_roll_override:
        case MessageType::enable_manual_camera:
        case MessageType::disable_manual_camera:
        case MessageType::set_campath_documents:
        case MessageType::status:
            return true;
    }
    return false;
}

[[nodiscard]] inline bool ValidateHeader(const MessageHeader& header) noexcept {
    return header.magic == kProtocolMagic &&
           header.version == kProtocolVersion &&
           IsKnownMessageType(header.type) &&
           header.payload_size <= kMaxMessageBytes &&
           header.replay_session_generation <= 0x7FFFFFFFFFFFFFFFULL;
}

[[nodiscard]] constexpr bool IsReplayScopedCameraRequest(const MessageType type) noexcept {
    switch (type) {
        case MessageType::enable_override:
        case MessageType::disable_override:
        case MessageType::set_camera_sample:
        case MessageType::set_campath:
        case MessageType::clear_campath:
        case MessageType::prepare_camera_observation:
        case MessageType::set_editor_campath:
        case MessageType::clear_editor_campath:
        case MessageType::set_roll_override:
        case MessageType::enable_manual_camera:
        case MessageType::disable_manual_camera:
        case MessageType::set_campath_documents:
            return true;
        default:
            return false;
    }
}

[[nodiscard]] constexpr bool IsReplaySessionRequestCurrent(
    const MessageType type,
    const std::uint64_t request_replay_session_generation,
    const bool snapshot_available,
    const std::uint64_t snapshot_replay_session_generation) noexcept {
    return !IsReplayScopedCameraRequest(type) ||
           request_replay_session_generation == 0 ||
           (snapshot_available && snapshot_replay_session_generation != 0 &&
            request_replay_session_generation == snapshot_replay_session_generation);
}

[[nodiscard]] inline bool ValidateSample(const CameraSample& sample) noexcept {
    const auto finite = std::isfinite(sample.x) && std::isfinite(sample.y) &&
                        std::isfinite(sample.z) && std::isfinite(sample.pitch) &&
                        std::isfinite(sample.yaw) && std::isfinite(sample.roll) &&
                        std::isfinite(sample.fov);
    return finite &&
           std::abs(sample.x) <= kMaxWorldCoordinate &&
           std::abs(sample.y) <= kMaxWorldCoordinate &&
           std::abs(sample.z) <= kMaxWorldCoordinate &&
           sample.pitch >= -89.0 && sample.pitch <= 89.0 &&
           sample.yaw >= -360.0 && sample.yaw <= 360.0 &&
           sample.roll >= -180.0 && sample.roll <= 180.0 &&
           sample.fov >= kMinFov && sample.fov <= kMaxFov;
}

[[nodiscard]] inline bool ValidateRoll(const double roll) noexcept {
    return std::isfinite(roll) && roll >= -180.0 && roll <= 180.0;
}

[[nodiscard]] inline bool ValidateSmvmInput(const std::uint32_t input) noexcept {
    if ((input & ~(kSmvmInputBaseMask | kSmvmInputModifierMask)) != 0)
        return false;
    if (input == 0)
        return true;
    const auto base = input & kSmvmInputBaseMask;
    return (base > 0 && base <= 0xFF) ||
           base == static_cast<std::uint32_t>(SmvmInputCode::mouse_middle) ||
           base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x1) ||
           base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x2) ||
           base == static_cast<std::uint32_t>(SmvmInputCode::wheel_up) ||
           base == static_cast<std::uint32_t>(SmvmInputCode::wheel_down);
}

[[nodiscard]] inline bool ValidateSmvmKeyboardInput(const std::uint32_t input) noexcept {
    if (!ValidateSmvmInput(input) || input == 0)
        return input == 0;
    const auto base = input & kSmvmInputBaseMask;
    return base > 0 && base <= 0xFF;
}

[[nodiscard]] inline bool ValidateSmvmSnapshotPayload(const SmvmSnapshotPayload& snapshot) noexcept {
    constexpr std::uint32_t max_playback_state = 14; // CampathPlaybackState::cancelled
    constexpr std::uint32_t max_start_failure = 18; // CampathStartFailure::unexpected_failure
    constexpr auto known_flags = smvm_snapshot_replay_active | smvm_snapshot_pause_known |
        smvm_snapshot_paused | smvm_snapshot_camera_readable | smvm_snapshot_fov_writable |
        smvm_snapshot_roll_writable | smvm_snapshot_campath_playing | smvm_snapshot_camera_owned |
        smvm_snapshot_editor_path | smvm_snapshot_internal_enabled | smvm_snapshot_show_toolbar |
        smvm_snapshot_show_path | smvm_snapshot_show_cameras | smvm_snapshot_show_labels |
        smvm_snapshot_fov_inverted | smvm_snapshot_manual_camera_requested |
        smvm_snapshot_manual_camera_active | smvm_snapshot_input_takeover | smvm_snapshot_invert_y |
        smvm_snapshot_show_minimal_pill | smvm_snapshot_notifications |
        smvm_snapshot_hide_path_while_playing | smvm_snapshot_campath_unsaved |
        smvm_snapshot_campath_recovery_available | smvm_snapshot_restore_workspace |
        smvm_snapshot_capture_diagnostics | smvm_snapshot_show_status_hud |
        smvm_snapshot_replay_seek_in_progress |
        smvm_snapshot_recording_profile_restore_pending |
        smvm_snapshot_recording_profile_transaction_in_progress;
    constexpr auto known_capabilities = smvm_capability_manual_camera | smvm_capability_rendered_roll |
        smvm_capability_path_visualization | smvm_capability_camera_self_test |
        smvm_capability_campath_self_test;
    constexpr auto known_ui_capabilities = deadlock_ui_capability_hide_panorama |
        deadlock_ui_capability_restore_panorama | deadlock_ui_capability_smvm_replay_ui |
        deadlock_ui_capability_clean_footage;
    const auto recording_active =
        (snapshot.movie_recording_flags & movie_recording_active) != 0;
    const auto recording_armed =
        (snapshot.movie_recording_flags & movie_recording_armed) != 0;
    const auto recording_finalizing =
        (snapshot.movie_recording_flags & movie_recording_finalizing) != 0;
    const auto recording_name_valid =
        [&snapshot, recording_active, recording_armed, recording_finalizing]() noexcept {
        if (snapshot.movie_recording_name.back() != '\0')
            return false;
        const auto end = std::find(
            snapshot.movie_recording_name.begin(),
            snapshot.movie_recording_name.end(),
            '\0');
        if (static_cast<unsigned>(recording_active) +
                static_cast<unsigned>(recording_armed) +
                static_cast<unsigned>(recording_finalizing) > 1u)
            return false;
        if (!recording_active && !recording_armed && !recording_finalizing)
            return end == snapshot.movie_recording_name.begin();
        if (end == snapshot.movie_recording_name.begin())
            return false;
        return std::all_of(
            snapshot.movie_recording_name.begin(), end, [](const char value) noexcept {
                return (value >= 'a' && value <= 'z') ||
                       (value >= 'A' && value <= 'Z') ||
                       (value >= '0' && value <= '9') || value == '-' || value == '_';
            });
    }();
    if (snapshot.snapshot_version != 13 || (snapshot.flags & ~known_flags) != 0 ||
        (snapshot.movie_recording_flags & ~kKnownMovieRecordingFlags) != 0 ||
        !recording_name_valid ||
        snapshot.movie_recording_reserved != 0 ||
        snapshot.movie_recording_fps < 1 || snapshot.movie_recording_fps > 1000 ||
        snapshot.movie_recording_preset > MovieRecordingPreset::greenscreen ||
        snapshot.movie_output_mode > MovieOutputMode::both ||
        snapshot.movie_capture_pass_flags == movie_capture_pass_none ||
        (snapshot.movie_capture_pass_flags & ~kKnownMovieCapturePassFlags) != 0 ||
        snapshot.movie_active_pass_flags == movie_capture_pass_none ||
        (snapshot.movie_active_pass_flags & ~kKnownMovieCapturePassFlags) != 0 ||
        snapshot.movie_output_resolution > MovieOutputResolution::full_hd ||
        snapshot.movie_compositing_stage > MovieCompositingStage::chroma ||
        snapshot.movie_capture_audio > 1u ||
        snapshot.movie_expected_frame_count >
            static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()) ||
        ((snapshot.movie_compositing_stage == MovieCompositingStage::chroma) !=
         (snapshot.movie_expected_frame_count > 0)) ||
        (snapshot.movie_tool_flags & ~kKnownMovieToolFlags) != 0 ||
        snapshot.greenscreen_mode > GreenscreenMode::free_camera ||
        !std::isfinite(snapshot.custom_fog_start) ||
        snapshot.custom_fog_start < -100000.0 || snapshot.custom_fog_start > 100000.0 ||
        !std::isfinite(snapshot.custom_fog_end) ||
        snapshot.custom_fog_end < snapshot.custom_fog_start ||
        snapshot.custom_fog_end > 100000.0 ||
        !std::isfinite(snapshot.custom_fog_max_density) ||
        snapshot.custom_fog_max_density < 0.0 || snapshot.custom_fog_max_density > 1.0 ||
        !std::isfinite(snapshot.custom_fog_exponent) ||
        snapshot.custom_fog_exponent < 0.01 || snapshot.custom_fog_exponent > 10.0 ||
        (snapshot.custom_fog_color_rgb & 0xFF000000u) != 0 ||
        (snapshot.greenscreen_color_rgb & 0xFF000000u) != 0 ||
        snapshot.retired_camera_attachment_enabled != 0 ||
        snapshot.retired_camera_attachment_bone != 0 ||
        snapshot.retired_camera_attachment_offset_x != 0.0 ||
        snapshot.retired_camera_attachment_offset_y != 0.0 ||
        snapshot.retired_camera_attachment_offset_z != 0.0 ||
        (snapshot.capabilities & ~known_capabilities) != 0 ||
        snapshot.deadlock_ui_mode > DeadlockUiMode::death_notices_only ||
        snapshot.deadlock_ui_error > DeadlockUiError::restore_failed ||
        (snapshot.deadlock_ui_capabilities & ~known_ui_capabilities) != 0 ||
        snapshot.vconsole_port == 0 || snapshot.vconsole_port > 65535 ||
        !std::isfinite(snapshot.replay_bar_scale) ||
        snapshot.replay_bar_scale < 0.75 || snapshot.replay_bar_scale > 2.0 ||
        !std::isfinite(snapshot.replay_bar_opacity) ||
        snapshot.replay_bar_opacity < 0.35 || snapshot.replay_bar_opacity > 1.0 ||
        snapshot.replay_bar_anchor > SmvmReplayBarAnchor::top ||
        snapshot.status_hud_anchor > SmvmNotificationAnchor::bottom_right ||
        !std::isfinite(snapshot.status_hud_scale) ||
        snapshot.status_hud_scale < 0.75 || snapshot.status_hud_scale > 1.5 ||
        !std::isfinite(snapshot.status_hud_opacity) ||
        snapshot.status_hud_opacity < 0.35 || snapshot.status_hud_opacity > 1.0 ||
        snapshot.recording_profile_ack_generation > 0x7FFFFFFFFFFFFFFFULL ||
        snapshot.replay_session_generation > 0x7FFFFFFFFFFFFFFFULL ||
        ((snapshot.flags & smvm_snapshot_replay_active) != 0 &&
         snapshot.replay_session_generation == 0) ||
        snapshot.current_tick < -1 || snapshot.total_ticks < -1 ||
        snapshot.keyframe_count > kMaxCampathKeyframes ||
        snapshot.playback_state > max_playback_state || snapshot.start_failure > max_start_failure ||
        !std::isfinite(snapshot.timescale) || snapshot.timescale < 0.01 || snapshot.timescale > 16.0 ||
        !std::isfinite(snapshot.fov_step) || snapshot.fov_step < 0.05 || snapshot.fov_step > 30.0 ||
        !ValidateSmvmInput(snapshot.menu_key) || !ValidateSmvmInput(snapshot.add_key) ||
        !ValidateSmvmInput(snapshot.delete_key) || !ValidateSmvmInput(snapshot.clean_view_key) ||
        !ValidateSmvmKeyboardInput(snapshot.restore_ui_key) ||
        !ValidateSmvmKeyboardInput(snapshot.cycle_ui_key) ||
        !ValidateSmvmKeyboardInput(snapshot.toggle_free_camera_key) ||
        !ValidateSmvmKeyboardInput(snapshot.replay_pause_key) ||
        !ValidateSmvmKeyboardInput(snapshot.step_back_key) ||
        !ValidateSmvmKeyboardInput(snapshot.step_forward_key) ||
        !ValidateSmvmKeyboardInput(snapshot.roll_left_key) ||
        !ValidateSmvmKeyboardInput(snapshot.roll_right_key) ||
        !ValidateSmvmKeyboardInput(snapshot.roll_reset_key) ||
        !ValidateSmvmKeyboardInput(snapshot.forward_key) ||
        !ValidateSmvmKeyboardInput(snapshot.backward_key) ||
        !ValidateSmvmKeyboardInput(snapshot.left_key) ||
        !ValidateSmvmKeyboardInput(snapshot.right_key) ||
        !ValidateSmvmKeyboardInput(snapshot.up_key) ||
        !ValidateSmvmKeyboardInput(snapshot.down_key) ||
        !ValidateSmvmKeyboardInput(snapshot.fast_key) ||
        !ValidateSmvmKeyboardInput(snapshot.precision_key) ||
        !ValidateSmvmInput(snapshot.play_start_key) ||
        !ValidateSmvmInput(snapshot.play_current_key) || !ValidateSmvmInput(snapshot.stop_key) ||
        !ValidateSmvmInput(snapshot.undo_key) || !ValidateSmvmInput(snapshot.redo_key) ||
        !ValidateSmvmInput(snapshot.show_path_key) || !ValidateSmvmInput(snapshot.show_cameras_key) ||
        !ValidateSmvmInput(snapshot.show_labels_key) ||
        snapshot.camera_availability > CameraAvailability::protocol_mismatch ||
        snapshot.camera_ownership > CameraOwnership::smvm_campath ||
        snapshot.campath_session > SmvmCampathSession::saved_path ||
        snapshot.saved_document_count > kMaxCampathDocuments ||
        snapshot.menu_anchor > SmvmMenuAnchor::right ||
        snapshot.notification_anchor > SmvmNotificationAnchor::bottom_right ||
        !std::isfinite(snapshot.movement_speed) || snapshot.movement_speed < 1.0 ||
        snapshot.movement_speed > 10000.0 ||
        !std::isfinite(snapshot.boost_multiplier) || snapshot.boost_multiplier < 1.0 ||
        snapshot.boost_multiplier > 20.0 ||
        !std::isfinite(snapshot.precision_multiplier) || snapshot.precision_multiplier < 0.01 ||
        snapshot.precision_multiplier > 1.0 ||
        !std::isfinite(snapshot.mouse_sensitivity) || snapshot.mouse_sensitivity < 0.001 ||
        snapshot.mouse_sensitivity > 5.0 ||
        !std::isfinite(snapshot.smoothing) || snapshot.smoothing < 0.0 || snapshot.smoothing > 0.95 ||
        !std::isfinite(snapshot.ui_scale) || snapshot.ui_scale < 0.75 || snapshot.ui_scale > 1.5 ||
        !std::isfinite(snapshot.menu_opacity) || snapshot.menu_opacity < 0.65 || snapshot.menu_opacity > 1.0 ||
        !std::isfinite(snapshot.path_label_scale) || snapshot.path_label_scale < 0.5 ||
        snapshot.path_label_scale > 2.0 ||
        (snapshot.interpolation != CampathInterpolation::linear &&
         snapshot.interpolation != CampathInterpolation::smooth) ||
        (snapshot.easing != CampathEasing::linear && snapshot.easing != CampathEasing::ease_in &&
         snapshot.easing != CampathEasing::ease_out && snapshot.easing != CampathEasing::ease_in_out) ||
        (snapshot.end_behavior != CampathEndBehavior::stop_and_release &&
         snapshot.end_behavior != CampathEndBehavior::hold_final_camera))
        return false;
    if ((snapshot.flags & smvm_snapshot_camera_readable) != 0 && !ValidateSample(snapshot.camera))
        return false;
    return snapshot.replay_name.back() == '\0' && snapshot.path_name.back() == '\0' &&
           snapshot.status.back() == '\0' && snapshot.camera_status.back() == '\0' &&
           snapshot.movie_capture_path.back() == '\0';
}

[[nodiscard]] inline bool ValidateCampath(
    const CampathPayloadHeader& header, const CampathKeyframe* keyframes) noexcept {
    if (keyframes == nullptr || header.keyframe_count < 2 ||
        header.keyframe_count > kMaxCampathKeyframes ||
        (header.interpolation != CampathInterpolation::linear &&
         header.interpolation != CampathInterpolation::smooth) ||
        (header.easing != CampathEasing::linear && header.easing != CampathEasing::ease_in &&
         header.easing != CampathEasing::ease_out && header.easing != CampathEasing::ease_in_out))
        return false;
    if (header.end_behavior != CampathEndBehavior::stop_and_release &&
        header.end_behavior != CampathEndBehavior::hold_final_camera)
        return false;

    for (std::uint32_t index = 0; index < header.keyframe_count; ++index) {
        if (keyframes[index].demo_tick < 0 || !ValidateSample(keyframes[index].camera) ||
            (index > 0 && keyframes[index - 1].demo_tick >= keyframes[index].demo_tick))
            return false;
    }
    return true;
}

[[nodiscard]] inline bool ValidateEditorCampath(
    const CampathPayloadHeader& header, const CampathKeyframe* keyframes) noexcept {
    if (keyframes == nullptr || header.keyframe_count < 1 ||
        header.keyframe_count > kMaxCampathKeyframes ||
        (header.interpolation != CampathInterpolation::linear &&
         header.interpolation != CampathInterpolation::smooth) ||
        (header.easing != CampathEasing::linear && header.easing != CampathEasing::ease_in &&
         header.easing != CampathEasing::ease_out && header.easing != CampathEasing::ease_in_out))
        return false;
    for (std::uint32_t index = 0; index < header.keyframe_count; ++index) {
        if (keyframes[index].demo_tick < 0 || !ValidateSample(keyframes[index].camera) ||
            (index > 0 && keyframes[index - 1].demo_tick >= keyframes[index].demo_tick))
            return false;
    }
    return true;
}

[[nodiscard]] inline std::size_t ExpectedPayloadSize(const MessageType type) noexcept {
    switch (type) {
        case MessageType::hello: return sizeof(HelloPayload);
        case MessageType::heartbeat: return sizeof(HeartbeatPayload);
        case MessageType::set_camera_sample: return sizeof(CameraSample);
        case MessageType::set_roll_override: return sizeof(RollPayload);
        case MessageType::set_campath: return kMaxMessageBytes + 1;
        case MessageType::set_editor_campath: return kMaxMessageBytes + 1;
        case MessageType::set_campath_documents: return kMaxMessageBytes + 1;
        case MessageType::update_smvm_snapshot: return sizeof(SmvmSnapshotPayload);
        case MessageType::enable_override:
        case MessageType::disable_override:
        case MessageType::get_status:
        case MessageType::shutdown:
        case MessageType::clear_campath:
        case MessageType::prepare_camera_observation:
        case MessageType::clear_editor_campath:
        case MessageType::enable_manual_camera:
        case MessageType::disable_manual_camera:
            return 0;
        case MessageType::status: return sizeof(StatusPayload);
    }
    return kMaxMessageBytes + 1;
}

[[nodiscard]] inline bool ValidatePayloadSize(
    const MessageType type, const std::size_t payload_size) noexcept {
    if (type == MessageType::set_campath || type == MessageType::set_editor_campath) {
        const auto minimum_count = type == MessageType::set_campath ? 2u : 1u;
        return payload_size >= sizeof(CampathPayloadHeader) + (minimum_count * sizeof(CampathKeyframe)) &&
               payload_size <= kMaxMessageBytes &&
               (payload_size - sizeof(CampathPayloadHeader)) % sizeof(CampathKeyframe) == 0;
    }
    if (type == MessageType::set_campath_documents) {
        return payload_size >= 8 &&
               payload_size <= 8 + (kMaxCampathDocuments * sizeof(CampathDocumentEntry)) &&
               (payload_size - 8) % sizeof(CampathDocumentEntry) == 0;
    }
    return payload_size == ExpectedPayloadSize(type);
}

} // namespace deadlock_mvm
