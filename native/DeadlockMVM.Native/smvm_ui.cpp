#include "smvm_ui.hpp"

#include "smvm_theme.hpp"

#include "imgui.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>

namespace deadlock_mvm::smvm_ui {
namespace {

// UTF-8 escapes keep the source ASCII-only (the product builds without /utf-8).
constexpr auto kDegree = "\xC2\xB0";
constexpr auto kMiddleDot = "\xC2\xB7";
constexpr auto kEmDash = "\xE2\x80\x94";
constexpr auto kEllipsis = "\xE2\x80\xA6";
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
    const char* text = nullptr) noexcept {
    if (!context.params->queue_action)
        return false;
    SmvmActionPayload action{};
    action.type = type;
    action.index = index;
    action.tick = tick;
    action.value = value;
    if (text != nullptr) {
        const auto length = std::min(std::strlen(text), action.text.size() - 1);
        if (length > 0)
            std::memcpy(action.text.data(), text, length);
    }
    const auto queued = context.params->queue_action(action);
    if (!queued) {
        if (type != SmvmActionType::request_path_list)
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

void RequestCapture(const FrameContext& context) noexcept {
    if (context.params->request_capture)
        context.params->request_capture();
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
    constexpr std::array<const char*, 29> names{
        "Forward", "Backward", "Left", "Right", "Up", "Down", "Fast", "Precision",
        "Roll Left", "Roll Right", "Reset Roll", "Menu", "Add Keyframe", "Delete Keyframe",
        "Clean Footage", "Play From Start", "Play From Current", "Stop", "Undo", "Redo",
        "Show Path", "Show Cameras", "Emergency Restore", "Cycle Replay Interface",
        "Toggle Free Camera", "Replay Play/Pause", "Show Labels", "Replay Step Back",
        "Replay Step Forward",
    };
    return action >= kSmvmFirstManualBindingAction && action <= kSmvmLastBindingAction
        ? names[static_cast<std::size_t>(action - kSmvmFirstManualBindingAction)]
        : nullptr;
}

[[nodiscard]] const char* PlaybackStateText(const std::uint32_t state) noexcept {
    switch (state) {
        case 1: case 2: return "Preparing";
        case 3: return "Seeking";
        case 4: return "Waiting for tick";
        case 5: return "Free roam";
        case 6: return "Transferring";
        case 7: return "Arming camera";
        case 8: return "Waiting for camera";
        case 9: return "Playing";
        case 10: return "Completed";
        case 11: return "Stopping";
        case 13: return "Failed";
        case 14: return "Cancelled";
        default: return "Stopped";
    }
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

void SeekToTickField(const FrameContext& context) noexcept {
    auto& state = *context.state;
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    ImGui::PushID("go_to_tick");
    ImGui::SetNextItemWidth(110.0F * scale);
    const auto submit = [&]() noexcept {
        const auto target = std::strtoll(state.go_to_tick.data(), nullptr, 10);
        const auto maximum = snapshot.total_ticks > 0
            ? snapshot.total_ticks
            : (std::numeric_limits<std::int64_t>::max)();
        QueueAction(
            context, SmvmActionType::seek_tick, -1,
            std::clamp<std::int64_t>(std::max<std::int64_t>(0, target), 0, maximum));
        state.go_to_tick.fill('\0');
    };
    ImGui::PushFont(smvm_theme::GetFonts().mono);
    const auto entered = ImGui::InputTextWithHint(
        "##tick", "Tick", state.go_to_tick.data(), state.go_to_tick.size(),
        ImGuiInputTextFlags_CharsDecimal | ImGuiInputTextFlags_EnterReturnsTrue);
    ImGui::PopFont();
    if (entered)
        submit();
    ImGui::SameLine();
    const auto replay_ready = (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
        snapshot.current_tick >= 0;
    if (smvm_theme::Button("Go", replay_ready && state.go_to_tick[0] != '\0'))
        submit();
    ImGui::PopID();
}

// --- Pages ------------------------------------------------------------------

void DrawReplayPage(const FrameContext& context) noexcept {
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

void DrawCameraPage(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto scale = smvm_theme::GetScale();
    PageHeader("Camera", nullptr);

    const auto camera_readable = (snapshot.flags & smvm_snapshot_camera_readable) != 0;
    const auto camera_ready = camera_readable &&
        snapshot.camera_availability == CameraAvailability::ready;
    const auto manual_active = (snapshot.flags & smvm_snapshot_manual_camera_active) != 0;
    const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    const auto campath_playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
    // Entry itself is what moves Deadlock into Free Roam and gives the native
    // backend a camera frame to resolve/hook. Requiring ManualCamera capability
    // here creates a circular gate on fresh replay sessions.
    const auto mode_available = replay_active && !campath_playing;

    if (smvm_theme::BeginSection("Camera Mode")) {
        constexpr std::array<const char*, 3> modes{"Free Camera", "In Eye", "Chase"};
        // Free Camera means the complete SMVM manual-camera mode. Ordinary
        // Deadlock Free Roam is an implementation detail and must never make
        // this owner-facing control appear active by itself.
        const auto current = manual_active ? 0 : (snapshot.observer_mode == 3 ? 1 :
            (snapshot.observer_mode == 6 ? 2 : -1));
        const auto chosen = smvm_theme::SegmentedControl(
            "camera_mode", modes.data(), static_cast<int>(modes.size()), current,
            mode_available);
        if (chosen != current) {
            if (chosen == 0) QueueAction(context, SmvmActionType::reacquire_camera);
            else if (chosen == 1) QueueAction(context, SmvmActionType::in_eye);
            else if (chosen == 2) QueueAction(context, SmvmActionType::chase);
        }
        ImGui::SameLine();
        if (smvm_theme::Button("Previous", true, ImVec2(84.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::previous_player);
        ImGui::SameLine();
        if (smvm_theme::Button("Next", true, ImVec2(70.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::next_player);
        if (context.params->free_camera_input_error) {
            ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kError));
            ImGui::TextUnformatted(
                "Free Camera kept the menu open because relative mouse input was not ready.");
            ImGui::PopStyleColor();
            if (smvm_theme::Button("Retry Input", manual_active, ImVec2(108.0F * scale, 0.0F)))
                SmvmReacquireFreeCameraInput();
        } else if (context.state->free_camera_activation_pending) {
            SecondaryText("Starting Free Camera from the current rendered view...");
        } else if (GetTickCount64() < context.state->free_camera_activation_error_until_ms) {
            ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kError));
            ImGui::TextUnformatted("Free Camera could not acquire the rendered camera. Try again after the replay settles.");
            ImGui::PopStyleColor();
        } else if (manual_active) {
            SecondaryText(
                "WASD Move  \xC2\xB7  Mouse Look  \xC2\xB7  Space/Ctrl Vertical  \xC2\xB7  "
                "Shift Fast  \xC2\xB7  Alt Precision  \xC2\xB7  Q/E Roll  \xC2\xB7  Wheel FOV");
        }
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Lens")) {
        const auto fov_writable = camera_ready &&
            (snapshot.flags & smvm_snapshot_fov_writable) != 0;
        auto out = 0.0F;
        if (camera_readable) {
            if (smvm_theme::SliderInput("fov", static_cast<float>(snapshot.camera.fov),
                                        static_cast<float>(kMinFov), static_cast<float>(kMaxFov),
                                        out, "%.1f", fov_writable, "FOV"))
                QueueAction(context, SmvmActionType::set_fov, -1, -1, out);
        } else {
            smvm_theme::ValueRow("FOV", nullptr, true, false);
        }
        if (!fov_writable)
            smvm_theme::Tooltip("Enter Free Camera to adjust the lens.");
        ImGui::SameLine(0.0F, 12.0F * scale);
        constexpr std::array<double, 5> presets{20.0, 30.0, 40.0, 60.0, 90.0};
        for (std::size_t index = 0; index < presets.size(); ++index) {
            if (index > 0)
                ImGui::SameLine(0.0F, 4.0F * scale);
            std::array<char, 12> label{};
            static_cast<void>(std::snprintf(label.data(), label.size(), "%.0f", presets[index]));
            ImGui::PushID(static_cast<int>(index));
            const auto active = camera_ready && std::abs(snapshot.camera.fov - presets[index]) < 0.1;
            if (active)
                ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kAccent));
            if (smvm_theme::Button(label.data(), fov_writable, ImVec2(40.0F * scale, 0.0F)))
                QueueAction(context, SmvmActionType::set_fov, -1, -1, presets[index]);
            if (active)
                ImGui::PopStyleColor();
            ImGui::PopID();
        }

        const auto rendered_roll = (snapshot.capabilities & smvm_capability_rendered_roll) != 0;
        const auto roll_writable = camera_ready && rendered_roll &&
            (snapshot.flags & smvm_snapshot_roll_writable) != 0;
        if (camera_readable) {
            if (smvm_theme::SliderInput("roll", static_cast<float>(snapshot.camera.roll),
                                        -45.0F, 45.0F, out, "%.1f", roll_writable, "Roll"))
                QueueAction(context, SmvmActionType::set_roll, -1, -1, out);
        } else {
            smvm_theme::ValueRow("Roll", nullptr, true, false);
        }
        if (!roll_writable)
            smvm_theme::Tooltip(rendered_roll
                ? "Enter Free Camera to adjust Roll."
                : "Rendered Roll is not available in this build.");
        ImGui::SameLine(0.0F, 12.0F * scale);
        if (smvm_theme::Button("Reset Roll", roll_writable, ImVec2(100.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::set_roll, -1, -1, 0.0);
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Shot")) {
        if (smvm_theme::Button("Save Camera", camera_ready, ImVec2(130.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::save_camera);
        ImGui::SameLine();
        if (smvm_theme::Button("Restore Camera", camera_ready, ImVec2(140.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::restore_camera);
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (ImGui::CollapsingHeader("Live Camera")) {
        std::array<char, 96> value{};
        ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(ImGui::GetStyle().ItemSpacing.x, 3.0F * scale));
        const auto pair = [&](const char* left_label, const double left,
                              const char* right_label, const double right) noexcept {
            value = {};
            if (camera_readable)
                static_cast<void>(std::snprintf(value.data(), 40, "%.2f", left));
            ImGui::PushFont(smvm_theme::GetFonts().secondary);
            ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kMuted));
            ImGui::TextUnformatted(left_label);
            ImGui::PopStyleColor();
            ImGui::SameLine(60.0F * scale);
            ImGui::PushFont(smvm_theme::GetFonts().mono);
            ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(
                camera_readable ? smvm_theme::colors::kText : smvm_theme::colors::kDisabledText));
            ImGui::TextUnformatted(camera_readable ? value.data() : "--");
            ImGui::PopStyleColor();
            ImGui::PopFont();
            ImGui::PopFont();
            ImGui::SameLine(0.0F, 24.0F * scale);
            value = {};
            if (camera_readable)
                static_cast<void>(std::snprintf(value.data(), 40, "%.2f", right));
            ImGui::PushFont(smvm_theme::GetFonts().secondary);
            ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kMuted));
            ImGui::TextUnformatted(right_label);
            ImGui::PopStyleColor();
            ImGui::SameLine(0.0F, 0.0F);
            ImGui::SetCursorPosX(ImGui::GetCursorPosX() + 34.0F * scale);
            ImGui::PushFont(smvm_theme::GetFonts().mono);
            ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(
                camera_readable ? smvm_theme::colors::kText : smvm_theme::colors::kDisabledText));
            ImGui::TextUnformatted(camera_readable ? value.data() : "--");
            ImGui::PopStyleColor();
            ImGui::PopFont();
            ImGui::PopFont();
        };
        pair("X", snapshot.camera.x, "Pitch", snapshot.camera.pitch);
        pair("Y", snapshot.camera.y, "Yaw", snapshot.camera.yaw);
        pair("Z", snapshot.camera.z, "Roll", snapshot.camera.roll);
        ImGui::PopStyleVar();
        if (!camera_readable) {
            if (snapshot.observer_mode != 4) {
                SecondaryText("Enter Free Camera to access camera controls.");
                ImGui::SameLine();
                if (smvm_theme::Button("Enter Free Camera", true, ImVec2(140.0F * scale, 0.0F)))
                    QueueAction(context, SmvmActionType::reacquire_camera);
            } else {
                SecondaryText("Camera is reconnecting\xE2\x80\xA6");
            }
        }
    }
}

void DrawCampathEmptyState(const FrameContext& context) noexcept {
    const auto scale = smvm_theme::GetScale();
    smvm_theme::EmptyState(
        "CREATE YOUR FIRST CINEMATIC",
        "Build a shot while the replay is playing or paused.");
    ImGui::Spacing();
    const auto width = ImGui::GetContentRegionAvail().x;
    ImGui::SetCursorPosX(ImGui::GetCursorPosX() + std::max(0.0F, (width - 390.0F * scale) * 0.5F));
    if (smvm_theme::PrimaryButton("Enter Free Camera", true, ImVec2(140.0F * scale, 0.0F)))
        QueueAction(context, SmvmActionType::reacquire_camera);
    ImGui::SameLine();
    if (smvm_theme::PrimaryButton("New Path", true, ImVec2(120.0F * scale, 0.0F))) {
        if (HasUnsavedCampathWork(*context.snapshot)) {
            context.state->confirm_action = SmvmConfirmAction::new_path;
            context.state->confirm_pending = true;
        } else {
            QueueAction(context, SmvmActionType::new_path);
        }
    }
    ImGui::SameLine();
    if (smvm_theme::Button("Load Path", true, ImVec2(120.0F * scale, 0.0F)))
        context.state->open_load_picker = true;
    ImGui::Spacing();
    ImGui::Spacing();
    SecondaryText("1. Enter Free Camera - F2");
    SecondaryText("2. Pause or seek to your first moment");
    SecondaryText("3. Position the camera");
    SecondaryText("4. Add a keyframe - Mouse3");
    SecondaryText("5. Move to another moment");
    SecondaryText("6. Add another keyframe - Mouse3");
    SecondaryText("7. Play From Start - F3");
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

    // Identity row: name, Unsaved pill, keyframe count.
    ImGui::PushFont(smvm_theme::GetFonts().page_title);
    ImGui::TextUnformatted(snapshot.path_name[0] != '\0' ? snapshot.path_name.data()
                                                         : "Untitled Path");
    ImGui::PopFont();
    if ((snapshot.flags & smvm_snapshot_campath_unsaved) != 0) {
        ImGui::SameLine();
        smvm_theme::StatusPill("UNSAVED", smvm_theme::PillKind::warning);
    }
    std::array<char, 32> count_text{};
    static_cast<void>(std::snprintf(count_text.data(), count_text.size(), "%u keyframes",
                                    snapshot.keyframe_count));
    ImGui::SameLine();
    SecondaryText(count_text.data());
    if (playing) {
        std::array<char, 48> playback_text{};
        static_cast<void>(std::snprintf(playback_text.data(), playback_text.size(),
                                        "Path playing %s %s", kEmDash,
                                        PlaybackStateText(snapshot.playback_state)));
        SecondaryText(playback_text.data());
    }

    // Workspace actions.
    if (smvm_theme::Button("New", !playing, ImVec2(64.0F * scale, 0.0F))) {
        if (HasUnsavedCampathWork(snapshot)) {
            state.confirm_action = SmvmConfirmAction::new_path;
            state.confirm_pending = true;
        } else {
            QueueAction(context, SmvmActionType::new_path);
        }
    }
    ImGui::SameLine();
    const auto has_session = snapshot.campath_session != SmvmCampathSession::no_path;
    if (smvm_theme::Button("Save", has_session && has_keys, ImVec2(64.0F * scale, 0.0F)))
        QueueAction(context, SmvmActionType::save_path);
    ImGui::SameLine();
    if (smvm_theme::Button("Save As", has_keys, ImVec2(84.0F * scale, 0.0F))) {
        const auto length = std::min(std::strlen(snapshot.path_name.data()),
                                     state.save_as_name.size() - 1);
        state.save_as_name.fill('\0');
        if (length > 0)
            std::memcpy(state.save_as_name.data(), snapshot.path_name.data(), length);
        state.open_save_as_modal = true;
    }
    ImGui::SameLine();
    if (smvm_theme::Button("Load", !playing, ImVec2(64.0F * scale, 0.0F)))
        state.open_load_picker = true;
    ImGui::SameLine();
    if (smvm_theme::Button("Close", has_session, ImVec2(70.0F * scale, 0.0F))) {
        if (HasUnsavedCampathWork(snapshot)) {
            state.confirm_action = SmvmConfirmAction::close_path;
            state.confirm_pending = true;
        } else {
            QueueAction(context, SmvmActionType::close_path);
        }
    }
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
        ImGui::TextUnformatted("Recover unsaved Campath?");
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

    if (snapshot.campath_session == SmvmCampathSession::no_path && keyframe_count == 0) {
        DrawCampathEmptyState(context);
        return;
    }

    // Keyframe list + selected keyframe panel.
    const auto columns_height = 218.0F * scale;
    const auto list_width = std::min(320.0F * scale, ImGui::GetContentRegionAvail().x * 0.52F);
    if (smvm_theme::BeginSection("Keyframes", columns_height, list_width)) {
        if (has_keys) {
            ImGui::BeginChild("##keyframe_list", ImVec2(0.0F, 0.0F), ImGuiChildFlags_None,
                              ImGuiWindowFlags_None);
            for (std::uint32_t index = 0; index < keyframe_count; ++index) {
                const auto& key = keys[index];
                const auto selected = static_cast<std::int32_t>(index) == snapshot.selected_keyframe;
                if (smvm_theme::KeyframeRow(static_cast<int>(index), key.demo_tick,
                                            key.camera.fov, key.camera.roll, selected))
                    QueueAction(context, SmvmActionType::select_keyframe,
                                static_cast<std::int32_t>(index));
            }
            ImGui::EndChild();
        } else {
            SecondaryText("No keyframes yet.");
            SecondaryText("Frame a shot, then add the current camera.");
        }
        smvm_theme::EndSection();
    }
    ImGui::SameLine();
    if (smvm_theme::BeginSection("Selected Keyframe", columns_height)) {
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
            if (smvm_theme::Button("Go To", true, ImVec2(76.0F * scale, 0.0F)))
                QueueAction(context, SmvmActionType::go_to_keyframe, snapshot.selected_keyframe);
            ImGui::SameLine();
            if (smvm_theme::Button("Update", !playing, ImVec2(84.0F * scale, 0.0F)))
                QueueAction(context, SmvmActionType::update_keyframe, snapshot.selected_keyframe);
            ImGui::SameLine();
            if (smvm_theme::DangerButton("Delete", !playing, ImVec2(80.0F * scale, 0.0F)))
                QueueAction(context, SmvmActionType::delete_keyframe, snapshot.selected_keyframe);
        } else {
            SecondaryText("Select a keyframe to inspect it.");
        }
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        if (smvm_theme::PrimaryButton("Add Current Camera", !playing,
                                      ImVec2(200.0F * scale, 0.0F)))
            RequestCapture(context);
        smvm_theme::Tooltip("Adds the live camera as a keyframe (same as Mouse3).");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Playback")) {
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
        ImGui::SameLine(0.0F, 20.0F * scale);
        constexpr std::array<const char*, 2> end_labels{"Stop & Release", "Hold Final"};
        const auto end_behavior = smvm_theme::SegmentedControl(
            "end_behavior", end_labels.data(), 2,
            snapshot.end_behavior == CampathEndBehavior::hold_final_camera ? 1 : 0,
            editing_enabled);
        if (end_behavior != (snapshot.end_behavior == CampathEndBehavior::hold_final_camera ? 1 : 0))
            QueueAction(context, SmvmActionType::set_end_behavior, end_behavior);

        ImGui::Spacing();
        const auto can_play = has_keys && keyframe_count >= 2 && editing_enabled;
        if (smvm_theme::PrimaryButton("Play From Start", can_play, ImVec2(140.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::play_from_start);
        ImGui::SameLine();
        if (smvm_theme::Button("Play From Current", can_play, ImVec2(150.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::play_from_current);
        ImGui::SameLine();
        if (smvm_theme::Button("Stop", playing, ImVec2(70.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::stop_campath);
        smvm_theme::EndSection();
    }
}

void DrawVisualsPage(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    PageHeader("Visuals", nullptr);
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Replay UI Mode")) {
        constexpr std::array<const char*, 3> modes{
            "Deadlock UI", "SMVM Movie UI", "Clean Footage"};
        const auto current = snapshot.deadlock_ui_mode == DeadlockUiMode::clean_footage
            ? 2
            : snapshot.deadlock_ui_mode == DeadlockUiMode::smvm_replay_ui ? 1 : 0;
        const auto selected = smvm_theme::SegmentedControl(
            "deadlock_ui_mode", modes.data(), static_cast<int>(modes.size()), current);
        if (selected != current) {
            if (selected == static_cast<int>(DeadlockUiMode::clean_footage)) {
                context.state->clean_hint_pending = true;
                context.state->clean_hint_until_ms = GetTickCount64() + 2400;
            } else {
                context.state->clean_hint_pending = false;
                context.state->clean_hint_until_ms = 0;
                QueueAction(context, SmvmActionType::set_deadlock_ui_mode, selected);
            }
        }
        ImGui::Spacing();
        smvm_theme::ValueRow("Active", DeadlockUiModeText(snapshot.deadlock_ui_mode));
        smvm_theme::ValueRow(
            "Suppression", DeadlockUiErrorText(snapshot.deadlock_ui_error), false,
            snapshot.deadlock_ui_error == DeadlockUiError::none);
        SecondaryText("F9 always restores Deadlock UI");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Campath Display")) {
        const auto path_available = (snapshot.capabilities & smvm_capability_path_visualization) != 0;
        if (smvm_theme::Toggle("Show Path", (snapshot.flags & smvm_snapshot_show_path) != 0,
                               path_available))
            QueueAction(context, SmvmActionType::toggle_show_path);
        if (smvm_theme::Toggle("Show Cameras", (snapshot.flags & smvm_snapshot_show_cameras) != 0,
                               path_available))
            QueueAction(context, SmvmActionType::toggle_show_cameras);
        if (smvm_theme::Toggle("Show Labels", (snapshot.flags & smvm_snapshot_show_labels) != 0,
                               path_available))
            QueueAction(context, SmvmActionType::toggle_show_labels);
        if (smvm_theme::Toggle("Hide While Playing",
                               (snapshot.flags & smvm_snapshot_hide_path_while_playing) != 0,
                               path_available))
            QueueAction(context, SmvmActionType::toggle_hide_path_while_playing);
        if (!path_available)
            smvm_theme::Tooltip("Path visualization is not available in this build.");
        auto label_scale = 0.0F;
        if (smvm_theme::SliderInput("label_scale", static_cast<float>(snapshot.path_label_scale),
                                    0.5F, 2.0F, label_scale, "%.2f", path_available, "Label Scale"))
            QueueAction(context, SmvmActionType::set_path_label_scale, -1, -1, label_scale);
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    if (smvm_theme::BeginSection("Interface")) {
        if (smvm_theme::Toggle("Notifications",
                               (snapshot.flags & smvm_snapshot_notifications) != 0))
            QueueAction(context, SmvmActionType::toggle_notifications);
        if (smvm_theme::Toggle("Minimal SMVM Button",
                               (snapshot.flags & smvm_snapshot_show_minimal_pill) != 0))
            QueueAction(context, SmvmActionType::toggle_minimal_pill);
        const auto clean = FormatInput(snapshot.clean_view_key);
        smvm_theme::ValueRow("Clean Footage", clean.data());
        ImGui::SameLine();
        SecondaryText("hides the menu, path, cameras, and labels");
        auto opacity = 0.0F;
        if (smvm_theme::SliderInput("menu_opacity", static_cast<float>(snapshot.menu_opacity),
                                    0.65F, 1.0F, opacity, "%.2f", true, "UI Opacity"))
            QueueAction(context, SmvmActionType::set_menu_opacity, -1, -1, opacity);
        smvm_theme::EndSection();
    }
}

void DrawCapturePage() noexcept {
    PageHeader("Capture", nullptr);
    ImGui::Spacing();
    smvm_theme::EmptyState(
        "The capture pipeline isn't implemented yet",
        "SMVM will not claim recording, encoding, or file output it cannot prove.");
    ImGui::Spacing();
    ImGui::Spacing();
    SecondaryText("Planned: image sequence, resolution, frame rate, audio, render passes.");
}

void DrawSettingsBindingsTwoColumn(
    const char* id,
    const char* const* labels,
    const std::uint32_t* values,
    const std::int32_t* actions,
    const std::size_t count) noexcept {
    const auto scale = smvm_theme::GetScale();
    const auto half = (count + 1) / 2;
    const auto column = [&](const std::size_t begin, const std::size_t end) noexcept {
        for (auto index = begin; index < end; ++index)
            BindingField(labels[index], values[index], actions[index]);
    };
    ImGui::PushID(id);
    const auto width = (ImGui::GetContentRegionAvail().x - 12.0F * scale) * 0.5F;
    ImGui::BeginChild("##left", ImVec2(width, 0.0F), ImGuiChildFlags_AutoResizeY);
    column(0, half);
    ImGui::EndChild();
    ImGui::SameLine();
    ImGui::BeginChild("##right", ImVec2(0.0F, 0.0F), ImGuiChildFlags_AutoResizeY);
    column(half, count);
    ImGui::EndChild();
    ImGui::PopID();
}

void DrawCameraTuningSection(const FrameContext& context, const float width) noexcept {
    const auto& snapshot = *context.snapshot;
    if (!smvm_theme::BeginSection("Camera Tuning", 0.0F, width))
        return;
    auto out = 0.0F;
    if (smvm_theme::SliderInput("move_speed", static_cast<float>(snapshot.movement_speed),
                                1.0F, 2000.0F, out, "%.0f", true, "Move Speed"))
        QueueAction(context, SmvmActionType::set_movement_speed, -1, -1, out);
    if (smvm_theme::SliderInput("sensitivity", static_cast<float>(snapshot.mouse_sensitivity),
                                0.001F, 5.0F, out, "%.3f", true, "Sensitivity"))
        QueueAction(context, SmvmActionType::set_mouse_sensitivity, -1, -1, out);
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
    if (smvm_theme::Toggle("Invert Y", (snapshot.flags & smvm_snapshot_invert_y) != 0))
        QueueAction(context, SmvmActionType::toggle_invert_y);
    if (smvm_theme::Toggle("Take Over Camera Input",
                           (snapshot.flags & smvm_snapshot_input_takeover) != 0))
        QueueAction(context, SmvmActionType::toggle_input_takeover);
    smvm_theme::Tooltip("While active, only SMVM-owned bindings are consumed. Focus loss, "
                        "menu open, and disconnect reset held input.");
    smvm_theme::EndSection();
}

void DrawLensBindingsSection(const FrameContext& context, const float width) noexcept {
    const auto& snapshot = *context.snapshot;
    if (!smvm_theme::BeginSection("Lens Bindings", 0.0F, width))
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
    if (smvm_theme::BeginSection("Free Camera")) {
        BindingField("Toggle Free Camera", snapshot.toggle_free_camera_key, 124);
        SecondaryText("F2 enters from the current rendered view and works while paused.");
        smvm_theme::EndSection();
    }
    ImGui::Spacing();
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
    if (smvm_theme::BeginSection("Movement Bindings")) {
        DrawSettingsBindingsTwoColumn("movement", movement_labels.data(), movement_values.data(),
                                      movement_actions.data(), movement_labels.size());
        smvm_theme::EndSection();
    }
    ImGui::Spacing();

    // Tuning and lens bindings share one row so the page fits the shell
    // without a page scrollbar.
    {
        const auto scale = smvm_theme::GetScale();
        const auto column_width = (ImGui::GetContentRegionAvail().x - 12.0F * scale) * 0.5F;
        ImGui::BeginGroup();
        DrawCameraTuningSection(context, column_width);
        ImGui::EndGroup();
        ImGui::SameLine(0.0F, 12.0F * scale);
        ImGui::BeginGroup();
        DrawLensBindingsSection(context, column_width);
        ImGui::EndGroup();
    }
}

void DrawCampathSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    constexpr std::array<const char*, 10> labels{
        "Add Keyframe", "Delete Keyframe", "Play From Start", "Play From Current", "Stop",
        "Undo", "Redo", "Show Path", "Show Cameras", "Show Labels"};
    const std::array<std::uint32_t, 10> values{
        snapshot.add_key, snapshot.delete_key, snapshot.play_start_key, snapshot.play_current_key,
        snapshot.stop_key, snapshot.undo_key, snapshot.redo_key, snapshot.show_path_key,
        snapshot.show_cameras_key, snapshot.show_labels_key};
    constexpr std::array<std::int32_t, 10> actions{
        kSmvmFirstEditorBindingAction + 1, kSmvmFirstEditorBindingAction + 2,
        kSmvmFirstEditorBindingAction + 4, kSmvmFirstEditorBindingAction + 5,
        kSmvmFirstEditorBindingAction + 6, kSmvmFirstEditorBindingAction + 7,
        kSmvmFirstEditorBindingAction + 8, kSmvmFirstEditorBindingAction + 9,
        kSmvmFirstEditorBindingAction + 10, 126};
    if (smvm_theme::BeginSection("Editor & Playback Bindings")) {
        DrawSettingsBindingsTwoColumn("campath_bindings", labels.data(), values.data(),
                                      actions.data(), labels.size());
        SecondaryText("Defaults: Mouse3 Add/Update, L Delete, F3 Play From Start, F4 Stop.");
        smvm_theme::EndSection();
    }
}

void DrawReplaySettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (smvm_theme::BeginSection("Replay Bindings")) {
        BindingField("Play / Pause", snapshot.replay_pause_key, 125);
        BindingField("Step Back", snapshot.step_back_key, 127);
        BindingField("Step Forward", snapshot.step_forward_key, 128);
        SecondaryText("Right Shift toggles playback. Precise step keys are optional.");
        smvm_theme::EndSection();
    }
}

void DrawInterfaceSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    if (smvm_theme::BeginSection("Menu")) {
        BindingField("Open Menu", snapshot.menu_key, kSmvmFirstEditorBindingAction + 0);
        BindingField("Cycle Replay Interface", snapshot.cycle_ui_key, 123);
        BindingField("Clean Footage", snapshot.clean_view_key, kSmvmFirstEditorBindingAction + 3);
        BindingField("Emergency Restore", snapshot.restore_ui_key, kSmvmFirstEditorBindingAction + 11);
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
        if (smvm_theme::PrimaryButton("APPLY MOVIE MAKER DEFAULTS", true,
                                      ImVec2(240.0F * smvm_theme::GetScale(), 0.0F)))
            QueueAction(context, SmvmActionType::apply_movie_maker_defaults);
        ImGui::SameLine();
        if (smvm_theme::Button("RESET ALL BINDINGS", true,
                               ImVec2(180.0F * smvm_theme::GetScale(), 0.0F)))
            QueueAction(context, SmvmActionType::reset_bindings);
        SecondaryText("Movie Maker uses F2 / Right Shift / Mouse3 / F3 / F4; Reset also restores F5 and PageUp / PageDown.");
        SecondaryText("Each preset is checked for conflicts before any binding is changed.");
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
        SecondaryText("Compact camera telemetry appears only during Free Camera or Campath playback.");
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
        smvm_theme::ValueRow("Campath Folder", "%LOCALAPPDATA%\\DeadlockMVM\\campaths", true);
        std::array<char, 24> count{};
        static_cast<void>(std::snprintf(count.data(), count.size(), "%u",
                                        snapshot.saved_document_count));
        smvm_theme::ValueRow("Saved Paths", count.data());
        if (smvm_theme::Toggle("Restore last workspace on startup",
                               (snapshot.flags & smvm_snapshot_restore_workspace) != 0))
            QueueAction(context, SmvmActionType::toggle_restore_workspace);
        smvm_theme::Tooltip("Default off. When off, a startup always opens a clean, empty Campath workspace.");
        smvm_theme::EndSection();
    }
}

void DrawAdvancedSettings(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    const auto& params = *context.params;
    const auto scale = smvm_theme::GetScale();
    if (smvm_theme::BeginSection("Diagnostics")) {
        smvm_theme::ValueRow("Protocol", "V10", true);
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

void DrawSettingsPage(const FrameContext& context) noexcept {
    auto& state = *context.state;
    PageHeader("Settings", nullptr);
    ImGui::Spacing();
    constexpr std::array<const char*, 6> sections{
        "Camera", "Replay", "Campath", "Interface", "Storage", "Advanced"};
    const auto chosen = smvm_theme::SegmentedControl(
        "settings_sections", sections.data(), static_cast<int>(sections.size()),
        static_cast<int>(state.settings_section));
    state.settings_section = static_cast<SmvmSettingsSection>(chosen);
    ImGui::Spacing();

    switch (state.settings_section) {
        case SmvmSettingsSection::camera: DrawCameraSettings(context); break;
        case SmvmSettingsSection::replay: DrawReplaySettings(context); break;
        case SmvmSettingsSection::campath: DrawCampathSettings(context); break;
        case SmvmSettingsSection::interface_settings: DrawInterfaceSettings(context); break;
        case SmvmSettingsSection::storage: DrawStorageSettings(context); break;
        case SmvmSettingsSection::advanced: DrawAdvancedSettings(context); break;
    }
}

// --- Modals (rendered at shell level so popup IDs share one stack) ---------

void DrawModals(const FrameContext& context) noexcept {
    auto& state = *context.state;
    const auto scale = smvm_theme::GetScale();

    if (state.open_save_as_modal) {
        ImGui::OpenPopup("Save Campath As");
        state.open_save_as_modal = false;
    }
    ImGui::SetNextWindowSizeConstraints(ImVec2(300.0F * scale, 0.0F),
                                        ImVec2(380.0F * scale, 200.0F * scale));
    if (ImGui::BeginPopupModal("Save Campath As", nullptr,
                               ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove)) {
        ImGui::PushFont(smvm_theme::GetFonts().section);
        ImGui::TextUnformatted("Save Campath As");
        ImGui::PopFont();
        ImGui::Spacing();
        ImGui::SetNextItemWidth(-1.0F);
        ImGui::InputTextWithHint("##path_name", "Path name", state.save_as_name.data(),
                                 state.save_as_name.size());
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        if (smvm_theme::PrimaryButton("Save", state.save_as_name[0] != '\0',
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
        ImGui::OpenPopup("Load Campath");
        state.open_load_picker = false;
    }
    auto picker_open = true;
    ImGui::SetNextWindowSize(ImVec2(460.0F * scale, 400.0F * scale), ImGuiCond_Appearing);
    if (ImGui::BeginPopupModal("Load Campath", &picker_open, ImGuiWindowFlags_NoResize)) {
        ImGui::PushFont(smvm_theme::GetFonts().section);
        ImGui::TextUnformatted("Load Campath");
        ImGui::PopFont();
        ImGui::Spacing();
        const auto* documents = state.documents_valid ? &state.documents : context.params->documents;
        const auto count = documents != nullptr
            ? std::min<std::uint32_t>(documents->count,
                                      static_cast<std::uint32_t>(kMaxCampathDocuments))
            : 0u;
        ImGui::BeginChild("##path_list", ImVec2(0.0F, -46.0F * scale), ImGuiChildFlags_Borders);
        if (count == 0) {
            smvm_theme::EmptyState("No saved paths", "Paths you save appear here.");
        } else {
            // Draft entry first, then saved paths in the order provided.
            for (std::uint32_t pass = 0; pass < 2; ++pass) {
                for (std::uint32_t index = 0; index < count; ++index) {
                    const auto& entry = documents->entries[index];
                    const auto draft = (entry.flags & kCampathDocumentDraft) != 0;
                    if ((pass == 0) != draft)
                        continue;
                    const auto matches = (entry.flags & kCampathDocumentMatchesReplay) != 0;
                    std::array<char, 128> label{};
                    static_cast<void>(std::snprintf(
                        label.data(), label.size(), "%s%s  %s  %u keyframes%s",
                        entry.name.data(), draft ? " (unsaved draft)" : "", kMiddleDot,
                        entry.keyframe_count, matches ? "" : "  \xC2\xB7 other replay"));
                    ImGui::PushID(static_cast<int>(index));
                    if (!matches)
                        ImGui::PushStyleVar(ImGuiStyleVar_Alpha, ImGui::GetStyle().Alpha * 0.45F);
                    if (ImGui::Selectable(label.data(), false, 0, ImVec2(0.0F, 24.0F * scale))) {
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
                        smvm_theme::Tooltip("Recorded for a different replay.");
                    }
                    if (!matches)
                        ImGui::PopStyleVar();
                    ImGui::PopID();
                }
            }
        }
        ImGui::EndChild();
        if (smvm_theme::Button("Cancel", true, ImVec2(80.0F * scale, 0.0F)))
            ImGui::CloseCurrentPopup();
        ImGui::EndPopup();
    }
    if (!picker_open)
        state.picker_requested_list = false;

    if (state.confirm_pending) {
        smvm_theme::OpenConfirmation("Discard unsaved Campath?");
        state.confirm_pending = false;
    }
    const auto confirmed = smvm_theme::ConfirmationModal(
        "Discard unsaved Campath?",
        "Discard unsaved Campath?",
        "Your unsaved keyframes will be lost. Saved path files are kept.",
        "Discard");
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

bool NavigationItem(const char* label, const bool selected) noexcept {
    const auto scale = smvm_theme::GetScale();
    ImGui::PushID(label);
    ImGui::PushStyleColor(
        ImGuiCol_Text,
        smvm_theme::Vec4(selected ? smvm_theme::colors::kAccent : smvm_theme::colors::kMuted));
    if (selected) {
        ImGui::PushStyleColor(ImGuiCol_Header, smvm_theme::Vec4(smvm_theme::colors::kAccentSoft));
        ImGui::PushStyleColor(ImGuiCol_HeaderHovered,
                              smvm_theme::Vec4(smvm_theme::Rgba(0xD2, 0xA4, 0x53, 56)));
    }
    const auto clicked = ImGui::Selectable(label, selected, 0, ImVec2(0.0F, 34.0F * scale));
    if (selected) {
        const auto min = ImGui::GetItemRectMin();
        const auto max = ImGui::GetItemRectMax();
        ImGui::GetWindowDrawList()->AddRectFilled(
            min, ImVec2(min.x + 2.0F * scale, max.y), smvm_theme::colors::kAccent);
        ImGui::PopStyleColor(2);
    }
    ImGui::PopStyleColor();
    ImGui::PopID();
    return clicked;
}

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

    // Light window shadow, drawn from the tracked geometry (one frame behind).
    if (state.window_positioned) {
        const auto sx = state.window_x;
        const auto sy = state.window_y;
        const auto sw = state.window_w * scale;
        const auto sh = state.window_h * scale;
        for (auto layer = 0; layer < 3; ++layer) {
            const auto grow = static_cast<float>(layer + 1) * 4.0F * scale;
            background->AddRectFilled(
                ImVec2(sx - grow, sy - grow + (2.0F * scale)),
                ImVec2(sx + sw + grow, sy + sh + grow + (2.0F * scale)),
                smvm_theme::colors::kShadow, 8.0F * scale);
        }
    }

    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, opacity);
    const auto open = ImGui::Begin(
        "##smvm_shell", nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoCollapse |
        ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse |
        ImGuiWindowFlags_NoSavedSettings);
    if (open) {
        auto position = ImGui::GetWindowPos();
        const auto size = ImGui::GetWindowSize();

        // Header: title, drag region, single status pill, close button.
        const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
        const auto paused = (snapshot.flags & smvm_snapshot_paused) != 0;
        const char* pill_text = "CONNECTING";
        auto pill_kind = smvm_theme::PillKind::muted;
        if (replay_active && paused) {
            pill_text = "PAUSED";
            pill_kind = smvm_theme::PillKind::warning;
        } else if (replay_active) {
            pill_text = "REPLAY ACTIVE";
            pill_kind = smvm_theme::PillKind::success;
        }
        ImGui::PushFont(smvm_theme::GetFonts().title);
        ImGui::PushStyleColor(ImGuiCol_Text, smvm_theme::Vec4(smvm_theme::colors::kAccent));
        ImGui::TextUnformatted("SMVM");
        ImGui::PopStyleColor();
        ImGui::PopFont();
        ImGui::SameLine();
        const auto pill_width = smvm_theme::StatusPillSize(pill_text).x;
        const auto close_width = 30.0F * scale;
        const auto spacing = ImGui::GetStyle().ItemSpacing.x;
        const auto remaining = ImGui::GetContentRegionAvail().x;
        ImGui::InvisibleButton(
            "##header_drag",
            ImVec2(std::max(8.0F, remaining - pill_width - close_width - spacing),
                   22.0F * scale));
        if (ImGui::IsItemActive() && ImGui::IsMouseDragging(ImGuiMouseButton_Left)) {
            const auto& io = ImGui::GetIO();
            ImGui::SetWindowPos(ImVec2(position.x + io.MouseDelta.x,
                                       position.y + io.MouseDelta.y));
        }
        ImGui::SameLine(0.0F, 0.0F);
        smvm_theme::StatusPill(pill_text, pill_kind);
        ImGui::SameLine();
        if (smvm_theme::Button(kTimes, true, ImVec2(close_width, 0.0F)))
            SmvmCloseMenu();
        smvm_theme::Tooltip("Close menu");
        ImGui::Separator();

        // Body: navigation rail + page content.
        const auto body_height = ImGui::GetContentRegionAvail().y;
        const auto rail_width = 148.0F * scale;
        ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0.0F, 0.0F, 0.0F, 0.0F));
        ImGui::BeginChild("##nav", ImVec2(rail_width, body_height), ImGuiChildFlags_None);
        {
            struct Item final { const char* label; SmvmPage page; };
            constexpr std::array<Item, 5> items{{
                {"Replay", SmvmPage::replay},
                {"Camera", SmvmPage::camera},
                {"Campath", SmvmPage::campath},
                {"Visuals", SmvmPage::visuals},
                {"Capture", SmvmPage::capture},
            }};
            for (const auto& item : items) {
                if (NavigationItem(item.label, state.page == item.page))
                    state.page = item.page;
            }
            const auto rail_height = ImGui::GetWindowHeight();
            ImGui::SetCursorPosY(std::max(ImGui::GetCursorPosY(),
                                          rail_height - (34.0F * scale) - (12.0F * scale)));
            if (NavigationItem("Settings", state.page == SmvmPage::settings))
                state.page = SmvmPage::settings;
        }
        ImGui::EndChild();
        const auto nav_min = ImGui::GetItemRectMin();
        const auto nav_max = ImGui::GetItemRectMax();
        ImGui::GetWindowDrawList()->AddLine(
            ImVec2(nav_max.x, nav_min.y), ImVec2(nav_max.x, nav_max.y),
            smvm_theme::colors::kHairline);
        ImGui::SameLine();
        // Every page owns its own scroll region. Nested list children consume
        // the wheel while hovered; otherwise the current page scrolls without
        // moving the shell or leaking wheel input to Deadlock/manual FOV.
        ImGui::BeginChild("##content", ImVec2(0.0F, body_height), ImGuiChildFlags_None);
        ImGui::Dummy(ImVec2(1.0F, 2.0F));
        ImGui::Indent(8.0F * scale);
        ImGui::PushID(static_cast<int>(state.page));
        switch (state.page) {
            case SmvmPage::replay: DrawReplayPage(context); break;
            case SmvmPage::camera: DrawCameraPage(context); break;
            case SmvmPage::campath: DrawCampathPage(context); break;
            case SmvmPage::visuals: DrawVisualsPage(context); break;
            case SmvmPage::capture: DrawCapturePage(); break;
            case SmvmPage::settings: DrawSettingsPage(context); break;
        }
        ImGui::PopID();
        ImGui::Unindent(8.0F * scale);
        ImGui::EndChild();
        ImGui::PopStyleColor();

        DrawModals(context);

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

void DrawReplayBar(const FrameContext& context) noexcept {
    const auto& snapshot = *context.snapshot;
    auto& state = *context.state;
    const auto scale = static_cast<float>(snapshot.replay_bar_scale);
    const auto opacity = static_cast<float>(snapshot.replay_bar_opacity);
    const auto width = std::min(context.params->viewport_width - (48.0F * scale), 980.0F * scale);
    const auto height = 104.0F * scale;
    const auto x = (context.params->viewport_width - width) * 0.5F;
    const auto y = snapshot.replay_bar_anchor == SmvmReplayBarAnchor::top
        ? 24.0F * scale
        : context.params->viewport_height - height - (24.0F * scale);
    // Mask the native replay strip slightly beyond the replacement controls so
    // responsive resizing cannot leak Deadlock labels around the panel edge.
    const auto mask_padding = 12.0F * scale;
    const auto mask_alpha = static_cast<ImU32>(
        static_cast<float>((smvm_theme::colors::kShell >> 24) & 0xFFu) * opacity);
    const auto mask_color = (smvm_theme::colors::kShell & 0x00FFFFFFu) | (mask_alpha << 24);
    auto* background = ImGui::GetBackgroundDrawList();
    background->AddRectFilled(
        ImVec2(x - mask_padding, y - mask_padding),
        ImVec2(x + width + mask_padding, y + height + mask_padding),
        mask_color, 8.0F * scale);
    background->AddRect(
        ImVec2(x - mask_padding, y - mask_padding),
        ImVec2(x + width + mask_padding, y + height + mask_padding),
        smvm_theme::colors::kHairline, 8.0F * scale);
    ImGui::SetNextWindowPos(ImVec2(x, y), ImGuiCond_Always);
    ImGui::SetNextWindowSize(ImVec2(width, height), ImGuiCond_Always);
    ImGui::PushStyleVar(ImGuiStyleVar_Alpha, opacity);
    const auto open = ImGui::Begin(
        "##smvm_replay_bar", nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoScrollbar |
        ImGuiWindowFlags_NoScrollWithMouse | ImGuiWindowFlags_NoSavedSettings);
    if (open) {
        const auto paused = (snapshot.flags & smvm_snapshot_paused) != 0;
        if (smvm_theme::PrimaryButton(paused ? "Play" : "Pause", true, ImVec2(66.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::toggle_replay_pause);
        ImGui::SameLine();
        if (smvm_theme::Button("-1", snapshot.current_tick > 0, ImVec2(42.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::step_back);
        ImGui::SameLine();
        if (smvm_theme::Button("+1", snapshot.current_tick >= 0, ImVec2(42.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::step_forward);
        constexpr std::array<double, 5> speeds{0.25, 0.5, 1.0, 2.0, 4.0};
        for (const auto speed : speeds) {
            ImGui::SameLine();
            std::array<char, 16> label{};
            static_cast<void>(std::snprintf(label.data(), label.size(), "%gx", speed));
            const auto active = std::abs(snapshot.timescale - speed) < 0.001;
            const auto clicked = active
                ? smvm_theme::PrimaryButton(label.data(), true, ImVec2(48.0F * scale, 0.0F))
                : smvm_theme::Button(label.data(), true, ImVec2(48.0F * scale, 0.0F));
            if (clicked) {
                if (!active)
                    QueueAction(context, SmvmActionType::set_timescale, -1, -1, speed);
            }
        }
        ImGui::SameLine();
        ImGui::SetCursorPosX(std::max(ImGui::GetCursorPosX(), width - (226.0F * scale)));
        if (smvm_theme::Button("Menu", true, ImVec2(64.0F * scale, 0.0F)))
            SmvmOpenMenu();
        ImGui::SameLine();
        if (smvm_theme::Button("Clean", true, ImVec2(64.0F * scale, 0.0F)))
        {
            state.clean_hint_pending = true;
            state.clean_hint_until_ms = GetTickCount64() + 2400;
        }
        ImGui::SameLine();
        if (smvm_theme::Button("Restore", true, ImVec2(72.0F * scale, 0.0F)))
            QueueAction(context, SmvmActionType::restore_deadlock_ui);

        if (!state.replay_scrubbing)
            state.replay_scrub_tick = std::max<std::int64_t>(snapshot.current_tick, 0);
        const std::int64_t minimum = 0;
        const auto maximum = std::max<std::int64_t>(snapshot.total_ticks, 1);
        ImGui::SetNextItemWidth(width - (188.0F * scale));
        if (ImGui::SliderScalar(
                "##replay_progress", ImGuiDataType_S64, &state.replay_scrub_tick,
                &minimum, &maximum, "", ImGuiSliderFlags_AlwaysClamp))
            state.replay_scrubbing = true;
        if (state.replay_scrubbing && ImGui::IsItemDeactivatedAfterEdit()) {
            QueueAction(context, SmvmActionType::seek_tick, -1, state.replay_scrub_tick);
            state.replay_scrubbing = false;
        }
        ImGui::SameLine();
        std::array<char, 64> tick_text{};
        static_cast<void>(std::snprintf(
            tick_text.data(), tick_text.size(), "%lld / %lld",
            static_cast<long long>(std::max<std::int64_t>(snapshot.current_tick, 0)),
            static_cast<long long>(std::max<std::int64_t>(snapshot.total_ticks, 0))));
        ImGui::TextUnformatted(tick_text.data());
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
    if (snapshot.deadlock_ui_mode == DeadlockUiMode::smvm_replay_ui) {
        const auto replay_clearance = 140.0F * static_cast<float>(snapshot.replay_bar_scale);
        if (bottom && snapshot.replay_bar_anchor == SmvmReplayBarAnchor::bottom)
            vertical_position -= replay_clearance;
        else if (!bottom && snapshot.replay_bar_anchor == SmvmReplayBarAnchor::top)
            vertical_position += replay_clearance;
    }
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
            ImGui::TextColored(smvm_theme::Vec4(smvm_theme::colors::kAccent), "CAMPATH");
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

void DrawCleanFootageHint(const FrameContext& context) noexcept {
    auto& state = *context.state;
    if (!state.clean_hint_pending)
        return;
    const auto now = GetTickCount64();
    if (now >= state.clean_hint_until_ms) {
        state.clean_hint_pending = false;
        state.clean_hint_until_ms = 0;
        QueueAction(context, SmvmActionType::set_deadlock_ui_mode,
                    static_cast<std::int32_t>(DeadlockUiMode::clean_footage));
        return;
    }

    const auto scale = smvm_theme::GetScale();
    ImGui::SetNextWindowPos(
        ImVec2(context.params->viewport_width * 0.5F, 42.0F * scale),
        ImGuiCond_Always, ImVec2(0.5F, 0.0F));
    ImGui::SetNextWindowBgAlpha(0.96F);
    ImGui::Begin(
        "##clean_footage_hint", nullptr,
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_AlwaysAutoResize |
        ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoSavedSettings);
    ImGui::PushFont(smvm_theme::GetFonts().section);
    ImGui::TextUnformatted("CLEAN FOOTAGE ENABLED");
    ImGui::PopFont();
    SecondaryText("F10 - Restore Movie UI");
    SecondaryText("TAB - Restore and Open Menu");
    SecondaryText("F9 - Emergency Restore");
    ImGui::End();
}

} // namespace

void DrawFrame(const SmvmUiFrameParams& params, SmvmUiState& state) noexcept {
    if (params.snapshot == nullptr)
        return;
    if (params.documents != nullptr) {
        state.documents = *params.documents;
        state.documents_valid = true;
    }
    FrameContext context{&params, &state, params.snapshot};
    if (state.free_camera_activation_pending) {
        const auto now = GetTickCount64();
        const auto manual_active =
            (params.snapshot->flags & smvm_snapshot_manual_camera_active) != 0;
        const auto manual_requested =
            (params.snapshot->flags & smvm_snapshot_manual_camera_requested) != 0;
        if (manual_active) {
            state.free_camera_activation_pending = false;
            state.free_camera_activation_started_ms = 0;
            SmvmCloseMenu();
            return;
        }
        if (!manual_requested && now - state.free_camera_activation_started_ms >= 8000) {
            state.free_camera_activation_pending = false;
            state.free_camera_activation_started_ms = 0;
            state.free_camera_activation_error_until_ms = now + 5000;
        }
    }
    if (params.menu_open) {
        DrawShell(context);
    } else if (params.snapshot->deadlock_ui_mode == DeadlockUiMode::smvm_replay_ui) {
        DrawReplayBar(context);
    } else {
        state.open_save_as_modal = false;
        state.open_load_picker = false;
        state.picker_requested_list = false;
        state.confirm_pending = false;
        state.confirm_action = SmvmConfirmAction::none;
        DrawMinimalPill(context);
    }
    if (!params.menu_open)
        DrawStatusHud(context);
    DrawCleanFootageHint(context);
    DrawToasts(context);
}

} // namespace deadlock_mvm::smvm_ui
