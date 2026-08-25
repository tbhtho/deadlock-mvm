#pragma once

#include <atomic>
#include <cstdint>

namespace deadlock_mvm {

// Canonical Free Camera mouse convention used by every transport:
//   +look_right = physical motion to the right
//   +look_up    = physical motion upward
// Win32 raw and legacy client coordinates both report +Y downward, so the
// vertical conversion happens exactly once at the transport boundary.
struct CanonicalMouseDelta final {
    std::int32_t look_right{};
    std::int32_t look_up{};
};

[[nodiscard]] constexpr CanonicalMouseDelta NormalizeRawMouseDelta(
    const std::int32_t delta_x,
    const std::int32_t delta_y) noexcept {
    return {delta_x, -delta_y};
}

[[nodiscard]] constexpr CanonicalMouseDelta NormalizeFallbackMouseDelta(
    const std::int32_t delta_x,
    const std::int32_t delta_y) noexcept {
    return {delta_x, -delta_y};
}

struct FreeCameraInputReadiness final {
    bool keyboard_ready{};
    bool relative_mouse_ready{};
    bool raw_input_ready{};
    bool cursor_ready{};
    bool foreground_ready{};
    bool window_procedure_ready{};
    bool engine_input_ready{};

    [[nodiscard]] constexpr bool KeyboardReady() const noexcept {
        return keyboard_ready && foreground_ready && window_procedure_ready && engine_input_ready;
    }

    [[nodiscard]] constexpr bool MouseReady() const noexcept {
        return relative_mouse_ready && raw_input_ready && cursor_ready && foreground_ready &&
               window_procedure_ready && engine_input_ready;
    }

    [[nodiscard]] constexpr bool FullyReady() const noexcept {
        return KeyboardReady() && MouseReady();
    }

    [[nodiscard]] constexpr bool AnyReady() const noexcept {
        return KeyboardReady() || MouseReady();
    }

    // Keyboard-only control is deliberately usable (including while paused),
    // but it is not a successful full acquisition. The overlay keeps a typed,
    // retryable error visible until the relative mouse route is also ready.
    [[nodiscard]] constexpr bool PartiallyReady() const noexcept {
        return AnyReady() && !FullyReady();
    }
};

[[nodiscard]] constexpr bool ShouldReacquireFreeCameraInputAfterMenu(
    const bool was_menu_open,
    const bool menu_open,
    const bool free_camera_requested,
    const bool free_camera_active) noexcept {
    return was_menu_open && !menu_open && free_camera_requested && free_camera_active;
}

struct MouseAcquisitionReset final {
    std::int64_t accumulated_look_right{};
    std::int64_t accumulated_look_up{};
    std::uint64_t raw_timestamp_ms{};
    std::uint64_t fallback_timestamp_ms{};
    bool fallback_seeded{};
    bool discard_next_fallback_sample{true};
};

[[nodiscard]] constexpr MouseAcquisitionReset ResetMouseAcquisition() noexcept {
    return {};
}

[[nodiscard]] constexpr bool CanConsumeFreeCameraInput(
    const bool menu_open,
    const bool free_camera_requested,
    const bool free_camera_active,
    const FreeCameraInputReadiness& readiness,
    const bool replay_seek_in_progress = false) noexcept {
    return !menu_open && !replay_seek_in_progress && free_camera_requested &&
           free_camera_active && readiness.AnyReady();
}

[[nodiscard]] constexpr bool CanConsumeFreeCameraKeyboardInput(
    const bool menu_open,
    const bool free_camera_requested,
    const bool free_camera_active,
    const FreeCameraInputReadiness& readiness,
    const bool replay_seek_in_progress = false) noexcept {
    return !menu_open && !replay_seek_in_progress && free_camera_requested &&
           free_camera_active && readiness.KeyboardReady();
}

[[nodiscard]] constexpr bool CanConsumeFreeCameraMouseInput(
    const bool menu_open,
    const bool free_camera_requested,
    const bool free_camera_active,
    const FreeCameraInputReadiness& readiness,
    const bool replay_seek_in_progress = false) noexcept {
    return !menu_open && !replay_seek_in_progress && free_camera_requested &&
           free_camera_active && readiness.MouseReady();
}

// Pure lifecycle mirror for transition tests. The Win32 implementation owns
// the actual SDL/Raw Input/cursor work, while this state keeps the invariants
// explicit: opening always suspends and clears, closing always starts from a
// fresh acquisition, and each input channel is consumable only after its own
// readiness contract is complete.
struct FreeCameraMenuLifecycle final {
    bool menu_open{};
    bool free_camera_requested{};
    bool free_camera_active{};
    FreeCameraInputReadiness readiness{};
    MouseAcquisitionReset mouse{ResetMouseAcquisition()};
    std::uint32_t successful_reacquisitions{};

    constexpr void OpenMenu() noexcept {
        menu_open = true;
        readiness = {};
        mouse = ResetMouseAcquisition();
    }

    [[nodiscard]] constexpr bool CloseMenu(
        const FreeCameraInputReadiness& acquired_readiness) noexcept {
        const auto should_reacquire = ShouldReacquireFreeCameraInputAfterMenu(
            menu_open, false, free_camera_requested, free_camera_active);
        menu_open = false;
        mouse = ResetMouseAcquisition();
        readiness = should_reacquire ? acquired_readiness : FreeCameraInputReadiness{};
        if (readiness.AnyReady())
            ++successful_reacquisitions;
        return CanConsume();
    }

    [[nodiscard]] constexpr bool CanConsume() const noexcept {
        return CanConsumeFreeCameraInput(
            menu_open, free_camera_requested, free_camera_active, readiness);
    }
};

constexpr std::uint64_t kFallbackMouseReacquisitionSettleMs = 75;
constexpr std::uint64_t kManualPointerHealthCheckIntervalMs = 250;

// Deadlock may rebuild its process-wide Raw Input registration when replay
// transport changes. The cached readiness bits cannot observe that external
// mutation, so Free Camera periodically validates the live registration and
// asks the window thread to rebuild the full pointer route when it drifts.
[[nodiscard]] constexpr bool ShouldRetryManualPointerAcquisition(
    const bool pointer_requested,
    const bool menu_open,
    const bool pointer_active,
    const bool raw_registration_exact,
    const std::uint64_t now_ms,
    const std::uint64_t next_health_check_ms) noexcept {
    return pointer_requested && !menu_open && now_ms >= next_health_check_ms &&
           (!pointer_active || !raw_registration_exact);
}

// Reacquiring SDL relative mode can queue more than one legacy WM_MOUSEMOVE:
// a baseline, a recenter, and a delayed echo of the pre-recenter coordinate.
// Raw Input remains live during this short legacy-only settle window. Every
// accepted fallback packet in the window refreshes the absolute baseline and
// consumes the one-sample guard, so the first packet after the queue settles is
// usable immediately. If no packet arrived while settling, retain the guard and
// discard exactly the first later accepted fallback sample.
[[nodiscard]] inline bool ShouldApplyFallbackMouseSample(
    const bool accepted,
    const std::uint64_t now_ms,
    const std::uint64_t settle_until_ms,
    std::atomic<bool>& discard_next_accepted_sample) noexcept {
    if (!accepted)
        return false;
    if (settle_until_ms != 0 && now_ms < settle_until_ms) {
        discard_next_accepted_sample.store(false, std::memory_order_release);
        return false;
    }
    return !discard_next_accepted_sample.exchange(false, std::memory_order_acq_rel);
}

} // namespace deadlock_mvm
