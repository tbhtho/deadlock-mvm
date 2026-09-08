#pragma once

#include <algorithm>
#include <cstdint>

namespace deadlock_mvm {

enum class MovieOutputResolution : std::uint32_t {
    game = 0,
    full_hd = 1,
};

struct MovieOutputDimensions final {
    std::uint32_t width{};
    std::uint32_t height{};
};

struct MovieResizeRegion final {
    std::uint32_t x{};
    std::uint32_t y{};
    std::uint32_t width{};
    std::uint32_t height{};
};

[[nodiscard]] constexpr MovieOutputDimensions ResolveMovieOutputDimensions(
    const MovieOutputResolution resolution,
    const std::uint32_t game_width,
    const std::uint32_t game_height) noexcept {
    if (resolution == MovieOutputResolution::full_hd)
        return {1920u, 1080u};
    return {game_width, game_height};
}

// Fits the game frame inside the requested canvas without changing its aspect
// ratio. The common 1600x900 -> 1920x1080 path occupies the complete canvas.
[[nodiscard]] constexpr MovieResizeRegion FitMovieResizeRegion(
    const std::uint32_t source_width,
    const std::uint32_t source_height,
    const std::uint32_t target_width,
    const std::uint32_t target_height) noexcept {
    if (source_width == 0 || source_height == 0 ||
        target_width == 0 || target_height == 0) {
        return {};
    }
    const auto width_limited =
        static_cast<std::uint64_t>(target_width) * source_height <=
        static_cast<std::uint64_t>(target_height) * source_width;
    const auto fitted_width = width_limited
        ? target_width
        : static_cast<std::uint32_t>(
              (static_cast<std::uint64_t>(target_height) * source_width) /
              source_height);
    const auto fitted_height = width_limited
        ? static_cast<std::uint32_t>(
              (static_cast<std::uint64_t>(target_width) * source_height) /
              source_width)
        : target_height;
    const auto safe_width = std::max<std::uint32_t>(1u, fitted_width);
    const auto safe_height = std::max<std::uint32_t>(1u, fitted_height);
    return {
        (target_width - safe_width) / 2u,
        (target_height - safe_height) / 2u,
        safe_width,
        safe_height,
    };
}

[[nodiscard]] constexpr std::uint32_t MovieNearestSourceCoordinate(
    const std::uint32_t destination,
    const std::uint32_t destination_extent,
    const std::uint32_t source_extent) noexcept {
    if (destination_extent == 0 || source_extent == 0)
        return 0;
    return std::min(
        source_extent - 1u,
        static_cast<std::uint32_t>(
            (static_cast<std::uint64_t>(destination) * source_extent) /
            destination_extent));
}

} // namespace deadlock_mvm
