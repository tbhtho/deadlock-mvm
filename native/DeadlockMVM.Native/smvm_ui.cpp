#include "smvm_ui.hpp"

#include "campath_math.hpp"
#include "movie_recording_progress.hpp"
#include "replay_timeline_policy.hpp"
#include "smvm_theme.hpp"

#include "imgui.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace deadlock_mvm::smvm_ui {
namespace {

// UTF-8 escapes keep the source ASCII-only (the product builds without /utf-8).
constexpr auto kDegree = "\xC2\xB0";
constexpr auto kMiddleDot = "\xC2\xB7";
constexpr auto kTimes = "\xC3\x97";

struct FrameContext final {
    const SmvmUiFrameParams* params{};
    SmvmUiState* state{};
    const SmvmSnapshotPayload* snapshot{};
};

void PushLocalToast(SmvmUiState& state, const char* message) noexcept {
    auto* slot = &state.toasts.front();
    for (auto& toast : state.toasts) {
        if (!toast.active) {
            slot = &toast;
            break;
        }
        if (toast.created_ms < slot->created_ms)
            slot = &toast;
    }
    static_cast<void>(std::snprintf(slot->text.data(), slot->text.size(), "%s", message));
    slot->created_ms = GetTickCount64();
    slot->active = true;
}

bool QueueAction(
    const FrameContext& context,
    const SmvmActionType type,
    const std::int32_t index = -1,
    const std::int64_t tick = -1,
    const double value = 0.0,
    const char* text = nullptr,
    const bool notify_failure = true) noexcept {
    if (!context.params->queue_action)
        return false;
    SmvmActionPayload action{};
    action.type = type;
    action.index = index;
    action.tick = tick;
    action.value = value;
    action.replay_session_generation = context.snapshot->replay_session_generation;
    if (text != nullptr) {
        const auto length = std::min(std::strlen(text), action.text.size() - 1);
        if (length > 0)
            std::memcpy(action.text.data(), text, length);
    }
    const auto queued = context.params->queue_action(action);
    if (!queued) {
        if (notify_failure && type != SmvmActionType::request_path_list)
            PushLocalToast(*context.state, "SMVM host is reconnecting - try again.");
        return false;
    }
    if (type == SmvmActionType::reacquire_camera) {
        context.state->free_camera_activation_pending = true;
        context.state->free_camera_activation_started_ms = GetTickCount64();
        context.state->free_camera_activation_error_until_ms = 0;
    }
    return true;
}

bool QueuePathPlayback(
    const FrameContext& context,
    const SmvmActionType playback_action) noexcept {
    // The simplified product always returns to SMVM Free Camera when a path
    // finishes. Older saved paths may still carry the legacy hold-final value,
    // so normalize it without exposing another ownership choice.
    if (context.snapshot->end_behavior == CampathEndBehavior::hold_final_camera &&
        !QueueAction(context, SmvmActionType::set_end_behavior, 0)) {
        return false;
    }
    if (playback_action == SmvmActionType::play_from_start)
        return SmvmArmCinematicStart(context.snapshot->replay_session_generation);
    return QueueAction(context, playback_action);
}

void RequestCapture(const FrameContext& context) noexcept {
    if (context.params->request_capture)
        context.params->request_capture(context.snapshot->replay_session_generation);
}

[[nodiscard]] constexpr std::uint32_t RequiredInputModifiers(const std::uint32_t binding) noexcept {
    return (binding & kSmvmInputModifierMask) >> 16;
}

[[nodiscard]] std::array<char, 48> FormatInput(const std::uint32_t input) noexcept {
    std::array<char, 48> result{};
    std::array<char, 20> generated{};
    const auto base = input & kSmvmInputBaseMask;
    const char* base_name = generated.data();
    if (base == 0) {
        base_name = "--";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_middle)) {
        base_name = "Mouse3";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x1)) {
        base_name = "Mouse4";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x2)) {
        base_name = "Mouse5";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::wheel_up)) {
        base_name = "Wheel Up";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::wheel_down)) {
        base_name = "Wheel Down";
    } else if ((base >= 'A' && base <= 'Z') || (base >= '0' && base <= '9')) {
        generated[0] = static_cast<char>(base);
        generated[1] = '\0';
    } else if (base >= VK_F1 && base <= VK_F24) {
        static_cast<void>(std::snprintf(generated.data(), generated.size(), "F%u", base - VK_F1 + 1));
    } else {
        switch (base) {
            case VK_CAPITAL: base_name = "Caps Lock"; break;
            case VK_TAB: base_name = "Tab"; break;
            case VK_RETURN: base_name = "Enter"; break;
            case VK_ESCAPE: base_name = "Esc"; break;
            case VK_SPACE: base_name = "Space"; break;
            case VK_CONTROL: base_name = "Ctrl"; break;
            case VK_LCONTROL: base_name = "Left Ctrl"; break;
            case VK_RCONTROL: base_name = "Right Ctrl"; break;
            case VK_MENU: base_name = "Alt"; break;
            case VK_LMENU: base_name = "Left Alt"; break;
            case VK_RMENU: base_name = "Right Alt"; break;
            case VK_SHIFT: base_name = "Shift"; break;
            case VK_LSHIFT: base_name = "Left Shift"; break;
            case VK_RSHIFT: base_name = "Right Shift"; break;
            case VK_DELETE: base_name = "Delete"; break;
            case VK_LEFT: base_name = "Left"; break;
            case VK_RIGHT: base_name = "Right"; break;
            case VK_UP: base_name = "Up"; break;
            case VK_DOWN: base_name = "Down"; break;
            default:
                static_cast<void>(std::snprintf(generated.data(), generated.size(), "VK%02X", base));
                break;
        }
    }
    const auto modifiers = RequiredInputModifiers(input);
    static_cast<void>(std::snprintf(
        result.data(), result.size(), "%s%s%s%s%s",
        (modifiers & 1u) != 0 ? "Ctrl+" : "",
        (modifiers & 2u) != 0 ? "Alt+" : "",
        (modifiers & 4u) != 0 ? "Shift+" : "",
        (modifiers & 8u) != 0 ? "Win+" : "",
        base_name));
    return result;
}

[[nodiscard]] const char* BindingActionName(const std::int32_t action) noexcept {
    constexpr std::array<const char*, 36> names{
        "Forward", "Backward", "Left", "Right", "Up", "Down", "Fast", "Precision",
        "Roll Left", "Roll Right", "Reset Roll", "Menu", "Add Keyframe", "Delete Keyframe",
        "Clean Footage", "Play From Start", "Play From Current", "Stop", "Undo", "Redo",
        "Show Path", "Show Cameras", "Emergency Exit + Restore", "Cycle Replay Interface",
        "Enter Free Camera", "Replay Play/Pause", "Show Labels", "Replay Step Back",
        "Replay Step Forward", "Effects panel", "Start ready cinematic", "Slower replay", "Faster replay", "Cancel recording / camera", "Slower camera", "Faster camera",
    };
    return action >= kSmvmFirstManualBindingAction && action <= kSmvmLastBindingAction
        ? names[static_cast<std::size_t>(action - kSmvmFirstManualBindingAction)]
        : nullptr;
}

[[nodiscard]] const char* DeadlockUiModeText(const DeadlockUiMode mode) noexcept {
    switch (mode) {
        case DeadlockUiMode::deadlock_ui: return "Deadlock UI";
        case DeadlockUiMode::smvm_replay_ui: return "SMVM Movie UI";
        case DeadlockUiMode::clean_footage: return "Clean Footage";
        case DeadlockUiMode::death_notices_only: return "Death Notices Only";
    }
    return "Unknown";
}

[[nodiscard]] const char* DeadlockUiErrorText(const DeadlockUiError error) noexcept {
    switch (error) {
        case DeadlockUiError::none: return "Ready";
        case DeadlockUiError::replay_unavailable: return "Replay unavailable";
        case DeadlockUiError::command_channel_unavailable: return "Command channel unavailable";
        case DeadlockUiError::unsupported_mode: return "Mode unsupported";
        case DeadlockUiError::apply_failed: return "Apply failed";
        case DeadlockUiError::restore_failed: return "Restore failed";
    }
    return "Unknown";
}

[[nodiscard]] const char* CameraAvailabilityText(const CameraAvailability availability) noexcept {
    switch (availability) {
        case CameraAvailability::initializing: return "Initializing";
        case CameraAvailability::ready: return "Ready";
        case CameraAvailability::replay_unavailable: return "Replay unavailable";
        case CameraAvailability::replay_seeking: return "Replay seeking";
        case CameraAvailability::not_in_free_roam: return "Free Camera required";
        case CameraAvailability::observer_target_active: return "Target active";
        case CameraAvailability::camera_manager_unavailable: return "Manager unavailable";
        case CameraAvailability::camera_object_unavailable: return "Camera unavailable";
        case CameraAvailability::camera_readback_unavailable: return "Readback unavailable";
        case CameraAvailability::native_backend_disconnected: return "Native disconnected";
        case CameraAvailability::signature_unavailable: return "Signature unavailable";
        case CameraAvailability::managed_host_disconnected: return "Host disconnected";
        case CameraAvailability::ownership_held_by_campath: return "Campath owns camera";
        case CameraAvailability::ownership_rejected: return "Ownership rejected";
        case CameraAvailability::snapshot_stale: return "Snapshot stale";
        case CameraAvailability::protocol_mismatch: return "Protocol mismatch";
        default: return "Unavailable";
    }
}

[[nodiscard]] const char* CameraOwnershipText(const CameraOwnership ownership) noexcept {
    switch (ownership) {
        case CameraOwnership::deadlock_spectator: return "Deadlock spectator";
        case CameraOwnership::smvm_manual_camera: return "SMVM Free Camera";
        case CameraOwnership::smvm_restore: return "Saved shot";
        case CameraOwnership::smvm_campath: return "Campath";
        default: return "None";
    }
}

[[nodiscard]] const char* RendererErrorText(const SmvmRendererError error) noexcept {
    switch (error) {
        case SmvmRendererError::none: return "None";
        case SmvmRendererError::renderer_not_loaded: return "Renderer not loaded";
        case SmvmRendererError::unsupported_renderer: return "Unsupported renderer";
        case SmvmRendererError::swapchain_probe_failed: return "Swapchain probe failed";
        case SmvmRendererError::hook_install_failed: return "Hook install failed";
        case SmvmRendererError::present_not_observed: return "Present not observed";
        case SmvmRendererError::device_unavailable: return "Device unavailable";
        case SmvmRendererError::resource_creation_failed: return "Resource creation failed";
        case SmvmRendererError::window_hook_failed: return "Window hook failed";
        case SmvmRendererError::device_reset: return "Device reset";
        default: return "Unknown";
    }
}

[[nodiscard]] const char* ManualInputFailureText(const std::uint32_t failure) noexcept {
    switch (failure) {
        case 0: return "None";
        case 1: return "Invalid HWND / window thread";
        case 2: return "SDL API unavailable";
        case 3: return "SDL window unavailable";
        case 4: return "Menu open / foreground mismatch";
        case 5: return "SDL relative enable failed";
        case 6: return "SDL relative verification failed";
        case 7: return "Raw Input registration missing";
        case 8: return "Readiness validation incomplete";
        case 9: return "Camera snapshot not ready";
        case 10: return "Menu input restore failed";
        default: return "Unknown";
    }
}

[[nodiscard]] const char* RawRegistrationText(const std::uint32_t disposition) noexcept {
    switch (disposition) {
        case 0: return "Query failed / not sampled";
        case 1: return "No mouse registration";
        case 2: return "Foreground target";
        case 3: return "Deadlock HWND target";
        case 4: return "Different HWND target";
        default: return "Unknown";
    }
}

[[nodiscard]] bool HasUnsavedCampathWork(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_campath_unsaved) != 0 ||
           (snapshot.campath_session == SmvmCampathSession::draft_path &&
            snapshot.keyframe_count > 0);
}

void SecondaryText(const char* text) noexcept {
    ImGui::PushFont(smvm_theme::GetFonts().secondary);
    ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kMuted));
    ImGui::TextUnformatted(text);
    ImGui::PopStyleColor();
    ImGui::PopFont();
}

void PageHeader(const char* title, const char* subtitle) noexcept {
    ImGui::PushFont(smvm_theme::GetFonts().page_title);
    ImGui::TextUnformatted(title);
    ImGui::PopFont();
    if (subtitle != nullptr && subtitle[0] != '\0') {
        ImGui::SameLine();
        ImGui::AlignTextToFramePadding();
        SecondaryText(subtitle);
    }
}

// Binding capture field: label left, capture button + clear right. The
// capture/pending/feedback state machine runs in the overlay (see
// SmvmPumpBindingRow); here we only render it.
void BindingField(const char* label, const std::uint32_t value, const std::int32_t action) noexcept {
    const auto status = SmvmPumpBindingRow(action, value);
    const auto scale = smvm_theme::GetScale();
    const auto field_width = 118.0F * scale;
    const auto clear_width = 24.0F * scale;
    const auto gap = 4.0F * scale;
    const auto line_x = ImGui::GetCursorPosX();
    const auto available = ImGui::GetContentRegionAvail().x;
    ImGui::PushID(action);
    ImGui::AlignTextToFramePadding();
    ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kMuted));
    ImGui::TextUnformatted(label);
    ImGui::PopStyleColor();
    ImGui::SameLine();
    ImGui::SetCursorPosX(line_x + std::max(0.0F, available - field_width - clear_width - gap));

    const auto formatted = FormatInput(value);
    std::array<char, 48> feedback{};
    const char* field_text = formatted.data();
    if (status.capturing) {
        field_text = "PRESS INPUT...";
    } else if (status.pending) {
        field_text = "SAVING...";
    } else if (status.feedback_kind == 1) {
        field_text = "SAVED";
    } else if (status.feedback_kind == 2) {
        if (const auto* conflict = BindingActionName(status.conflict_action)) {
            static_cast<void>(std::snprintf(
                feedback.data(), feedback.size(), "USED BY %s", conflict));
            field_text = feedback.data();
        } else {
            field_text = "CONFLICT";
        }
    } else if (status.feedback_kind == 3) {
        field_text = "KEYBOARD ONLY";
    }
    if (smvm_theme::Button(field_text, !status.pending, ImVec2(field_width, 0.0F)))
        SmvmBeginBindingCapture(action, value);
    smvm_theme::Tooltip("Click, then press a key, Mouse3-5, or the wheel. Escape cancels.");
    ImGui::SameLine(0.0F, gap);
    if (smvm_theme::Button(kTimes, value != 0 && !status.pending && !status.capturing,
                           ImVec2(clear_width, 0.0F)))
        SmvmClearBinding(action, value);
    smvm_theme::Tooltip("Clear binding");
    ImGui::PopID();
}

void SeekToTickField(
    const FrameContext& context,
    const char* hint = "Tick",
    const float logical_width = 110.0F,
    const bool timeline_field = false,
    const bool actions_enabled = true) noexcept {
    auto& state = *context.state;
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    ClearPendingReplaySeekWhenDisabled(
        actions_enabled,
        state.replay_seek_pending,
        state.replay_seek_retry_at_ms,
        state.replay_seek_session_generation);
    const auto try_pending_seek = [&]() noexcept {
        if (!actions_enabled)
            return;
        if (state.replay_seek_pending &&
            !IsReplayUiActionSessionCurrent(
                replay_active,
                snapshot.replay_session_generation,
                state.replay_seek_session_generation)) {
            state.replay_seek_pending = false;
            state.replay_seek_retry_at_ms = 0;
            state.replay_seek_session_generation = 0;
            return;
        }
        const auto now = GetTickCount64();
        if (!ShouldQueuePendingReplaySeek(
                state.replay_seek_pending,
                replay_active,
                snapshot.current_tick,
                now,
                state.replay_seek_retry_at_ms))
            return;
        const auto target = ClampReplayTimelineTick(
            state.replay_seek_target, snapshot.total_ticks);
        if (QueueAction(
                context,
                SmvmActionType::seek_tick,
                -1,
                target,
                0.0,
                nullptr,
                false)) {
            state.replay_seek_pending = false;
            state.replay_seek_retry_at_ms = 0;
            state.replay_seek_session_generation = 0;
            return;
        }
        // A freshly loaded replay can expose the timeline one or two frames
        // before the managed action channel is ready. Retain the owner's first
        // request and retry quietly instead of making them submit it again.
        state.replay_seek_retry_at_ms = now + 100;
    };
    try_pending_seek();
    ImGui::PushID("go_to_tick");
    ImGui::SetNextItemWidth(logical_width * scale);
    const auto submit = [&]() noexcept -> bool {
        if (!actions_enabled || !replay_active || state.go_to_tick[0] == '\0')
            return false;
        state.replay_seek_target = std::strtoll(state.go_to_tick.data(), nullptr, 10);
        state.replay_seek_pending = true;
        state.replay_seek_retry_at_ms = 0;
        state.replay_seek_session_generation = snapshot.replay_session_generation;
        state.go_to_tick.fill('\0');
        try_pending_seek();
        return true;
    };
    ImGui::BeginDisabled(!actions_enabled);
    ImGui::PushFont(smvm_theme::GetFonts().mono);
    const auto entered = ImGui::InputTextWithHint(
        "##tick", hint, state.go_to_tick.data(), state.go_to_tick.size(),
        ImGuiInputTextFlags_CharsDecimal | ImGuiInputTextFlags_EnterReturnsTrue);
    const auto finished_editing = ImGui::IsItemDeactivatedAfterEdit();
    ImGui::PopFont();
    if (timeline_field)
        state.replay_tick_input_active = ImGui::IsItemActive();
    smvm_theme::Tooltip("Type an absolute replay tick. Press Enter or click away to seek.");
    if ((entered || finished_editing) && submit()) {
        if (entered)
            ImGui::SetKeyboardFocusHere();
        if (timeline_field && entered)
            state.replay_tick_input_active = false;
    }
    ImGui::EndDisabled();
    ImGui::PopID();
}

// --- Pages ------------------------------------------------------------------

