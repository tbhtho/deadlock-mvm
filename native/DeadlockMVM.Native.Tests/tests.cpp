#include "campath_math.hpp"
#include "hook_lifecycle.hpp"
#include "manual_camera_math.hpp"
#include "pattern_scan.hpp"
#include "protocol.hpp"
#include "smvm_action_queue.hpp"

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
    int hook_target = 0;
    int original_target = 0;
    int foreign_target = 0;
    Check(deadlock_mvm::IsSafeHookSlotObservation(&hook_target, &hook_target, &original_target),
          "hook detach accepts removing the published hook");
    Check(deadlock_mvm::IsSafeHookSlotObservation(&original_target, &hook_target, &original_target),
          "hook detach accepts an already-restored original");
    Check(!deadlock_mvm::IsSafeHookSlotObservation(&foreign_target, &hook_target, &original_target),
          "hook detach rejects an unknown chained target");

    deadlock_mvm::SmvmActionQueue action_queue;
    deadlock_mvm::SmvmActionPayload queued_action{};
    queued_action.type = deadlock_mvm::SmvmActionType::toggle_replay_pause;
    const auto first_generation = action_queue.Generation();
    Check(action_queue.TryPush(queued_action, first_generation), "editor action queues in its connection epoch");
    action_queue.Invalidate();
    deadlock_mvm::SmvmActionPayload popped_action{};
    Check(!action_queue.TryPop(popped_action), "editor action does not survive connection invalidation");
    Check(!action_queue.TryPush(queued_action, first_generation), "stale producer epoch is rejected");
    const auto second_generation = action_queue.Generation();
    Check(action_queue.TryPush(queued_action, second_generation) && action_queue.TryPop(popped_action) &&
              popped_action.type == deadlock_mvm::SmvmActionType::toggle_replay_pause,
          "current connection epoch still transfers editor actions");

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
    Check(sizeof(SmvmSnapshotPayload) == 640 && sizeof(StatusPayload) == 264,
          "SMVM v7 snapshot and status layouts are fixed");
    Check(ExpectedPayloadSize(MessageType::set_roll_override) == sizeof(RollPayload),
          "roll override payload is one narrowly typed value");
    Check(ValidatePayloadSize(MessageType::prepare_camera_observation, 0),
          "passive camera observation request has no payload");
    Check(ValidatePayloadSize(MessageType::set_campath,
                              sizeof(CampathPayloadHeader) + (5 * sizeof(CampathKeyframe))),
          "bounded variable multi-keyframe payload size is accepted");
    Check(!ValidatePayloadSize(MessageType::set_campath,
                               sizeof(CampathPayloadHeader) + sizeof(CampathKeyframe)),
          "single-keyframe payload is rejected");
    Check(ValidatePayloadSize(MessageType::set_editor_campath,
                              sizeof(CampathPayloadHeader) + sizeof(CampathKeyframe)),
          "single-keyframe editor visualization payload is accepted");
    Check(ValidatePayloadSize(MessageType::update_smvm_snapshot, sizeof(SmvmSnapshotPayload)),
          "fixed SMVM snapshot payload is accepted");

    SmvmSnapshotPayload snapshot{};
    snapshot.snapshot_version = 2;
    snapshot.current_tick = -1;
    snapshot.total_ticks = -1;
    snapshot.timescale = 1.0;
    snapshot.fov_step = 1.0;
    snapshot.menu_key = 0x09;
    snapshot.add_key = static_cast<std::uint32_t>(SmvmInputCode::mouse_middle);
    snapshot.delete_key = 'L';
    snapshot.clean_view_key = 0x79;
    snapshot.roll_left_key = 0;
    snapshot.roll_right_key = 0;
    snapshot.roll_reset_key = 0;
    snapshot.movement_speed = 500.0;
    snapshot.boost_multiplier = 3.0;
    snapshot.precision_multiplier = 0.25;
    snapshot.mouse_sensitivity = 0.08;
    snapshot.smoothing = 0.0;
    snapshot.ui_scale = 1.0;
    snapshot.menu_opacity = 1.0;
    snapshot.path_label_scale = 1.0;
    Check(ValidateSmvmSnapshotPayload(snapshot), "well-formed immutable SMVM snapshot is accepted");
    snapshot.flags = 1u << 31;
    Check(!ValidateSmvmSnapshotPayload(snapshot), "unknown SMVM snapshot flags fail closed");
    snapshot.flags = 0;
    snapshot.add_key = 0xDEADBEEFu;
    Check(!ValidateSmvmSnapshotPayload(snapshot), "invalid SMVM input encoding is rejected");
    snapshot.add_key = 1u << 16;
    Check(!ValidateSmvmSnapshotPayload(snapshot), "modifier without a base input is rejected");
    snapshot.add_key = static_cast<std::uint32_t>(SmvmInputCode::mouse_middle) | (1u << 16);
    Check(ValidateSmvmSnapshotPayload(snapshot), "modifier plus mouse binding is accepted");
    snapshot.forward_key = static_cast<std::uint32_t>(SmvmInputCode::mouse_middle);
    Check(!ValidateSmvmSnapshotPayload(snapshot), "manual held-input slots reject mouse bindings");
    snapshot.forward_key = 'W';
    Check(ValidateSmvmSnapshotPayload(snapshot), "manual held-input slots accept keyboard bindings");
    snapshot.playback_state = 15;
    Check(!ValidateSmvmSnapshotPayload(snapshot), "out-of-range Campath playback state is rejected");
    snapshot.playback_state = 14;
    snapshot.start_failure = 19;
    Check(!ValidateSmvmSnapshotPayload(snapshot), "out-of-range Campath failure reason is rejected");
    snapshot.start_failure = 18;
    Check(ValidateSmvmSnapshotPayload(snapshot), "typed terminal Campath state and failure are accepted");
    snapshot.flags = smvm_snapshot_camera_readable;
    Check(!ValidateSmvmSnapshotPayload(snapshot), "camera-readable snapshot requires a valid sample");
    snapshot.camera = CameraSample{1, 2, 3, 4, 5, 0, 70};
    Check(ValidateSmvmSnapshotPayload(snapshot), "camera-readable snapshot accepts typed camera data");

    CameraSample sample{100, -200, 300, 5, 120, 0, 70};
    Check(ValidateSample(sample), "cinematic sample is accepted");
    Check(ValidateRoll(15.0) && ValidateRoll(-15.0), "proven cinematic roll values are accepted");
    Check(!ValidateRoll(181.0), "out-of-range roll fails closed");
    sample.x = kMaxWorldCoordinate + 1;
    Check(!ValidateSample(sample), "out-of-world position is rejected");
    sample = CameraSample{0, 0, 0, 90, 0, 0, 70};
    Check(!ValidateSample(sample), "out-of-range pitch is rejected");
    sample = CameraSample{0, 0, 0, 0, 0, 0, kMaxFov + 1};
    Check(!ValidateSample(sample), "out-of-range FOV is rejected");
    sample = CameraSample{0, 0, 0, 0, std::numeric_limits<double>::quiet_NaN(), 0, 70};
    Check(!ValidateSample(sample), "non-finite sample is rejected");

    ManualCameraTuning tuning{};
    ManualCameraMotion motion{};
    motion.forward = 1.0;
    motion.right = 1.0;
    const auto diagonal = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, tuning, 1.0);
    Check(std::abs(std::hypot(diagonal.x, diagonal.y) - tuning.movement_speed * 0.1) < 1e-8,
          "manual diagonal movement is normalized and frame time is bounded");
    motion = {};
    motion.mouse_x = 500.0;
    motion.mouse_y = 2000.0;
    motion.roll = 10.0;
    motion.wheel_steps = 100.0;
    const auto rotated = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 179, 0, 70}, motion, tuning, 1.0 / 60.0);
    const auto rotation_safe = rotated.yaw >= -180.0 && rotated.yaw <= 180.0 &&
        std::abs(rotated.pitch - 89.0) < 1e-8 &&
        std::abs(rotated.roll - 7.5) < 1e-8 &&
        std::abs(rotated.fov - kMinFov) < 1e-8;
    Check(rotation_safe, "manual rotation wraps yaw and clamps pitch, roll, and FOV safely");
    motion = {};
    motion.reset_roll = true;
    const auto reset = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 42, 70}, motion, tuning, 1.0 / 60.0);
    Check(reset.roll == 0.0, "manual roll reset is deterministic");
    motion = {};
    motion.forward = 1.0;
    motion.roll = 1.0;
    const auto one_frame = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, tuning, 1.0 / 30.0);
    const auto half_frame = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, tuning, 1.0 / 60.0);
    const auto two_frames = ApplyManualCameraMotion(half_frame, motion, tuning, 1.0 / 60.0);
    Check(std::abs(one_frame.x - two_frames.x) < 1e-8 &&
              std::abs(one_frame.roll - two_frames.roll) < 1e-8,
          "manual held motion is frame-rate invariant across equivalent elapsed time");
    auto inverted_tuning = tuning;
    inverted_tuning.invert_y = true;
    inverted_tuning.invert_fov = true;
    motion = {};
    motion.mouse_y = 10.0;
    motion.wheel_steps = 2.0;
    const auto inverted = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, inverted_tuning, 1.0 / 60.0);
    Check(inverted.pitch < 0.0 && inverted.fov > 70.0,
          "manual camera honors invert-Y and inverted FOV wheel direction");
    motion = {};
    motion.forward = 1.0;
    auto boosted_tuning = tuning;
    boosted_tuning.movement_speed *= 4.0;
    auto precision_tuning = tuning;
    precision_tuning.movement_speed *= 0.2;
    const auto base_move = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, tuning, 1.0 / 60.0);
    const auto boosted_move = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, boosted_tuning, 1.0 / 60.0);
    const auto precision_move = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, precision_tuning, 1.0 / 60.0);
    Check(std::abs(boosted_move.x - (base_move.x * 4.0)) < 1e-8 &&
              std::abs(precision_move.x - (base_move.x * 0.2)) < 1e-8,
          "manual boost and precision multipliers scale movement deterministically");

    const CampathPayloadHeader path_header{
        3, CampathInterpolation::smooth, CampathEasing::ease_in_out,
        CampathEndBehavior::stop_and_release};
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
