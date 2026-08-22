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

    const LinearCampathPayload path{{100, {0, 0, 0, 5, 30, 0, 35}},
                                    {200, {100, 200, 300, -15, 120, 0, 70}}};
    Check(ValidateCampath(path), "ordered typed linear Campath is accepted");
    auto invalid_path = path;
    invalid_path.to.demo_tick = 100;
    Check(!ValidateCampath(invalid_path), "Campath rejects duplicate or reversed ticks");
}

} // namespace

int main() {
    PatternTests();
    ProtocolTests();
    if (failures == 0)
        std::cout << "DeadlockMVM.Native.Tests PASS\n";
    return failures == 0 ? 0 : 1;
}
