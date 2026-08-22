#pragma once

#include "protocol.hpp"

#include <algorithm>
#include <array>
#include <cmath>

namespace deadlock_mvm {

[[nodiscard]] inline double LerpValue(const double from, const double to, const double amount) noexcept {
    return from + ((to - from) * amount);
}

[[nodiscard]] inline double ShortestAngleDelta(const double from, const double to) noexcept {
    auto delta = std::fmod(to - from, 360.0);
    if (delta > 180.0) delta -= 360.0;
    if (delta < -180.0) delta += 360.0;
    return delta;
}

[[nodiscard]] inline double NormalizeAngle(double value) noexcept {
    value = std::fmod(value, 360.0);
    if (value > 180.0) value -= 360.0;
    if (value < -180.0) value += 360.0;
    return value;
}

[[nodiscard]] inline double ApplyCampathEasing(double amount, const CampathEasing easing) noexcept {
    amount = std::clamp(amount, 0.0, 1.0);
    switch (easing) {
        case CampathEasing::ease_in: return amount * amount;
        case CampathEasing::ease_out: return 1.0 - ((1.0 - amount) * (1.0 - amount));
        case CampathEasing::ease_in_out: return amount * amount * (3.0 - (2.0 * amount));
        case CampathEasing::linear: return amount;
    }
    return amount;
}

[[nodiscard]] inline CameraSample EvaluateLinearCamera(
    const CameraSample& from, const CameraSample& to, const double amount) noexcept {
    const auto t = std::clamp(amount, 0.0, 1.0);
    return CameraSample{
        LerpValue(from.x, to.x, t),
        LerpValue(from.y, to.y, t),
        LerpValue(from.z, to.z, t),
        LerpValue(from.pitch, to.pitch, t),
        NormalizeAngle(from.yaw + (ShortestAngleDelta(from.yaw, to.yaw) * t)),
        NormalizeAngle(from.roll + (ShortestAngleDelta(from.roll, to.roll) * t)),
        LerpValue(from.fov, to.fov, t),
    };
}

namespace campath_detail {

struct Vector3 final {
    double x;
    double y;
    double z;
};

[[nodiscard]] inline Vector3 Add(const Vector3& left, const Vector3& right) noexcept {
    return {left.x + right.x, left.y + right.y, left.z + right.z};
}

[[nodiscard]] inline Vector3 Subtract(const Vector3& left, const Vector3& right) noexcept {
    return {left.x - right.x, left.y - right.y, left.z - right.z};
}

[[nodiscard]] inline Vector3 Scale(const Vector3& value, const double amount) noexcept {
    return {value.x * amount, value.y * amount, value.z * amount};
}

[[nodiscard]] inline double Distance(const Vector3& left, const Vector3& right) noexcept {
    const auto delta = Subtract(left, right);
    return std::sqrt((delta.x * delta.x) + (delta.y * delta.y) + (delta.z * delta.z));
}

[[nodiscard]] inline double Blend(
    const double left, const double right, const double left_time,
    const double right_time, const double time) noexcept {
    return LerpValue(left, right, (time - left_time) / std::max(right_time - left_time, 1e-9));
}

[[nodiscard]] inline Vector3 Blend(
    const Vector3& left, const Vector3& right, const double left_time,
    const double right_time, const double time) noexcept {
    const auto amount = (time - left_time) / std::max(right_time - left_time, 1e-9);
    return Add(left, Scale(Subtract(right, left), amount));
}

[[nodiscard]] inline double Interpolate(
    const std::array<double, 4>& values, const std::array<double, 4>& knots,
    const double amount) noexcept {
    const auto parameter = LerpValue(knots[1], knots[2], amount);
    const auto a1 = Blend(values[0], values[1], knots[0], knots[1], parameter);
    const auto a2 = Blend(values[1], values[2], knots[1], knots[2], parameter);
    const auto a3 = Blend(values[2], values[3], knots[2], knots[3], parameter);
    const auto b1 = Blend(a1, a2, knots[0], knots[2], parameter);
    const auto b2 = Blend(a2, a3, knots[1], knots[3], parameter);
    return Blend(b1, b2, knots[1], knots[2], parameter);
}

[[nodiscard]] inline Vector3 Interpolate(
    const std::array<Vector3, 4>& values, const std::array<double, 4>& knots,
    const double amount) noexcept {
    const auto parameter = LerpValue(knots[1], knots[2], amount);
    const auto a1 = Blend(values[0], values[1], knots[0], knots[1], parameter);
    const auto a2 = Blend(values[1], values[2], knots[1], knots[2], parameter);
    const auto a3 = Blend(values[2], values[3], knots[2], knots[3], parameter);
    const auto b1 = Blend(a1, a2, knots[0], knots[2], parameter);
    const auto b2 = Blend(a2, a3, knots[1], knots[3], parameter);
    return Blend(b1, b2, knots[1], knots[2], parameter);
}

[[nodiscard]] inline std::array<double, 4> Unwrap(
    const double zero, const double one, const double two, const double three) noexcept {
    const auto normalized_one = NormalizeAngle(one);
    const auto unwrapped_zero = normalized_one - ShortestAngleDelta(zero, normalized_one);
    const auto unwrapped_two = normalized_one + ShortestAngleDelta(normalized_one, two);
    const auto unwrapped_three = unwrapped_two + ShortestAngleDelta(unwrapped_two, three);
    return {unwrapped_zero, normalized_one, unwrapped_two, unwrapped_three};
}

} // namespace campath_detail

[[nodiscard]] inline CameraSample ReflectCamera(
    const CameraSample& origin, const CameraSample& other) noexcept {
    return CameraSample{
        (2.0 * origin.x) - other.x,
        (2.0 * origin.y) - other.y,
        (2.0 * origin.z) - other.z,
        origin.pitch - ShortestAngleDelta(origin.pitch, other.pitch),
        origin.yaw - ShortestAngleDelta(origin.yaw, other.yaw),
        origin.roll - ShortestAngleDelta(origin.roll, other.roll),
        std::clamp((2.0 * origin.fov) - other.fov, kMinFov, kMaxFov),
    };
}

[[nodiscard]] inline CameraSample EvaluateSmoothCamera(
    const CameraSample& p0, const CameraSample& p1, const CameraSample& p2,
    const CameraSample& p3, const double amount) noexcept {
    using campath_detail::Vector3;
    const std::array<Vector3, 4> positions{{
        {p0.x, p0.y, p0.z}, {p1.x, p1.y, p1.z},
        {p2.x, p2.y, p2.z}, {p3.x, p3.y, p3.z},
    }};
    std::array<double, 4> knots{};
    for (std::size_t index = 1; index < knots.size(); ++index) {
        const auto distance = campath_detail::Distance(positions[index], positions[index - 1]);
        knots[index] = knots[index - 1] + std::max(std::sqrt(distance), 1e-6);
    }

    const auto position = campath_detail::Interpolate(positions, knots, amount);
    const auto pitch = campath_detail::Interpolate(
        campath_detail::Unwrap(p0.pitch, p1.pitch, p2.pitch, p3.pitch), knots, amount);
    const auto yaw = campath_detail::Interpolate(
        campath_detail::Unwrap(p0.yaw, p1.yaw, p2.yaw, p3.yaw), knots, amount);
    const auto roll = campath_detail::Interpolate(
        campath_detail::Unwrap(p0.roll, p1.roll, p2.roll, p3.roll), knots, amount);
    const std::array<double, 4> fovs{p0.fov, p1.fov, p2.fov, p3.fov};
    const auto fov_limits = std::minmax_element(fovs.begin(), fovs.end());
    const auto fov = campath_detail::Interpolate(fovs, knots, amount);

    return CameraSample{
        position.x, position.y, position.z,
        std::clamp(pitch, -89.0, 89.0),
        NormalizeAngle(yaw), NormalizeAngle(roll),
        std::clamp(fov, *fov_limits.first, *fov_limits.second),
    };
}

} // namespace deadlock_mvm
