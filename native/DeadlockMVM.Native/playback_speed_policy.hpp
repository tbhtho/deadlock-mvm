#pragma once

#include <array>
#include <cmath>
#include <optional>

namespace deadlock_mvm {

inline constexpr std::array<double, 5> kPlaybackSpeedPresets{
    0.25,
    0.5,
    1.0,
    2.0,
    4.0,
};

enum class PlaybackSpeedShortcutAction {
    none,
    slower,
    faster,
};

[[nodiscard]] constexpr PlaybackSpeedShortcutAction ResolvePlaybackSpeedShortcut(
    const bool left_arrow,
    const bool right_arrow,
    const bool replay_active,
    const bool menu_open,
    const bool text_input_owns_keyboard,
    const bool modifiers_allowed) noexcept {
    if (!replay_active || menu_open || text_input_owns_keyboard || !modifiers_allowed)
        return PlaybackSpeedShortcutAction::none;
    if (left_arrow)
        return PlaybackSpeedShortcutAction::slower;
    if (right_arrow)
        return PlaybackSpeedShortcutAction::faster;
    return PlaybackSpeedShortcutAction::none;
}

// Arrow stepping follows the same complete preset ladder exposed by the
// timeline. Custom speeds step to the nearest preset strictly in the requested
// direction; values already at an endpoint remain there.
[[nodiscard]] inline std::optional<double> StepPlaybackSpeedPreset(
    const double current_speed,
    const PlaybackSpeedShortcutAction action) noexcept {
    if (!std::isfinite(current_speed) || action == PlaybackSpeedShortcutAction::none)
        return std::nullopt;

    constexpr auto tolerance = 0.001;
    for (std::size_t index = 0; index < kPlaybackSpeedPresets.size(); ++index) {
        if (std::abs(current_speed - kPlaybackSpeedPresets[index]) > tolerance)
            continue;
        if (action == PlaybackSpeedShortcutAction::faster &&
            index + 1 < kPlaybackSpeedPresets.size()) {
            return kPlaybackSpeedPresets[index + 1];
        }
        if (action == PlaybackSpeedShortcutAction::slower && index > 0)
            return kPlaybackSpeedPresets[index - 1];
        return std::nullopt;
    }

    if (action == PlaybackSpeedShortcutAction::faster) {
        for (const auto preset : kPlaybackSpeedPresets) {
            if (preset > current_speed)
                return preset;
        }
        return std::nullopt;
    }

    for (auto index = kPlaybackSpeedPresets.size(); index > 0; --index) {
        const auto preset = kPlaybackSpeedPresets[index - 1];
        if (preset < current_speed)
            return preset;
    }
    return std::nullopt;
}

} // namespace deadlock_mvm
