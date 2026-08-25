#pragma once

#include <cstdint>
#include <utility>

namespace deadlock_mvm {

[[nodiscard]] constexpr bool IsReplaySessionGenerationTransition(
    const bool previous_snapshot_available,
    const std::uint64_t previous_replay_session_generation,
    const std::uint64_t next_replay_session_generation) noexcept {
    return previous_snapshot_available &&
           previous_replay_session_generation != next_replay_session_generation;
}

template <typename ResetCameraOwnership,
          typename ResetEditorCampath,
          typename ResetCampathDocuments,
          typename ResetCinematicState>
[[nodiscard]] bool InvalidateReplaySessionStateIfChanged(
    const bool previous_snapshot_available,
    const std::uint64_t previous_replay_session_generation,
    const std::uint64_t next_replay_session_generation,
    ResetCameraOwnership&& reset_camera_ownership,
    ResetEditorCampath&& reset_editor_campath,
    ResetCampathDocuments&& reset_campath_documents,
    ResetCinematicState&& reset_cinematic_state) {
    if (!IsReplaySessionGenerationTransition(
            previous_snapshot_available,
            previous_replay_session_generation,
            next_replay_session_generation)) {
        return false;
    }

    std::forward<ResetCameraOwnership>(reset_camera_ownership)();
    std::forward<ResetEditorCampath>(reset_editor_campath)();
    std::forward<ResetCampathDocuments>(reset_campath_documents)();
    std::forward<ResetCinematicState>(reset_cinematic_state)();
    return true;
}

} // namespace deadlock_mvm
