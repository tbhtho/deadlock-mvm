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

namespace campath_detail {

// HLAE's default campath uses a whole-path cubic spline instead of easing each
// keyframe pair independently.  The latter makes the camera brake and restart
// at every keyframe.  These helpers use the real demo ticks as knot times, so
// velocity and direction remain continuous even when keyframes are unevenly
// spaced.  Ease modes become endpoint boundary conditions; they never alter an
// interior segment's clock.
[[nodiscard]] inline std::array<double, kMaxCampathKeyframes> SplineSecondDerivatives(
    const CampathKeyframe* keys,
    const std::uint32_t count,
    const std::array<double, kMaxCampathKeyframes>& values,
    const bool ease_in,
    const bool ease_out) noexcept {
    std::array<double, kMaxCampathKeyframes> second{};
    std::array<double, kMaxCampathKeyframes> upper{};
    if (keys == nullptr || count < 2 || count > kMaxCampathKeyframes)
        return second;

    const auto first_span = std::max(
        static_cast<double>(keys[1].demo_tick - keys[0].demo_tick), 1.0);
    if (ease_in) {
        second[0] = -0.5;
        upper[0] = (3.0 / first_span) * ((values[1] - values[0]) / first_span);
    }

    for (std::uint32_t index = 1; index + 1 < count; ++index) {
        const auto previous_span = std::max(
            static_cast<double>(keys[index].demo_tick - keys[index - 1].demo_tick), 1.0);
        const auto next_span = std::max(
            static_cast<double>(keys[index + 1].demo_tick - keys[index].demo_tick), 1.0);
        const auto sigma = previous_span / (previous_span + next_span);
        const auto pivot = (sigma * second[index - 1]) + 2.0;
        second[index] = (sigma - 1.0) / pivot;
        const auto slope_delta =
            ((values[index + 1] - values[index]) / next_span) -
            ((values[index] - values[index - 1]) / previous_span);
        upper[index] = ((6.0 * slope_delta / (previous_span + next_span)) -
                        (sigma * upper[index - 1])) /
                       pivot;
    }

    auto final_second = 0.0;
    auto final_upper = 0.0;
    if (ease_out) {
        const auto final_span = std::max(
            static_cast<double>(keys[count - 1].demo_tick - keys[count - 2].demo_tick), 1.0);
        final_second = 0.5;
        final_upper = (-3.0 / final_span) *
                      ((values[count - 1] - values[count - 2]) / final_span);
    }
    second[count - 1] =
        (final_upper - (final_second * upper[count - 2])) /
        ((final_second * second[count - 2]) + 1.0);
    for (std::uint32_t index = count - 1; index-- > 0;)
        second[index] = (second[index] * second[index + 1]) + upper[index];
    return second;
}

[[nodiscard]] inline double EvaluateSpline(
    const CampathKeyframe* keys,
    const std::uint32_t count,
    const std::uint32_t segment,
    const double amount,
    const std::array<double, kMaxCampathKeyframes>& values,
    const CampathEasing easing) noexcept {
    const auto second = SplineSecondDerivatives(
        keys,
        count,
        values,
        easing == CampathEasing::ease_in || easing == CampathEasing::ease_in_out,
        easing == CampathEasing::ease_out || easing == CampathEasing::ease_in_out);
    const auto span = std::max(
        static_cast<double>(keys[segment + 1].demo_tick - keys[segment].demo_tick), 1.0);
    const auto right = std::clamp(amount, 0.0, 1.0);
    const auto left = 1.0 - right;
    return (left * values[segment]) + (right * values[segment + 1]) +
           ((((left * left * left) - left) * second[segment]) +
            (((right * right * right) - right) * second[segment + 1])) *
               (span * span) / 6.0;
}

struct Quaternion final {
    double w{1.0};
    double x{};
    double y{};
    double z{};
};

[[nodiscard]] inline Quaternion Scale(const Quaternion value, const double amount) noexcept {
    return {value.w * amount, value.x * amount, value.y * amount, value.z * amount};
}

[[nodiscard]] inline double Dot(const Quaternion left, const Quaternion right) noexcept {
    return (left.w * right.w) + (left.x * right.x) +
           (left.y * right.y) + (left.z * right.z);
}

[[nodiscard]] inline Quaternion Normalize(const Quaternion value) noexcept {
    const auto length = std::sqrt(std::max(Dot(value, value), 1e-24));
    return Scale(value, 1.0 / length);
}

[[nodiscard]] inline Quaternion Multiply(
    const Quaternion left, const Quaternion right) noexcept {
    return {
        (left.w * right.w) - (left.x * right.x) -
            (left.y * right.y) - (left.z * right.z),
        (left.w * right.x) + (left.x * right.w) +
            (left.y * right.z) - (left.z * right.y),
        (left.w * right.y) - (left.x * right.z) +
            (left.y * right.w) + (left.z * right.x),
        (left.w * right.z) + (left.x * right.y) -
            (left.y * right.x) + (left.z * right.w),
    };
}

[[nodiscard]] inline Quaternion Inverse(const Quaternion value) noexcept {
    const auto length_squared = std::max(Dot(value, value), 1e-24);
    return {value.w / length_squared, -value.x / length_squared,
            -value.y / length_squared, -value.z / length_squared};
}

[[nodiscard]] inline Vector3 QuaternionLog(const Quaternion raw) noexcept {
    const auto value = Normalize(raw);
    const auto vector_length = std::sqrt(
        (value.x * value.x) + (value.y * value.y) + (value.z * value.z));
    if (vector_length < 1e-12)
        return {};
    const auto angle = std::atan2(vector_length, value.w);
    const auto scale = angle / vector_length;
    return {value.x * scale, value.y * scale, value.z * scale};
}

[[nodiscard]] inline Quaternion QuaternionExp(const Vector3 value) noexcept {
    const auto angle = std::sqrt(
        (value.x * value.x) + (value.y * value.y) + (value.z * value.z));
    if (angle < 1e-12)
        return Normalize({1.0, value.x, value.y, value.z});
    const auto scale = std::sin(angle) / angle;
    return {std::cos(angle), value.x * scale, value.y * scale, value.z * scale};
}

[[nodiscard]] inline Quaternion Slerp(
    Quaternion from, Quaternion to, const double raw_amount) noexcept {
    from = Normalize(from);
    to = Normalize(to);
    auto cosine = Dot(from, to);
    if (cosine < 0.0) {
        to = Scale(to, -1.0);
        cosine = -cosine;
    }
    const auto amount = std::clamp(raw_amount, 0.0, 1.0);
    if (cosine > 0.9995) {
        return Normalize({
            LerpValue(from.w, to.w, amount), LerpValue(from.x, to.x, amount),
            LerpValue(from.y, to.y, amount), LerpValue(from.z, to.z, amount),
        });
    }
    const auto angle = std::acos(std::clamp(cosine, -1.0, 1.0));
    const auto divisor = std::sin(angle);
    const auto left = std::sin((1.0 - amount) * angle) / divisor;
    const auto right = std::sin(amount * angle) / divisor;
    return Normalize({
        (from.w * left) + (to.w * right), (from.x * left) + (to.x * right),
        (from.y * left) + (to.y * right), (from.z * left) + (to.z * right),
    });
}

[[nodiscard]] inline Quaternion QuaternionFromCamera(const CameraSample& camera) noexcept {
    constexpr auto radians = 3.14159265358979323846 / 180.0;
    const auto half_roll = camera.roll * radians * 0.5;
    const auto half_pitch = camera.pitch * radians * 0.5;
    const auto half_yaw = camera.yaw * radians * 0.5;
    const auto sr = std::sin(half_roll);
    const auto cr = std::cos(half_roll);
    const auto sp = std::sin(half_pitch);
    const auto cp = std::cos(half_pitch);
    const auto sy = std::sin(half_yaw);
    const auto cy = std::cos(half_yaw);
    return Normalize({
        (cr * cp * cy) + (sr * sp * sy),
        (sr * cp * cy) - (cr * sp * sy),
        (cr * sp * cy) + (sr * cp * sy),
        (cr * cp * sy) - (sr * sp * cy),
    });
}

[[nodiscard]] inline CameraSample CameraAnglesFromQuaternion(
    const Quaternion raw, CameraSample sample) noexcept {
    constexpr auto degrees = 180.0 / 3.14159265358979323846;
    const auto value = Normalize(raw);
    const auto matrix_11 = (2.0 * ((value.w * value.w) + (value.x * value.x))) - 1.0;
    const auto matrix_12 = 2.0 * ((value.x * value.y) + (value.w * value.z));
    const auto matrix_13 = 2.0 * ((value.x * value.z) - (value.w * value.y));
    const auto matrix_23 = 2.0 * ((value.y * value.z) + (value.w * value.x));
    const auto matrix_33 = (2.0 * ((value.w * value.w) + (value.z * value.z))) - 1.0;
    sample.pitch = std::clamp(
        std::asin(std::clamp(-matrix_13, -1.0, 1.0)) * degrees, -89.0, 89.0);
    sample.yaw = NormalizeAngle(std::atan2(matrix_12, matrix_11) * degrees);
    sample.roll = NormalizeAngle(std::atan2(matrix_23, matrix_33) * degrees);
    return sample;
}

[[nodiscard]] inline std::array<Quaternion, kMaxCampathKeyframes> AlignedRotations(
    const CampathKeyframe* keys, const std::uint32_t count) noexcept {
    std::array<Quaternion, kMaxCampathKeyframes> rotations{};
    for (std::uint32_t index = 0; index < count; ++index) {
        rotations[index] = QuaternionFromCamera(keys[index].camera);
        if (index > 0 && Dot(rotations[index - 1], rotations[index]) < 0.0)
            rotations[index] = Scale(rotations[index], -1.0);
    }
    return rotations;
}

[[nodiscard]] inline Vector3 RotationTangent(
    const CampathKeyframe* keys,
    const std::array<Quaternion, kMaxCampathKeyframes>& rotations,
    const std::uint32_t count,
    const std::uint32_t index) noexcept {
    if (index == 0 || index + 1 >= count)
        return {};
    const auto previous_span = std::max(
        static_cast<double>(keys[index].demo_tick - keys[index - 1].demo_tick), 1.0);
    const auto next_span = std::max(
        static_cast<double>(keys[index + 1].demo_tick - keys[index].demo_tick), 1.0);
    const auto previous = Scale(
        QuaternionLog(Multiply(Inverse(rotations[index]), rotations[index - 1])),
        -1.0 / previous_span);
    const auto next = Scale(
        QuaternionLog(Multiply(Inverse(rotations[index]), rotations[index + 1])),
        1.0 / next_span);
    return Scale(
        Add(Scale(previous, next_span), Scale(next, previous_span)),
        1.0 / (previous_span + next_span));
}

[[nodiscard]] inline Quaternion EvaluateRotationSpline(
    const CampathKeyframe* keys,
    const std::uint32_t count,
    const std::uint32_t segment,
    const double raw_amount) noexcept {
    const auto rotations = AlignedRotations(keys, count);
    const auto span = std::max(
        static_cast<double>(keys[segment + 1].demo_tick - keys[segment].demo_tick), 1.0);
    const auto from_tangent = RotationTangent(keys, rotations, count, segment);
    const auto to_tangent = RotationTangent(keys, rotations, count, segment + 1);
    const auto first_control = Multiply(
        rotations[segment], QuaternionExp(Scale(from_tangent, span / 3.0)));
    const auto second_control = Multiply(
        rotations[segment + 1], QuaternionExp(Scale(to_tangent, -span / 3.0)));
    const auto amount = std::clamp(raw_amount, 0.0, 1.0);
    const auto first = Slerp(rotations[segment], first_control, amount);
    const auto middle = Slerp(first_control, second_control, amount);
    const auto last = Slerp(second_control, rotations[segment + 1], amount);
    const auto left = Slerp(first, middle, amount);
    const auto right = Slerp(middle, last, amount);
    return Slerp(left, right, amount);
}

} // namespace campath_detail

