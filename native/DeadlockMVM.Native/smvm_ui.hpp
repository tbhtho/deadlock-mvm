#pragma once

// SMVM internal editor pages (Dear ImGui). Implements the shell, navigation,
// pages, toasts and modals from tools/research/smvm_ui_v2.md. The overlay
// (smvm_overlay.cpp) owns the ImGui context, feeds io events, and calls
// smvm_ui::DrawFrame once per rendered frame inside NewFrame/Render.

#include "protocol.hpp"

#include <array>
#include <cstdint>
#include <functional>

namespace deadlock_mvm {

enum class SmvmPage : std::uint32_t {
    replay,
    camera,
    campath,
    visuals,
    capture,
    settings,
};

enum class SmvmSettingsSection : std::uint32_t {
    camera,
    replay,
    campath,
    interface_settings,
    storage,
    advanced,
};

// Binding action indices carried in SmvmActionPayload.index for set_binding.
// Shared with the overlay-side capture state machine.
inline constexpr std::int32_t kSmvmFirstManualBindingAction = 100;
inline constexpr std::int32_t kSmvmFirstEditorBindingAction = 111;
inline constexpr std::int32_t kSmvmLastBindingAction = 128;

inline constexpr std::size_t kSmvmMaxToasts = 3;
inline constexpr std::uint64_t kSmvmToastDurationMs = 2500;

struct SmvmUiToast final {
    std::array<char, 128> text{};
    std::uint64_t created_ms{};
    bool active{};
};

// Confirmation flows that need a modal before queueing the action.
enum class SmvmConfirmAction : std::uint32_t {
    none = 0,
    new_path = 1,
    close_path = 2,
    load_path = 3,
};

struct SmvmUiState final {
    SmvmPage page{SmvmPage::campath};
    SmvmSettingsSection settings_section{SmvmSettingsSection::camera};
    // Remembered window geometry (logical, unscaled pixels).
    bool window_positioned{};
    float window_x{};
    float window_y{};
    float window_w{780.0F};
    float window_h{600.0F};
    std::array<char, 24> go_to_tick{};
    std::array<char, 64> save_as_name{};
    bool open_save_as_modal{};
    bool open_load_picker{};
    bool picker_requested_list{};
    bool confirm_pending{};
    SmvmConfirmAction confirm_action{SmvmConfirmAction::none};
    std::int32_t confirm_load_index{-1};
    std::array<SmvmUiToast, kSmvmMaxToasts> toasts{};
    std::array<char, 128> last_status{};
    bool have_last_status{};
    // Last successfully read documents payload, kept so the Load picker does
    // not flicker when the channel goes stale between refreshes.
    CampathDocumentsPayload documents{};
    bool documents_valid{};
    std::int64_t replay_scrub_tick{};
    bool replay_scrubbing{};
    bool clean_hint_pending{};
    std::uint64_t clean_hint_until_ms{};
    bool free_camera_activation_pending{};
    std::uint64_t free_camera_activation_started_ms{};
    std::uint64_t free_camera_activation_error_until_ms{};
};

struct SmvmUiFrameParams final {
    const SmvmSnapshotPayload* snapshot{};
    const CameraSample* rendered_camera{};
    const CampathPayloadHeader* path_header{};
    const CampathKeyframe* keyframes{};
    bool has_path{};
    const CampathDocumentsPayload* documents{};
    bool menu_open{};
    float viewport_width{};
    float viewport_height{};
    std::uint32_t frame_microseconds{};
    std::uint32_t overlay_flags{};
    std::uint32_t renderer_error{};
    std::function<bool(const SmvmActionPayload&)> queue_action{};
    std::function<void()> request_capture{};
};

namespace smvm_ui {

// Toast state tracking; needs no live ImGui frame and must run every frame so
// closed-menu notifications are detected.
void UpdateToasts(const SmvmSnapshotPayload& snapshot, SmvmUiState& state) noexcept;
void DrawFrame(const SmvmUiFrameParams& params, SmvmUiState& state) noexcept;

} // namespace smvm_ui

// --- Overlay-provided hooks (implemented by smvm_overlay.cpp) -------------
// The binding capture state machine and the modal menu transition stay
// authoritative in the overlay; the UI renders and triggers them.

struct SmvmBindingRowStatus final {
    bool capturing{};
    bool pending{};
    // 0 none, 1 saved, 2 rejected (conflict), 3 keyboard-only.
    std::uint32_t feedback_kind{};
    std::int32_t conflict_action{-1};
};

[[nodiscard]] SmvmBindingRowStatus SmvmPumpBindingRow(
    std::int32_t action,
    std::uint32_t current_value) noexcept;
void SmvmBeginBindingCapture(std::int32_t action, std::uint32_t original) noexcept;
void SmvmClearBinding(std::int32_t action, std::uint32_t original) noexcept;
void SmvmCloseMenu() noexcept;
void SmvmOpenMenu() noexcept;

} // namespace deadlock_mvm
