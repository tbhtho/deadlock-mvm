#pragma once

#include <algorithm>
#include <cmath>

namespace deadlock_mvm {

enum class ManualCameraShortcutAction {
    none,
    enter_or_reacquire,
    exit,
    consume,
};

enum class MovieMakerShortcutAction {
    none,
    toggle_replay_pause,
    decrease_camera_speed,
    increase_camera_speed,
};

[[nodiscard]] constexpr MovieMakerShortcutAction ResolveMovieMakerShortcut(
    const bool fixed_pause_matches,
    const bool configured_pause_matches,
    const bool decrease_speed_matches,
    const bool increase_speed_matches,
    const bool replay_active,
    const bool manual_camera_active,
    const bool speed_modifiers_allowed) noexcept {
    if (replay_active && (fixed_pause_matches || configured_pause_matches))
        return MovieMakerShortcutAction::toggle_replay_pause;
    if (!manual_camera_active || !speed_modifiers_allowed)
        return MovieMakerShortcutAction::none;
    if (decrease_speed_matches)
        return MovieMakerShortcutAction::decrease_camera_speed;
    if (increase_speed_matches)
        return MovieMakerShortcutAction::increase_camera_speed;
    return MovieMakerShortcutAction::none;
}

// F2 (or its configured replacement) is deliberately one-way: it enters or
// reacquires SMVM Free Camera and can never toggle an owned camera off. Escape
// is the sole normal closed-menu exit gesture. F9 keeps its emergency restore
// action, whose managed handler also performs the same explicit camera exit.
[[nodiscard]] constexpr ManualCameraShortcutAction ResolveManualCameraShortcut(
    const bool enter_binding_matches,
    const bool escape_matches,
    const bool menu_open,
    const bool replay_active,
    const bool campath_playing,
    const bool camera_owned,
    const bool full_camera_ownership) noexcept {
    if (menu_open)
        return ManualCameraShortcutAction::none;
    if (escape_matches && camera_owned)
        return ManualCameraShortcutAction::exit;
    if (enter_binding_matches) {
        if (full_camera_ownership || campath_playing)
            return ManualCameraShortcutAction::consume;
        if (replay_active && !campath_playing)
            return ManualCameraShortcutAction::enter_or_reacquire;
        if (camera_owned)
            return ManualCameraShortcutAction::consume;
    }
    return ManualCameraShortcutAction::none;
}

// CameraOwned is the durable managed ownership intent. It covers acquisition,
// manual motion, path playback, and restore/rebase windows; InputTakeover makes
// that intent authoritative for Deadlock input suppression throughout each
// transition. Manual motion still uses HasSmvmManualCameraOwnership below.
[[nodiscard]] constexpr bool HasSmvmCameraInputTakeover(
    const bool camera_owned,
    const bool input_takeover) noexcept {
    return camera_owned && input_takeover;
}

// Once SMVM owns the rendered camera, every otherwise-unhandled keyboard or
// mouse event belongs to SMVM too. This prevents Deadlock movement, attacks,
// spectator cycling, and observer camera input from competing with Free Cam.
// Windows-level escape routes such as Alt+F4 are handled before this policy.
[[nodiscard]] constexpr bool ShouldSuppressGameplayInput(
    const bool camera_input_takeover,
    const bool handled_by_smvm,
    const bool reserved_for_system) noexcept {
    return camera_input_takeover && !handled_by_smvm && !reserved_for_system;
}

// The editor menu binding is reserved for SMVM for the entire replay, not only
// after camera readiness is complete. Tab is Deadlock's scoreboard key, so a
// transient camera reacquisition window must never leak it back to the game.
[[nodiscard]] constexpr bool ShouldReserveEditorMenuBinding(
    const bool internal_enabled,
    const bool replay_active,
    const bool binding_matches) noexcept {
    return internal_enabled && replay_active && binding_matches;
}

[[nodiscard]] constexpr bool HasSmvmManualCameraOwnership(
    const bool manual_camera_requested,
    const bool manual_camera_active) noexcept {
    return manual_camera_requested && manual_camera_active;
}

// Event delivery remains authoritative. The foreground polling fallback may
// supplement it only after observing the key released once in the current
// ownership epoch, preventing a key held across focus/menu/camera transitions
// from starting motion. Takeover additionally requires an SMVM-owned route.
[[nodiscard]] constexpr bool ResolveHeldManualBinding(
    const bool event_down,
    const bool polled_down,
    const bool polling_armed,
    const bool modifiers_match,
    const bool input_takeover,
    const bool smvm_route_owned) noexcept {
    if (!modifiers_match)
        return false;
    const auto physically_down = event_down || (polling_armed && polled_down);
    return physically_down && (!input_takeover || smvm_route_owned);
}

// The restart workflow intentionally exposes one coarse camera-speed control:
// - slows Free Camera and + speeds it up. Reciprocal factors make one step in
// either direction reversible (600 -> 750 -> 600) without a settings page.
[[nodiscard]] inline double AdjustManualCameraSpeed(
    const double current_speed,
    const bool increase) noexcept {
    if (!std::isfinite(current_speed))
        return 600.0;
    const auto adjusted = current_speed * (increase ? 1.25 : 0.8);
    return std::clamp(std::round(adjusted * 100.0) / 100.0, 1.0, 10000.0);
}

} // namespace deadlock_mvm
