#include "campath_math.hpp"
#include "pattern_scan.hpp"
#include "protocol.hpp"

#include <array>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
#include <string_view>

namespace {

int failures = 0;

void Check(const bool condition, const std::string_view message) {
    if (condition)
        return;
    ++failures;
    std::cerr << "FAIL: " << message << '\n';
}

void PatternTests() {
    const auto pattern = deadlock_mvm::ParsePattern("48 8D ?? 01");
    Check(pattern.has_value(), "valid pattern parses");
    Check(!deadlock_mvm::ParsePattern("48 XYZ").has_value(), "invalid token is rejected");
    Check(!deadlock_mvm::ParsePattern("").has_value(), "empty pattern is rejected");
    if (!pattern)
        return;

    constexpr std::array<std::uint8_t, 12> bytes{
        0x00, 0x48, 0x8D, 0x10, 0x01, 0xFF,
        0x48, 0x8D, 0x99, 0x01, 0x00, 0x00,
    };
    const auto hits = deadlock_mvm::FindPattern(bytes.data(), bytes.size(), *pattern);
    Check(hits.size() == 2 && hits[0] == 1 && hits[1] == 6, "wildcard scan finds both exact offsets");
    Check(deadlock_mvm::FindPattern(bytes.data(), bytes.size(), *pattern, 1).size() == 1,
          "scan honors result cap");

    std::array<std::uint8_t, 16> instruction{};
    instruction[3] = 5;
    const auto address = reinterpret_cast<std::uintptr_t>(instruction.data());
    const auto target = deadlock_mvm::ResolveRipRelative(address, 3, 7);
    Check(target.has_value() && *target == address + 12, "RIP-relative displacement resolves");
}

void ProtocolTests() {
    using namespace deadlock_mvm;
    const MessageHeader valid{kProtocolMagic, kProtocolVersion, MessageType::get_status, 0, 7};
    Check(ValidateHeader(valid), "valid header is accepted");
    auto bad = valid;
    bad.magic = 0;
    Check(!ValidateHeader(bad), "bad magic is rejected");
    bad = valid;
    bad.version = kProtocolVersion + 1;
    Check(!ValidateHeader(bad), "bad version is rejected");
    bad = valid;
    bad.payload_size = static_cast<std::uint32_t>(kMaxMessageBytes + 1);
    Check(!ValidateHeader(bad), "oversized payload is rejected");

    Check(ExpectedPayloadSize(MessageType::set_camera_sample) == sizeof(CameraSample),
          "sample payload size is fixed");
    Check(ExpectedPayloadSize(MessageType::get_status) == 0, "status request has no payload");
    Check(sizeof(HeartbeatPayload) == 24, "heartbeat carries the replay clock calibration");
    Check(ValidatePayloadSize(MessageType::prepare_camera_observation, 0),
          "passive camera observation request has no payload");
    Check(ValidatePayloadSize(MessageType::set_campath,
                              sizeof(CampathPayloadHeader) + (5 * sizeof(CampathKeyframe))),
          "bounded variable multi-keyframe payload size is accepted");
    Check(!ValidatePayloadSize(MessageType::set_campath,
                               sizeof(CampathPayloadHeader) + sizeof(CampathKeyframe)),
          "single-keyframe payload is rejected");

    CameraSample sample{100, -200, 300, 5, 120, 0, 70};
    Check(ValidateSample(sample), "cinematic sample is accepted");
    sample.x = kMaxWorldCoordinate + 1;
    Check(!ValidateSample(sample), "out-of-world position is rejected");
    sample = CameraSample{0, 0, 0, 90, 0, 0, 70};
    Check(!ValidateSample(sample), "out-of-range pitch is rejected");
    sample = CameraSample{0, 0, 0, 0, 0, 0, kMaxFov + 1};
    Check(!ValidateSample(sample), "out-of-range FOV is rejected");
    sample = CameraSample{0, 0, 0, 0, std::numeric_limits<double>::quiet_NaN(), 0, 70};
    Check(!ValidateSample(sample), "non-finite sample is rejected");

    const CampathPayloadHeader path_header{
        3, CampathInterpolation::smooth, CampathEasing::ease_in_out, 0};
    std::array<CampathKeyframe, 3> path{{
        {100, {0, 0, 0, 5, 350, 0, 35}},
        {200, {100, 200, 300, -15, 10, 0, 70}},
        {350, {150, 250, 400, 0, 40, 0, 40}},
    }};
    Check(ValidateCampath(path_header, path.data()), "ordered typed multi-keyframe Campath is accepted");
    path[1].demo_tick = 100;
    Check(!ValidateCampath(path_header, path.data()), "Campath rejects duplicate or reversed ticks");

    const auto linear = EvaluateLinearCamera(
        CameraSample{0, 0, 0, 5, 350, 0, 35},
        CameraSample{100, 200, 300, -15, 10, 0, 70}, 0.5);
    Check(std::abs(linear.x - 50.0) < 1e-9 && std::abs(linear.yaw) < 1e-9 &&
          std::abs(linear.fov - 52.5) < 1e-9,
          "linear camera uses shortest rotation and interpolates FOV");

    const CameraSample p1{0, 0, 0, 0, 350, 0, 30};
    const CameraSample p2{100, 50, 25, 10, 10, 0, 65};
    const auto smooth_start = EvaluateSmoothCamera(ReflectCamera(p1, p2), p1, p2, p2, 0.0);
    const auto smooth_end = EvaluateSmoothCamera(p1, p1, p2, ReflectCamera(p2, p1), 1.0);
    Check(std::abs(smooth_start.x - p1.x) < 1e-8 && std::abs(smooth_start.yaw + 10.0) < 1e-8,
          "smooth interpolation starts exactly at the keyframe");
    Check(std::abs(smooth_end.x - p2.x) < 1e-8 && std::abs(smooth_end.yaw - p2.yaw) < 1e-8,
          "smooth interpolation ends exactly at the keyframe without a rotation flip");
}

} // namespace

int main() {
    PatternTests();
    ProtocolTests();
    if (failures == 0)
        std::cout << "DeadlockMVM.Native.Tests PASS\n";
    return failures == 0 ? 0 : 1;
}
