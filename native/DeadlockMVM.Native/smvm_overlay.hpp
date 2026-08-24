#pragma once

#include "protocol.hpp"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace deadlock_mvm {

struct SmvmOverlayCallbacks final {
    void* context{};
    bool (*read_snapshot)(void* context, SmvmSnapshotPayload& snapshot) noexcept{};
    // Final camera sample observed at the end of the game-thread camera hook.
    // The overlay uses this instead of the slower managed snapshot so world
    // projection and movie telemetry share the frame Deadlock actually drew.
    bool (*read_rendered_camera)(void* context, CameraSample& camera) noexcept{};
    bool (*read_editor_path)(
        void* context,
        CampathPayloadHeader& header,
        CampathKeyframe* keyframes,
        std::size_t capacity) noexcept{};
    // Latest Campath document list pushed by the managed host; returns false
    // when none was received or the last copy is stale.
    bool (*read_campath_documents)(void* context, CampathDocumentsPayload& documents) noexcept{};
    bool (*queue_action)(void* context, const SmvmActionPayload& action) noexcept{};
    void (*request_camera_capture)(void* context) noexcept{};
    void (*publish_status)(
        void* context,
        SmvmRendererBackend backend,
        SmvmRendererError error,
        std::uint32_t flags,
        std::uint32_t frame_microseconds) noexcept{};
};

struct SmvmManualInputFrame final {
    double look_right{};
    double look_up{};
    double wheel_steps{};
    bool forward{};
    bool backward{};
    bool left{};
    bool right{};
    bool up{};
    bool down{};
    bool fast{};
    bool precision{};
    bool roll_left{};
    bool roll_right{};
    bool reset_roll{};
};

[[nodiscard]] bool StartSmvmOverlay(HMODULE self_module, const SmvmOverlayCallbacks& callbacks) noexcept;
// Returns true only after every published hook/window callback has been
// detached and drained. A false result means the caller must keep the module
// resident because an external subclass/hook chain may still reference it.
bool StopSmvmOverlay() noexcept;
[[nodiscard]] bool ConsumeSmvmManualInput(
    const SmvmSnapshotPayload& snapshot,
    SmvmManualInputFrame& frame) noexcept;
void ResetSmvmManualInput() noexcept;
// Re-runs only the Free Camera pointer acquisition path. Camera transform,
// FOV, Roll, and camera ownership remain untouched.
void SmvmReacquireFreeCameraInput() noexcept;

} // namespace deadlock_mvm