[[maybe_unused]] void DrawReplayPage(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    PageHeader(
        "Replay",
        snapshot.replay_name[0] != '\0' ? snapshot.replay_name.data() : "Untitled Replay");
    ImGui::Spacing();

    const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    if (smvm_theme::BeginSection("Session")) {
        std::array<char, 64> value{};
        if (snapshot.current_tick >= 0 && snapshot.total_ticks > 0)
            static_cast<void>(std::snprintf(
                value.data(), value.size(), "%lld / %lld",
                static_cast<long long>(snapshot.current_tick),
                static_cast<long long>(snapshot.total_ticks)));
        smvm_theme::ValueRow("Tick", value.data(), true,
                             snapshot.current_tick >= 0 && snapshot.total_ticks > 0);
        value = {};
        static_cast<void>(std::snprintf(value.data(), value.size(), "%.2g%s",
                                        snapshot.timescale, kTimes));
        smvm_theme::ValueRow("Speed", value.data(), true, replay_active);
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Transport")) {
        const auto pause_known = (snapshot.flags & smvm_snapshot_pause_known) != 0;
        const auto paused = (snapshot.flags & smvm_snapshot_paused) != 0;
        const auto tick_known = snapshot.current_tick >= 0;
        if (smvm_theme::PrimaryButton(paused ? "Play" : "Pause", replay_active && pause_known,
                                      ImVec2(96.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::toggle_replay_pause);
        ImGui::SameLine();
        if (smvm_theme::Button("-1 Tick", replay_active && tick_known, ImVec2(84.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::step_back);
        ImGui::SameLine();
        if (smvm_theme::Button("+1 Tick", replay_active && tick_known, ImVec2(84.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::step_forward);
        ImGui::SameLine();
        SeekToTickField(context);

        ImGui::Spacing();
        constexpr std::array<double, 5> speeds{0.25, 0.5, 1.0, 2.0, 4.0};
        constexpr std::array<const char*, 5> labels{
            ".25\xC3\x97", ".5\xC3\x97", "1\xC3\x97", "2\xC3\x97", "4\xC3\x97"};
        auto selected = 2;
        for (std::size_t index = 0; index < speeds.size(); ++index) {
            if (std::abs(snapshot.timescale - speeds[index]) < 0.001)
                selected = static_cast<int>(index);
        }
        const auto chosen = smvm_theme::SegmentedControl(
            "speed", labels.data(), static_cast<int>(labels.size()), selected, replay_active);
        if (chosen != selected)
            QueueAction(context, SmvmActionType::set_timescale, -1, -1,
                        speeds[static_cast<std::size_t>(chosen)]);
        smvm_theme::EndSection();
    }
}

[[maybe_unused]] void DrawCameraPage(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    PageHeader("Camera", "SMVM Free Camera");

    const auto camera_readable = (snapshot.flags & smvm_snapshot_camera_readable) != 0;
    const auto camera_ready = camera_readable &&
        snapshot.camera_availability == CameraAvailability::ready;
    const auto manual_active = (snapshot.flags & smvm_snapshot_manual_camera_active) != 0;
    const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    const auto campath_playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
    const auto can_enter = replay_active && !campath_playing;

    // SMVM Free Camera is the only owner-facing camera. Deadlock observer
    // preparation and native camera ownership remain implementation details in
    // the managed activation action.
    if (smvm_theme::BeginSection("Free Camera")) {
        if (manual_active) {
            smvm_theme::StatusPill("ACTIVE", smvm_theme::PillKind::success);
            ImGui::SameLine();
            SecondaryText("Close the menu to position your shot.");
            SecondaryText(
                "Move  \xC2\xB7  Mouse Look  \xC2\xB7  Vertical  \xC2\xB7  "
                "Fast / Precision  \xC2\xB7  Roll  \xC2\xB7  Wheel FOV");
            SecondaryText("With the menu closed, press Escape to exit Free Camera.");
            if (context.params->free_camera_input_error) {
                ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kError));
                ImGui::TextUnformatted("Free Camera input needs to be reconnected.");
                ImGui::PopStyleColor();
                if (smvm_theme::Button("Try Again", true, ImVec2(96.0F * scale, 0.0F)))
                    SmvmReacquireFreeCameraInput();
            }
        } else if (campath_playing) {
            smvm_theme::StatusPill("PATH PLAYING", smvm_theme::PillKind::accent);
            ImGui::SameLine();
            SecondaryText("Stop the path before positioning another shot.");
        } else if (context.state->free_camera_activation_pending) {
            smvm_theme::StatusPill("STARTING", smvm_theme::PillKind::warning);
            ImGui::SameLine();
            SecondaryText("Starting from the current view...");
        } else {
            if (GetTickCount64() < context.state->free_camera_activation_error_until_ms) {
                ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kError));
                ImGui::TextUnformatted("Free Camera could not start. Let the replay settle, then try again.");
                ImGui::PopStyleColor();
            } else {
                SecondaryText("Enter once, then stay in Free Camera while you pause, seek, and build a path.");
            }
            if (smvm_theme::PrimaryButton(
                    "Enter Free Camera", can_enter, ImVec2(168.0F * scale, 0.0F)))
                QueueAction(context, SmvmActionType::reacquire_camera);
        }
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Lens")) {
        const auto fov_writable = camera_ready &&
            (snapshot.flags & smvm_snapshot_fov_writable) != 0;
        auto out = 0.0F;
        if (fov_writable) {
            if (smvm_theme::SliderInput("fov", static_cast<float>(snapshot.camera.fov),
                                        static_cast<float>(kMinFov), static_cast<float>(kMaxFov),
                                        out, "%.1f", true, "FOV"))
                QueueAction(context, SmvmActionType::set_fov, -1, -1, out);
            ImGui::SameLine(0.0F, 12.0F * scale);
            constexpr std::array<double, 5> presets{20.0, 30.0, 40.0, 60.0, 90.0};
            for (std::size_t index = 0; index < presets.size(); ++index) {
                if (index > 0)
                    ImGui::SameLine(0.0F, 4.0F * scale);
                std::array<char, 12> label{};
                static_cast<void>(
                    std::snprintf(label.data(), label.size(), "%.0f", presets[index]));
                ImGui::PushID(static_cast<int>(index));
                const auto active = std::abs(snapshot.camera.fov - presets[index]) < 0.1;
                if (active)
                    ImGui::PushStyleColor(
                        ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kAccent));
                if (smvm_theme::Button(label.data(), true, ImVec2(40.0F * scale, 0.0F)))
                    QueueAction(context, SmvmActionType::set_fov, -1, -1, presets[index]);
                if (active)
                    ImGui::PopStyleColor();
                ImGui::PopID();
            }
        } else {
            std::array<char, 24> value{};
            if (camera_readable)
                static_cast<void>(std::snprintf(
                    value.data(), value.size(), "%.1f%s", snapshot.camera.fov, kDegree));
            smvm_theme::ValueRow("FOV", camera_readable ? value.data() : nullptr, true,
                                 camera_readable);
            SecondaryText(manual_active
                ? "Mouse wheel adjusts FOV."
                : "Enter Free Camera to adjust FOV.");
        }

        const auto rendered_roll = (snapshot.capabilities & smvm_capability_rendered_roll) != 0;
        const auto roll_writable = camera_ready && rendered_roll &&
            (snapshot.flags & smvm_snapshot_roll_writable) != 0;
        if (roll_writable) {
            if (smvm_theme::SliderInput("roll", static_cast<float>(snapshot.camera.roll),
                                        -45.0F, 45.0F, out, "%.1f", true, "Roll"))
                QueueAction(context, SmvmActionType::set_roll, -1, -1, out);
            ImGui::SameLine(0.0F, 12.0F * scale);
            if (smvm_theme::Button("Reset Roll", true, ImVec2(100.0F * scale, 0.0F)))
                QueueAction(context, SmvmActionType::set_roll, -1, -1, 0.0);
        } else {
            std::array<char, 24> value{};
            if (camera_readable)
                static_cast<void>(std::snprintf(
                    value.data(), value.size(), "%+.1f%s", snapshot.camera.roll, kDegree));
            smvm_theme::ValueRow("Roll", camera_readable ? value.data() : nullptr, true,
                                 camera_readable && rendered_roll);
            if (!rendered_roll) {
                SecondaryText("Roll is unavailable.");
            } else if (!manual_active) {
                SecondaryText("Enter Free Camera to adjust Roll.");
            } else {
                SecondaryText("Roll controls are connecting.");
            }
        }
        smvm_theme::EndSection();
    }
}

void DrawPathWorkflow(
    const FrameContext& context,
    const std::uint32_t keyframe_count) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    const auto pause_known = (snapshot.flags & smvm_snapshot_pause_known) != 0;
    const auto paused = (snapshot.flags & smvm_snapshot_paused) != 0;
    const auto manual_active = (snapshot.flags & smvm_snapshot_manual_camera_active) != 0;
    const auto camera_readable = (snapshot.flags & smvm_snapshot_camera_readable) != 0;
    const auto playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
    const auto can_enter = replay_active && !playing;
    const auto can_add = replay_active && pause_known && paused && manual_active &&
        camera_readable && !playing;
    const auto can_play = replay_active && manual_active && camera_readable &&
        keyframe_count >= 2 && !playing;

    if (!smvm_theme::BeginSection("Create a Camera Path"))
        return;
    SecondaryText("Pause the replay, position each shot in Free Camera, add keyframes, then play the path.");
    ImGui::Spacing();

    if (ImGui::BeginTable("##path_workflow", 2, ImGuiTableFlags_SizingStretchProp)) {
        ImGui::TableSetupColumn("##workflow_step", ImGuiTableColumnFlags_WidthStretch);
        ImGui::TableSetupColumn(
            "##workflow_action", ImGuiTableColumnFlags_WidthFixed, 176.0F * scale);
        const auto begin_step = [](const char* title, const char* detail) noexcept {
            ImGui::TableNextRow();
            ImGui::TableSetColumnIndex(0);
            ImGui::AlignTextToFramePadding();
            ImGui::PushFont(smvm_theme::GetFonts().section);
            ImGui::TextUnformatted(title);
            ImGui::PopFont();
            SecondaryText(detail);
            ImGui::TableSetColumnIndex(1);
        };

        begin_step("1  Pause Replay", paused ? "The scene is frozen." : "Freeze the scene you want to frame.");
        if (paused) {
            smvm_theme::StatusPill("PAUSED", smvm_theme::PillKind::success);
        } else if (smvm_theme::PrimaryButton(
                       "Pause Replay", replay_active && pause_known,
                       ImVec2(168.0F * scale, 0.0F))) {
            QueueAction(context, SmvmActionType::toggle_replay_pause);
        }

        begin_step(
            "2  Enter Free Camera",
            manual_active ? "Free Camera stays active while you edit." : "Start from the current rendered view.");
        if (manual_active) {
            smvm_theme::StatusPill("ACTIVE", smvm_theme::PillKind::success);
        } else {
            const auto enter_clicked = paused
                ? smvm_theme::PrimaryButton(
                      "Enter Free Camera", can_enter, ImVec2(168.0F * scale, 0.0F))
                : smvm_theme::Button(
                      "Enter Free Camera", can_enter, ImVec2(168.0F * scale, 0.0F));
            if (enter_clicked)
                QueueAction(context, SmvmActionType::reacquire_camera);
        }

        std::array<char, 64> keyframe_detail{};
        if (keyframe_count == 0) {
            static_cast<void>(std::snprintf(
                keyframe_detail.data(), keyframe_detail.size(), "Frame the first shot."));
        } else {
            static_cast<void>(std::snprintf(
                keyframe_detail.data(), keyframe_detail.size(), "%u keyframe%s added.",
                keyframe_count, keyframe_count == 1 ? "" : "s"));
        }
        begin_step("3  Add Keyframes", keyframe_detail.data());
        const auto add_primary = paused && manual_active && keyframe_count < 2;
        const auto add_clicked = add_primary
            ? smvm_theme::PrimaryButton("Add Keyframe", can_add, ImVec2(168.0F * scale, 0.0F))
            : smvm_theme::Button("Add Keyframe", can_add, ImVec2(168.0F * scale, 0.0F));
        if (add_clicked)
            RequestCapture(context);
        smvm_theme::Tooltip("Adds or replaces the keyframe at the current replay moment.");

        begin_step(
            "4  Play Path",
            keyframe_count >= 2 ? "The path is ready to preview." : "Add at least two keyframes.");
        if (playing) {
            if (smvm_theme::PrimaryButton("Stop Path", true, ImVec2(168.0F * scale, 0.0F)))
                QueueAction(context, SmvmActionType::stop_campath);
        } else if (smvm_theme::PrimaryButton(
                       "Play Path", can_play, ImVec2(168.0F * scale, 0.0F))) {
            if (QueuePathPlayback(context, SmvmActionType::play_from_start))
                SmvmCloseMenu();
        }
        ImGui::EndTable();
    }
    smvm_theme::EndSection();
}

void DrawCampathPage(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    auto& state = *context.state;
    const auto scale = smvm_theme::GetScale();
    const auto playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
    const auto* path_header = context.params->path_header;
    const auto* keys = context.params->keyframes;
    const auto keyframe_count = context.params->has_path && path_header != nullptr
        ? path_header->keyframe_count
        : 0;
    const auto has_keys = keyframe_count > 0 && keys != nullptr;
    const auto manual_active = (snapshot.flags & smvm_snapshot_manual_camera_active) != 0;
    const auto camera_readable = (snapshot.flags & smvm_snapshot_camera_readable) != 0;
    const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    const auto paused = (snapshot.flags & smvm_snapshot_paused) != 0;
    const auto pause_known = (snapshot.flags & smvm_snapshot_pause_known) != 0;
    const auto can_add = replay_active && pause_known && paused && manual_active &&
        camera_readable && !playing;
    const auto can_play = replay_active && manual_active && camera_readable && has_keys &&
        keyframe_count >= 2 && !playing;
    const auto saved_session = snapshot.campath_session == SmvmCampathSession::saved_path;
    const auto prepare_save_name = [&]() noexcept {
        state.save_as_name.fill('\0');
        const auto* current_name = snapshot.path_name.data();
        if (current_name[0] == '\0' ||
            std::strcmp(current_name, "Untitled Path") == 0)
            return;
        const auto length = std::min(std::strlen(current_name), state.save_as_name.size() - 1);
        if (length > 0)
            std::memcpy(state.save_as_name.data(), current_name, length);
    };

    std::array<char, 64> count_text{};
    if (has_keys && keyframe_count >= 2) {
        // The recorded clip spans exactly the keyframed tick range, so show the
        // resulting file length next to the keyframe count. A sub-second path
        // produces a sub-second AVI even though the replay preview takes
        // longer at the cinematic slowdown.
        const auto clip_seconds =
            static_cast<double>(keys[keyframe_count - 1].demo_tick - keys[0].demo_tick) *
            kSource2ReplayTickInterval;
        static_cast<void>(std::snprintf(
            count_text.data(), count_text.size(), "%u keyframes  |  clip %.1f s",
            snapshot.keyframe_count, clip_seconds));
    } else {
        static_cast<void>(std::snprintf(count_text.data(), count_text.size(),
                                        "%u keyframes", snapshot.keyframe_count));
    }
    SecondaryText(count_text.data());
    if (playing) {
        ImGui::SameLine();
        smvm_theme::StatusPill("PLAYING", smvm_theme::PillKind::accent);
    }

    if (smvm_theme::Button("Save Path", has_keys && !playing, ImVec2(104.0F * scale, 0.0F))) {
        if (saved_session) {
            QueueAction(context, SmvmActionType::save_path);
        } else {
            prepare_save_name();
            state.open_save_as_modal = true;
        }
    }
    ImGui::SameLine();
    if (smvm_theme::Button("Open Path", !playing, ImVec2(104.0F * scale, 0.0F)))
        state.open_load_picker = true;
    ImGui::Spacing();

    DrawPathWorkflow(context, keyframe_count);
    ImGui::Spacing();

    // Recovery banner.
    if ((snapshot.flags & smvm_snapshot_campath_recovery_available) != 0) {
        ImGui::PushStyleColor(
            ImGuiCol_ChildBg,
            smvm_theme::Vec4((smvm_theme::colors::kWarning & 0x00FFFFFFu) | (28u << 24)));
        ImGui::PushStyleColor(
            ImGuiCol_Border,
            smvm_theme::Vec4((smvm_theme::colors::kWarning & 0x00FFFFFFu) | (120u << 24)));
        ImGui::BeginChild("##recovery", ImVec2(0.0F, 0.0F),
                          ImGuiChildFlags_Borders | ImGuiChildFlags_AutoResizeY);
        ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kWarning));
        ImGui::TextUnformatted("We found an unfinished path from your last session.");
        ImGui::PopStyleColor();
        ImGui::SameLine(0.0F, 16.0F * scale);
        if (smvm_theme::Button("Recover", true, ImVec2(90.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::recover_draft);
        ImGui::SameLine();
        if (smvm_theme::Button("Discard", true, ImVec2(84.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::discard_draft);
        ImGui::EndChild();
        ImGui::PopStyleColor(2);
        ImGui::Spacing();
    }

    if (!has_keys)
        return;

    if (smvm_theme::BeginSection("Keyframes", 242.0F * scale)) {
        ImGui::BeginChild(
            "##keyframe_list", ImVec2(0.0F, 0.0F), ImGuiChildFlags_None,
            ImGuiWindowFlags_None);
        for (std::uint32_t index = 0; index < keyframe_count; ++index) {
            const auto& key = keys[index];
            const auto selected = static_cast<std::int32_t>(index) == snapshot.selected_keyframe;
            if (smvm_theme::KeyframeRow(static_cast<int>(index), key.demo_tick,
                                        key.camera.fov, key.camera.roll, selected)) {
                QueueAction(context, SmvmActionType::select_keyframe,
                            static_cast<std::int32_t>(index));
            }
        }
        ImGui::EndChild();
        smvm_theme::EndSection();
    }

    if (ImGui::CollapsingHeader("Keyframe Tools")) {
        ImGui::Indent(8.0F * scale);
        if (smvm_theme::BeginSection("Selected Keyframe")) {
            const auto selection_valid = has_keys && snapshot.selected_keyframe >= 0 &&
                static_cast<std::uint32_t>(snapshot.selected_keyframe) < keyframe_count;
            if (selection_valid) {
                const auto& selected = keys[snapshot.selected_keyframe];
                std::array<char, 48> value{};
                static_cast<void>(std::snprintf(value.data(), value.size(), "%lld",
                                                static_cast<long long>(selected.demo_tick)));
                smvm_theme::ValueRow("Tick", value.data(), true);
                value = {};
                static_cast<void>(std::snprintf(value.data(), value.size(), "%.1f%s",
                                                selected.camera.fov, kDegree));
                smvm_theme::ValueRow("FOV", value.data(), true);
                value = {};
                static_cast<void>(std::snprintf(value.data(), value.size(), "%.1f%s",
                                                selected.camera.roll, kDegree));
                smvm_theme::ValueRow("Roll", value.data(), true);
                ImGui::Spacing();
                if (smvm_theme::Button("View Keyframe", true, ImVec2(112.0F * scale, 0.0F)))
                    QueueAction(context, SmvmActionType::go_to_keyframe, snapshot.selected_keyframe);
                ImGui::SameLine();
                if (smvm_theme::Button(
                        "Replace with Current View", can_add,
                        ImVec2(188.0F * scale, 0.0F)))
                    QueueAction(context, SmvmActionType::update_keyframe, snapshot.selected_keyframe);
                ImGui::SameLine();
                if (smvm_theme::DangerButton("Delete", !playing, ImVec2(80.0F * scale, 0.0F)))
                    QueueAction(context, SmvmActionType::delete_keyframe, snapshot.selected_keyframe);
            } else {
                SecondaryText("Select a keyframe above to view or change it.");
            }
            smvm_theme::EndSection();
        }
        ImGui::Unindent(8.0F * scale);
    }
    ImGui::Spacing();

    if (ImGui::CollapsingHeader("Path Motion")) {
        ImGui::Indent(8.0F * scale);
        if (smvm_theme::BeginSection("Path Motion")) {
            const auto editing_enabled = !playing;
            constexpr std::array<const char*, 2> interpolation_labels{"Linear", "Smooth"};
            const auto interpolation = smvm_theme::SegmentedControl(
                "interpolation", interpolation_labels.data(), 2,
                snapshot.interpolation == CampathInterpolation::smooth ? 1 : 0, editing_enabled);
            if (interpolation != (snapshot.interpolation == CampathInterpolation::smooth ? 1 : 0))
                QueueAction(context, SmvmActionType::set_interpolation, interpolation);
            ImGui::SameLine(0.0F, 20.0F * scale);
            constexpr std::array<const char*, 4> easing_labels{"Linear", "In", "Out", "In-Out"};
            const auto easing = smvm_theme::SegmentedControl(
                "easing", easing_labels.data(), 4,
                static_cast<int>(snapshot.easing), editing_enabled);
            if (easing != static_cast<int>(snapshot.easing))
                QueueAction(context, SmvmActionType::set_easing, easing);
            ImGui::Spacing();
            if (smvm_theme::Button(
                    "Play From Current", can_play, ImVec2(150.0F * scale, 0.0F)) &&
                QueuePathPlayback(context, SmvmActionType::play_from_current)) {
                SmvmCloseMenu();
            }
            ImGui::SameLine();
            SecondaryText("Path playback returns to Free Camera when it finishes.");
            smvm_theme::EndSection();
        }
        ImGui::Unindent(8.0F * scale);
    }

    if (ImGui::CollapsingHeader("Start Over")) {
        ImGui::Indent(8.0F * scale);
        if (smvm_theme::BeginSection("Start Over")) {
            if (smvm_theme::Button("Start New Path", !playing, ImVec2(124.0F * scale, 0.0F))) {
                if (HasUnsavedCampathWork(snapshot)) {
                    state.confirm_action = SmvmConfirmAction::new_path;
                    state.confirm_pending = true;
                } else {
                    QueueAction(context, SmvmActionType::new_path);
                }
            }
            SecondaryText("Adding a keyframe starts the working path automatically.");
            smvm_theme::EndSection();
        }
        ImGui::Unindent(8.0F * scale);
    }
}

[[maybe_unused]] void DrawCapturePage() noexcept {
    PageHeader("Capture", nullptr);
    ImGui::Spacing();
    smvm_theme::EmptyState(
        "The capture pipeline isn't implemented yet",
        "SMVM will not claim recording, encoding, or file output it cannot prove.");
    ImGui::Spacing();
    ImGui::Spacing();
    SecondaryText("Planned: image sequence, resolution, frame rate, audio, render passes.");
}

void DrawEssentialControls(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (!smvm_theme::BeginSection("Essential Controls"))
        return;
    SecondaryText("SMVM is ready to use. Change a control only when you want to.");
    SecondaryText("Shortcuts work with the menu closed; on-screen buttons work here.");
    ImGui::Spacing();
    BindingField("Open SMVM", snapshot.menu_key, kSmvmFirstEditorBindingAction + 0);
    BindingField("Pause / Play Replay", snapshot.replay_pause_key, 125);
    BindingField("Enter Free Camera", snapshot.toggle_free_camera_key, 124);
    BindingField("Add Keyframe", snapshot.add_key, kSmvmFirstEditorBindingAction + 1);
    BindingField("Play Path", snapshot.play_start_key, kSmvmFirstEditorBindingAction + 4);
    BindingField("Stop Path", snapshot.stop_key, kSmvmFirstEditorBindingAction + 6);
    smvm_theme::EndSection();
}

void DrawCameraFeelSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (!smvm_theme::BeginSection("Camera Feel"))
        return;
    auto out = 0.0F;
    if (smvm_theme::SliderInput("move_speed", static_cast<float>(snapshot.movement_speed),
                                1.0F, 2000.0F, out, "%.0f", true, "Move Speed"))
        QueueAction(context, SmvmActionType::set_movement_speed, -1, -1, out);
    if (smvm_theme::SliderInput("sensitivity", static_cast<float>(snapshot.mouse_sensitivity),
                                0.001F, 5.0F, out, "%.3f", true, "Look Sensitivity"))
        QueueAction(context, SmvmActionType::set_mouse_sensitivity, -1, -1, out);
    if (smvm_theme::Toggle("Invert Vertical Look",
                           (snapshot.flags & smvm_snapshot_invert_y) != 0))
        QueueAction(context, SmvmActionType::toggle_invert_y);
    smvm_theme::EndSection();
}

void DrawCameraTuningSection(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (!smvm_theme::BeginSection("Fine Tuning"))
        return;
    auto out = 0.0F;
    if (smvm_theme::SliderInput("smoothing", static_cast<float>(snapshot.smoothing),
                                0.0F, 0.95F, out, "%.2f", true, "Smoothing"))
        QueueAction(context, SmvmActionType::set_smoothing, -1, -1, out);
    std::array<char, 32> value{};
    static_cast<void>(std::snprintf(value.data(), value.size(), "%s%.2f", kTimes,
                                    snapshot.boost_multiplier));
    smvm_theme::ValueRow("Boost", value.data());
    ImGui::SameLine(0.0F, 18.0F * smvm_theme::GetScale());
    value = {};
    static_cast<void>(std::snprintf(value.data(), value.size(), "%s%.2f", kTimes,
                                    snapshot.precision_multiplier));
    smvm_theme::ValueRow("Precision", value.data());
    smvm_theme::EndSection();
}

void DrawLensBindingsSection(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (!smvm_theme::BeginSection("Lens Controls"))
        return;
    constexpr std::array<const char*, 3> roll_labels{"Roll Left", "Roll Right", "Reset Roll"};
    const std::array<std::uint32_t, 3> roll_values{
        snapshot.roll_left_key, snapshot.roll_right_key, snapshot.roll_reset_key};
    constexpr std::array<std::int32_t, 3> roll_actions{
        kSmvmFirstManualBindingAction + 8, kSmvmFirstManualBindingAction + 9,
        kSmvmFirstManualBindingAction + 10};
    for (std::size_t index = 0; index < roll_labels.size(); ++index)
        BindingField(roll_labels[index], roll_values[index], roll_actions[index]);
    auto step = 0.0F;
    if (smvm_theme::SliderInput("fov_step", static_cast<float>(snapshot.fov_step),
                                0.05F, 30.0F, step, "%.2f", true, "Wheel Step"))
        QueueAction(context, SmvmActionType::set_binding, 4, -1, step);
    smvm_theme::EndSection();
}

void DrawCameraSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    constexpr std::array<const char*, 8> movement_labels{
        "Forward", "Backward", "Left", "Right", "Up", "Down", "Fast", "Precision"};
    const std::array<std::uint32_t, 8> movement_values{
        snapshot.forward_key, snapshot.backward_key, snapshot.left_key, snapshot.right_key,
        snapshot.up_key, snapshot.down_key, snapshot.fast_key, snapshot.precision_key};
    constexpr std::array<std::int32_t, 8> movement_actions{
        kSmvmFirstManualBindingAction + 0, kSmvmFirstManualBindingAction + 1,
        kSmvmFirstManualBindingAction + 2, kSmvmFirstManualBindingAction + 3,
        kSmvmFirstManualBindingAction + 4, kSmvmFirstManualBindingAction + 5,
        kSmvmFirstManualBindingAction + 6, kSmvmFirstManualBindingAction + 7};
    if (smvm_theme::BeginSection("Movement Controls")) {
        for (std::size_t index = 0; index < movement_labels.size(); ++index)
            BindingField(movement_labels[index], movement_values[index], movement_actions[index]);
        smvm_theme::EndSection();
    }
    ImGui::Spacing();
    DrawCameraTuningSection(context);
    ImGui::Spacing();
    DrawLensBindingsSection(context);
}

void DrawCampathSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    constexpr std::array<const char*, 7> labels{
        "Delete Keyframe", "Play From Current", "Undo", "Redo", "Show Path", "Show Cameras",
        "Show Labels"};
    const std::array<std::uint32_t, 7> values{
        snapshot.delete_key, snapshot.play_current_key, snapshot.undo_key, snapshot.redo_key,
        snapshot.show_path_key, snapshot.show_cameras_key, snapshot.show_labels_key};
    constexpr std::array<std::int32_t, 7> actions{
        kSmvmFirstEditorBindingAction + 2, kSmvmFirstEditorBindingAction + 5,
        kSmvmFirstEditorBindingAction + 7, kSmvmFirstEditorBindingAction + 8,
        kSmvmFirstEditorBindingAction + 9, kSmvmFirstEditorBindingAction + 10, 126};
    if (smvm_theme::BeginSection("Path Shortcuts")) {
        for (std::size_t index = 0; index < labels.size(); ++index)
            BindingField(labels[index], values[index], actions[index]);
        SecondaryText("Optional editing and path-display shortcuts.");
        smvm_theme::EndSection();
    }
}

void DrawReplaySettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (smvm_theme::BeginSection("Replay Stepping")) {
        BindingField("Step Back", snapshot.step_back_key, 127);
        BindingField("Step Forward", snapshot.step_forward_key, 128);
        SecondaryText("Precise frame stepping is optional.");
        smvm_theme::EndSection();
    }
}

void DrawInterfaceSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (smvm_theme::BeginSection("Menu")) {
        BindingField("Cycle Replay UI", snapshot.cycle_ui_key, 123);
        BindingField("Clean Footage", snapshot.clean_view_key, kSmvmFirstEditorBindingAction + 3);
        BindingField("Emergency Exit + Restore", snapshot.restore_ui_key,
                     kSmvmFirstEditorBindingAction + 11);
        auto out = 0.0F;
        if (smvm_theme::SliderInput("ui_scale", static_cast<float>(snapshot.ui_scale),
                                    0.75F, 1.5F, out, "%.2f", true, "UI Scale"))
            QueueAction(context, SmvmActionType::set_ui_scale, -1, -1, out);
        if (smvm_theme::SliderInput("menu_opacity_settings",
                                    static_cast<float>(snapshot.menu_opacity),
                                    0.65F, 1.0F, out, "%.2f", true, "Menu Opacity"))
            QueueAction(context, SmvmActionType::set_menu_opacity, -1, -1, out);
        constexpr std::array<const char*, 2> anchor_labels{"Left", "Right"};
        const auto anchor = smvm_theme::SegmentedControl(
            "menu_anchor", anchor_labels.data(), 2,
            snapshot.menu_anchor == SmvmMenuAnchor::right ? 1 : 0);
        if (anchor != (snapshot.menu_anchor == SmvmMenuAnchor::right ? 1 : 0))
            QueueAction(context, SmvmActionType::set_menu_anchor, anchor);
        ImGui::SameLine();
        SecondaryText("Menu Anchor");
        constexpr std::array<const char*, 4> notification_labels{"Top Left", "Top Right",
                                                                 "Bottom Left", "Bottom Right"};
        const auto notification_anchor = static_cast<int>(snapshot.notification_anchor);
        const auto picked_anchor = smvm_theme::SegmentedControl(
            "notification_anchor", notification_labels.data(), 4, notification_anchor);
        if (picked_anchor != notification_anchor)
            QueueAction(context, SmvmActionType::set_notification_anchor, picked_anchor);
        ImGui::SameLine();
        SecondaryText("Notification Anchor");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Binding Presets")) {
        if (smvm_theme::PrimaryButton("Restore Recommended Controls", true,
                                      ImVec2(240.0F * smvm_theme::GetScale(), 0.0F)))
            QueueAction(context, SmvmActionType::apply_movie_maker_defaults);
        ImGui::SameLine();
        if (smvm_theme::Button("Restore Every Default", true,
                               ImVec2(180.0F * smvm_theme::GetScale(), 0.0F)))
            QueueAction(context, SmvmActionType::reset_bindings);
        SecondaryText("Recommended controls cover the complete movie-making workflow.");
        SecondaryText("Presets are checked for conflicts before anything changes.");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Interface")) {
        if (smvm_theme::Toggle("Movie Status HUD",
                               (snapshot.flags & smvm_snapshot_show_status_hud) != 0))
            QueueAction(context, SmvmActionType::toggle_status_hud);
        auto hud_value = 0.0F;
        if (smvm_theme::SliderInput(
                "status_hud_scale", static_cast<float>(snapshot.status_hud_scale),
                0.75F, 1.5F, hud_value, "%.2f", true, "Status HUD Scale"))
            QueueAction(context, SmvmActionType::set_status_hud_scale, -1, -1, hud_value);
        if (smvm_theme::SliderInput(
                "status_hud_opacity", static_cast<float>(snapshot.status_hud_opacity),
                0.35F, 1.0F, hud_value, "%.2f", true, "Status HUD Opacity"))
            QueueAction(context, SmvmActionType::set_status_hud_opacity, -1, -1, hud_value);
        constexpr std::array<const char*, 4> hud_anchors{
            "Top Left", "Top Right", "Bottom Left", "Bottom Right"};
        const auto hud_anchor = static_cast<int>(snapshot.status_hud_anchor);
        const auto picked_hud_anchor = smvm_theme::SegmentedControl(
            "status_hud_anchor", hud_anchors.data(), static_cast<int>(hud_anchors.size()),
            hud_anchor);
        if (picked_hud_anchor != hud_anchor)
            QueueAction(context, SmvmActionType::set_status_hud_anchor, picked_hud_anchor);
        SecondaryText("Compact camera telemetry appears only during Free Camera or Path playback.");
        ImGui::Spacing();
        if (smvm_theme::Toggle("Minimal SMVM Button",
                               (snapshot.flags & smvm_snapshot_show_minimal_pill) != 0))
            QueueAction(context, SmvmActionType::toggle_minimal_pill);
        if (smvm_theme::Toggle("Notifications",
                               (snapshot.flags & smvm_snapshot_notifications) != 0))
            QueueAction(context, SmvmActionType::toggle_notifications);
        auto replay_scale = 0.0F;
        if (smvm_theme::SliderInput(
                "replay_bar_scale", static_cast<float>(snapshot.replay_bar_scale),
                0.75F, 2.0F, replay_scale, "%.2f", true, "Replay Bar Scale"))
            QueueAction(context, SmvmActionType::set_replay_bar_scale, -1, -1, replay_scale);
        auto replay_opacity = 0.0F;
        if (smvm_theme::SliderInput(
                "replay_bar_opacity", static_cast<float>(snapshot.replay_bar_opacity),
                0.35F, 1.0F, replay_opacity, "%.2f", true, "Replay Bar Opacity"))
            QueueAction(context, SmvmActionType::set_replay_bar_opacity, -1, -1, replay_opacity);
        constexpr std::array<const char*, 2> replay_anchors{"Bottom", "Top"};
        const auto replay_anchor = static_cast<int>(snapshot.replay_bar_anchor);
        const auto picked_replay_anchor = smvm_theme::SegmentedControl(
            "replay_bar_anchor", replay_anchors.data(),
            static_cast<int>(replay_anchors.size()), replay_anchor);
        if (picked_replay_anchor != replay_anchor)
            QueueAction(context, SmvmActionType::set_replay_bar_anchor, picked_replay_anchor);
        smvm_theme::EndSection();
    }
}

void DrawStorageSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (smvm_theme::BeginSection("Storage")) {
        smvm_theme::ValueRow("Path Folder", "%LOCALAPPDATA%\\DeadlockMVM\\campaths", true);
        std::array<char, 24> count{};
        static_cast<void>(std::snprintf(count.data(), count.size(), "%u",
                                        snapshot.saved_document_count));
        smvm_theme::ValueRow("Saved Paths", count.data());
        if (smvm_theme::Toggle("Restore last workspace on startup",
                               (snapshot.flags & smvm_snapshot_restore_workspace) != 0))
            QueueAction(context, SmvmActionType::toggle_restore_workspace);
        smvm_theme::Tooltip("Default off. When off, SMVM starts with an empty path workspace.");
        smvm_theme::EndSection();
    }
}

void DrawAdvancedSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto& params = *context.params;
    const auto scale = smvm_theme::GetScale();
    if (smvm_theme::BeginSection("Diagnostics")) {
        smvm_theme::ValueRow("Protocol", "V11", true);
        const auto renderer_error = static_cast<SmvmRendererError>(params.renderer_error);
        std::array<char, 48> renderer{};
        static_cast<void>(std::snprintf(renderer.data(), renderer.size(), "D3D11 %s %s",
                                        kMiddleDot, RendererErrorText(renderer_error)));
        smvm_theme::ValueRow("Renderer", renderer.data(), true, renderer_error == SmvmRendererError::none);
        smvm_theme::ValueRow("Camera", CameraAvailabilityText(snapshot.camera_availability), false,
                             snapshot.camera_availability == CameraAvailability::ready);
        smvm_theme::ValueRow("Camera Control", CameraOwnershipText(snapshot.camera_ownership));
        std::array<char, 32> value{};
        static_cast<void>(std::snprintf(value.data(), value.size(), "0x%08X", snapshot.capabilities));
        smvm_theme::ValueRow("Capabilities", value.data(), true);
        value = {};
        static_cast<void>(std::snprintf(
            value.data(), value.size(), "0x%08X", snapshot.deadlock_ui_capabilities));
        smvm_theme::ValueRow("Deadlock UI Caps", value.data(), true);
        smvm_theme::ValueRow("Deadlock UI", DeadlockUiModeText(snapshot.deadlock_ui_mode));
        smvm_theme::ValueRow("UI Restore", DeadlockUiErrorText(snapshot.deadlock_ui_error), false,
                             snapshot.deadlock_ui_error == DeadlockUiError::none);
        value = {};
        static_cast<void>(std::snprintf(value.data(), value.size(), "%u \xC2\xB5s",
                                        params.frame_microseconds));
        smvm_theme::ValueRow("Present Cost", value.data(), true);
        value = {};
        static_cast<void>(std::snprintf(value.data(), value.size(), "0x%08X", params.overlay_flags));
        smvm_theme::ValueRow("Overlay Flags", value.data(), true);
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Free Camera Input")) {
        const auto flags = params.overlay_flags;
        const auto flag = [flags](const std::uint32_t value) noexcept {
            return (flags & value) != 0;
        };
        const auto keyboard_ready = flag(smvm_overlay_keyboard_ready);
        const auto relative_ready = flag(smvm_overlay_relative_mouse_ready);
        const auto raw_ready = flag(smvm_overlay_raw_input_ready);
        const auto cursor_ready = flag(smvm_overlay_cursor_ready);
        const auto foreground_ready = flag(smvm_overlay_foreground_ready);
        const auto wndproc_ready = flag(smvm_overlay_window_procedure_ready);
        const auto engine_ready = flag(smvm_overlay_engine_input_ready);
        smvm_theme::ValueRow("Keyboard", keyboard_ready ? "Ready" : "Suspended", false, true);
        smvm_theme::ValueRow("SDL Relative", relative_ready ? "Ready" : "Suspended", false,
                             true);
        smvm_theme::ValueRow("Raw Input", raw_ready ? "Registered" : "Not registered", false, true);
        smvm_theme::ValueRow("Cursor", cursor_ready ? "Captured / hidden" : "Editor owned", false,
                             true);
        smvm_theme::ValueRow("Focus / WndProc",
                             foreground_ready && wndproc_ready ? "Ready" : "Not ready", false,
                             true);
        smvm_theme::ValueRow("Engine Input", engine_ready ? "Restored" : "Suspended", false, true);
        smvm_theme::ValueRow(
            "Last Failure", ManualInputFailureText(params.free_camera_input_failure), false, true);
        smvm_theme::ValueRow(
            "Raw Target", RawRegistrationText(params.raw_registration_disposition), false, true);
        std::array<char, 64> mouse_age{};
        const auto now = GetTickCount64();
        if (params.raw_mouse_timestamp_ms != 0) {
            static_cast<void>(std::snprintf(
                mouse_age.data(), mouse_age.size(), "Raw %llu ms ago",
                static_cast<unsigned long long>(now - params.raw_mouse_timestamp_ms)));
        } else if (params.fallback_mouse_timestamp_ms != 0) {
            static_cast<void>(std::snprintf(
                mouse_age.data(), mouse_age.size(), "Fallback %llu ms ago",
                static_cast<unsigned long long>(now - params.fallback_mouse_timestamp_ms)));
        } else {
            static_cast<void>(std::snprintf(mouse_age.data(), mouse_age.size(), "Awaiting motion"));
        }
        smvm_theme::ValueRow("Last Mouse", mouse_age.data(), true);
        const auto manual_active = (snapshot.flags & smvm_snapshot_manual_camera_active) != 0;
        if (smvm_theme::Button(
                "REACQUIRE FREE CAMERA INPUT", manual_active, ImVec2(248.0F * scale, 0.0F)))
            SmvmReacquireFreeCameraInput();
        if (!manual_active)
            smvm_theme::Tooltip("Enter Free Camera before reacquiring its input route.");
        SecondaryText("Preserves Position, Pitch/Yaw, Roll, FOV, and camera ownership.");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Known Boundary")) {
        smvm_theme::ValueRow("Death Notices Only", "Blocked by Panorama ownership", true, false);
        SecondaryText("Deadlock does not currently expose an authoritative death-notice channel independent of Panorama. No pixel or entity inference is used.");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Self-Tests")) {
        const auto test_ownership_clear =
            snapshot.camera_ownership != CameraOwnership::smvm_restore &&
            snapshot.camera_ownership != CameraOwnership::smvm_campath &&
            (snapshot.flags & smvm_snapshot_campath_playing) == 0;
        const auto test_prerequisites = test_ownership_clear &&
            (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
            (snapshot.flags & smvm_snapshot_camera_readable) != 0 && snapshot.observer_mode == 4;
        const auto camera_test_ready = test_prerequisites &&
            (snapshot.capabilities & smvm_capability_camera_self_test) != 0;
        const auto path_test_ready = test_prerequisites && snapshot.keyframe_count >= 2 &&
            (snapshot.capabilities & smvm_capability_campath_self_test) != 0;
        if (smvm_theme::Button("Camera Self-Test", camera_test_ready, ImVec2(150.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::camera_self_test);
        if (!camera_test_ready)
            smvm_theme::Tooltip("Requires Free Camera with a readable camera.");
        ImGui::SameLine();
        if (smvm_theme::Button("Path Self-Test", path_test_ready, ImVec2(140.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::campath_self_test);
        if (!path_test_ready)
            smvm_theme::Tooltip("Requires Free Camera and at least two keyframes.");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Status")) {
        SecondaryText(snapshot.status[0] != '\0' ? snapshot.status.data() : "Ready");
        smvm_theme::EndSection();
    }
}

[[maybe_unused]] void DrawSettingsPage(const FrameContext& context) noexcept {
    const auto scale = smvm_theme::GetScale();
    PageHeader("Settings", "Ready-to-use defaults");
    ImGui::Spacing();

    DrawEssentialControls(context);
    ImGui::Spacing();
    DrawCameraFeelSettings(context);
    ImGui::Spacing();

    if (ImGui::CollapsingHeader("More Controls")) {
        ImGui::Indent(8.0F * scale);
        DrawCameraSettings(context);
        ImGui::Spacing();
        DrawReplaySettings(context);
        ImGui::Spacing();
        DrawCampathSettings(context);
        ImGui::Unindent(8.0F * scale);
    }
    if (ImGui::CollapsingHeader("Interface")) {
        ImGui::Indent(8.0F * scale);
        DrawInterfaceSettings(context);
        ImGui::Unindent(8.0F * scale);
    }
    if (ImGui::CollapsingHeader("Advanced")) {
        ImGui::Indent(8.0F * scale);
        DrawStorageSettings(context);
        ImGui::Spacing();
        DrawAdvancedSettings(context);
        ImGui::Unindent(8.0F * scale);
    }
}

// --- Modals (rendered at shell level so popup IDs share one stack) ---------

void DrawModals(const FrameContext& context) noexcept {
    auto& state = *context.state;
    const auto scale = smvm_theme::GetScale();

    if (state.open_save_as_modal) {
        ImGui::OpenPopup("Save Path");
        state.open_save_as_modal = false;
    }
    ImGui::SetNextWindowSizeConstraints(ImVec2(300.0F * scale, 0.0F),
                                        ImVec2(380.0F * scale, 200.0F * scale));
    if (ImGui::BeginPopupModal("Save Path", nullptr,
                               ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove)) {
        ImGui::PushFont(smvm_theme::GetFonts().section);
        ImGui::TextUnformatted("Name Your Path");
        ImGui::PopFont();
        ImGui::Spacing();
        ImGui::SetNextItemWidth(-1.0F);
        ImGui::InputTextWithHint("##path_name", "Path name", state.save_as_name.data(),
                                 state.save_as_name.size());
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        if (smvm_theme::PrimaryButton("Save Path", state.save_as_name[0] != '\0',
                                      ImVec2(90.0F * scale, 0.0F))) {
            QueueAction(context, SmvmActionType::save_path_as, -1, -1, 0.0,
                        state.save_as_name.data());
            ImGui::CloseCurrentPopup();
        }
        ImGui::SameLine();
        if (smvm_theme::Button("Cancel", true, ImVec2(80.0F * scale, 0.0F)))
            ImGui::CloseCurrentPopup();
        ImGui::EndPopup();
    }

    if (state.open_load_picker) {
        if (!state.picker_requested_list) {
            state.picker_requested_list =
                QueueAction(context, SmvmActionType::request_path_list);
        }
        ImGui::OpenPopup("Open Path");
        state.open_load_picker = false;
    }
    auto picker_open = true;
    ImGui::SetNextWindowSize(ImVec2(460.0F * scale, 400.0F * scale), ImGuiCond_Appearing);
    if (ImGui::BeginPopupModal("Open Path", &picker_open, ImGuiWindowFlags_NoResize)) {
        ImGui::PushFont(smvm_theme::GetFonts().section);
        ImGui::TextUnformatted("Open a Saved Path");
        ImGui::PopFont();
        ImGui::Spacing();
        const auto* documents = state.documents_valid ? &state.documents : context.params->documents;
        const auto count = documents != nullptr
            ? std::min<std::uint32_t>(documents->count,
                                      static_cast<std::uint32_t>(kMaxCampathDocuments))
            : 0u;
        auto saved_count = 0u;
        for (std::uint32_t index = 0; index < count; ++index) {
            if ((documents->entries[index].flags & kCampathDocumentDraft) == 0)
                ++saved_count;
        }
        ImGui::BeginChild("##path_list", ImVec2(0.0F, -46.0F * scale), ImGuiChildFlags_Borders);
        if (saved_count == 0) {
            smvm_theme::EmptyState("No saved paths", "Paths you save appear here.");
        } else {
            for (std::uint32_t index = 0; index < count; ++index) {
                const auto& entry = documents->entries[index];
                if ((entry.flags & kCampathDocumentDraft) != 0)
                    continue;
                const auto matches = (entry.flags & kCampathDocumentMatchesReplay) != 0;
                std::array<char, 128> label{};
                static_cast<void>(std::snprintf(
                    label.data(), label.size(), "%s  %s  %u keyframes%s",
                    entry.name.data(), kMiddleDot, entry.keyframe_count,
                    matches ? "" : "  \xC2\xB7 other replay"));
                ImGui::PushID(static_cast<int>(index));
                const auto selectable_flags = matches
                    ? ImGuiSelectableFlags_None
                    : ImGuiSelectableFlags_Disabled;
                if (ImGui::Selectable(
                        label.data(), false, selectable_flags, ImVec2(0.0F, 24.0F * scale))) {
                    if (HasUnsavedCampathWork(*context.snapshot)) {
                        state.confirm_action = SmvmConfirmAction::load_path;
                        state.confirm_load_index = static_cast<std::int32_t>(index);
                        state.confirm_pending = true;
                    } else {
                        if (QueueAction(context, SmvmActionType::load_path,
                                        static_cast<std::int32_t>(index)))
                            ImGui::CloseCurrentPopup();
                    }
                }
                if (matches) {
                    smvm_theme::Tooltip("Matches the current replay.");
                } else {
                    smvm_theme::Tooltip("This path belongs to a different replay.");
                }
                ImGui::PopID();
            }
        }
        ImGui::EndChild();
        if (smvm_theme::Button("Cancel", true, ImVec2(80.0F * scale, 0.0F)))
            ImGui::CloseCurrentPopup();
        ImGui::EndPopup();
    }
    if (!picker_open)
        state.picker_requested_list = false;

    // One modal, several destructive actions: the wording has to describe the
    // action that actually queued, and the popup id stays constant so switching
    // actions never leaks an open modal.
    const auto confirming_exit = state.confirm_action == SmvmConfirmAction::exit_deadlock;
    const auto* const confirmation_id = "##smvm_confirmation";
    const auto* const confirmation_title = confirming_exit
        ? "Force-close Deadlock?"
        : "Discard current path changes?";
    const auto* const confirmation_body = confirming_exit
        ? "Deadlock closes immediately. An armed or in-progress recording and any unsaved replay position are lost."
        : "Your current keyframes will be lost. Saved path files are kept.";
    const auto* const confirmation_label = confirming_exit ? "Exit Deadlock" : "Discard";
    if (state.confirm_pending) {
        smvm_theme::OpenConfirmation(confirmation_id);
        state.confirm_pending = false;
    }
    const auto confirmed = smvm_theme::ConfirmationModal(
        confirmation_id,
        confirmation_title,
        confirmation_body,
        confirmation_label);
    if (confirmed == smvm_theme::ConfirmResult::confirm) {
        switch (state.confirm_action) {
            case SmvmConfirmAction::new_path:
                QueueAction(context, SmvmActionType::new_path);
                break;
            case SmvmConfirmAction::close_path:
                QueueAction(context, SmvmActionType::close_path);
                break;
            case SmvmConfirmAction::load_path:
                QueueAction(context, SmvmActionType::load_path, state.confirm_load_index);
                break;
            case SmvmConfirmAction::exit_deadlock:
                QueueAction(context, SmvmActionType::exit_deadlock);
                break;
            case SmvmConfirmAction::none:
            default:
                break;
        }
        state.confirm_action = SmvmConfirmAction::none;
        state.confirm_load_index = -1;
    } else if (confirmed == smvm_theme::ConfirmResult::cancel) {
        state.confirm_action = SmvmConfirmAction::none;
        state.confirm_load_index = -1;
    }
}

// --- Shell ------------------------------------------------------------------

void DrawShell(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    auto& state = *context.state;
    const auto scale = smvm_theme::GetScale();
    const auto viewport_width = context.params->viewport_width;
    const auto viewport_height = context.params->viewport_height;
    const auto opacity = static_cast<float>(std::clamp(snapshot.menu_opacity, 0.65, 1.0));

    auto* background = ImGui::GetBackgroundDrawList();
    const auto dim_alpha = static_cast<ImU32>(std::clamp(89.0F * static_cast<float>(opacity),
                                                         0.0F, 255.0F));
    background->AddRectFilled(
        ImVec2(0.0F, 0.0F), ImVec2(viewport_width, viewport_height), dim_alpha << 24);

    const auto safe = 24.0F * scale;
    const auto window_width = std::clamp(
        state.window_w * scale, 640.0F * scale, viewport_width - (safe * 2.0F));
    const auto window_height = std::clamp(
        state.window_h * scale, 440.0F * scale, viewport_height - (safe * 2.0F));
    if (!state.window_positioned) {
        const auto anchor_right = snapshot.menu_anchor == SmvmMenuAnchor::right;
        const auto x = anchor_right ? viewport_width - safe - window_width : safe;
        const auto y = std::clamp((viewport_height - window_height) * 0.5F, safe,
                                  std::max(safe, viewport_height - safe - window_height));
        ImGui::SetNextWindowPos(ImVec2(x, y), ImGuiCond_Always);
        ImGui::SetNextWindowSize(ImVec2(window_width, window_height), ImGuiCond_Always);
    }
    ImGui::SetNextWindowSizeConstraints(
        ImVec2(std::min(640.0F * scale, viewport_width), std::min(440.0F * scale, viewport_height)),
        ImVec2(viewport_width, viewport_height));

    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, opacity);
    const auto open = ImGui::Begin(
        "##smvm_shell", nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoCollapse |
        ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse |
        ImGuiWindowFlags_NoSavedSettings);
    if (open) {
        auto position = ImGui::GetWindowPos();
        const auto size = ImGui::GetWindowSize();

        // Blank restart shell: brand, drag region, and close control only.
        ImGui::PushFont(smvm_theme::GetFonts().title);
        ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kAccent));
        ImGui::TextUnformatted("SMVM");
        ImGui::PopStyleColor();
        ImGui::PopFont();
        ImGui::SameLine();
        const auto close_width = 30.0F * scale;
        const auto remaining = ImGui::GetContentRegionAvail().x;
        ImGui::InvisibleButton(
            "##header_drag",
            ImVec2(std::max(8.0F, remaining - close_width), 22.0F * scale));
        if (ImGui::IsItemActive() && ImGui::IsMouseDragging(ImGuiMouseButton_Left)) {
            const auto& io = ImGui::GetIO();
            ImGui::SetWindowPos(ImVec2(position.x + io.MouseDelta.x,
                                       position.y + io.MouseDelta.y));
        }
        ImGui::SameLine(0.0F, 0.0F);
        if (smvm_theme::Button(kTimes, true, ImVec2(close_width, 0.0F)))
            SmvmCloseMenu();
        smvm_theme::Tooltip("Close menu");
        ImGui::Separator();

        // The owner is restarting the product UI. Keep one empty page and do
        // not render any of the previous workflow, document, or settings UI.
        state.page = SmvmPage::campath;
        state.open_save_as_modal = false;
        state.open_load_picker = false;
        state.picker_requested_list = false;
        state.confirm_pending = false;
        state.confirm_action = SmvmConfirmAction::none;
        state.confirm_load_index = -1;

        // Clamp fully inside the viewport and remember geometry (logical px).
        position = ImGui::GetWindowPos();
        const auto clamped_x = std::clamp(position.x, 0.0F,
                                          std::max(0.0F, viewport_width - size.x));
        const auto clamped_y = std::clamp(position.y, 0.0F,
                                          std::max(0.0F, viewport_height - size.y));
        if (clamped_x != position.x || clamped_y != position.y) {
            ImGui::SetWindowPos(ImVec2(clamped_x, clamped_y));
            position = ImVec2(clamped_x, clamped_y);
        }
        state.window_x = position.x;
        state.window_y = position.y;
        if (scale > 0.0F) {
            state.window_w = std::clamp(size.x / scale, 640.0F, 1600.0F);
            state.window_h = std::clamp(size.y / scale, 440.0F, 1200.0F);
        }
        state.window_positioned = true;
    }
    ImGui::End();
    ImGui::PopStyleVar();
}

// --- Toasts + minimal pill --------------------------------------------------

void CopyText(char* destination, const std::size_t capacity, const char* source) noexcept {
    const auto length = std::min(std::strlen(source), capacity - 1);
    if (length > 0)
        std::memcpy(destination, source, length);
    destination[length] = '\0';
}

} // namespace

void UpdateToasts(const SmvmSnapshotPayload& snapshot, SmvmUiState& state) noexcept {
    const auto* status = snapshot.status.data();
    const auto changed = !state.have_last_status ||
        std::strcmp(state.last_status.data(), status) != 0;
    if (!changed)
        return;
    const auto had_status = state.have_last_status;
    CopyText(state.last_status.data(), state.last_status.size(), status);
    state.have_last_status = true;
    if (!had_status || status[0] == '\0' ||
        (snapshot.flags & smvm_snapshot_notifications) == 0)
        return;
    const auto now = GetTickCount64();
    for (auto& toast : state.toasts) {
        if (toast.active && std::strcmp(toast.text.data(), status) == 0) {
            toast.created_ms = now;
            return;
        }
    }
    auto* slot = &state.toasts.front();
    for (auto& toast : state.toasts) {
        if (!toast.active) {
            slot = &toast;
            break;
        }
        if (toast.created_ms < slot->created_ms)
            slot = &toast;
    }
    CopyText(slot->text.data(), slot->text.size(), status);
    slot->created_ms = now;
    slot->active = true;
}

namespace {

void DrawToasts(const FrameContext& context) noexcept {
    auto& state = *context.state;
    const auto scale = smvm_theme::GetScale();
    const auto now = GetTickCount64();
    const auto anchor = context.snapshot->notification_anchor;
    const auto margin = 24.0F * scale;
    const auto viewport = ImVec2(context.params->viewport_width, context.params->viewport_height);
    auto* foreground = ImGui::GetForegroundDrawList();
    const auto& fonts = smvm_theme::GetFonts();

    auto offset = 0.0F;
    for (auto& toast : state.toasts) {
        if (!toast.active)
            continue;
        const auto age = now - toast.created_ms;
        if (age >= kSmvmToastDurationMs) {
            toast.active = false;
            continue;
        }
        auto alpha = 1.0F;
        if (age < 150)
            alpha = static_cast<float>(age) / 150.0F;
        else if (age > kSmvmToastDurationMs - 400)
            alpha = static_cast<float>(kSmvmToastDurationMs - age) / 400.0F;
        ImGui::PushFont(fonts.normal);
        const auto text_size = ImGui::CalcTextSize(toast.text.data());
        ImGui::PopFont();
        const auto box = ImVec2(text_size.x + (24.0F * scale), text_size.y + (14.0F * scale));
        const auto right_side = anchor == SmvmNotificationAnchor::top_right ||
            anchor == SmvmNotificationAnchor::bottom_right;
        const auto bottom = anchor == SmvmNotificationAnchor::bottom_left ||
            anchor == SmvmNotificationAnchor::bottom_right;
        const auto x = right_side ? viewport.x - margin - box.x : margin;
        const auto y = bottom ? viewport.y - margin - box.y - offset : margin + offset;
        const auto apply_alpha = [alpha](const ImU32 color) noexcept {
            const auto a = static_cast<ImU32>(
                static_cast<float>((color >> 24) & 0xFFu) * alpha);
            return (color & 0x00FFFFFFu) | (a << 24);
        };
        foreground->AddRectFilled(
            ImVec2(x, y), ImVec2(x + box.x, y + box.y),
            apply_alpha(smvm_theme::colors::kShell), 6.0F * scale);
        foreground->AddRect(
            ImVec2(x, y), ImVec2(x + box.x, y + box.y),
            apply_alpha(smvm_theme::colors::kHairline), 6.0F * scale);
        foreground->AddRectFilled(
            ImVec2(x, y), ImVec2(x + 3.0F * scale, y + box.y),
            apply_alpha(smvm_theme::colors::kAccent), 6.0F * scale);
        foreground->AddText(
            fonts.normal, fonts.normal->FontSize,
            ImVec2(x + (13.0F * scale), y + (7.0F * scale)),
            apply_alpha(smvm_theme::colors::kText), toast.text.data());
        offset += box.y + (8.0F * scale);
    }
}

void DrawMinimalPill(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if ((snapshot.flags & smvm_snapshot_show_minimal_pill) == 0)
        return;
    const auto scale = smvm_theme::GetScale();
    const auto key = FormatInput(snapshot.menu_key);
    std::array<char, 64> text{};
    static_cast<void>(std::snprintf(text.data(), text.size(), "SMVM %s %s", kMiddleDot,
                                    key.data()));
    const auto& fonts = smvm_theme::GetFonts();
    ImGui::PushFont(fonts.hint);
    const auto text_size = ImGui::CalcTextSize(text.data());
    ImGui::PopFont();
    const auto box = ImVec2(text_size.x + (26.0F * scale), text_size.y + (12.0F * scale));
    const auto safe = 22.0F * scale;
    const auto x = snapshot.menu_anchor == SmvmMenuAnchor::right
        ? context.params->viewport_width - safe - box.x
        : safe;
    const auto y = safe;
    auto* foreground = ImGui::GetForegroundDrawList();
    foreground->AddRectFilled(
        ImVec2(x, y), ImVec2(x + box.x, y + box.y), smvm_theme::colors::kShell, 6.0F * scale);
    foreground->AddRect(
        ImVec2(x, y), ImVec2(x + box.x, y + box.y), smvm_theme::colors::kHairline, 6.0F * scale);
    foreground->AddRectFilled(
        ImVec2(x, y), ImVec2(x + 3.0F * scale, y + box.y), smvm_theme::colors::kAccent,
        6.0F * scale);
    foreground->AddText(
        fonts.hint, fonts.hint->FontSize, ImVec2(x + (12.0F * scale), y + (6.0F * scale)),
        smvm_theme::colors::kText, text.data());
}

void DrawAllBindings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    ImGui::TextWrapped("Click a binding, then press the new key. Use Clear to unbind. Changes save to the launcher. Escape cancels key capture.");
    BindingField("Move forward", snapshot.forward_key, 100);
    BindingField("Move backward", snapshot.backward_key, 101);
    BindingField("Move left", snapshot.left_key, 102);
    BindingField("Move right", snapshot.right_key, 103);
    BindingField("Move up", snapshot.up_key, 104);
    BindingField("Move down", snapshot.down_key, 105);
    BindingField("Fast movement", snapshot.fast_key, 106);
    BindingField("Precision movement", snapshot.precision_key, 107);
    BindingField("Roll left", snapshot.roll_left_key, 108);
    BindingField("Roll right", snapshot.roll_right_key, 109);
    BindingField("Reset roll", snapshot.roll_reset_key, 110);
    BindingField("Main menu", snapshot.menu_key, 111);
    BindingField("Add keyframe", snapshot.add_key, 112);
    BindingField("Delete keyframe", snapshot.delete_key, 113);
    BindingField("Clean footage", snapshot.clean_view_key, 114);
    BindingField("Play path from start", snapshot.play_start_key, 115);
    BindingField("Play path from current", snapshot.play_current_key, 116);
    BindingField("Stop path", snapshot.stop_key, 117);
    BindingField("Undo", snapshot.undo_key, 118);
    BindingField("Redo", snapshot.redo_key, 119);
    BindingField("Show path", snapshot.show_path_key, 120);
    BindingField("Show cameras", snapshot.show_cameras_key, 121);
    BindingField("Emergency restore UI", snapshot.restore_ui_key, 122);
    BindingField("Cycle UI", snapshot.cycle_ui_key, 123);
    BindingField("Toggle free camera", snapshot.toggle_free_camera_key, 124);
    BindingField("Pause replay", snapshot.replay_pause_key, 125);
    BindingField("Show labels", snapshot.show_labels_key, 126);
    BindingField("Step back", snapshot.step_back_key, 127);
    BindingField("Step forward", snapshot.step_forward_key, 128);
    BindingField("Effects panel", snapshot.effects_key, 129);
    BindingField("Start ready cinematic", snapshot.cinematic_start_key, 130);
    BindingField("Slower replay", snapshot.playback_slower_key, 131);
    BindingField("Faster replay", snapshot.playback_faster_key, 132);
    BindingField("Cancel recording / camera", snapshot.cancel_key, 133);
    BindingField("Slower camera", snapshot.camera_slower_key, 134);
    BindingField("Faster camera", snapshot.camera_faster_key, 135);
}

