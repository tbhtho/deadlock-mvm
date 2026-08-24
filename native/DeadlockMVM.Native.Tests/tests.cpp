#include "campath_math.hpp"
#include "free_camera_input.hpp"
#include "hook_lifecycle.hpp"
#include "manual_camera_math.hpp"
#include "manual_mouse_fallback.hpp"
#include "optimistic_edit.hpp"
#include "pattern_scan.hpp"
#include "protocol.hpp"
#include "render_camera_policy.hpp"
#include "smvm_action_queue.hpp"
#include "smvm_input_gate.hpp"
#include "smvm_input_route.hpp"

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

    const auto ready_capture = deadlock_mvm::SmvmCaptureAvailability{
        true, true, true, true, true, true, false, false};
    Check(deadlock_mvm::CaptureRejectionFor(ready_capture) ==
              deadlock_mvm::SmvmCaptureRejection::none,
          "capture gate accepts one fresh Free Roam frame");
    auto rejected_capture = ready_capture;
    rejected_capture.replay_available = false;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::replay_unavailable,
          "capture gate reports replay loss precisely");
    rejected_capture = ready_capture;
    rejected_capture.free_roam = false;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::not_in_free_roam,
          "capture gate reports observer-mode loss precisely");
    rejected_capture = ready_capture;
    rejected_capture.snapshot_fresh = false;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::snapshot_stale,
          "capture gate distinguishes a stale editor snapshot");
    rejected_capture = ready_capture;
    rejected_capture.camera_readable = false;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::camera_unreadable,
          "capture gate distinguishes an unreadable camera");
    rejected_capture = ready_capture;
    rejected_capture.hook_frame_fresh = false;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::hook_frame_stale,
          "capture gate distinguishes a stale hook frame");
    rejected_capture = ready_capture;
    rejected_capture.campath_owns_camera = true;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::campath_owns_camera,
          "capture gate reports Campath ownership precisely");
    rejected_capture = ready_capture;
    rejected_capture.capture_pending = true;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::capture_already_pending,
          "capture gate rejects a duplicate pending request");

    deadlock_mvm::SmvmInputRoute mouse_route;
    Check(mouse_route.Claim(1u), "first raw mouse route owns the physical press");
    Check(!mouse_route.Claim(2u), "legacy duplicate does not create a second mouse press");
    Check(mouse_route.Release(1u), "raw mouse release is consumed by its owning route");
    Check(mouse_route.HasAny(), "legacy route remains owned until its matching release");
    Check(mouse_route.Release(2u) && !mouse_route.HasAny(),
          "both delivery routes drain without a stuck mouse button");

    constexpr std::array<std::uint8_t, 4> enable_input_code{0x88, 0x51, 0x48, 0xC3};
    std::uint8_t input_state_offset = 0;
    Check(deadlock_mvm::TryDecodeInputEnabledStateOffset(
              enable_input_code.data(), enable_input_code.size(), input_state_offset) &&
              input_state_offset == 0x48,
          "typed input gate decodes the validated InputSystem enable-state offset");
    auto invalid_enable_input_code = enable_input_code;
    invalid_enable_input_code[3] = 0x90;
    Check(!deadlock_mvm::TryDecodeInputEnabledStateOffset(
              invalid_enable_input_code.data(), invalid_enable_input_code.size(),
              input_state_offset),
          "input gate fails closed when the EnableInput implementation changes");

    float optimistic_value = 70.0F;
    double optimistic_since = -1.0;
    deadlock_mvm::MarkOptimisticEdit(
        90.0F, 10.0F, 170.0F, 1.0, optimistic_value, optimistic_since);
    Check(optimistic_value == 90.0F && optimistic_since == 1.0,
          "slider edit publishes one clamped optimistic value");
    const auto waiting_edit = deadlock_mvm::ReconcileOptimisticEdit(
        70.0F, 1.05, 2.0, 0.001F, optimistic_value, optimistic_since);
    Check(waiting_edit == deadlock_mvm::OptimisticEditResult::pending &&
              optimistic_value == 90.0F,
          "slider does not snap back while its snapshot acknowledgement is pending");
    const auto acknowledged_edit = deadlock_mvm::ReconcileOptimisticEdit(
        90.0F, 1.1, 2.0, 0.001F, optimistic_value, optimistic_since);
    Check(acknowledged_edit == deadlock_mvm::OptimisticEditResult::acknowledged &&
              optimistic_since < 0.0,
          "slider returns to authoritative tracking after acknowledgement");
    deadlock_mvm::MarkOptimisticEdit(
        200.0F, 10.0F, 170.0F, 2.0, optimistic_value, optimistic_since);
    const auto timed_out_edit = deadlock_mvm::ReconcileOptimisticEdit(
        75.0F, 4.1, 2.0, 0.001F, optimistic_value, optimistic_since);
    Check(timed_out_edit == deadlock_mvm::OptimisticEditResult::timed_out &&
              optimistic_value == 75.0F && optimistic_since < 0.0,
          "slider fails back to the authoritative value after an unacknowledged edit");

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
    Check(sizeof(SmvmSnapshotPayload) == 728 && sizeof(StatusPayload) == 264,
          "SMVM v10 snapshot and status layouts are fixed");
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
    snapshot.snapshot_version = 6;
    snapshot.current_tick = -1;
    snapshot.total_ticks = -1;
    snapshot.timescale = 1.0;
    snapshot.fov_step = 1.0;
    snapshot.menu_key = 0x09;
    snapshot.add_key = static_cast<std::uint32_t>(SmvmInputCode::mouse_middle);
    snapshot.delete_key = 'L';
    snapshot.clean_view_key = 0x79;
    snapshot.restore_ui_key = 0x78;
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
    snapshot.vconsole_port = 29000;
    snapshot.replay_bar_scale = 1.0;
    snapshot.replay_bar_opacity = 0.92;
    snapshot.status_hud_anchor = SmvmNotificationAnchor::top_right;
    snapshot.status_hud_scale = 1.0;
    snapshot.status_hud_opacity = 0.92;
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
    snapshot.campath_session = SmvmCampathSession::saved_path;
    snapshot.flags |= smvm_snapshot_campath_unsaved | smvm_snapshot_campath_recovery_available;
    Check(ValidateSmvmSnapshotPayload(snapshot), "typed Campath session and workspace flags are accepted");
    snapshot.campath_session = static_cast<SmvmCampathSession>(3);
    Check(!ValidateSmvmSnapshotPayload(snapshot), "out-of-range Campath session is rejected");
    snapshot.campath_session = SmvmCampathSession::no_path;
    snapshot.saved_document_count = kMaxCampathDocuments + 1;
    Check(!ValidateSmvmSnapshotPayload(snapshot), "out-of-range saved document count is rejected");
    snapshot.saved_document_count = 2;
    snapshot.flags &= ~(smvm_snapshot_campath_unsaved | smvm_snapshot_campath_recovery_available);
    Check(ValidateSmvmSnapshotPayload(snapshot), "bounded saved document count is accepted");

    const CameraSample stale_snapshot{1, 2, 3, 4, 5, 0, 70};
    const CameraSample rendered_frame{10, 20, 30, 12, 140, 27.5, 42.25};
    const auto selected_frame = SelectRenderCamera(stale_snapshot, rendered_frame, true);
    Check(selected_frame.fov == 42.25 && selected_frame.roll == 27.5 &&
              selected_frame.x == 10 && selected_frame.yaw == 140,
          "world projection and HUD select the same final rendered FOV/roll camera frame");
    const auto fallback_frame = SelectRenderCamera(stale_snapshot, {}, false);
    Check(fallback_frame.fov == 70 && fallback_frame.roll == 0 && fallback_frame.x == 1,
          "world projection and HUD fall back to the immutable snapshot together");

    Check(!ResolveLegacyMouseDelta(false, 0, 0, 120, 80, false).accepted,
          "first legacy mouse event seeds without jumping the camera");
    Check(!ResolveLegacyMouseDelta(true, 100, 100, 110, 94, true).accepted,
          "recent raw relative input always wins over the legacy fallback");
    const auto legacy_delta = ResolveLegacyMouseDelta(true, 100, 100, 110, 94, false);
    Check(legacy_delta.accepted && legacy_delta.x == 10 && legacy_delta.y == -6,
          "bounded legacy mouse motion remains available when raw SDL packets are absent");
    const auto raw_canonical = NormalizeRawMouseDelta(10, -6);
    const auto fallback_canonical = NormalizeFallbackMouseDelta(legacy_delta.x, legacy_delta.y);
    Check(raw_canonical.look_right == fallback_canonical.look_right &&
              raw_canonical.look_up == fallback_canonical.look_up &&
              raw_canonical.look_right == 10 && raw_canonical.look_up == 6,
          "Raw Input and fallback normalize to one physical right/up convention");
    Check(!ResolveLegacyMouseDelta(true, 100, 100, 900, 100, false).accepted,
          "absolute cursor warps cannot jump the fallback camera");
    std::atomic<bool> discard_next_fallback{true};
    Check(!ShouldApplyFallbackMouseSample(false, 100, 175, discard_next_fallback) &&
              discard_next_fallback.load(std::memory_order_acquire),
          "rejected fallback samples preserve the post-menu reacquisition guard");
    Check(!ShouldApplyFallbackMouseSample(true, 110, 175, discard_next_fallback) &&
              !discard_next_fallback.load(std::memory_order_acquire),
          "an accepted recenter packet is discarded during the fallback settle window");
    Check(!ShouldApplyFallbackMouseSample(true, 140, 175, discard_next_fallback),
          "a delayed legacy recenter echo is also discarded during the settle window");
    Check(ShouldApplyFallbackMouseSample(true, 176, 175, discard_next_fallback),
          "the first fallback sample after the recenter queue settles is usable immediately");
    std::atomic<bool> no_recenter_packet{true};
    Check(!ShouldApplyFallbackMouseSample(true, 176, 175, no_recenter_packet) &&
              !no_recenter_packet.load(std::memory_order_acquire) &&
              ShouldApplyFallbackMouseSample(true, 177, 175, no_recenter_packet),
          "without a settle-window packet the original one-sample fallback guard remains");

    Check(sizeof(CampathDocumentEntry) == 144, "Campath document entry layout is fixed");
    Check(ValidatePayloadSize(MessageType::set_campath_documents,
                              8 + (3 * sizeof(CampathDocumentEntry))),
          "bounded Campath document list payload is accepted");
    Check(!ValidatePayloadSize(MessageType::set_campath_documents,
                               8 + (kMaxCampathDocuments + 1) * sizeof(CampathDocumentEntry)),
          "oversized Campath document list payload is rejected");
    Check(!ValidatePayloadSize(MessageType::set_campath_documents, 8 + 7),
          "misaligned Campath document list payload is rejected");

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
    motion.look_right = 500.0;
    motion.look_up = 2000.0;
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
    motion.look_up = 10.0;
    motion.wheel_steps = 2.0;
    const auto inverted = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, motion, inverted_tuning, 1.0 / 60.0);
    Check(inverted.pitch < 0.0 && inverted.fov > 70.0,
          "manual camera honors invert-Y and inverted FOV wheel direction");

    ManualCameraMotion right_look{};
    right_look.look_right = 10.0;
    const auto looked_right = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, right_look, tuning, 0.0);
    ManualCameraMotion left_look{};
    left_look.look_right = -10.0;
    const auto looked_left = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, left_look, tuning, 0.0);
    Check(looked_right.yaw < 0.0 && looked_left.yaw > 0.0,
          "positive canonical X turns rendered Yaw right and negative X turns left");

    ManualCameraMotion up_look{};
    up_look.look_up = 10.0;
    const auto looked_up = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, up_look, tuning, 0.0);
    ManualCameraMotion down_look{};
    down_look.look_up = -10.0;
    const auto looked_down = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, down_look, tuning, 0.0);
    auto only_y_inverted = tuning;
    only_y_inverted.invert_y = true;
    const auto inverted_up = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, up_look, only_y_inverted, 0.0);
    const auto inverted_right = ApplyManualCameraMotion(
        CameraSample{0, 0, 0, 0, 0, 0, 70}, right_look, only_y_inverted, 0.0);
    Check(looked_up.pitch > 0.0 && looked_down.pitch < 0.0 && inverted_up.pitch < 0.0 &&
              std::abs(inverted_right.yaw - looked_right.yaw) < 1e-9,
          "default vertical is physical up/down and Invert Y changes vertical only");

    constexpr FreeCameraInputReadiness all_ready{true, true, true, true, true, true, true};
    auto mouse_missing = all_ready;
    mouse_missing.raw_input_ready = false;
    Check(all_ready.FullyReady() && !mouse_missing.FullyReady() && !mouse_missing.MouseReady(),
          "keyboard readiness cannot mask a missing relative mouse route");
    Check(ShouldReacquireFreeCameraInputAfterMenu(true, false, true, true) &&
              !ShouldReacquireFreeCameraInputAfterMenu(false, false, true, true) &&
              !ShouldReacquireFreeCameraInputAfterMenu(true, false, false, true),
          "only an OPEN to CLOSED transition with requested active Free Camera reacquires input");
    const auto acquisition_reset = ResetMouseAcquisition();
    Check(acquisition_reset.accumulated_look_right == 0 &&
              acquisition_reset.accumulated_look_up == 0 &&
              acquisition_reset.raw_timestamp_ms == 0 &&
              acquisition_reset.fallback_timestamp_ms == 0 &&
              !acquisition_reset.fallback_seeded &&
              acquisition_reset.discard_next_fallback_sample,
          "input reacquisition clears stale deltas/timestamps and requests one fresh fallback sample");
    FreeCameraMenuLifecycle lifecycle{};
    lifecycle.free_camera_requested = true;
    lifecycle.free_camera_active = true;
    lifecycle.readiness = all_ready;
    const CameraSample retained_composition{123, -1177, 473.2, -7.38, 95.8, 15, 40};
    auto composition = retained_composition;
    auto repeated_transitions_ready = lifecycle.CanConsume();
    for (auto cycle = 0; cycle < 20; ++cycle) {
        lifecycle.mouse = {19, -7, 101, 202, true, false};
        lifecycle.OpenMenu();
        repeated_transitions_ready = repeated_transitions_ready &&
            lifecycle.menu_open && !lifecycle.CanConsume() && !lifecycle.readiness.FullyReady() &&
            lifecycle.mouse.accumulated_look_right == 0 &&
            lifecycle.mouse.accumulated_look_up == 0 &&
            lifecycle.mouse.raw_timestamp_ms == 0 &&
            lifecycle.mouse.fallback_timestamp_ms == 0 &&
            !lifecycle.mouse.fallback_seeded &&
            lifecycle.mouse.discard_next_fallback_sample;
        repeated_transitions_ready = repeated_transitions_ready &&
            lifecycle.CloseMenu(all_ready) && lifecycle.CanConsume() &&
            composition.x == retained_composition.x &&
            composition.y == retained_composition.y &&
            composition.z == retained_composition.z &&
            composition.pitch == retained_composition.pitch &&
            composition.yaw == retained_composition.yaw &&
            composition.roll == retained_composition.roll &&
            composition.fov == retained_composition.fov;
    }
    Check(repeated_transitions_ready && lifecycle.successful_reacquisitions == 20,
          "twenty mutable OPEN to CLOSED cycles suspend, reset, reacquire, and retain composition");
    lifecycle.OpenMenu();
    Check(!lifecycle.CloseMenu(mouse_missing) && !lifecycle.CanConsume(),
          "incomplete mouse readiness cannot consume keyboard or wheel input");
    lifecycle.OpenMenu();
    Check(lifecycle.CloseMenu(all_ready) && lifecycle.CanConsume(),
          "retry restores a fully consumable Free Camera input route");
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
