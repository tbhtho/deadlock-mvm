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
    bool (*read_rendered_camera)(
        void* context,
        CameraSample& camera,
        std::int64_t& replay_tick,
        std::uint64_t& frame_sequence) noexcept{};
    bool (*read_editor_path)(
        void* context,
        CampathPayloadHeader& header,
        CampathKeyframe* keyframes,
        std::size_t capacity) noexcept{};
    // Latest Campath document list pushed by the managed host; returns false
    // when none was received or the last copy is stale.
    bool (*read_campath_documents)(void* context, CampathDocumentsPayload& documents) noexcept{};
    bool (*queue_action)(void* context, const SmvmActionPayload& action) noexcept{};
    void (*request_camera_capture)(
        void* context,
        std::uint64_t replay_session_generation) noexcept{};
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
// Typed FOV edits are submitted while Tab interaction has suspended the
// relative-pointer route. Keep them separate from mouse input so the next
// rendered manual-camera frame can apply the exact value while paused.
[[nodiscard]] bool ConsumeSmvmManualFovTarget(double& fov) noexcept;
void ResetSmvmManualInput() noexcept;
// Mirrors presentation state as soon as the pipe receives a validated managed
// snapshot, even when no renderer frame has been observed yet.
void ObserveSmvmRecordingVisualSnapshot(const SmvmSnapshotPayload& snapshot) noexcept;
// Fail-closed host-loss route. Physical restore retries without Present, while
// generation-tagged F9/reconnect intent survives until the managed owner
// acknowledges the complete inverse or forward profile transaction.
void NotifySmvmHostDisconnected() noexcept;
// If shutdown cannot restore the presentation while VConsole is unavailable,
// the resident backend calls this renderer-independent pump until a later
// retry succeeds. The DLL remains loaded throughout that fail-closed state.
void PumpSmvmOverlayResidentRecovery() noexcept;
// Invalidates replay-owned prompt/input state before a replacement replay
// snapshot becomes visible on the same native pipe.
void InvalidateSmvmReplaySessionState() noexcept;
// Validated bounded LUT data arrives on the pipe thread. Resource publication
// is performed by the existing maintenance thread, never by UI/Present.
bool SetSmvmLookLut(std::uint64_t revision, std::uint32_t size,
    const float* values, std::size_t count) noexcept;
// Re-runs only the Free Camera pointer acquisition path. Camera transform,
// FOV, Roll, and camera ownership remain untouched.
void SmvmReacquireFreeCameraInput() noexcept;
[[nodiscard]] bool SmvmSetManualFovTarget(double fov) noexcept;

} // namespace deadlock_mvm