void DrawMovieRecordingMenu(const FrameContext& context) noexcept {
    if (!context.params->menu_open && !context.params->effects_open)
        return;

    const auto& snapshot = *context.snapshot;
    auto& state = *context.state;
    const auto effects = context.params->effects_open;
    auto& page = effects ? state.effects_page : state.page;
    if (page == SmvmPage::replay || page == SmvmPage::camera)
        page = SmvmPage::capture;
    const auto scale = smvm_theme::GetScale();
    // A viewport-bounded inspector stays narrow at high DPI and uses the
    // available vertical space above the replay timeline.
    const auto margin = std::min(16.0F * scale, 24.0F);
    const auto width = effects ? std::min(480.0F * scale, context.params->viewport_width * 0.38F) : std::min(660.0F * scale, context.params->viewport_width - 32.0F * scale);
    const auto timeline = ComputeReplayTimelineGeometry(
        context.params->viewport_width, context.params->viewport_height, scale, false);
    const auto height = effects ? std::max(240.0F, timeline.y - margin * 2.0F) : std::min(570.0F * scale, context.params->viewport_height - 32.0F * scale);
    const auto x = effects ? context.params->viewport_width - width - margin : (context.params->viewport_width - width) * .5F;
    const auto y = effects ? margin : (context.params->viewport_height - height) * .5F;

    ImGui::SetNextWindowPos(ImVec2(x, y), effects ? ImGuiCond_Always : ImGuiCond_Once);
    ImGui::SetNextWindowSize(ImVec2(width, height), ImGuiCond_Always);
    if (effects) {
    // Scope the reference's compact charcoal/blue skin to this inspector.
    ImGui::PushFont(smvm_theme::GetFonts().mono);
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, 1.0F);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(8.0F * scale, 6.0F * scale));
    ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(2.0F * scale, 2.0F * scale));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(4.0F * scale, 4.0F * scale));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0.0F);
    ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 3.0F * scale);
    ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 3.0F * scale);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 1.0F);
    ImGui::PushStyleVar(ImGuiStyleVar_ChildBorderSize, 1.0F);
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(.12F, .12F, .12F, 1));
    ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(.115F, .115F, .115F, 1));
    ImGui::PushStyleColor(ImGuiCol_Border, ImVec4(.34F, .34F, .34F, 1));
    ImGui::PushStyleColor(ImGuiCol_FrameBg, ImVec4(.15F, .16F, .16F, 1));
    ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(.23F, .32F, .46F, 1));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(.32F, .44F, .62F, 1));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, ImVec4(.36F, .50F, .72F, 1));
    ImGui::PushStyleColor(ImGuiCol_Header, ImVec4(.23F, .32F, .46F, 1));
    ImGui::PushStyleColor(ImGuiCol_HeaderHovered, ImVec4(.32F, .44F, .62F, 1));
    ImGui::PushStyleColor(ImGuiCol_HeaderActive, ImVec4(.36F, .50F, .72F, 1));
    ImGui::PushStyleColor(ImGuiCol_CheckMark, ImVec4(.38F, .57F, .86F, 1));
    ImGui::PushStyleColor(ImGuiCol_SliderGrab, ImVec4(.38F, .57F, .86F, 1));
    ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(.80F, .80F, .80F, 1));
    } else {
        ImGui::PushStyleVar(ImGuiStyleVar_Alpha, .97F);
        ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(20.0F * scale, 16.0F * scale));
    }
    const auto open = ImGui::Begin(
        effects ? "##deadlockmvm_effects" : "Deadlock MVM - drag to move###deadlockmvm_movie_tools",
        nullptr,
        (effects ? ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoMove : 0) | ImGuiWindowFlags_NoResize |
            ImGuiWindowFlags_NoCollapse |
            ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse |
            ImGuiWindowFlags_NoSavedSettings);
    if (open) {
        const auto active =
            (snapshot.movie_recording_flags & movie_recording_active) != 0;
        const auto armed =
            (snapshot.movie_recording_flags & movie_recording_armed) != 0;
        const auto finalizing =
            (snapshot.movie_recording_flags & movie_recording_finalizing) != 0;
        const auto ready = context.params->cinematic_start_ready;
        const auto tab = [&page, &state, effects](const char* label, SmvmPage target, int info = 0) {
            const auto selected = page == target && (!effects || state.look_info_tab == info);
            if (selected) ImGui::PushStyleColor(ImGuiCol_Button, effects ? ImVec4(.36F,.50F,.72F,1) : smvm_theme::Vec4(smvm_theme::colors::kAccent));
            if (ImGui::Button(label)) { page = target; state.look_info_tab = info; }
            if (selected) ImGui::PopStyleColor();
            ImGui::SameLine();
        };
        if (effects) {
            tab("Home", SmvmPage::visuals);
            tab("Keybinds", SmvmPage::campath);
            tab("Stats", SmvmPage::visuals, 1);
            tab("About", SmvmPage::visuals, 2);
        } else {
            tab("RECORD", SmvmPage::capture);
            tab("PASSES", SmvmPage::settings);
            tab("CAMPATHS", SmvmPage::campath);
        }
        ImGui::NewLine();
        ImGui::Separator();

        const auto draw_recording_option = [&context, &snapshot](
            const char* label,
            const std::uint32_t flag,
            const MovieRecordingOption option,
            const char* tooltip) noexcept {
            auto enabled = (snapshot.movie_recording_flags & flag) != 0;
            if (ImGui::Checkbox(label, &enabled))
                QueueAction(
                    context,
                    SmvmActionType::set_movie_recording_option,
                    static_cast<std::int32_t>(option),
                    -1,
                    enabled ? 1.0 : 0.0);
            smvm_theme::Tooltip(tooltip);
        };
        const auto draw_pass = [&context, &snapshot](
            const char* label,
            const std::uint32_t flag,
            const std::int32_t index,
            const char* tooltip) noexcept {
            auto enabled = (snapshot.movie_capture_pass_flags & flag) != 0;
            const auto is_last_pass = enabled && snapshot.movie_capture_pass_flags == flag;
            ImGui::BeginDisabled(is_last_pass);
            if (ImGui::Checkbox(label, &enabled))
                QueueAction(
                    context,
                    SmvmActionType::set_movie_capture_pass,
                    index,
                    -1,
                    enabled ? 1.0 : 0.0);
            ImGui::EndDisabled();
            smvm_theme::Tooltip(
                is_last_pass ? "Keep at least one file." : tooltip);
        };

        ImGui::BeginChild(
            "##movie_setup_page",
            ImVec2(0.0F, std::max(80.0F, ImGui::GetContentRegionAvail().y -
                (effects || page == SmvmPage::campath ? 0.0F : 150.0F * scale))),
            false,
            ImGuiWindowFlags_NoBackground);
        ImGui::BeginDisabled((active || armed || finalizing) && page != SmvmPage::campath);
        if (effects && page == SmvmPage::campath) {
            DrawAllBindings(context);
        } else if (!effects && page == SmvmPage::campath) {
            DrawCampathPage(context);
        } else if (page == SmvmPage::capture) {
            auto preset = static_cast<int>(snapshot.movie_recording_preset);
            ImGui::SetNextItemWidth(250.0F * scale);
            if (ImGui::Combo(
                    "Preset",
                    &preset,
                    "Best Quality\0Quick AVI\0World + Depth + Green Screen\0Custom\0Green Screen\0")) {
                QueueAction(context, SmvmActionType::set_movie_recording_preset, preset);
            }
            smvm_theme::Tooltip("Pick a starting setup.");

            auto output = static_cast<int>(snapshot.movie_output_mode);
            ImGui::SetNextItemWidth(250.0F * scale);
            const auto greenscreen_plate =
                (snapshot.movie_capture_pass_flags &
                 movie_capture_pass_greenscreen_free_camera) != 0;
            ImGui::BeginDisabled(greenscreen_plate);
            if (ImGui::Combo("Output", &output, "TGA + WAV\0AVI + WAV\0TGA + AVI + WAV\0"))
                QueueAction(context, SmvmActionType::set_movie_output_mode, output);
            ImGui::EndDisabled();
            if (greenscreen_plate)
                smvm_theme::Tooltip("Green Screen saves as AVI.");

            auto output_resolution = static_cast<int>(snapshot.movie_output_resolution);
            ImGui::SetNextItemWidth(250.0F * scale);
            if (ImGui::Combo(
                    "Resolution",
                    &output_resolution,
                    "Game resolution\0Full HD - 1920 x 1080\0")) {
                QueueAction(
                    context,
                    SmvmActionType::set_movie_output_resolution,
                    output_resolution);
            }
            smvm_theme::Tooltip(
                "Full HD saves every pass at 1920x1080.");
            if (snapshot.movie_output_resolution == MovieOutputResolution::full_hd) {
                ImGui::TextDisabled(
                    "Game %ux%u  ->  output 1920x1080%s",
                    static_cast<unsigned>(std::max(0.0F, context.params->viewport_width)),
                    static_cast<unsigned>(std::max(0.0F, context.params->viewport_height)),
                    context.params->viewport_width == 1920.0F &&
                            context.params->viewport_height == 1080.0F
                        ? " (native)"
                        : " (scaled)");
            }

            if (!state.movie_capture_fps_initialized ||
                state.movie_capture_fps_snapshot != snapshot.movie_recording_fps) {
                state.movie_capture_fps = static_cast<std::int32_t>(snapshot.movie_recording_fps);
                state.movie_capture_fps_snapshot = snapshot.movie_recording_fps;
                state.movie_capture_fps_initialized = true;
            }
            ImGui::SetNextItemWidth(110.0F * scale);
            ImGui::InputInt("Capture FPS", &state.movie_capture_fps, 1, 10);
            if (ImGui::IsItemDeactivatedAfterEdit()) {
                state.movie_capture_fps = std::clamp(state.movie_capture_fps, 1, 1000);
                QueueAction(
                    context,
                    SmvmActionType::set_movie_recording_fps,
                    -1,
                    -1,
                    static_cast<double>(state.movie_capture_fps));
            }
            ImGui::SameLine();
            constexpr std::array<std::int32_t, 6> fps_choices{24, 30, 60, 120, 300, 600};
            for (std::size_t index = 0; index < fps_choices.size(); ++index) {
                if (index > 0)
                    ImGui::SameLine();
                std::array<char, 12> label{};
                static_cast<void>(std::snprintf(label.data(), label.size(), "%d", fps_choices[index]));
                if (ImGui::SmallButton(label.data()))
                    QueueAction(
                        context,
                        SmvmActionType::set_movie_recording_fps,
                        -1,
                        -1,
                        static_cast<double>(fps_choices[index]));
            }
            const auto cinematic_speed =
                snapshot.timescale > 0.0 && std::isfinite(snapshot.timescale)
                    ? snapshot.timescale
                    : 1.0;
            // The saved clip already carries the cinematic slowdown, so its
            // declared frame rate is the sampling rate scaled by the preview
            // speed instead of the raw capture rate.
            const auto clip_fps = std::max(
                1.0,
                static_cast<double>(snapshot.movie_recording_fps) * cinematic_speed);
            if (snapshot.movie_output_mode != MovieOutputMode::image_sequence &&
                (snapshot.movie_capture_pass_flags & movie_capture_pass_beauty) != 0) {
                const auto output_dimensions = ResolveMovieOutputDimensions(
                    snapshot.movie_output_resolution,
                    static_cast<std::uint32_t>(std::max(0.0F, context.params->viewport_width)),
                    static_cast<std::uint32_t>(std::max(0.0F, context.params->viewport_height)));
                const auto gib_per_second =
                    static_cast<double>(output_dimensions.width) *
                    static_cast<double>(output_dimensions.height) * 3.0 *
                    static_cast<double>(snapshot.movie_recording_fps) /
                    (1024.0 * 1024.0 * 1024.0);
                const auto world_avi_count =
                    ((snapshot.movie_capture_pass_flags & movie_capture_pass_beauty) != 0 ? 1u : 0u) +
                    ((snapshot.movie_capture_pass_flags & movie_capture_pass_world_depth_avi) != 0 ? 1u : 0u);
                const auto chroma_avi_count =
                    (snapshot.movie_capture_pass_flags &
                     movie_capture_pass_greenscreen_free_camera) != 0 ? 1u : 0u;
                const auto peak_avi_count = std::max(1u, std::max(world_avi_count, chroma_avi_count));
                ImGui::TextDisabled(
                    "%.0f FPS clip at %.2fx speed  |  ~%.1f GiB/s",
                    clip_fps,
                    cinematic_speed,
                    gib_per_second * peak_avi_count);
            } else {
                ImGui::TextDisabled(
                    "%.0f FPS clip at %.2fx speed",
                    clip_fps,
                    cinematic_speed);
            }
            ImGui::TextColored(
                smvm_theme::Vec4(smvm_theme::colors::kSuccess),
                "Cinematic speed: %.2fx",
                cinematic_speed);

            draw_recording_option(
                "Post-processing off",
                movie_recording_disable_post_processing,
                MovieRecordingOption::disable_post_processing,
                "Saves a cleaner source for color work.");
            draw_recording_option(
                "Mute dialogue",
                movie_recording_mute_dialogue,
                MovieRecordingOption::mute_dialogue,
                "Keeps other game sounds.");
        } else if (page == SmvmPage::settings) {
            ImGui::TextUnformatted("Choose your files");
            draw_pass(
                "World",
                movie_capture_pass_beauty,
                0,
                "Normal color video.");
            draw_pass(
                "Depth",
                movie_capture_pass_world_depth_pfm,
                1,
                "High-quality PFM files for effects.");
            draw_pass(
                "Depth Video",
                movie_capture_pass_world_depth_avi,
                2,
                "Grayscale depth AVI for your editor.");
            draw_pass(
                "Green Screen",
                movie_capture_pass_greenscreen_free_camera,
                3,
                "Records a second matching play on green.");
            ImGui::Spacing();
            if ((snapshot.movie_capture_pass_flags & movie_capture_pass_beauty) != 0 &&
                (snapshot.movie_capture_pass_flags &
                 movie_capture_pass_greenscreen_free_camera) != 0) {
                ImGui::TextColored(
                    smvm_theme::Vec4(smvm_theme::colors::kAccent),
                    "RECORDS TWICE: WORLD + DEPTH, THEN GREEN SCREEN");
            }
            ImGui::TextDisabled("Stopped or incomplete files are marked PARTIAL.");
        } else if (page == SmvmPage::visuals) {
            if (state.look_info_tab == 1) {
                ImGui::TextWrapped("%s", context.params->look_status);
                ImGui::Separator();
                ImGui::Text("Capture target: %u FPS", snapshot.movie_recording_fps);
                ImGui::TextWrapped("GPU timings measure effects only. Game rendering, source recovery and capture readback are excluded.");
            } else if (state.look_info_tab == 2) {
                ImGui::TextUnformatted("Deadlock MVM - In-house Reshade");
                auto thirds = (snapshot.movie_tool_flags & movie_tool_rule_of_thirds) != 0;
                if (ImGui::Checkbox("Rule of thirds preview guide", &thirds))
                    QueueAction(context, SmvmActionType::set_rule_of_thirds, -1, -1, thirds ? 1.0 : 0.0);

                ImGui::Separator();
                ImGui::TextWrapped("Integrated D3D11 post-processing. Effects apply before the editor and Beauty export; raw depth and chroma stay clean.");
                ImGui::TextWrapped("SDR color grading, bloom, 3D CUBE LUT, sharpening, vignette and deterministic grain. Depth effects require validated scene depth.");

                ImGui::Separator();
                // This tab is informational text, so a bare full-width button
                // here was one misclick away from killing the live session.
                if (smvm_theme::DangerButton(
                        "Exit Deadlock", true,
                        ImVec2(ImGui::GetContentRegionAvail().x, 0.0F))) {
                    state.confirm_action = SmvmConfirmAction::exit_deadlock;
                    state.confirm_pending = true;
                }
                smvm_theme::Tooltip("Force-closes the game immediately. Asks for confirmation first.");
            } else {
            const auto& look = snapshot.look;
            ImGui::SetNextItemWidth(ImGui::GetContentRegionAvail().x - 54.0F * scale);
            if (ImGui::BeginCombo("##look_preset", snapshot.look_preset_name[0] ? snapshot.look_preset_name.data() : "Neutral")) {
                constexpr std::array<const char*, 6> names{"Neutral", "Clean Cinematic", "Warm Film", "Cool Night", "Moody Contrast", "Soft Dream"};
                for (std::size_t i = 0; i < names.size(); ++i)
                    if (ImGui::Selectable(names[i])) QueueAction(context, SmvmActionType::select_look_preset, static_cast<std::int32_t>(i));
                ImGui::EndCombo();
            }
            ImGui::SameLine();
            if (ImGui::Button("+")) QueueAction(context, SmvmActionType::save_look_preset, 1);
            smvm_theme::Tooltip("Save As / Duplicate look");
            ImGui::SameLine();
            if (ImGui::Button("...")) ImGui::OpenPopup("Preset options");
            smvm_theme::Tooltip("Preset options");
            if (ImGui::BeginPopup("Preset options")) {
            if (ImGui::SmallButton("Save look")) QueueAction(context, SmvmActionType::save_look_preset, 0);
            ImGui::SameLine();
            if (ImGui::SmallButton("Save As / Duplicate")) QueueAction(context, SmvmActionType::save_look_preset, 1);
            if (ImGui::SmallButton("Rename look")) {
                state.look_rename = snapshot.look_preset_name;
                ImGui::OpenPopup("Rename Reshade look");
            }
            if (ImGui::BeginPopup("Rename Reshade look")) {
                ImGui::InputText("Name", state.look_rename.data(), 49);
                if (ImGui::Button("Apply name")) {
                    QueueAction(context, SmvmActionType::save_look_preset, 2, -1, 0, state.look_rename.data());
                    ImGui::CloseCurrentPopup();
                }
                ImGui::TextDisabled("Factory definitions are protected; save a user copy.");
                ImGui::EndPopup();
            }
            if (ImGui::SmallButton("Open / Import look")) QueueAction(context, SmvmActionType::import_look_preset);
            ImGui::SameLine();
            if (ImGui::SmallButton("Export look")) QueueAction(context, SmvmActionType::export_look_preset);
                ImGui::EndPopup();
            }

            if (ImGui::SmallButton("Load LUT...")) QueueAction(context, SmvmActionType::load_look_lut);
            ImGui::Separator();
            auto look_enabled = look.enabled != 0;
            if (ImGui::Checkbox("Enable effects", &look_enabled))
                QueueAction(context, SmvmActionType::set_look_enabled, -1, -1, look_enabled ? 1.0 : 0.0);
            if (snapshot.look_modified) ImGui::TextDisabled("Modified (unsaved)");
            if (ImGui::SmallButton("Reset Reshade")) QueueAction(context, SmvmActionType::reset_look);
            smvm_theme::Tooltip("Resets only Reshade. Fog, camera and capture settings are preserved.");
            ImGui::SameLine();
            ImGui::Button("Hold: before / after");
            state.look_compare = ImGui::IsItemActive();
            smvm_theme::Tooltip("Temporary ungraded preview. The recorded look is unchanged.");
            const auto search_buttons_width = ImGui::CalcTextSize("Active to top").x +
                ImGui::CalcTextSize("Collapse all").x + ImGui::GetStyle().FramePadding.x * 4 +
                ImGui::GetStyle().ItemSpacing.x * 2;
            ImGui::SetNextItemWidth(std::max(60.0F * scale, ImGui::GetContentRegionAvail().x - search_buttons_width));
            ImGui::InputTextWithHint("##effect_search", "Search", state.look_search.data(), state.look_search.size());
            ImGui::SameLine();
            if (ImGui::Button("Active to top")) state.look_active_first = !state.look_active_first;
            ImGui::SameLine();
            if (ImGui::Button("Collapse all")) state.look_collapse_all = true;
            const ImGuiTextFilter filter(state.look_search.data());
            constexpr std::array<const char*, 8> effect_names{"Fog", "Color", "Bloom", "LUT", "Sharpen", "Vignette", "Grain", "Depth of Field"};
            const std::array<bool, 8> enabled{
                (snapshot.movie_tool_flags & movie_tool_custom_fog) != 0,
                look.enabled != 0 && (look.exposure != 0 || look.contrast != 1 || look.saturation != 1 || look.temperature != 0 || look.tint != 0 || look.lift_r != 0 || look.lift_g != 0 || look.lift_b != 0 || look.gamma_r != 1 || look.gamma_g != 1 || look.gamma_b != 1 || look.gain_r != 1 || look.gain_g != 1 || look.gain_b != 1 || look.shadows != 0 || look.highlights != 0 || look.vibrance != 0),
                look.enabled != 0 && look.bloom_intensity > 0,
                look.enabled != 0 && look.lut_intensity > 0 && look.lut_size > 0,
                look.enabled != 0 && look.sharpen > 0,
                look.enabled != 0 && look.vignette > 0,
                look.enabled != 0 && look.grain_strength > 0,
                state.dof_enabled};
            const auto list_height = std::max(110.0F * scale, ImGui::GetContentRegionAvail().y * .43F);
            ImGui::BeginChild("##effects_list", ImVec2(0, list_height), true);
            for (int pass = 0; pass < (state.look_active_first ? 2 : 1); ++pass) {
            for (std::size_t i = 0; i < effect_names.size(); ++i) {
                    if (!filter.PassFilter(effect_names[i]) || (state.look_active_first && enabled[i] != (pass == 0))) continue;
                    ImGui::PushID(static_cast<int>(i));
                    auto on = enabled[i];
                    ImGui::BeginDisabled(i == 3 && look.lut_size == 0);
                    if (ImGui::Checkbox("##enabled", &on)) {
                        if (i == 0) QueueAction(context, SmvmActionType::set_custom_fog_enabled, -1, -1, on ? 1 : 0);
                        else if (i == 7) {
                            // Native-owned: no wire action, no Reshade grading change.
                            state.dof_enabled = on;
                        }
                        else {
                            if (on && !look.enabled) QueueAction(context, SmvmActionType::set_look_enabled, -1, -1, 1);
                            if (i == 1) {
                                const LookSettings neutral{};
                                const auto color_differs = [&neutral](const LookSettings& values) {
                                    return std::memcmp(reinterpret_cast<const char*>(&values) + 20,
                                        reinterpret_cast<const char*>(&neutral) + 20, 17 * sizeof(float)) != 0;
                                };
                                if (!on || (!look.enabled && color_differs(look))) state.look_disabled_values = look;
                                for (int field = 1; field <= 17; ++field) {
                                    float value{};
                                    const auto& source = on ? state.look_disabled_values : neutral;
                                    std::memcpy(&value, reinterpret_cast<const char*>(&source) + 16 + field * 4, sizeof(value));
                                    if (on && field == 2 && !color_differs(source)) value = 1.06F;
                                    QueueAction(context, SmvmActionType::set_look_value, field, -1, value);
                                }
                            } else {
                                constexpr std::array<int, 7> fields{0, 0, 20, 25, 22, 23, 24};
                                const std::array<float, 7> current{0, 0, look.bloom_intensity, look.lut_intensity,
                                    look.sharpen, look.vignette, look.grain_strength};
                                if (!on) state.look_effect_restore[i] = current[i];
                                const auto restored = current[i] > 0 ? current[i] : state.look_effect_restore[i];
                                QueueAction(context, SmvmActionType::set_look_value, fields[i], -1, on ? restored : 0);
                            }
                        }
                    }
                    ImGui::EndDisabled();
                    ImGui::SameLine();
                    if (!enabled[i]) ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(.48F,.48F,.48F,1));
                    if (ImGui::Selectable(effect_names[i], state.look_jump == static_cast<int>(i)) && enabled[i]) state.look_jump = static_cast<int>(i);
                    if (!enabled[i]) ImGui::PopStyleColor();
                    ImGui::PopID();
                }
            }
            ImGui::EndChild();
            ImGui::Separator();
            ImGui::BeginChild("##effect_parameters", ImVec2(0, -30.0F * scale), true);
            const auto section = [&](const char* label, int group) {
                if (!enabled[group]) return false;
                if (state.look_collapse_all) ImGui::SetNextItemOpen(false, ImGuiCond_Always);
                if (state.look_jump == group) ImGui::SetNextItemOpen(true, ImGuiCond_Always);
                const auto expanded = ImGui::CollapsingHeader(label, ImGuiTreeNodeFlags_DefaultOpen);
                if (state.look_jump == group) { ImGui::SetScrollHereY(0); state.look_jump = -1; }
                return expanded;
            };

            const auto greenscreen = snapshot.greenscreen_mode == GreenscreenMode::free_camera;
            if (section("Fog", 0)) {
            auto fog_enabled = (snapshot.movie_tool_flags & movie_tool_custom_fog) != 0;
            ImGui::BeginDisabled(greenscreen);
            if (ImGui::Checkbox("Enable Fog", &fog_enabled))
                QueueAction(
                    context,
                    SmvmActionType::set_custom_fog_enabled,
                    -1,
                    -1,
                    fog_enabled ? 1.0 : 0.0);
            ImGui::TextDisabled("Appears in the game and World video.");
            ImGui::BeginDisabled(!fog_enabled);
            auto fog_value = 0.0F;
            const auto maximum_fog_start = static_cast<float>(std::clamp(
                snapshot.custom_fog_end - 100.0, -250.0, 4000.0));
            if (smvm_theme::SliderInput(
                    "fog_start", static_cast<float>(snapshot.custom_fog_start),
                    -250.0F, maximum_fog_start, fog_value, "%.0f", true, "Start"))
                QueueAction(context, SmvmActionType::set_custom_fog_value, 0, -1, fog_value);
            const auto minimum_fog_end = static_cast<float>(std::clamp(
                snapshot.custom_fog_start + 100.0, 100.0, 8000.0));
            if (smvm_theme::SliderInput(
                    "fog_end", static_cast<float>(snapshot.custom_fog_end),
                    minimum_fog_end, 8000.0F, fog_value, "%.0f", true, "End"))
                QueueAction(context, SmvmActionType::set_custom_fog_value, 1, -1, fog_value);
            if (smvm_theme::SliderInput(
                    "fog_density", static_cast<float>(snapshot.custom_fog_max_density),
                    0.05F, 1.0F, fog_value, "%.2f", true, "Density"))
                QueueAction(context, SmvmActionType::set_custom_fog_value, 2, -1, fog_value);
            if (smvm_theme::SliderInput(
                    "fog_exponent", static_cast<float>(snapshot.custom_fog_exponent),
                    0.1F, 4.0F, fog_value, "%.2f", true, "Falloff"))
                QueueAction(context, SmvmActionType::set_custom_fog_value, 3, -1, fog_value);
            smvm_theme::Tooltip("Shapes how quickly fog builds between Start and End.");
            std::array<float, 3> fog_color{
                static_cast<float>((snapshot.custom_fog_color_rgb >> 16) & 0xFFu) / 255.0F,
                static_cast<float>((snapshot.custom_fog_color_rgb >> 8) & 0xFFu) / 255.0F,
                static_cast<float>(snapshot.custom_fog_color_rgb & 0xFFu) / 255.0F,
            };
            if (ImGui::ColorEdit3("Fog color", fog_color.data(), ImGuiColorEditFlags_NoInputs)) {
                const auto red = static_cast<std::uint32_t>(std::clamp(fog_color[0], 0.0F, 1.0F) * 255.0F + 0.5F);
                const auto green = static_cast<std::uint32_t>(std::clamp(fog_color[1], 0.0F, 1.0F) * 255.0F + 0.5F);
                const auto blue = static_cast<std::uint32_t>(std::clamp(fog_color[2], 0.0F, 1.0F) * 255.0F + 0.5F);
                QueueAction(
                    context,
                    SmvmActionType::set_custom_fog_color,
                    static_cast<std::int32_t>((red << 16) | (green << 8) | blue));
            }
            ImGui::EndDisabled();
            ImGui::EndDisabled();
            if (greenscreen)
                ImGui::TextDisabled("Fog is off during Green Screen.");
            }
            ImGui::BeginDisabled(!look_enabled || greenscreen);
            const auto look_slider = [&](const char* label, const float current, const float minimum, const float maximum, const std::int32_t index) {
                auto value = current;
                ImGui::SetNextItemWidth(ImGui::GetContentRegionAvail().x * .68F);
                if (ImGui::SliderFloat(label, &value, minimum, maximum, "%.3f", ImGuiSliderFlags_AlwaysClamp))
                    QueueAction(context, SmvmActionType::set_look_value, index, -1, value);
                if (ImGui::BeginPopupContextItem(label)) {
                    if (ImGui::Selectable("Reset parameter")) {
                        const LookSettings defaults{};
                        float reset{};
                        if (index >= 0 && index < 25) std::memcpy(&reset, reinterpret_cast<const char*>(&defaults) + 16 + index * 4, sizeof(float));
                        QueueAction(context, SmvmActionType::set_look_value, index, -1, reset);
                    }
                    ImGui::EndPopup();
                }
            };
            if (look_enabled) look_slider("Look strength", look.strength, 0.0F, 1.0F, 0);
            if (section("Color", 1)) {
                look_slider("Exposure", look.exposure, -5.0F, 5.0F, 1);
                look_slider("Contrast", look.contrast, 0.0F, 2.0F, 2);
                look_slider("Saturation", look.saturation, 0.0F, 2.0F, 3);
                look_slider("Temperature", look.temperature, -1.0F, 1.0F, 4);
                look_slider("Tint", look.tint, -1.0F, 1.0F, 5);
                look_slider("Lift R", look.lift_r, -1.0F, 1.0F, 6);
                look_slider("Lift G", look.lift_g, -1.0F, 1.0F, 7);
                look_slider("Lift B", look.lift_b, -1.0F, 1.0F, 8);
                look_slider("Gamma R", look.gamma_r, 0.1F, 4.0F, 9);
                look_slider("Gamma G", look.gamma_g, 0.1F, 4.0F, 10);
                look_slider("Gamma B", look.gamma_b, 0.1F, 4.0F, 11);
                look_slider("Gain R", look.gain_r, 0.0F, 4.0F, 12);
                look_slider("Gain G", look.gain_g, 0.0F, 4.0F, 13);
                look_slider("Gain B", look.gain_b, 0.0F, 4.0F, 14);
                look_slider("Shadows", look.shadows, -1.0F, 1.0F, 15);
                look_slider("Highlights", look.highlights, -1.0F, 1.0F, 16);
                look_slider("Vibrance", look.vibrance, -1.0F, 1.0F, 17);
            }
            if (section("Bloom", 2)) {
                look_slider("Threshold", look.bloom_threshold, 0.0F, 1.0F, 18);
                look_slider("Knee", look.bloom_knee, 0.0F, 1.0F, 19);
                look_slider("Intensity", look.bloom_intensity, 0.0F, 2.0F, 20);
                look_slider("Radius", look.bloom_radius, 0.25F, 4.0F, 21);
                auto quality = static_cast<int>(look.bloom_quality);
                if (ImGui::Combo("Bloom quality", &quality, "Low\0Medium\0High\0")) QueueAction(context, SmvmActionType::set_look_value, 26, -1, quality);
            }
            if (section("LUT", 3)) {
                ImGui::TextWrapped("3D CUBE, size 2-33. Trilinear SDR color lookup.");
                if (ImGui::Button("Load .cube")) QueueAction(context, SmvmActionType::load_look_lut);
                ImGui::Text("Loaded cube: %u", look.lut_size);
                ImGui::BeginDisabled(look.lut_size == 0);
                look_slider("LUT intensity", look.lut_intensity, 0.0F, 1.0F, 25);
                ImGui::EndDisabled();
            }
            if (section("Sharpen", 4)) look_slider("Sharpen", look.sharpen, 0.0F, 1.0F, 22);
            if (section("Vignette", 5)) look_slider("Vignette", look.vignette, 0.0F, 1.0F, 23);
            if (section("Grain", 6)) {
                look_slider("Grain Strength", look.grain_strength, 0.0F, 0.2F, 24);
                auto seed = look.grain_seed;
                if (ImGui::InputScalar("Grain seed", ImGuiDataType_U32, &seed)) QueueAction(context, SmvmActionType::set_look_value, 27, -1, static_cast<double>(seed));
                ImGui::TextWrapped("Grain is deterministic; seed is stored in the preset.");
            }
            if (section("Depth of Field", 7)) {
                ImGui::SetNextItemWidth(ImGui::GetContentRegionAvail().x * .68F);
                ImGui::SliderFloat("Focus", &state.dof_focus, 0.0F, 1.0F, "%.3f", ImGuiSliderFlags_AlwaysClamp);
                smvm_theme::Tooltip("Higher is closer to the camera (reversed-Z depth).");
                ImGui::SetNextItemWidth(ImGui::GetContentRegionAvail().x * .68F);
                ImGui::SliderFloat("Defocus", &state.dof_strength, 0.0F, 8.0F, "%.2f", ImGuiSliderFlags_AlwaysClamp);
                smvm_theme::Tooltip("How quickly blur grows away from the focus plane.");
                ImGui::SetNextItemWidth(ImGui::GetContentRegionAvail().x * .68F);
                ImGui::SliderFloat("Max blur", &state.dof_radius, 0.0F, 24.0F, "%.1f px", ImGuiSliderFlags_AlwaysClamp);
                ImGui::TextDisabled(state.dof_depth_available
                    ? "Uses the live world depth."
                    : "Waiting for sampleable world depth...");
            }
            ImGui::EndDisabled();
            if (greenscreen) ImGui::TextDisabled("Reshade is bypassed for clean chroma.");


            state.look_collapse_all = false;
            ImGui::EndChild();
            if (ImGui::Button("Save look", ImVec2(ImGui::GetContentRegionAvail().x * .5F, 0)))
                QueueAction(context, SmvmActionType::save_look_preset, 0);
            ImGui::SameLine();
            if (ImGui::Button("Reset all to default", ImVec2(ImGui::GetContentRegionAvail().x, 0)))
                QueueAction(context, SmvmActionType::reset_look);
            }

        }
        ImGui::EndDisabled();
        ImGui::EndChild();

        if (!effects && page != SmvmPage::campath) {
        ImGui::Separator();
        ImGui::TextDisabled(active || armed || finalizing ? "SAVING TO" : "CAPTURE FOLDER");
        ImGui::SameLine();
        ImGui::TextWrapped("%s", snapshot.movie_capture_path.data());
        if (ImGui::SmallButton("Open folder"))
            QueueAction(context, SmvmActionType::open_movie_capture_folder);
        ImGui::SameLine();
        ImGui::BeginDisabled(active || armed || finalizing);
        if (ImGui::SmallButton("Choose folder"))
            QueueAction(context, SmvmActionType::choose_movie_capture_folder);
        ImGui::EndDisabled();
        if (active) {
            const auto wants_depth =
                (snapshot.movie_active_pass_flags &
                 (movie_capture_pass_world_depth_pfm |
                  movie_capture_pass_world_depth_avi |
                  movie_capture_pass_greenscreen_free_camera)) != 0;
            const auto wants_avi =
                (snapshot.movie_output_mode != MovieOutputMode::image_sequence &&
                 (snapshot.movie_active_pass_flags & movie_capture_pass_beauty) != 0) ||
                (snapshot.movie_active_pass_flags &
                 (movie_capture_pass_world_depth_avi |
                  movie_capture_pass_greenscreen_free_camera)) != 0;
            const auto native_active =
                (context.params->overlay_flags & smvm_overlay_movie_recording_active) != 0;
            const auto depth_available =
                (context.params->overlay_flags & smvm_overlay_world_depth_available) != 0;
            const auto avi_available =
                (context.params->overlay_flags & smvm_overlay_movie_avi_available) != 0;
            const auto capture_failed =
                (context.params->overlay_flags & smvm_overlay_world_depth_failed) != 0;
            const auto depth_incomplete =
                (context.params->overlay_flags & smvm_overlay_world_depth_incomplete) != 0;
            if (capture_failed) {
                ImGui::TextColored(
                    smvm_theme::Vec4(smvm_theme::colors::kError),
                    "RECORDING FAILED - CHECK CAPTURE REPORT");
            } else if (context.params->movie_depth_observer_failed && wants_depth) {
                ImGui::TextColored(
                    smvm_theme::Vec4(smvm_theme::colors::kError),
                    "DEPTH FAILED - WORLD VIDEO CONTINUES");
            } else if (depth_incomplete && wants_depth) {
                ImGui::TextColored(
                    smvm_theme::Vec4(smvm_theme::colors::kWarning),
                    "DEPTH INCOMPLETE - FILE MARKED PARTIAL");
            } else if (context.params->movie_audio_failed) {
                ImGui::TextColored(
                    smvm_theme::Vec4(smvm_theme::colors::kError),
                    "AUDIO FAILED - VIDEO CONTINUES");
            } else if (wants_depth || wants_avi) {
                ImGui::TextDisabled(
                    "VIDEO %s  |  AUDIO %s  |  DEPTH %s  |  AVI %s",
                    native_active ? "ACTIVE" : "STARTING",
                    context.params->movie_audio_active ? "RECORDING" : "STARTING",
                    !wants_depth ? "N/A" :
                        context.params->movie_depth_observer_active ? "ARMED" :
                        depth_available ? "DIRECT" : "WAITING",
                    !wants_avi ? "N/A" : avi_available ? "WRITING" : "WAITING");
            } else {
                ImGui::TextDisabled(
                    "VIDEO %s  |  AUDIO %s",
                    native_active ? "WRITING" : "STARTING",
                    context.params->movie_audio_active ? "RECORDING" : "STARTING");
            }
            if (native_active) {
                ImGui::TextDisabled(
                    "FRAMES  TGA %llu  |  WORLD %llu  |  PFM %llu  |  DEPTH %llu  |  GREEN %llu  /  %llu",
                    static_cast<unsigned long long>(
                        context.params->movie_beauty_tga_frames_written),
                    static_cast<unsigned long long>(
                        context.params->movie_beauty_frames_written),
                    static_cast<unsigned long long>(
                        context.params->movie_depth_pfm_frames_written),
                    static_cast<unsigned long long>(
                        context.params->movie_depth_avi_frames_written),
                    static_cast<unsigned long long>(
                        context.params->movie_depth_key_frames_written),
                    static_cast<unsigned long long>(
                        context.params->movie_frames_observed));
                if (context.params->movie_repeated_visual_samples_captured != 0) {
                    ImGui::TextColored(
                        smvm_theme::Vec4(smvm_theme::colors::kWarning),
                        "CADENCE  VISUAL %llu  |  %llu DUPLICATE PRESENTS SKIPPED  |  QUEUE %u/%u  |  %llu WAITS (MAX %llu MS)",
                        static_cast<unsigned long long>(
                            context.params->movie_repeated_visual_samples_captured),
                        static_cast<unsigned long long>(
                            context.params->movie_repeated_camera_sequences_captured),
                        context.params->movie_maximum_queue_depth,
                        context.params->movie_queue_capacity,
                        static_cast<unsigned long long>(
                            context.params->movie_queue_backpressure_events),
                        static_cast<unsigned long long>(
                            context.params->movie_maximum_queue_wait_microseconds / 1000u));
                } else if (context.params->movie_repeated_camera_sequences_captured != 0) {
                    ImGui::TextDisabled(
                        "%llu DUPLICATE PRESENTS SKIPPED  |  QUEUE %u/%u  |  %llu WAITS",
                        static_cast<unsigned long long>(
                            context.params->movie_repeated_camera_sequences_captured),
                        context.params->movie_maximum_queue_depth,
                        context.params->movie_queue_capacity,
                        static_cast<unsigned long long>(
                            context.params->movie_queue_backpressure_events));
                }
            }
        }
        const auto seek_in_progress =
            (snapshot.flags & smvm_snapshot_replay_seek_in_progress) != 0;
        const auto playing =
            (snapshot.flags & smvm_snapshot_campath_playing) != 0;
        const auto can_arm = (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
            snapshot.keyframe_count >= 3 && !playing && !seek_in_progress;
        if (active) {
            if (smvm_theme::PrimaryButton(
                    "Stop recording",
                    true,
                    ImVec2(ImGui::GetContentRegionAvail().x, 0.0F)))
                QueueAction(context, SmvmActionType::stop_movie_recording);
        } else if (finalizing) {
            smvm_theme::PrimaryButton(
                "Finishing files...",
                false,
                ImVec2(ImGui::GetContentRegionAvail().x, 0.0F));
        } else {
            const auto main_label = armed ? "Cancel recording" :
                (ready ? "Record this cinematic" : "Record next cinematic");
            const auto offer_play_without_recording = ready && !armed;
            if (smvm_theme::PrimaryButton(
                    main_label,
                    armed || can_arm,
                    ImVec2(
                        offer_play_without_recording
                            ? (ImGui::GetContentRegionAvail().x - (8.0F * scale)) * 0.62F
                            : ImGui::GetContentRegionAvail().x,
                        0.0F))) {
                if (QueueAction(
                        context,
                        armed ? SmvmActionType::stop_movie_recording
                              : SmvmActionType::start_movie_recording))
                    SmvmCloseMenu();
            }
            if (offer_play_without_recording) {
                ImGui::SameLine();
                if (ImGui::Button(
                        "Play without recording",
                        ImVec2(ImGui::GetContentRegionAvail().x, 0.0F))) {
                    SmvmCloseMenu();
                }
            }
        }
    }
    }
    ImGui::End();
    if (effects) { ImGui::PopStyleVar(9); ImGui::PopStyleColor(13); ImGui::PopFont(); }
    else ImGui::PopStyleVar(2);
}

