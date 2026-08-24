#pragma once

#include <cstdint>
#include <cstdlib>

namespace deadlock_mvm {

struct LegacyMouseDelta final {
    std::int32_t x{};
    std::int32_t y{};
    bool accepted{};
};

// SDL raw-relative input remains authoritative. This fallback accepts only a
// small client-coordinate delta after a seed event, so an absolute cursor warp
// or the first frame after closing the menu can never jump the camera.
[[nodiscard]] inline LegacyMouseDelta ResolveLegacyMouseDelta(
    const bool seeded,
    const std::int32_t previous_x,
    const std::int32_t previous_y,
    const std::int32_t current_x,
    const std::int32_t current_y,
    const bool raw_input_recent) noexcept {
    if (!seeded || raw_input_recent)
        return {};
    const auto delta_x = static_cast<std::int64_t>(current_x) - previous_x;
    const auto delta_y = static_cast<std::int64_t>(current_y) - previous_y;
    constexpr auto kMaximumLegacyDelta = std::int64_t{256};
    if (std::abs(delta_x) > kMaximumLegacyDelta ||
        std::abs(delta_y) > kMaximumLegacyDelta ||
        (delta_x == 0 && delta_y == 0))
        return {};
    return {
        static_cast<std::int32_t>(delta_x),
        static_cast<std::int32_t>(delta_y),
        true,
    };
}

} // namespace deadlock_mvm
