#pragma once
#include <cmath>
#include <cstdint>
#include <type_traits>
namespace deadlock_mvm {
#pragma pack(push, 1)
struct LookSettings final {
    std::uint32_t schema_version{1}, enabled{0};
    std::uint64_t revision{0};
    float strength{1.0f};
    float exposure{0.0f};
    float contrast{1.0f};
    float saturation{1.0f};
    float temperature{0.0f};
    float tint{0.0f};
    float lift_r{0.0f};
    float lift_g{0.0f};
    float lift_b{0.0f};
    float gamma_r{1.0f};
    float gamma_g{1.0f};
    float gamma_b{1.0f};
    float gain_r{1.0f};
    float gain_g{1.0f};
    float gain_b{1.0f};
    float shadows{0.0f};
    float highlights{0.0f};
    float vibrance{0.0f};
    float bloom_threshold{0.8f};
    float bloom_knee{0.5f};
    float bloom_intensity{0.0f};
    float bloom_radius{1.0f};
    float sharpen{0.0f};
    float vignette{0.0f};
    float grain_strength{0.0f};
    std::uint32_t grain_seed{1}, bloom_quality{1};
    float lut_intensity{0};
    std::uint32_t lut_size{0};
    std::uint64_t lut_revision{0};
};
#pragma pack(pop)
static_assert(sizeof(LookSettings) == 140);
static_assert(std::is_trivially_copyable_v<LookSettings>);
inline bool ValidateLookSettings(const LookSettings& s) noexcept {
    if (s.schema_version != 1 || s.enabled > 1 || s.bloom_quality > 2 || (s.lut_size != 0 && (s.lut_size < 2 || s.lut_size > 33))) return false;
    if (!std::isfinite(s.strength) || s.strength < 0.0f || s.strength > 1.0f) return false;
    if (!std::isfinite(s.exposure) || s.exposure < -5.0f || s.exposure > 5.0f) return false;
    if (!std::isfinite(s.contrast) || s.contrast < 0.0f || s.contrast > 2.0f) return false;
    if (!std::isfinite(s.saturation) || s.saturation < 0.0f || s.saturation > 2.0f) return false;
    if (!std::isfinite(s.temperature) || s.temperature < -1.0f || s.temperature > 1.0f) return false;
    if (!std::isfinite(s.tint) || s.tint < -1.0f || s.tint > 1.0f) return false;
    if (!std::isfinite(s.lift_r) || s.lift_r < -1.0f || s.lift_r > 1.0f) return false;
    if (!std::isfinite(s.lift_g) || s.lift_g < -1.0f || s.lift_g > 1.0f) return false;
    if (!std::isfinite(s.lift_b) || s.lift_b < -1.0f || s.lift_b > 1.0f) return false;
    if (!std::isfinite(s.gamma_r) || s.gamma_r < 0.1f || s.gamma_r > 4.0f) return false;
    if (!std::isfinite(s.gamma_g) || s.gamma_g < 0.1f || s.gamma_g > 4.0f) return false;
    if (!std::isfinite(s.gamma_b) || s.gamma_b < 0.1f || s.gamma_b > 4.0f) return false;
    if (!std::isfinite(s.gain_r) || s.gain_r < 0.0f || s.gain_r > 4.0f) return false;
    if (!std::isfinite(s.gain_g) || s.gain_g < 0.0f || s.gain_g > 4.0f) return false;
    if (!std::isfinite(s.gain_b) || s.gain_b < 0.0f || s.gain_b > 4.0f) return false;
    if (!std::isfinite(s.shadows) || s.shadows < -1.0f || s.shadows > 1.0f) return false;
    if (!std::isfinite(s.highlights) || s.highlights < -1.0f || s.highlights > 1.0f) return false;
    if (!std::isfinite(s.vibrance) || s.vibrance < -1.0f || s.vibrance > 1.0f) return false;
    if (!std::isfinite(s.bloom_threshold) || s.bloom_threshold < 0.0f || s.bloom_threshold > 1.0f) return false;
    if (!std::isfinite(s.bloom_knee) || s.bloom_knee < 0.0f || s.bloom_knee > 1.0f) return false;
    if (!std::isfinite(s.bloom_intensity) || s.bloom_intensity < 0.0f || s.bloom_intensity > 2.0f) return false;
    if (!std::isfinite(s.bloom_radius) || s.bloom_radius < 0.25f || s.bloom_radius > 4.0f) return false;
    if (!std::isfinite(s.sharpen) || s.sharpen < 0.0f || s.sharpen > 1.0f) return false;
    if (!std::isfinite(s.vignette) || s.vignette < 0.0f || s.vignette > 1.0f) return false;
    if (!std::isfinite(s.grain_strength) || s.grain_strength < 0.0f || s.grain_strength > 0.2f) return false;
    if (!std::isfinite(s.lut_intensity) || s.lut_intensity < 0.0f || s.lut_intensity > 1.0f) return false;
    return true;
}
inline bool IsNeutralLook(const LookSettings& s) noexcept {
    if (!s.enabled || s.strength == 0) return true;
    return s.exposure == 0.0f && s.contrast == 1.0f && s.saturation == 1.0f && s.temperature == 0.0f && s.tint == 0.0f && s.lift_r == 0.0f && s.lift_g == 0.0f && s.lift_b == 0.0f && s.gamma_r == 1.0f && s.gamma_g == 1.0f && s.gamma_b == 1.0f && s.gain_r == 1.0f && s.gain_g == 1.0f && s.gain_b == 1.0f && s.shadows == 0.0f && s.highlights == 0.0f && s.vibrance == 0.0f && s.bloom_intensity == 0.0f && s.sharpen == 0.0f && s.vignette == 0.0f && s.grain_strength == 0.0f && s.lut_intensity == 0;
}
}