void DrawCampathMiniMenu(const FrameContext& context) noexcept {
    if (context.params->path_header == nullptr || context.params->keyframes == nullptr ||
        !ShouldShowCampathPlacementList(
            context.params->has_path,
            context.params->path_header->keyframe_count))
        return;

    const auto count = std::min<std::uint32_t>(
        context.params->path_header->keyframe_count,
        static_cast<std::uint32_t>(kMaxCampathKeyframes));
    if (count == 0)
        return;

    const auto& snapshot = *context.snapshot;
    auto& state = *context.state;
    const auto scale = smvm_theme::GetScale();
    const auto width = 196.0F * scale;
    const auto row_height = 24.0F * scale;
    // Leave a full extra row of breathing room around the list and actions.
    // The previous base height technically fit the rows but clipped the first
    // entry once the header, separators, and footer buttons were laid out.
    // The Save Path row added one more button plus spacing.
    const auto base_height = 140.0F * scale;
    const auto available_height = std::max(
        106.0F * scale,
        context.params->viewport_height - (106.0F * scale));
    auto visible_rows = static_cast<std::uint32_t>(std::max(
        1.0F,
        std::floor((available_height - base_height) / row_height)));
    visible_rows = std::min(visible_rows, count);
    const auto height = base_height + (visible_rows * row_height);
    const auto x = 16.0F * scale;
    const auto centered_y = (context.params->viewport_height - height) * 0.5F;
    const auto y = std::max(16.0F * scale, centered_y);

    ImGui::SetNextWindowPos(ImVec2(x, y), ImGuiCond_Always);
    ImGui::SetNextWindowSize(ImVec2(width, height), ImGuiCond_Always);
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, 0.96F);
    ImGui::PushStyleVar(
        ImGuiStyleVar_WindowPadding,
        ImVec2(12.0F * scale, 9.0F * scale));
    auto window_flags =
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
        ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse |
        ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse |
        ImGuiWindowFlags_NoSavedSettings;
    if (ShouldLockCampathMiniMenuInput(
            context.params->menu_open,
            (snapshot.flags & smvm_snapshot_replay_seek_in_progress) != 0))
        window_flags |= ImGuiWindowFlags_NoInputs;
    const auto open = ImGui::Begin(
        "##smvm_campath_mini_menu",
        nullptr,
        window_flags);
    if (open) {
        const auto& fonts = smvm_theme::GetFonts();
        ImGui::PushFont(fonts.section);
        ImGui::TextColored(smvm_theme::Vec4(smvm_theme::colors::kAccent), "CAMPATHS");
        ImGui::PopFont();

        std::array<char, 32> count_text{};
        static_cast<void>(std::snprintf(
            count_text.data(), count_text.size(), "%u PLACED", count));
        const auto count_width = ImGui::CalcTextSize(count_text.data()).x;
        ImGui::SameLine();
        ImGui::SetCursorPosX(width - (12.0F * scale) - count_width);
        ImGui::TextColored(smvm_theme::Vec4(smvm_theme::colors::kMuted), "%s", count_text.data());
        ImGui::Separator();

        const auto list_height = visible_rows * row_height;
        ImGui::BeginChild(
            "##campath_rows",
            ImVec2(0.0F, list_height),
            false,
            ImGuiWindowFlags_NoBackground);
        for (std::uint32_t index = 0; index < count; ++index) {
            const auto selected = snapshot.selected_keyframe == static_cast<std::int32_t>(index);
            std::array<char, 32> label{};
            std::array<char, 32> tick{};
            static_cast<void>(std::snprintf(label.data(), label.size(), "Campath %u", index + 1));
            static_cast<void>(std::snprintf(
                tick.data(), tick.size(), "T %lld",
                static_cast<long long>(context.params->keyframes[index].demo_tick)));
            ImGui::PushID(static_cast<int>(index));
            if (ImGui::Selectable(
                    "##campath_row",
                    selected,
                    ImGuiSelectableFlags_None,
                    ImVec2(ImGui::GetContentRegionAvail().x, row_height)) &&
                context.params->menu_open) {
                QueueAction(
                    context,
                    SmvmActionType::select_keyframe,
                    static_cast<std::int32_t>(index));
            }
            const auto row_min = ImGui::GetItemRectMin();
            const auto row_max = ImGui::GetItemRectMax();
            auto* draw = ImGui::GetWindowDrawList();
            const auto text_y = row_min.y + (4.0F * scale);
            draw->AddText(
                ImVec2(row_min.x + (4.0F * scale), text_y),
                selected ? smvm_theme::colors::kAccent : smvm_theme::colors::kText,
                label.data());
            const auto tick_width = ImGui::CalcTextSize(tick.data()).x;
            draw->AddText(
                ImVec2(row_max.x - tick_width - (4.0F * scale), text_y),
                smvm_theme::colors::kMuted,
                tick.data());
            if (selected && !context.params->menu_open)
                ImGui::SetScrollHereY(0.5F);
            ImGui::PopID();
        }
        ImGui::EndChild();

        ImGui::Separator();
        const auto playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
        const auto selected = snapshot.selected_keyframe >= 0 &&
            snapshot.selected_keyframe < static_cast<std::int32_t>(count);
        // Save the whole placed keyframe bunch. A path that has never been
        // named opens the Save-As modal so the owner can name it; an existing
        // named path is overwritten in place.
        if (smvm_theme::PrimaryButton(
                "Save Path",
                context.params->menu_open && count >= 2 && !playing,
                ImVec2(ImGui::GetContentRegionAvail().x, 0.0F))) {
            if (snapshot.campath_session == SmvmCampathSession::saved_path) {
                QueueAction(context, SmvmActionType::save_path);
            } else {
                state.save_as_name.fill('\0');
                const auto* current_name = snapshot.path_name.data();
                if (current_name[0] != '\0' &&
                    std::strcmp(current_name, "Untitled Path") != 0) {
                    const auto length = std::min(
                        std::strlen(current_name), state.save_as_name.size() - 1);
                    std::memcpy(state.save_as_name.data(), current_name, length);
                }
                state.open_save_as_modal = true;
            }
        }
        ImGui::Spacing();
        const auto button_width =
            (ImGui::GetContentRegionAvail().x - ImGui::GetStyle().ItemSpacing.x) * 0.5F;
        if (smvm_theme::Button(
                "Delete",
                context.params->menu_open && selected && !playing,
                ImVec2(button_width, 0.0F))) {
            QueueAction(
                context,
                SmvmActionType::delete_keyframe,
                snapshot.selected_keyframe);
        }
        ImGui::SameLine();
        if (smvm_theme::Button(
                "Clear All",
                context.params->menu_open && !playing,
                ImVec2(button_width, 0.0F))) {
            QueueAction(context, SmvmActionType::clear_path);
        }
    }
    ImGui::End();
    ImGui::PopStyleVar(2);
}