// Whole-path cinematic evaluation used by both the rendered camera and the
// preview geometry. Position/FOV are tick-aware cubic splines; orientation is a
// shortest-path spherical cubic with continuous interior tangents and zero
// endpoint angular velocity.
[[nodiscard]] inline CameraSample EvaluateCampathCamera(
    const CampathKeyframe* keys,
    const std::uint32_t count,
    const std::uint32_t segment,
    const double raw_amount,
    const CampathInterpolation interpolation,
    const CampathEasing easing) noexcept {
    if (keys == nullptr || count < 2 || count > kMaxCampathKeyframes || segment + 1 >= count)
        return {};
    if (interpolation == CampathInterpolation::linear) {
        return EvaluateLinearCamera(
            keys[segment].camera,
            keys[segment + 1].camera,
            ApplyCampathEasing(raw_amount, easing));
    }

    std::array<double, kMaxCampathKeyframes> x{};
    std::array<double, kMaxCampathKeyframes> y{};
    std::array<double, kMaxCampathKeyframes> z{};
    std::array<double, kMaxCampathKeyframes> fov{};
    auto minimum_fov = kMaxFov;
    auto maximum_fov = kMinFov;
    for (std::uint32_t index = 0; index < count; ++index) {
        x[index] = keys[index].camera.x;
        y[index] = keys[index].camera.y;
        z[index] = keys[index].camera.z;
        fov[index] = keys[index].camera.fov;
        minimum_fov = std::min(minimum_fov, fov[index]);
        maximum_fov = std::max(maximum_fov, fov[index]);
    }
    CameraSample sample{
        campath_detail::EvaluateSpline(keys, count, segment, raw_amount, x, easing),
        campath_detail::EvaluateSpline(keys, count, segment, raw_amount, y, easing),
        campath_detail::EvaluateSpline(keys, count, segment, raw_amount, z, easing),
        0.0,
        0.0,
        0.0,
        std::clamp(
            campath_detail::EvaluateSpline(keys, count, segment, raw_amount, fov, easing),
            minimum_fov,
            maximum_fov),
    };
    return campath_detail::CameraAnglesFromQuaternion(
        campath_detail::EvaluateRotationSpline(keys, count, segment, raw_amount), sample);
}

} // namespace deadlock_mvm
