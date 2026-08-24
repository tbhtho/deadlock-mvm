#pragma once

#include "protocol.hpp"

namespace deadlock_mvm {

// Selects the single camera basis used by both world projection and the movie
// status HUD. The rendered sample is captured after all same-frame manual,
// Campath, FOV, and roll writes; the managed snapshot is a safe fallback.
[[nodiscard]] inline CameraSample SelectRenderCamera(
    const CameraSample& snapshot_camera,
    const CameraSample& rendered_camera,
    const bool rendered_camera_available) noexcept {
    return rendered_camera_available && ValidateSample(rendered_camera)
        ? rendered_camera
        : snapshot_camera;
}

} // namespace deadlock_mvm