void DrawPlaybackSpeedControls(const FrameContext& context) noexcept {
    auto& state = *context.state;
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    if (!state.playback_speed_initialized) {
        state.playback_speed_percent = static_cast<float>(
            PlaybackSpeedFromPercent(snapshot.timescale * 100.0) * 100.0);
        state.playback_speed_snapshot = snapshot.timescale;
        state.playback_speed_initialized = true;
    } else if (!state.playback_speed_input_active &&
               std::abs(state.playback_speed_snapshot - snapshot.timescale) > 0.000001) {
        state.playback_speed_percent = static_cast<float>(
            PlaybackSpeedFromPercent(snapshot.timescale * 100.0) * 100.0);
        state.playback_speed_snapshot = snapshot.timescale;
    }

    ImGui::PushFont(smvm_theme::GetFonts().hint);
    ImGui::TextColored(
        smvm_theme::Vec4(smvm_theme::colors::kMuted),
        "SPEED");
    ImGui::PopFont();
    smvm_theme::Tooltip("Replay playback speed. The Campath remains synchronized to replay time.");

    constexpr std::array<double, 5> presets{0.25, 0.5, 1.0, 2.0, 4.0};
    constexpr std::array<const char*, 5> labels{"1/4x", "1/2x", "1x", "2x", "4x"};
    for (std::size_t index = 0; index < presets.size(); ++index) {
        ImGui::SameLine();
        const auto active = std::abs(snapshot.timescale - presets[index]) < 0.001;
        const auto clicked = active
            ? smvm_theme::PrimaryButton(labels[index], true, ImVec2(42.0F * scale, 0.0F))
            : smvm_theme::Button(labels[index], true, ImVec2(42.0F * scale, 0.0F));
        if (clicked && QueueAction(
                context,
                SmvmActionType::set_timescale,
                -1,
                -1,
                presets[index])) {
            state.playback_speed_percent = static_cast<float>(presets[index] * 100.0);
        }
    }

    ImGui::SameLine();
    ImGui::SetNextItemWidth(72.0F * scale);
    ImGui::PushFont(smvm_theme::GetFonts().mono);
    const auto entered = ImGui::InputFloat(
        "##playback_speed_percent",
        &state.playback_speed_percent,
        0.0F,
        0.0F,
        "%.2f%%",
        ImGuiInputTextFlags_CharsDecimal | ImGuiInputTextFlags_EnterReturnsTrue);
    state.playback_speed_input_active = ImGui::IsItemActive();
    const auto finished_editing = ImGui::IsItemDeactivatedAfterEdit();
    ImGui::PopFont();
    smvm_theme::Tooltip("Custom replay playback percentage (1% to 1000%). Press Enter or click away to apply.");
    if (entered || finished_editing) {
        const auto timescale = PlaybackSpeedFromPercent(state.playback_speed_percent);
        state.playback_speed_percent = static_cast<float>(timescale * 100.0);
        QueueAction(context, SmvmActionType::set_timescale, -1, -1, timescale);
    }
}

void DrawManualFovControl(const FrameContext& context) noexcept {
    auto& state = *context.state;
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    const auto& camera = context.params->rendered_camera != nullptr &&
            ValidateSample(*context.params->rendered_camera)
        ? *context.params->rendered_camera
        : snapshot.camera;
    const auto now = GetTickCount64();
    const auto camera_ready = ValidateSample(camera) &&
        (snapshot.flags & smvm_snapshot_manual_camera_requested) != 0 &&
        (snapshot.flags & smvm_snapshot_manual_camera_active) != 0 &&
        (snapshot.flags & smvm_snapshot_campath_playing) == 0 &&
        (snapshot.flags & smvm_snapshot_replay_seek_in_progress) == 0;

    if (!state.manual_fov_initialized) {
        state.manual_fov = static_cast<float>(camera.fov);
        state.manual_fov_snapshot = camera.fov;
        state.manual_fov_initialized = true;
    } else if (state.manual_fov_pending_until_ms != 0 &&
               std::abs(camera.fov - state.manual_fov) <= 0.05) {
        state.manual_fov_pending_until_ms = 0;
        state.manual_fov_snapshot = camera.fov;
    } else if (!state.manual_fov_input_active &&
               (state.manual_fov_pending_until_ms == 0 ||
                now >= state.manual_fov_pending_until_ms) &&
               std::abs(state.manual_fov_snapshot - camera.fov) > 0.001) {
        state.manual_fov = static_cast<float>(camera.fov);
        state.manual_fov_snapshot = camera.fov;
        state.manual_fov_pending_until_ms = 0;
    }

    ImGui::SameLine();
    ImGui::PushFont(smvm_theme::GetFonts().hint);
    ImGui::TextColored(smvm_theme::Vec4(smvm_theme::colors::kMuted), "FOV");
    ImGui::PopFont();
    ImGui::SameLine();
    ImGui::BeginDisabled(!camera_ready);
    ImGui::SetNextItemWidth(64.0F * scale);
    ImGui::PushFont(smvm_theme::GetFonts().mono);
    const auto entered = ImGui::InputFloat(
        "##manual_fov",
        &state.manual_fov,
        0.0F,
        0.0F,
        "%.1f",
        ImGuiInputTextFlags_CharsDecimal | ImGuiInputTextFlags_EnterReturnsTrue);
    state.manual_fov_input_active = ImGui::IsItemActive();
    const auto finished_editing = ImGui::IsItemDeactivatedAfterEdit();
    ImGui::PopFont();
    smvm_theme::Tooltip("Type the rendered SMVM Free Camera FOV (5 to 170 degrees). Press Enter or click away to apply.");
    if (entered || finished_editing) {
        const auto target = std::clamp(
            static_cast<double>(state.manual_fov), kMinFov, kMaxFov);
        state.manual_fov = static_cast<float>(target);
        if (SmvmSetManualFovTarget(target)) {
            state.manual_fov_snapshot = target;
            state.manual_fov_pending_until_ms = now + 1000;
        }
        if (entered) {
            ImGui::SetKeyboardFocusHere();
            state.manual_fov_input_active = false;
        }
    }
    ImGui::EndDisabled();
}

void DrawReplayPauseIndicator(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    const auto pause_known = (snapshot.flags & smvm_snapshot_pause_known) != 0;
    const auto paused = (snapshot.flags & smvm_snapshot_paused) != 0;
    const auto* label = !pause_known
        ? "REPLAY STATUS..."
        : (paused ? "REPLAY PAUSED" : "REPLAY PLAYING");
    const auto color = !pause_known
        ? smvm_theme::colors::kMuted
        : (paused ? smvm_theme::colors::kWarning : smvm_theme::colors::kSuccess);

    ImGui::SetNextWindowPos(ImVec2(16.0F * scale, 16.0F * scale), ImGuiCond_Always);
    ImGui::SetNextWindowSize(ImVec2(154.0F * scale, 38.0F * scale), ImGuiCond_Always);
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, 0.96F);
    ImGui::PushStyleVar(
        ImGuiStyleVar_WindowPadding,
        ImVec2(12.0F * scale, 10.0F * scale));
    const auto open = ImGui::Begin(
        "##smvm_replay_pause_indicator",
        nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
            ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse |
            ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse |
            ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoInputs);
    if (open) {
        ImGui::PushFont(smvm_theme::GetFonts().section);
        ImGui::TextColored(smvm_theme::Vec4(color), "%s", label);
        ImGui::PopFont();
    }
    ImGui::End();
    ImGui::PopStyleVar(2);
}

// A prominent, always-on-top readiness badge while a movie take is armed. A
// filled red dot means the replay has landed on the first camera and the start
// key will begin the recorded cinematic; a hollow amber dot means the take is
// armed but the free-camera boundary is not ready yet.
void DrawRecordingReadinessBadge(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto armed = (snapshot.movie_recording_flags & movie_recording_armed) != 0;
    if (!armed)
        return;
    const auto ready = context.params->cinematic_start_ready;
    const auto scale = smvm_theme::GetScale();
    const auto start_key = FormatInput(snapshot.cinematic_start_key);
    const auto color = ready ? smvm_theme::colors::kError : smvm_theme::colors::kWarning;
    const auto* label = ready ? "READY TO RECORD" : "RECORDING ARMED";

    ImGui::SetNextWindowPos(
        ImVec2(context.params->viewport_width * 0.5F, 18.0F * scale),
        ImGuiCond_Always,
        ImVec2(0.5F, 0.0F));
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, 0.95F);
    ImGui::PushStyleVar(
        ImGuiStyleVar_WindowPadding,
        ImVec2(12.0F * scale, 9.0F * scale));
    if (ImGui::Begin(
            "##deadlockmvm_record_ready",
            nullptr,
            ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
                ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse |
                ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_AlwaysAutoResize |
                ImGuiWindowFlags_NoInputs)) {
        const auto radius = 6.0F * scale;
        const auto line_height = ImGui::GetTextLineHeight();
        const auto origin = ImGui::GetCursorScreenPos();
        auto* draw = ImGui::GetWindowDrawList();
        const auto center = ImVec2(origin.x + radius, origin.y + (line_height * 0.5F));
        if (ready)
            draw->AddCircleFilled(center, radius, color, 20);
        else
            draw->AddCircle(center, radius, color, 20, 2.0F * scale);
        ImGui::Dummy(ImVec2(radius * 2.0F, line_height));
        ImGui::SameLine();
        ImGui::PushFont(smvm_theme::GetFonts().section);
        ImGui::TextColored(smvm_theme::Vec4(color), "%s", label);
        ImGui::PopFont();
        if (ready && start_key[0] != '\0') {
            ImGui::SameLine();
            ImGui::TextDisabled("PRESS %s", start_key.data());
        }
    }
    ImGui::End();
    ImGui::PopStyleVar(2);
}

void DrawCenteredProgressPrompt(const FrameContext& context, const char* text) noexcept {
    const auto scale = smvm_theme::GetScale();
    const auto& fonts = smvm_theme::GetFonts();
    const auto font_size = fonts.title->FontSize * 1.65F;
    const auto text_size = fonts.title->CalcTextSizeA(font_size, 100000.0F, 0.0F, text);
    const auto band_height = text_size.y + (36.0F * scale);
    const auto band_y = (context.params->viewport_height - band_height) * 0.44F;
    const auto text_position = ImVec2(
        (context.params->viewport_width - text_size.x) * 0.5F,
        band_y + ((band_height - text_size.y) * 0.5F));
    auto* foreground = ImGui::GetForegroundDrawList();
    foreground->AddRectFilled(
        ImVec2(0.0F, band_y),
        ImVec2(context.params->viewport_width, band_y + band_height),
        smvm_theme::colors::kShell);
    foreground->AddLine(
        ImVec2(0.0F, band_y),
        ImVec2(context.params->viewport_width, band_y),
        smvm_theme::colors::kHairline,
        scale);
    foreground->AddLine(
        ImVec2(0.0F, band_y + band_height),
        ImVec2(context.params->viewport_width, band_y + band_height),
        smvm_theme::colors::kHairline,
        scale);
    foreground->AddText(
        fonts.title,
        font_size,
        text_position,
        smvm_theme::colors::kAccent,
        text);
}

void DrawCinematicStartPrompt(const FrameContext& context) noexcept {
    if (context.params->cinematic_start_ready) {
        const auto key = FormatInput(context.snapshot->cinematic_start_key);
        std::array<char, 96> prompt{};
        static_cast<void>(std::snprintf(prompt.data(), prompt.size(), "%s TO START CINEMATIC", key.data()));
        DrawCenteredProgressPrompt(context, prompt.data());
    }
}

void DrawUpdatingTicksPrompt(const FrameContext& context) noexcept {
    if ((context.snapshot->flags & smvm_snapshot_replay_seek_in_progress) != 0)
        DrawCenteredProgressPrompt(context, "UPDATING TICKS");
}

