#pragma once

#include "protocol.hpp"

#include <array>
#include <cstdint>
#include <string_view>

namespace deadlock_mvm {

[[nodiscard]] constexpr DeadlockUiMode ResolveLocalRecordingPresentationMode(
    const DeadlockUiMode managed_mode,
    const bool explicit_restore_pending,
    const bool hard_restore_pending,
    const bool connection_recovery_pending,
    const bool owner_transition_pending,
    const DeadlockUiMode owner_target,
    const bool owner_action_queued,
    const bool active_replay,
    const bool profile_error) noexcept {
    if (explicit_restore_pending || hard_restore_pending || connection_recovery_pending)
        return DeadlockUiMode::deadlock_ui;
    if (!owner_transition_pending)
        return managed_mode;
    if (owner_target == DeadlockUiMode::deadlock_ui)
        return DeadlockUiMode::deadlock_ui;
    return owner_action_queued && active_replay && !profile_error
        ? owner_target
        : DeadlockUiMode::deadlock_ui;
}

enum class ReplayEndRecordingVisualDisposition : std::uint32_t {
    wait_for_inverse = 0,
    wait_for_managed_restore = 1,
    retire_deadlock = 2,
    reassert_active = 3,
};

[[nodiscard]] constexpr ReplayEndRecordingVisualDisposition
ResolveReplayEndRecordingVisualDisposition(
    const bool active_replay,
    const bool hard_restore_pending,
    const bool deadlock_mode,
    const bool managed_restore_pending,
    const bool managed_transaction_in_progress) noexcept {
    if (hard_restore_pending || managed_transaction_in_progress)
        return ReplayEndRecordingVisualDisposition::wait_for_inverse;
    if (deadlock_mode) {
        return managed_restore_pending
            ? ReplayEndRecordingVisualDisposition::wait_for_managed_restore
            : ReplayEndRecordingVisualDisposition::retire_deadlock;
    }
    return active_replay
        ? ReplayEndRecordingVisualDisposition::reassert_active
        : ReplayEndRecordingVisualDisposition::wait_for_managed_restore;
}

[[nodiscard]] constexpr bool MayQueueRecordingVisualActionWithoutActiveReplay(
    const SmvmActionType action,
    const std::int32_t index) noexcept {
    return action == SmvmActionType::restore_deadlock_ui ||
           (action == SmvmActionType::set_deadlock_ui_mode &&
            index == static_cast<std::int32_t>(DeadlockUiMode::deadlock_ui));
}

constexpr std::uint64_t kRecordingVisualRestoreRetryMilliseconds = 1000;
constexpr std::uint32_t kRecordingVisualShutdownRestoreAttempts = 3;

enum class RecordingVisualShutdownAction : std::uint32_t {
    complete = 0,
    attempt_restore = 1,
    keep_module_resident = 2,
};

// Fixed, current-build replay presentation profiles. Keep these typed and
// narrow: they are also used by the native F9 / host-loss recovery path.
inline constexpr std::array<std::string_view, 13> kReplayPresentationCommands{
    "citadel_player_glow_disabled true",
    "citadel_trooper_glow_disabled true",
    "citadel_trooper_friendly_glow_disabled true",
    "citadel_trooper_outline_enabled false",
    "citadel_boss_glow_disabled true",
    "citadel_unit_status_allies_see_thru_walls false",
    "citadel_unit_status_enabled false",
    "citadel_healthbars_enabled false",
    "citadel_unit_status_hide_names true",
    "citadel_unit_status_old_hide_names true",
    "citadel_camera_fade_viewed_near_opacity 1",
    "citadel_camera_fade_other_near_opacity 1",
    "r_drawpanorama false",
};

inline constexpr std::array<std::string_view, 13> kDeadlockPresentationRestoreCommands{
    "citadel_player_glow_disabled false",
    "citadel_trooper_glow_disabled false",
    "citadel_trooper_friendly_glow_disabled false",
    "citadel_trooper_outline_enabled true",
    "citadel_boss_glow_disabled false",
    "citadel_unit_status_allies_see_thru_walls true",
    "citadel_unit_status_enabled true",
    "citadel_healthbars_enabled true",
    "citadel_unit_status_hide_names false",
    "citadel_unit_status_old_hide_names false",
    "citadel_camera_fade_viewed_near_opacity 0.4",
    "citadel_camera_fade_other_near_opacity 0.4",
    "r_drawpanorama true",
};

[[nodiscard]] constexpr bool ShouldAttemptRecordingVisualRestore(
    const bool restore_pending,
    const bool attempt_in_progress,
    const std::uint64_t now_milliseconds,
    const std::uint64_t last_attempt_milliseconds) noexcept {
    return restore_pending && !attempt_in_progress &&
           (last_attempt_milliseconds == 0 ||
            now_milliseconds - last_attempt_milliseconds >=
                kRecordingVisualRestoreRetryMilliseconds);
}

[[nodiscard]] constexpr bool ShouldRequestManagedRecordingVisualRestore(
    const bool restore_pending,
    const bool transaction_in_progress,
    const bool deadlock_mode,
    const bool native_restore_satisfied) noexcept {
    return restore_pending && !transaction_in_progress && deadlock_mode &&
           !native_restore_satisfied;
}

[[nodiscard]] constexpr bool ShouldQueueRecordingVisualRecoveryAction(
    const bool forward_profile_recovery,
    const bool hard_restore_pending,
    const bool active_replay,
    const bool transaction_in_progress,
    const std::uint64_t now_milliseconds,
    const std::uint64_t last_attempt_milliseconds) noexcept {
    // A reconnect reassert or retried owner-forward transition is a forward
    // profile transaction. It must never overtake the host-loss inverse on the
    // independent VConsole route.
    // Explicit F9 recovery queues another inverse, so hard debt does not block
    // that same-direction managed action.
    return (!forward_profile_recovery || active_replay) && !transaction_in_progress &&
           (!forward_profile_recovery || !hard_restore_pending) &&
           (last_attempt_milliseconds == 0 ||
            now_milliseconds - last_attempt_milliseconds >=
                kRecordingVisualRestoreRetryMilliseconds);
}

[[nodiscard]] constexpr bool ShouldQueueInitialOwnerPresentationAction(
    const bool forward_target,
    const bool hard_restore_pending) noexcept {
    // A forward profile must never race an independent native inverse. The
    // durable owner target remains armed and Reconcile queues it after the
    // inverse debt has been satisfied. Deadlock is the same direction as the
    // inverse and may be queued immediately.
    return !forward_target || !hard_restore_pending;
}

[[nodiscard]] constexpr bool ManagedSnapshotProvesRecordingVisualRestore(
    const bool deadlock_mode,
    const bool restore_pending,
    const bool transaction_in_progress,
    const bool profile_error) noexcept {
    return deadlock_mode && !restore_pending && !transaction_in_progress &&
           !profile_error;
}

[[nodiscard]] constexpr bool ShouldCancelRecordingVisualReassert(
    const bool active_replay,
    const bool managed_restore_proven) noexcept {
    // Replay end cancels only the logical forward reassert. Its caller must
    // retain hard inverse debt unless a complete managed restore is proven.
    return !active_replay || managed_restore_proven;
}

[[nodiscard]] constexpr bool IsOwnerPresentationTransitionComplete(
    const std::uint64_t requested_generation,
    const std::uint64_t acknowledged_generation,
    const bool target_matches,
    const bool target_is_deadlock,
    const bool restore_pending,
    const bool transaction_in_progress,
    const bool profile_error) noexcept {
    return requested_generation != 0 &&
           acknowledged_generation == requested_generation &&
           target_matches && (!target_is_deadlock || !restore_pending) &&
           !transaction_in_progress && !profile_error;
}

[[nodiscard]] constexpr RecordingVisualShutdownAction RecordingVisualShutdownStep(
    const bool restore_pending,
    const std::uint32_t completed_attempts) noexcept {
    if (!restore_pending)
        return RecordingVisualShutdownAction::complete;
    return completed_attempts < kRecordingVisualShutdownRestoreAttempts
        ? RecordingVisualShutdownAction::attempt_restore
        : RecordingVisualShutdownAction::keep_module_resident;
}

[[nodiscard]] constexpr bool ShouldClearObservedRecordingVisualRestoreDebt(
    const bool hard_restore_pending,
    const bool managed_restore_pending,
    const bool managed_transaction_in_progress,
    const bool deadlock_mode) noexcept {
    if (hard_restore_pending)
        return false;
    if (!managed_transaction_in_progress && deadlock_mode && !managed_restore_pending)
        return true;
    return managed_transaction_in_progress || !deadlock_mode;
}

[[nodiscard]] constexpr bool ManagedSnapshotMayHaveRecordingVisualProfile(
    const bool active_replay,
    const bool deadlock_mode,
    const bool restore_pending,
    const bool transaction_in_progress) noexcept {
    return active_replay || !deadlock_mode || restore_pending || transaction_in_progress;
}

[[nodiscard]] constexpr bool ShouldRestoreRecordingVisualProfileOnHostLoss(
    const bool profile_may_be_active,
    const bool restore_pending,
    const bool hard_restore_pending,
    const bool logical_recovery_pending) noexcept {
    // Logical recovery can outlive a successful direct inverse while an older
    // managed forward transaction is still in flight. Host loss must re-arm
    // hard debt so that late forward completion is inverted again.
    return profile_may_be_active || restore_pending || hard_restore_pending ||
           logical_recovery_pending;
}

[[nodiscard]] constexpr bool IsExplicitRecordingVisualRecoveryAcknowledged(
    const std::uint64_t requested_generation,
    const std::uint64_t acknowledged_generation,
    const bool deadlock_mode,
    const bool restore_pending,
    const bool transaction_in_progress) noexcept {
    return requested_generation != 0 &&
           acknowledged_generation == requested_generation &&
           deadlock_mode && !restore_pending && !transaction_in_progress;
}

[[nodiscard]] constexpr bool IsRecordingVisualReassertAcknowledged(
    const std::uint64_t requested_generation,
    const std::uint64_t acknowledged_generation,
    const bool deadlock_mode,
    const bool transaction_in_progress) noexcept {
    return requested_generation != 0 &&
           acknowledged_generation == requested_generation &&
           !deadlock_mode && !transaction_in_progress;
}

[[nodiscard]] constexpr bool CanRetireRecordingVisualRestoreAttempt(
    const std::uint64_t attempted_request_epoch,
    const std::uint64_t current_request_epoch,
    const bool disconnected_restore_guard,
    const bool shutdown_in_progress) noexcept {
    if (attempted_request_epoch != current_request_epoch)
        return false;
    // With no managed snapshot connection, an old forward batch can still be
    // in flight even after one inverse succeeds. Keep hard debt alive and send
    // the inverse again until a fresh snapshot establishes ordering. Shutdown
    // is the terminal boundary and may retire after its final successful inverse.
    return !disconnected_restore_guard || shutdown_in_progress;
}

} // namespace deadlock_mvm
