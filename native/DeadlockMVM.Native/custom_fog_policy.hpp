#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>

namespace deadlock_mvm {

// Deadlock's current D3D11 world depth is reversed-Z (far == 0). The default
// world projection uses a four-unit near plane; this converts the movie-tool
// world-distance controls into device-depth thresholds without reading the
// depth surface back to the CPU.
constexpr float kDeadlockFogNearPlane = 4.0F;
constexpr std::size_t kCustomFogLayerCount = 64;

struct CustomFogLayer final {
    float device_depth{};
    float incremental_alpha{};
};

[[nodiscard]] inline std::array<CustomFogLayer, kCustomFogLayerCount>
BuildCustomFogLayers(
    const float requested_start,
    const float requested_end,
    const float requested_maximum_density,
    const float requested_exponent) noexcept {
    std::array<CustomFogLayer, kCustomFogLayerCount> layers{};
    if (!std::isfinite(requested_start) || !std::isfinite(requested_end) ||
        !std::isfinite(requested_maximum_density) || !std::isfinite(requested_exponent) ||
        requested_end <= requested_start) {
        return layers;
    }

    const auto maximum_density = std::clamp(requested_maximum_density, 0.0F, 1.0F);
    const auto exponent = std::clamp(requested_exponent, 0.01F, 10.0F);
    auto previous_opacity = 0.0F;
    for (std::size_t index = 0; index < layers.size(); ++index) {
        const auto t = static_cast<float>(index + 1) /
            static_cast<float>(layers.size());
        const auto distance = std::max(
            1.0F,
            requested_start + ((requested_end - requested_start) * t));
        const auto target_opacity = maximum_density * std::pow(t, exponent);
        const auto remaining = std::max(0.0F, 1.0F - previous_opacity);
        const auto incremental_alpha = remaining <= 0.000001F
            ? 0.0F
            : std::clamp(
                1.0F - ((1.0F - target_opacity) / remaining),
                0.0F,
                1.0F);
        layers[index] = {
            std::clamp(kDeadlockFogNearPlane / distance, 0.0F, 1.0F),
            incremental_alpha,
        };
        previous_opacity = target_opacity;
    }
    return layers;
}

[[nodiscard]] inline float CompositeFogOpacity(
    const std::array<CustomFogLayer, kCustomFogLayerCount>& layers) noexcept {
    auto transparency = 1.0F;
    for (const auto& layer : layers)
        transparency *= 1.0F - std::clamp(layer.incremental_alpha, 0.0F, 1.0F);
    return 1.0F - transparency;
}

} // namespace deadlock_mvm