void DrawReplayTimeline(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    auto& state = *context.state;
    // The restart timeline has one stable layout. Legacy replay-bar scale,
    // opacity, and anchor preferences remain protocol-compatible but no longer
    // reshape this product surface.
    const auto scale = smvm_theme::GetScale();
    constexpr auto opacity = 0.96F;
    const auto geometry = ComputeReplayTimelineGeometry(
        context.params->viewport_width,
        context.params->viewport_height,
        scale,
        false);
    ImGui::SetNextWindowPos(ImVec2(geometry.x, geometry.y), ImGuiCond_Always);
    ImGui::SetNextWindowSize(ImVec2(geometry.width, geometry.height), ImGuiCond_Always);
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, opacity);
    auto window_flags =
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoScrollbar |
        ImGuiWindowFlags_NoScrollWithMouse | ImGuiWindowFlags_NoSavedSettings;
    if (ShouldLockReplayTimelineInput(
            context.params->menu_open,
            (snapshot.flags & smvm_snapshot_replay_seek_in_progress) != 0))
        window_flags |= ImGuiWindowFlags_NoInputs;
    const auto open = ImGui::Begin(
        "##smvm_replay_timeline", nullptr, window_flags);
    if (open) {
        const auto seek_in_progress =
            (snapshot.flags & smvm_snapshot_replay_seek_in_progress) != 0;
        const auto paused = (snapshot.flags & smvm_snapshot_paused) != 0;
        const auto pause_known = (snapshot.flags & smvm_snapshot_pause_known) != 0;
        if (smvm_theme::PrimaryButton(
                paused ? "Resume" : "Pause",
                pause_known && !seek_in_progress,
                ImVec2(72.0F * scale, 0.0F))) {
            // Index 1 means reach paused state; 0 means reach playing state.
            // The host keeps -1 as the legacy toggle value.
            QueueAction(
                context,
                SmvmActionType::toggle_replay_pause,
                DesiredReplayPauseActionIndex(paused));
        }
        const auto playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
        const auto offer_cinematic = ShouldOfferPlayCinematic(snapshot.keyframe_count);
        const auto can_play_cinematic =
            offer_cinematic &&
            (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
            (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
            (snapshot.flags & smvm_snapshot_camera_readable) != 0 &&
            !playing && !seek_in_progress;
        ImGui::SameLine();
        const auto cinematic_button_x = ImGui::GetCursorPosX();
        if (smvm_theme::PrimaryButton(
                playing ? "Stop Cinematic" : "Play Cinematic",
                playing || can_play_cinematic,
                ImVec2(118.0F * scale, 0.0F))) {
            if (playing) {
                QueueAction(context, SmvmActionType::stop_campath);
            } else if (QueuePathPlayback(context, SmvmActionType::play_from_start)) {
                SmvmCloseMenu();
            }
        }
        if (!offer_cinematic) {
            const auto add_key = FormatInput(snapshot.add_key);
            std::array<char, 80> placement_hint{};
            static_cast<void>(std::snprintf(
                placement_hint.data(), placement_hint.size(),
                "Place at least 3 cameras with %s.", add_key.data()));
            smvm_theme::Tooltip(placement_hint.data());
        }

        ClearReplayScrubWhenSeekActive(
            seek_in_progress,
            state.replay_scrubbing,
            state.replay_scrub_session_generation);
        if (!state.replay_scrubbing)
            state.replay_scrub_tick = std::max<std::int64_t>(snapshot.current_tick, 0);
        const std::int64_t minimum = 0;
        const auto maximum = std::max<std::int64_t>(snapshot.total_ticks, 1);
        ImGui::SameLine();
        ImGui::SetNextItemWidth(std::max(96.0F * scale, geometry.width - (440.0F * scale)));
        ImGui::BeginDisabled(seek_in_progress);
        if (ImGui::SliderScalar(
                "##replay_progress", ImGuiDataType_S64, &state.replay_scrub_tick,
                &minimum, &maximum, "", ImGuiSliderFlags_AlwaysClamp)) {
            if (!state.replay_scrubbing)
                state.replay_scrub_session_generation = snapshot.replay_session_generation;
            state.replay_scrubbing = true;
        }
        const auto slider_min = ImGui::GetItemRectMin();
        const auto slider_max = ImGui::GetItemRectMax();
        if (context.params->has_path && context.params->path_header != nullptr &&
            context.params->keyframes != nullptr && snapshot.total_ticks > 0) {
            const auto count = std::min<std::uint32_t>(
                context.params->path_header->keyframe_count,
                static_cast<std::uint32_t>(kMaxCampathKeyframes));
            auto* draw = ImGui::GetWindowDrawList();
            for (std::uint32_t index = 0; index < count; ++index) {
                const auto fraction = std::clamp(
                    static_cast<double>(context.params->keyframes[index].demo_tick) /
                        static_cast<double>(snapshot.total_ticks),
                    0.0,
                    1.0);
                const auto marker_x = slider_min.x +
                    static_cast<float>(fraction) * (slider_max.x - slider_min.x);
                const auto selected = snapshot.selected_keyframe == static_cast<std::int32_t>(index);
                draw->AddLine(
                    ImVec2(marker_x, slider_min.y - (2.0F * scale)),
                    ImVec2(marker_x, slider_max.y + (2.0F * scale)),
                    selected ? smvm_theme::colors::kAccent : smvm_theme::colors::kMuted,
                    selected ? 2.0F * scale : 1.0F * scale);
            }
        }
        if (ShouldSubmitReplayScrub(
                state.replay_scrubbing,
                ImGui::IsItemDeactivatedAfterEdit(),
                seek_in_progress)) {
            if (IsReplayUiActionSessionCurrent(
                    true,
                    snapshot.replay_session_generation,
                    state.replay_scrub_session_generation)) {
                QueueAction(
                    context,
                    SmvmActionType::seek_tick,
                    -1,
                    ClampReplayTimelineTick(state.replay_scrub_tick, snapshot.total_ticks));
            }
            state.replay_scrubbing = false;
            state.replay_scrub_session_generation = 0;
        }
        ImGui::EndDisabled();
        ImGui::SameLine();
        std::array<char, 32> current_tick_hint{};
        static_cast<void>(std::snprintf(
            current_tick_hint.data(), current_tick_hint.size(), "%lld",
            static_cast<long long>(std::max<std::int64_t>(snapshot.current_tick, 0))));
        SeekToTickField(
            context,
            current_tick_hint.data(),
            82.0F,
            true,
            !seek_in_progress);
        ImGui::SameLine();
        std::array<char, 40> total_tick_text{};
        static_cast<void>(std::snprintf(
            total_tick_text.data(), total_tick_text.size(), "/ %lld",
            static_cast<long long>(std::max<std::int64_t>(snapshot.total_ticks, 0))));
        ImGui::TextUnformatted(total_tick_text.data());

        ImGui::SetCursorPosX(cinematic_button_x);
        ImGui::Checkbox("Hide UI", &state.hide_ui_during_cinematic);
        smvm_theme::Tooltip(
            "Hide the Campath list and timeline only while the cinematic is playing.");
        ImGui::SameLine();
        DrawPlaybackSpeedControls(context);
        DrawManualFovControl(context);
        SmvmSetReplayTickInputActive(
            state.replay_tick_input_active ||
            state.playback_speed_input_active ||
            state.manual_fov_input_active);
    }
    ImGui::End();
    ImGui::PopStyleVar();
}

void DrawStatusHud(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto free_camera = (snapshot.flags & smvm_snapshot_manual_camera_active) != 0;
    const auto campath_playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
    if ((snapshot.flags & smvm_snapshot_show_status_hud) == 0 ||
        (!free_camera && !campath_playing))
        return;

    const auto& camera = context.params->rendered_camera != nullptr
        ? *context.params->rendered_camera
        : snapshot.camera;
    const auto scale = smvm_theme::GetScale() * static_cast<float>(snapshot.status_hud_scale);
    const auto opacity = static_cast<float>(snapshot.status_hud_opacity);
    const auto anchor = snapshot.status_hud_anchor;
    const auto right = anchor == SmvmNotificationAnchor::top_right ||
        anchor == SmvmNotificationAnchor::bottom_right;
    const auto bottom = anchor == SmvmNotificationAnchor::bottom_left ||
        anchor == SmvmNotificationAnchor::bottom_right;
    const auto margin = 24.0F * scale;
    auto vertical_position = bottom
        ? context.params->viewport_height - margin
        : margin;
    ImGui::SetNextWindowPos(
        ImVec2(right ? context.params->viewport_width - margin : margin,
               vertical_position),
        ImGuiCond_Always, ImVec2(right ? 1.0F : 0.0F, bottom ? 1.0F : 0.0F));
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, opacity);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(12.0F * scale, 7.0F * scale));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(7.0F * scale, 4.0F * scale));
    const auto open = ImGui::Begin(
        "##smvm_status_hud", nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
        ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoMove |
        ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoScrollbar |
        ImGuiWindowFlags_NoScrollWithMouse | ImGuiWindowFlags_NoSavedSettings);
    if (open) {
        ImGui::SetWindowFontScale(static_cast<float>(snapshot.status_hud_scale));
        std::array<char, 160> detail{};
        if (campath_playing) {
            auto segment = 0u;
            auto segment_count = 0u;
            auto progress = 0.0;
            if (context.params->has_path && context.params->path_header != nullptr &&
                context.params->keyframes != nullptr &&
                context.params->path_header->keyframe_count > 1) {
                const auto count = context.params->path_header->keyframe_count;
                segment_count = count - 1;
                while (segment + 1 < count - 1 &&
                       snapshot.current_tick >= context.params->keyframes[segment + 1].demo_tick)
                    ++segment;
                const auto start = context.params->keyframes[segment].demo_tick;
                const auto end = context.params->keyframes[segment + 1].demo_tick;
                if (end > start)
                    progress = std::clamp(
                        static_cast<double>(snapshot.current_tick - start) /
                            static_cast<double>(end - start),
                        0.0, 1.0);
            }
            static_cast<void>(std::snprintf(
                detail.data(), detail.size(),
                "SEGMENT %u/%u  %s  %.0f%%  %s  FOV %.1f%s  %s  ROLL %+.1f%s",
                segment_count > 0 ? segment + 1 : 0, segment_count, kMiddleDot, progress * 100.0,
                kMiddleDot, camera.fov, kDegree, kMiddleDot, camera.roll, kDegree));
            ImGui::PushFont(smvm_theme::GetFonts().section);
            ImGui::TextColored(smvm_theme::Vec4(smvm_theme::colors::kAccent), "PATH");
            ImGui::PopFont();
        } else {
            static_cast<void>(std::snprintf(
                detail.data(), detail.size(),
                "FOV %.1f%s  %s  ROLL %+.1f%s  %s  KEYS %u",
                camera.fov, kDegree, kMiddleDot, camera.roll, kDegree, kMiddleDot,
                snapshot.keyframe_count));
            ImGui::PushFont(smvm_theme::GetFonts().section);
            ImGui::TextColored(smvm_theme::Vec4(smvm_theme::colors::kAccent), "FREE CAM");
            ImGui::PopFont();
        }
        ImGui::SameLine();
        ImGui::PushFont(smvm_theme::GetFonts().mono);
        ImGui::TextUnformatted(detail.data());
        ImGui::PopFont();
    }
    ImGui::End();
    ImGui::PopStyleVar(3);
}

} // namespace

void ObserveReplaySession(
    const SmvmSnapshotPayload* snapshot,
    SmvmUiState& state) noexcept {
    const auto replay_active = snapshot != nullptr &&
        (snapshot->flags & smvm_snapshot_replay_active) != 0 &&
        snapshot->replay_session_generation > 0;
    const auto generation = replay_active
        ? snapshot->replay_session_generation
        : std::uint64_t{0};
    if (generation == state.observed_replay_session_generation)
        return;

    state.observed_replay_session_generation = generation;
    state.replay_seek_pending = false;
    state.replay_seek_target = 0;
    state.replay_seek_retry_at_ms = 0;
    state.replay_seek_session_generation = 0;
    state.replay_scrubbing = false;
    state.replay_scrub_tick = 0;
    state.replay_scrub_session_generation = 0;
    state.go_to_tick.fill('\0');
    state.manual_fov_initialized = false;
    state.manual_fov_input_active = false;
    state.manual_fov_pending_until_ms = 0;
    state.replay_tick_input_active = false;
    SmvmSetReplayTickInputActive(false);
}

void DrawRuleOfThirdsGuide(const FrameContext& context) noexcept {
    if ((context.snapshot->movie_tool_flags & movie_tool_rule_of_thirds) == 0)
        return;
    const auto width = context.params->viewport_width;
    const auto height = context.params->viewport_height;
    if (width <= 0.0F || height <= 0.0F)
        return;
    auto* draw = ImGui::GetForegroundDrawList();
    constexpr auto color = IM_COL32(255, 255, 255, 118);
    const auto thickness = std::max(1.0F, smvm_theme::GetScale());
    draw->AddLine(ImVec2(width / 3.0F, 0.0F), ImVec2(width / 3.0F, height), color, thickness);
    draw->AddLine(ImVec2(width * 2.0F / 3.0F, 0.0F), ImVec2(width * 2.0F / 3.0F, height), color, thickness);
    draw->AddLine(ImVec2(0.0F, height / 3.0F), ImVec2(width, height / 3.0F), color, thickness);
    draw->AddLine(ImVec2(0.0F, height * 2.0F / 3.0F), ImVec2(width, height * 2.0F / 3.0F), color, thickness);
}

void DrawMovieRecordingProgress(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    auto& state = *context.state;
    const auto active = (snapshot.movie_recording_flags & movie_recording_active) != 0;
    if (!active) {
        state.movie_progress_active = false;
        state.movie_progress_start_tick = 0;
        state.movie_progress_end_tick = 0;
        return;
    }

    const auto has_progress_path = context.params->has_path &&
        context.params->path_header != nullptr &&
        context.params->keyframes != nullptr &&
        context.params->path_header->keyframe_count >= 2;
    if (!state.movie_progress_active && has_progress_path) {
        state.movie_progress_active = true;
        const auto count = std::min<std::uint32_t>(
            context.params->path_header->keyframe_count,
            static_cast<std::uint32_t>(kMaxCampathKeyframes));
        state.movie_progress_start_tick = context.params->keyframes[0].demo_tick;
        state.movie_progress_end_tick = context.params->keyframes[count - 1].demo_tick;
        if (state.movie_progress_end_tick < state.movie_progress_start_tick)
            std::swap(state.movie_progress_start_tick, state.movie_progress_end_tick);
    }

    const auto progress = ComputeMovieRecordingTickProgress(
        state.movie_progress_active ? state.movie_progress_start_tick : snapshot.current_tick,
        state.movie_progress_active ? state.movie_progress_end_tick : snapshot.current_tick,
        snapshot.current_tick);
    std::array<char, 176> tick_text{};
    std::array<char, 160> frame_text{};
    std::array<char, 160> cadence_text{};
    const auto cancel_key = FormatInput(snapshot.cancel_key);
    static_cast<void>(std::snprintf(
        tick_text.data(), tick_text.size(),
        "RECORDING  %lld / %lld TICKS  |  %lld TICKS LEFT  |  %s TO PAUSE + END",
        static_cast<long long>(progress.completed),
        static_cast<long long>(progress.total),
        static_cast<long long>(progress.remaining), cancel_key.data()));
    const auto native_pass_active =
        (snapshot.movie_output_mode != MovieOutputMode::image_sequence &&
         (snapshot.movie_active_pass_flags & movie_capture_pass_beauty) != 0) ||
        (snapshot.movie_active_pass_flags &
         (movie_capture_pass_world_depth_pfm |
          movie_capture_pass_world_depth_avi |
          movie_capture_pass_greenscreen_free_camera)) != 0;
    if (native_pass_active) {
        static_cast<void>(std::snprintf(
            frame_text.data(), frame_text.size(),
            "%s  |  WORLD %llu  PFM %llu  Z %llu  CHROMA %llu  |  CAPTURED %llu / %llu",
            snapshot.movie_compositing_stage == MovieCompositingStage::world
                ? "PASS 1/2 WORLD + Z"
                : snapshot.movie_compositing_stage == MovieCompositingStage::chroma
                    ? "PASS 2/2 CHROMA"
                    : "PASS",
            static_cast<unsigned long long>(context.params->movie_beauty_frames_written),
            static_cast<unsigned long long>(context.params->movie_depth_pfm_frames_written),
            static_cast<unsigned long long>(context.params->movie_depth_avi_frames_written),
            static_cast<unsigned long long>(context.params->movie_depth_key_frames_written),
            static_cast<unsigned long long>(context.params->movie_frames_observed),
            static_cast<unsigned long long>(
                snapshot.movie_expected_frame_count != 0
                    ? snapshot.movie_expected_frame_count
                    : context.params->movie_frames_observed)));
    } else {
        static_cast<void>(std::snprintf(
            frame_text.data(), frame_text.size(), "STOCK TGA/WAV WRITER ACTIVE"));
    }
    const auto cadence_warning = native_pass_active &&
        context.params->movie_repeated_visual_samples_captured != 0;
    const auto duplicates_observed = native_pass_active &&
        context.params->movie_repeated_camera_sequences_captured != 0;
    if (cadence_warning) {
        static_cast<void>(std::snprintf(
            cadence_text.data(), cadence_text.size(),
            "CADENCE WARNING  VISUAL %llu  |  %llu DUPLICATE PRESENTS KEPT  |  QUEUE %u/%u  |  %llu WAITS",
            static_cast<unsigned long long>(
                context.params->movie_repeated_visual_samples_captured),
            static_cast<unsigned long long>(
                context.params->movie_repeated_camera_sequences_captured),
            context.params->movie_maximum_queue_depth,
            context.params->movie_queue_capacity,
            static_cast<unsigned long long>(
                context.params->movie_queue_backpressure_events)));
    } else if (duplicates_observed) {
        static_cast<void>(std::snprintf(
            cadence_text.data(), cadence_text.size(),
            "CADENCE CLEANUP  %llu DUPLICATE PRESENTS KEPT  |  QUEUE %u/%u  |  %llu WAITS",
            static_cast<unsigned long long>(
                context.params->movie_repeated_camera_sequences_captured),
            context.params->movie_maximum_queue_depth,
            context.params->movie_queue_capacity,
            static_cast<unsigned long long>(
                context.params->movie_queue_backpressure_events)));
    }

    const auto scale = smvm_theme::GetScale();
    const auto width = std::min(520.0F * scale, context.params->viewport_width - (32.0F * scale));
    ImGui::SetNextWindowPos(
        ImVec2((context.params->viewport_width - width) * 0.5F, 18.0F * scale),
        ImGuiCond_Always);
    ImGui::SetNextWindowSize(
        ImVec2(width, (cadence_warning || duplicates_observed ? 90.0F : 70.0F) * scale),
        ImGuiCond_Always);
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, 0.94F);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(14.0F * scale, 10.0F * scale));
    if (ImGui::Begin(
            "##deadlockmvm_recording_progress",
            nullptr,
            ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
                ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse |
                ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoInputs)) {
        ImGui::PushFont(smvm_theme::GetFonts().section);
        ImGui::TextColored(smvm_theme::Vec4(smvm_theme::colors::kWarning), "%s", tick_text.data());
        ImGui::PopFont();
        ImGui::TextDisabled("%s", frame_text.data());
        if (cadence_warning || duplicates_observed) {
            if (cadence_warning) {
                ImGui::TextColored(
                    smvm_theme::Vec4(smvm_theme::colors::kWarning),
                    "%s",
                    cadence_text.data());
            } else {
                ImGui::TextDisabled("%s", cadence_text.data());
            }
        }
    }
    ImGui::End();
    ImGui::PopStyleVar(2);
}

void DrawFrame(const SmvmUiFrameParams& params, SmvmUiState& state) noexcept {
    // Comparison is a frame-local hold, never a latched setting after page/tab changes.
    state.look_compare = false;
    if (params.snapshot == nullptr)
        return;
    ObserveReplaySession(params.snapshot, state);
    // Host-reported notifications are folded into the toast list here; the list
    // is drawn unconditionally at the end of the frame so a failed action
    // (QueueAction pushes a local toast) is actually visible to the user.
    UpdateToasts(*params.snapshot, state);
    const auto show_timeline = ShouldShowReplayTimeline(
        (params.snapshot->flags & smvm_snapshot_internal_enabled) != 0,
        (params.snapshot->flags & smvm_snapshot_replay_active) != 0,
        params.snapshot->deadlock_ui_mode == DeadlockUiMode::smvm_replay_ui);
    const auto hide_editor_ui = ShouldHideEditorUiDuringCinematic(
        state.hide_ui_during_cinematic,
        (params.snapshot->flags & smvm_snapshot_campath_playing) != 0);
    const auto show_editor_ui = ShouldShowEditorUi(
        show_timeline,
        hide_editor_ui,
        params.menu_open || params.effects_open);
    if ((params.snapshot->flags & smvm_snapshot_replay_active) == 0) {
        state.replay_seek_pending = false;
        state.replay_seek_retry_at_ms = 0;
        state.replay_seek_session_generation = 0;
        state.replay_scrubbing = false;
        state.replay_scrub_session_generation = 0;
    }
    ClearReplayInputLeasesWhenSeekActive(
        (params.snapshot->flags & smvm_snapshot_replay_seek_in_progress) != 0,
        state.replay_seek_pending,
        state.replay_seek_retry_at_ms,
        state.replay_seek_session_generation,
        state.replay_scrubbing,
        state.replay_scrub_session_generation);
    if (!show_editor_ui || !params.menu_open)
        SmvmSetReplayTickInputActive(false);
    if (params.documents != nullptr) {
        state.documents = *params.documents;
        state.documents_valid = true;
    }
    FrameContext context{&params, &state, params.snapshot};
    state.open_save_as_modal = false;
    state.open_load_picker = false;
    state.picker_requested_list = false;
    state.confirm_pending = false;
    state.confirm_action = SmvmConfirmAction::none;
    state.free_camera_activation_pending = false;
    state.free_camera_activation_started_ms = 0;
    state.free_camera_activation_error_until_ms = 0;
    DrawMovieRecordingProgress(context);
    if (show_editor_ui) {
        DrawRuleOfThirdsGuide(context);
        DrawReplayPauseIndicator(context);
        DrawRecordingReadinessBadge(context);
        if (!params.effects_open && !params.menu_open) {
            const auto scale = smvm_theme::GetScale();
            const auto key = FormatInput(params.snapshot->effects_key);
            ImGui::SetNextWindowPos(ImVec2(params.viewport_width - 185.0F * scale, 16.0F * scale), ImGuiCond_Always);
            ImGui::SetNextWindowBgAlpha(.9F);
            if (ImGui::Begin("##effects_tab", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings)) {
                if (ImGui::Button("Effects")) SmvmToggleEffectsMenu();
                ImGui::SameLine(); ImGui::TextDisabled("%s", key.data());
            }
            ImGui::End();
        }
        DrawMovieRecordingMenu(context);
        DrawCampathMiniMenu(context);
        DrawModals(context);
        DrawReplayTimeline(context);
        if ((params.snapshot->flags & smvm_snapshot_replay_seek_in_progress) != 0)
            DrawUpdatingTicksPrompt(context);
        else
            DrawCinematicStartPrompt(context);
    }
    // Drawn last and regardless of editor visibility: toasts report host
    // reconnect and action failures, which can happen while the menu is closed.
    DrawToasts(context);
}

} // namespace deadlock_mvm::smvm_ui
