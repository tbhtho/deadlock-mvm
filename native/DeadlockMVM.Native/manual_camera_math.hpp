#pragma once

#include "campath_math.hpp"

#include <algorithm>
#include <cmath>

namespace deadlock_mvm {

struct ManualCameraMotion final {
    double forward{};
    double right{};
    double up{};
    // Canonical mouse impulses: positive is physical right/up.
    double look_right{};
    double look_up{};
    double roll{};
    double wheel_steps{};
    bool reset_roll{};
};

struct ManualCameraTuning final {
    double movement_speed{600.0};
    double mouse_sensitivity{0.08};
    double roll_speed{45.0};
    double fov_step{1.0};
    bool invert_y{};
    bool invert_fov{};
};

[[nodiscard]] inline CameraSample ApplyManualCameraMotion(
    const CameraSample& current,
    ManualCameraMotion motion,
    const ManualCameraTuning& tuning,
    const double delta_seconds) noexcept {
    if (!ValidateSample(current) || !std::isfinite(delta_seconds) || delta_seconds < 0.0 ||
        !std::isfinite(tuning.movement_speed) || !std::isfinite(tuning.mouse_sensitivity) ||
        !std::isfinite(tuning.roll_speed) || !std::isfinite(tuning.fov_step))
        return current;

    CameraSample result = current;
    const auto move_length = std::sqrt(
        (motion.forward * motion.forward) + (motion.right * motion.right) + (motion.up * motion.up));
    if (move_length > 1.0) {
        motion.forward /= move_length;
        motion.right /= move_length;
        motion.up /= move_length;
    }

    constexpr auto radians = 3.14159265358979323846 / 180.0;
    const auto pitch = result.pitch * radians;
    const auto yaw = result.yaw * radians;
    const auto roll = result.roll * radians;
    const auto sp = std::sin(pitch);
    const auto cp = std::cos(pitch);
    const auto sy = std::sin(yaw);
    const auto cy = std::cos(yaw);
    const auto sr = std::sin(roll);
    const auto cr = std::cos(roll);
    const double forward_x = cp * cy;
    const double forward_y = cp * sy;
    const double forward_z = -sp;
    const double right_x = (-sr * sp * cy) + (cr * sy);
    const double right_y = (-sr * sp * sy) - (cr * cy);
    const double right_z = -sr * cp;
    const double up_x = (cr * sp * cy) + (sr * sy);
    const double up_y = (cr * sp * sy) - (sr * cy);
    const double up_z = cr * cp;
    const auto distance = tuning.movement_speed * std::clamp(delta_seconds, 0.0, 0.1);
    result.x += distance * ((motion.forward * forward_x) + (motion.right * right_x) + (motion.up * up_x));
    result.y += distance * ((motion.forward * forward_y) + (motion.right * right_y) + (motion.up * up_y));
    result.z += distance * ((motion.forward * forward_z) + (motion.right * right_z) + (motion.up * up_z));

    // Deadlock's rendered spectator angles turn toward physical right when Yaw
    // decreases and toward physical up when Pitch decreases. Keep that engine
    // convention here, after both Raw Input and the fallback have normalized to
    // the transport-independent right/up convention.
    result.yaw = NormalizeAngle(result.yaw - (motion.look_right * tuning.mouse_sensitivity));
    const auto pitch_delta = motion.look_up * tuning.mouse_sensitivity *
        (tuning.invert_y ? -1.0 : 1.0);
    result.pitch = std::clamp(result.pitch - pitch_delta, -89.0, 89.0);
    result.roll = motion.reset_roll
        ? 0.0
        : NormalizeAngle(result.roll + (motion.roll * tuning.roll_speed * std::clamp(delta_seconds, 0.0, 0.1)));
    const auto fov_direction = tuning.invert_fov ? 1.0 : -1.0;
    result.fov = std::clamp(result.fov + (motion.wheel_steps * tuning.fov_step * fov_direction),
                            kMinFov, kMaxFov);
    return ValidateSample(result) ? result : current;
}

} // namespace deadlock_mvm
