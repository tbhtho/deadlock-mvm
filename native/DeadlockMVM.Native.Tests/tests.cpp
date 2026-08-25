#include "campath_math.hpp"
#include "free_camera_input.hpp"
#include "hook_lifecycle.hpp"
#include "manual_camera_input_policy.hpp"
#include "manual_camera_math.hpp"
#include "manual_mouse_fallback.hpp"
#include "optimistic_edit.hpp"
#include "pattern_scan.hpp"
#include "protocol.hpp"
#include "replay_timeline_policy.hpp"
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
    using deadlock_mvm::ClampReplayTimelineTick;
    using deadlock_mvm::ComputeReplayTimelineGeometry;
    using deadlock_mvm::DesiredReplayPauseActionIndex;
    using deadlock_mvm::HostTimescaleFromPercent;
    using deadlock_mvm::IsCinematicStartPromptReady;
    using deadlock_mvm::ReplayTimelineEditorOwnsKeyboard;
    using deadlock_mvm::ReplayTimelineTextInputConsumesKey;
    using deadlock_mvm::ShouldDrawCampathPlacementGuides;
    using deadlock_mvm::ShouldOfferPlayCinematic;
    using deadlock_mvm::ShouldQueuePendingReplaySeek;
    using deadlock_mvm::ShouldStartCinematicForSpaceEvent;
    using deadlock_mvm::ShouldShowCampathPlacementList;
    using deadlock_mvm::ShouldShowReplayTimeline;

    Check(ClampReplayTimelineTick(-50, 1000) == 0 &&
              ClampReplayTimelineTick(500, 1000) == 500 &&
              ClampReplayTimelineTick(1500, 1000) == 1000,
          "typed timeline ticks clamp to the replay range");
    Check(DesiredReplayPauseActionIndex(false) == 1 &&
              DesiredReplayPauseActionIndex(true) == 0,
          "timeline pause button requests an explicit destination state");
    Check(!ShouldOfferPlayCinematic(0) && !ShouldOfferPlayCinematic(2) &&
              ShouldOfferPlayCinematic(3) && ShouldOfferPlayCinematic(128),
          "timeline replaces Add Keyframe with Play Cinematic at three cameras");
    Check(ShouldShowCampathPlacementList(true, 1) &&
              !ShouldShowCampathPlacementList(false, 1) &&
              !ShouldShowCampathPlacementList(true, 0),
          "left Campath mini menu appears only after a camera is placed");
    Check(ShouldDrawCampathPlacementGuides(false, true, false, true) &&
              !ShouldDrawCampathPlacementGuides(true, true, false, true) &&
              !ShouldDrawCampathPlacementGuides(false, false, false, true) &&
              !ShouldDrawCampathPlacementGuides(false, true, true, true) &&
              !ShouldDrawCampathPlacementGuides(false, true, false, false),
          "world camera and path guides hide for Clean Footage and cinematic playback");
    Check(ReplayTimelineEditorOwnsKeyboard(true, true, true) &&
              !ReplayTimelineEditorOwnsKeyboard(false, true, true) &&
              !ReplayTimelineEditorOwnsKeyboard(true, false, true) &&
              !ReplayTimelineEditorOwnsKeyboard(true, true, false),
          "timeline text input owns keyboard only in visible timeline interaction mode");
    Check(ReplayTimelineTextInputConsumesKey(true, false) &&
              !ReplayTimelineTextInputConsumesKey(true, true) &&
              !ReplayTimelineTextInputConsumesKey(false, false),
          "timeline text input never consumes the configured menu escape key");
    Check(!ShouldQueuePendingReplaySeek(true, true, -1, 100, 0) &&
              !ShouldQueuePendingReplaySeek(true, false, 0, 100, 0) &&
              !ShouldQueuePendingReplaySeek(true, true, 0, 99, 100) &&
              ShouldQueuePendingReplaySeek(true, true, 0, 100, 100),
          "the first typed seek waits for fresh replay readiness and retries without another click");
    Check(IsCinematicStartPromptReady(true, true, 3, 1002, 1000, 1000, true, false) &&
              !IsCinematicStartPromptReady(true, true, 2, 1000, 1000, 1000, true, false) &&
              !IsCinematicStartPromptReady(true, true, 3, 1003, 1000, 1000, true, false) &&
              !IsCinematicStartPromptReady(true, true, 3, 1000, 1001, 1000, true, false) &&
              !IsCinematicStartPromptReady(true, true, 3, 1000, 1000, 1000, false, false) &&
              !IsCinematicStartPromptReady(true, true, 3, 1000, 1000, 1000, true, true),
          "cinematic Space prompt appears only at the first camera after protected preparation");
    Check(HostTimescaleFromPercent(25.0) == 0.25 &&
              HostTimescaleFromPercent(100.0) == 1.0 &&
              HostTimescaleFromPercent(400.0) == 4.0 &&
              HostTimescaleFromPercent(-20.0) == 0.01 &&
              HostTimescaleFromPercent(5000.0) == 10.0,
          "custom host timescale converts percentages and clamps to the supported engine range");
    Check(ShouldStartCinematicForSpaceEvent(true, true, true, false, false) &&
              !ShouldStartCinematicForSpaceEvent(true, false, true, false, false) &&
              !ShouldStartCinematicForSpaceEvent(true, true, true, true, false) &&
              !ShouldStartCinematicForSpaceEvent(true, true, true, false, true) &&
              !ShouldStartCinematicForSpaceEvent(false, true, true, false, false),
          "cinematic start requires a fresh non-repeated Space press after the prompt appears");
    Check(ShouldShowReplayTimeline(true, true, true) &&
              !ShouldShowReplayTimeline(false, true, true) &&
              !ShouldShowReplayTimeline(true, false, true) &&
              !ShouldShowReplayTimeline(true, true, false),
          "mini timeline renders only for the active internal replay UI");
    constexpr auto bottom_timeline = ComputeReplayTimelineGeometry(1920.0F, 1080.0F, 1.0F, false);
    Check(bottom_timeline.x == 600.0F && bottom_timeline.y == 968.0F &&
              bottom_timeline.width == 720.0F && bottom_timeline.height == 96.0F,
          "mini timeline has a compact two-row centered bottom layout");
    constexpr auto narrow_timeline = ComputeReplayTimelineGeometry(320.0F, 200.0F, 1.0F, true);
    Check(narrow_timeline.x == 16.0F && narrow_timeline.y == 16.0F &&
              narrow_timeline.width == 288.0F && narrow_timeline.height == 96.0F,
          "mini timeline clamps inside narrow viewports");

    Check(deadlock_mvm::ShouldSuppressGameplayInput(true, false, false) &&
              !deadlock_mvm::ShouldSuppressGameplayInput(false, false, false) &&
              !deadlock_mvm::ShouldSuppressGameplayInput(true, true, false) &&
              !deadlock_mvm::ShouldSuppressGameplayInput(true, false, true),
          "camera takeover suppresses only otherwise-unhandled gameplay input");
    Check(deadlock_mvm::ShouldReserveEditorMenuBinding(true, true, true) &&
              !deadlock_mvm::ShouldReserveEditorMenuBinding(false, true, true) &&
              !deadlock_mvm::ShouldReserveEditorMenuBinding(true, false, true) &&
              !deadlock_mvm::ShouldReserveEditorMenuBinding(true, true, false),
          "the editor menu binding stays reserved for the active SMVM replay");

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
        true, true, true, true, true, true, true, true, false, false};
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
    rejected_capture.manual_camera_requested = false;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::not_in_free_roam,
          "capture gate rejects Deadlock observer roam without an SMVM camera request");
    rejected_capture = ready_capture;
    rejected_capture.manual_camera_active = false;
    Check(deadlock_mvm::CaptureRejectionFor(rejected_capture) ==
              deadlock_mvm::SmvmCaptureRejection::not_in_free_roam,
          "capture gate rejects an SMVM camera request before rendered ownership is active");
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
    snapshot.roll_left_key = 'Z';
    snapshot.roll_right_key = 'C';
    snapshot.roll_reset_key = 'R';
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
    Check(snapshot.roll_left_key == 'Z' && snapshot.roll_right_key == 'C' &&
              snapshot.roll_reset_key == 'R',
          "owner Z/C/R roll bindings survive native snapshot validation exactly");
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
        std::abs(rotated.pitch + 89.0) < 1e-8 &&
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
    Check(inverted.pitch > 0.0 && inverted.fov > 70.0,
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
    Check(looked_up.pitch < 0.0 && looked_down.pitch > 0.0 && inverted_up.pitch > 0.0 &&
              std::abs(inverted_right.yaw - looked_right.yaw) < 1e-9,
          "physical mouse up renders upward and Invert Y changes vertical only");

    constexpr FreeCameraInputReadiness all_ready{true, true, true, true, true, true, true};
    auto mouse_missing = all_ready;
    mouse_missing.raw_input_ready = false;
    Check(all_ready.FullyReady() && !mouse_missing.FullyReady() &&
              mouse_missing.KeyboardReady() && !mouse_missing.MouseReady() &&
              mouse_missing.PartiallyReady() && !all_ready.PartiallyReady(),
          "keyboard-only readiness stays usable but remains an explicit partial input state");
    constexpr auto replay_paused = true;
    Check(replay_paused &&
              CanConsumeFreeCameraKeyboardInput(false, true, true, mouse_missing) &&
              CanConsumeFreeCameraInput(false, true, true, mouse_missing) &&
              !CanConsumeFreeCameraMouseInput(false, true, true, mouse_missing),
          "paused Free Camera consumes keyboard motion while unavailable mouse input stays gated");
    Check(ResolveManualCameraShortcut(true, false, false, true, false, false, false) ==
              ManualCameraShortcutAction::enter_or_reacquire &&
              ResolveManualCameraShortcut(true, false, false, true, false, true, false) ==
              ManualCameraShortcutAction::enter_or_reacquire,
          "F2 enters or reacquires and never becomes a toggle-off action");
    Check(ResolveManualCameraShortcut(false, true, false, true, false, true, true) ==
              ManualCameraShortcutAction::exit &&
              ResolveManualCameraShortcut(false, true, true, true, false, true, true) ==
              ManualCameraShortcutAction::none,
          "Escape exits owned Free Camera only while the editor menu is closed");
    Check(ResolveManualCameraShortcut(false, true, false, true, true, true, false) ==
              ManualCameraShortcutAction::exit &&
              ResolveManualCameraShortcut(false, true, false, true, false, true, false) ==
              ManualCameraShortcutAction::exit &&
              ResolveManualCameraShortcut(false, true, false, true, false, false, true) ==
              ManualCameraShortcutAction::none,
          "Escape follows durable CameraOwned intent through path, restore, and pending states");
    Check(ResolveManualCameraShortcut(true, false, false, true, true, true, true) ==
              ManualCameraShortcutAction::consume &&
              ResolveManualCameraShortcut(true, false, false, true, false, true, true) ==
              ManualCameraShortcutAction::consume,
          "the Enter Free Camera binding is swallowed during path and restore ownership");
    Check(ResolveManualCameraShortcut(true, false, false, true, false, true, false) ==
              ManualCameraShortcutAction::enter_or_reacquire,
          "F2 explicitly retries durable pending Free Camera acquisition");
    Check(HasSmvmCameraInputTakeover(true, true) &&
              !HasSmvmCameraInputTakeover(true, false) &&
              !HasSmvmCameraInputTakeover(false, true),
          "input takeover spans every CameraOwned transition and fails closed otherwise");
    Check(HasSmvmManualCameraOwnership(true, true) &&
              !HasSmvmManualCameraOwnership(true, false) &&
              !HasSmvmManualCameraOwnership(false, true),
          "shot editing and manual motion require both requested and active SMVM camera state");
    Check(ResolveHeldManualBinding(false, true, true, true, true, true) &&
              !ResolveHeldManualBinding(false, true, false, true, true, false) &&
              ResolveHeldManualBinding(true, false, false, true, true, true),
          "release-armed polling supplements routed Z/C/R events without accepting pre-held keys");
    Check(ShouldReacquireFreeCameraInputAfterMenu(true, false, true, true) &&
              !ShouldReacquireFreeCameraInputAfterMenu(false, false, true, true) &&
              !ShouldReacquireFreeCameraInputAfterMenu(true, false, false, true),
          "only an OPEN to CLOSED transition with requested active Free Camera reacquires input");
    Check(ShouldRetryManualPointerAcquisition(true, false, false, false, 250, 250) &&
              ShouldRetryManualPointerAcquisition(true, false, true, false, 500, 250) &&
              !ShouldRetryManualPointerAcquisition(true, false, true, true, 500, 250) &&
              !ShouldRetryManualPointerAcquisition(true, true, false, false, 500, 250) &&
              !ShouldRetryManualPointerAcquisition(true, false, false, false, 249, 250),
          "pointer health checks retry only a due, degraded live route");
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
    Check(lifecycle.CloseMenu(mouse_missing) && lifecycle.CanConsume() &&
              lifecycle.readiness.KeyboardReady() && !lifecycle.readiness.MouseReady(),
          "menu close restores paused keyboard control even when mouse reacquisition is incomplete");
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
    Check(std::abs(AdjustManualCameraSpeed(600.0, true) - 750.0) < 1e-9 &&
              std::abs(AdjustManualCameraSpeed(750.0, false) - 600.0) < 1e-9 &&
              AdjustManualCameraSpeed(10000.0, true) == 10000.0 &&
              AdjustManualCameraSpeed(1.0, false) == 1.0,
          "minus and plus camera-speed steps are reciprocal and bounded");
    Check(ResolveMovieMakerShortcut(true, false, false, false, true, false, true) ==
              MovieMakerShortcutAction::toggle_replay_pause &&
              ResolveMovieMakerShortcut(false, false, true, false, true, true, true) ==
                  MovieMakerShortcutAction::decrease_camera_speed &&
              ResolveMovieMakerShortcut(false, false, false, true, true, true, true) ==
                  MovieMakerShortcutAction::increase_camera_speed &&
              ResolveMovieMakerShortcut(false, false, false, true, true, true, false) ==
                  MovieMakerShortcutAction::none,
          "fixed N and minus/plus shortcuts resolve only in their owned product states");

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

    const std::array<CampathKeyframe, 4> cinematic_path{{
        {0, {0, 0, 0, 0, 350, 0, 60}},
        {100, {100, 40, 20, 5, 5, 10, 65}},
        {260, {180, 140, 50, -10, 45, 20, 50}},
        {500, {260, 160, 100, 0, 90, 0, 70}},
    }};
    const auto evaluate_tick = [&cinematic_path](const std::int64_t tick) {
        auto segment = 0u;
        while (segment + 2 < cinematic_path.size() &&
               tick > cinematic_path[segment + 1].demo_tick)
            ++segment;
        const auto span = cinematic_path[segment + 1].demo_tick -
                          cinematic_path[segment].demo_tick;
        const auto amount = static_cast<double>(tick - cinematic_path[segment].demo_tick) /
                            static_cast<double>(span);
        return EvaluateCampathCamera(
            cinematic_path.data(),
            static_cast<std::uint32_t>(cinematic_path.size()),
            segment,
            amount,
            CampathInterpolation::smooth,
            CampathEasing::ease_in_out);
    };
    const auto before_key = evaluate_tick(259);
    const auto at_key = evaluate_tick(260);
    const auto after_key = evaluate_tick(261);
    const auto incoming_x = at_key.x - before_key.x;
    const auto outgoing_x = after_key.x - at_key.x;
    const auto incoming_y = at_key.y - before_key.y;
    const auto outgoing_y = after_key.y - at_key.y;
    Check(std::hypot(incoming_x - outgoing_x, incoming_y - outgoing_y) < 0.05 &&
              std::hypot(incoming_x, incoming_y) > 0.05,
          "whole-path smoothing preserves non-zero continuous velocity through keyframes");
    const auto first_step = evaluate_tick(1);
    const auto second_step = evaluate_tick(2);
    const auto penultimate_step = evaluate_tick(499);
    const auto final_step = evaluate_tick(500);
    Check(std::hypot(first_step.x, first_step.y) <
              std::hypot(second_step.x - first_step.x, second_step.y - first_step.y) &&
              std::hypot(final_step.x - penultimate_step.x, final_step.y - penultimate_step.y) <
              std::hypot(penultimate_step.x - evaluate_tick(498).x,
                         penultimate_step.y - evaluate_tick(498).y),
          "whole-path smoothing accelerates and decelerates only at path endpoints");
}

} // namespace

int main() {
    PatternTests();
    ProtocolTests();
    if (failures == 0)
        std::cout << "DeadlockMVM.Native.Tests PASS\n";
    return failures == 0 ? 0 : 1;
}
