#pragma once

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace deadlock_mvm {

constexpr std::uint32_t kProtocolMagic = 0x4D564D43; // "CMVM" little-endian
constexpr std::uint16_t kProtocolVersion = 2;
constexpr std::size_t kMaxMessageBytes = 512;
constexpr double kMinFov = 5.0;
constexpr double kMaxFov = 170.0;
constexpr double kMaxWorldCoordinate = 200000.0;

enum class MessageType : std::uint16_t {
    hello = 1,
    heartbeat = 2,
    enable_override = 3,
    disable_override = 4,
    set_camera_sample = 5,
    get_status = 6,
    shutdown = 7,
    set_linear_campath = 8,
    clear_campath = 9,
    status = 100,
};

enum class BackendState : std::uint32_t {
    unavailable = 0,
    loading = 1,
    connected = 2,
    ready = 3,
    failed = 4,
};

enum class ErrorCode : std::uint32_t {
    none = 0,
    wrong_process = 1,
    replay_launch_required = 2,
    client_module_missing = 3,
    signature_missing = 4,
    signature_ambiguous = 5,
    camera_unavailable = 6,
    hook_target_mismatch = 7,
    hook_install_failed = 8,
    protocol_error = 9,
    replay_gate_closed = 10,
    invalid_sample = 11,
    observer_not_roaming = 12,
    heartbeat_stale = 13,
    hook_runtime_invalid = 14,
    replay_clock_unavailable = 15,
};

enum StatusFlags : std::uint32_t {
    status_resolved = 1u << 0,
    status_hook_installed = 1u << 1,
    status_pipe_connected = 1u << 2,
    status_replay_gate = 1u << 3,
    status_override_requested = 1u << 4,
    status_override_active = 1u << 5,
    status_has_sample = 1u << 6,
    status_command_line_replay = 1u << 7,
    status_campath_active = 1u << 8,
};

#pragma pack(push, 1)
struct MessageHeader final {
    std::uint32_t magic;
    std::uint16_t version;
    MessageType type;
    std::uint32_t payload_size;
    std::uint64_t sequence;
};

struct HelloPayload final {
    std::uint32_t expected_process_id;
    std::uint32_t client_build;
};

struct HeartbeatPayload final {
    std::uint8_t replay_active;
    std::uint8_t free_roam;
    std::array<std::uint8_t, 6> reserved;
    std::int64_t replay_tick;
    std::int64_t game_tick_offset;
};

struct CameraSample final {
    double x;
    double y;
    double z;
    double pitch;
    double yaw;
    double roll;
    double fov;
};

struct CampathKeyframe final {
    std::int64_t demo_tick;
    CameraSample camera;
};

struct LinearCampathPayload final {
    CampathKeyframe from;
    CampathKeyframe to;
};

struct StatusPayload final {
    BackendState state;
    ErrorCode error;
    std::uint32_t flags;
    std::uint32_t process_id;
    std::uint64_t accepted_sequence;
    std::uint64_t applied_sequence;
    std::uint64_t hook_calls;
    std::int64_t replay_tick;
    CameraSample camera;
};
#pragma pack(pop)

static_assert(sizeof(MessageHeader) == 20);
static_assert(sizeof(HeartbeatPayload) == 24);
static_assert(sizeof(CameraSample) == 56);
static_assert(sizeof(CampathKeyframe) == 64);
static_assert(sizeof(LinearCampathPayload) == 128);
static_assert(sizeof(StatusPayload) == 104);

[[nodiscard]] inline bool IsKnownMessageType(const MessageType type) noexcept {
    switch (type) {
        case MessageType::hello:
        case MessageType::heartbeat:
        case MessageType::enable_override:
        case MessageType::disable_override:
        case MessageType::set_camera_sample:
        case MessageType::get_status:
        case MessageType::shutdown:
        case MessageType::set_linear_campath:
        case MessageType::clear_campath:
        case MessageType::status:
            return true;
    }
    return false;
}

[[nodiscard]] inline bool ValidateHeader(const MessageHeader& header) noexcept {
    return header.magic == kProtocolMagic &&
           header.version == kProtocolVersion &&
           IsKnownMessageType(header.type) &&
           header.payload_size <= kMaxMessageBytes;
}

[[nodiscard]] inline bool ValidateSample(const CameraSample& sample) noexcept {
    const auto finite = std::isfinite(sample.x) && std::isfinite(sample.y) &&
                        std::isfinite(sample.z) && std::isfinite(sample.pitch) &&
                        std::isfinite(sample.yaw) && std::isfinite(sample.roll) &&
                        std::isfinite(sample.fov);
    return finite &&
           std::abs(sample.x) <= kMaxWorldCoordinate &&
           std::abs(sample.y) <= kMaxWorldCoordinate &&
           std::abs(sample.z) <= kMaxWorldCoordinate &&
           sample.pitch >= -89.0 && sample.pitch <= 89.0 &&
           sample.yaw >= -360.0 && sample.yaw <= 360.0 &&
           sample.roll >= -180.0 && sample.roll <= 180.0 &&
           sample.fov >= kMinFov && sample.fov <= kMaxFov;
}

[[nodiscard]] inline bool ValidateCampath(const LinearCampathPayload& path) noexcept {
    return path.from.demo_tick >= 0 && path.to.demo_tick > path.from.demo_tick &&
           ValidateSample(path.from.camera) && ValidateSample(path.to.camera);
}

[[nodiscard]] inline std::size_t ExpectedPayloadSize(const MessageType type) noexcept {
    switch (type) {
        case MessageType::hello: return sizeof(HelloPayload);
        case MessageType::heartbeat: return sizeof(HeartbeatPayload);
        case MessageType::set_camera_sample: return sizeof(CameraSample);
        case MessageType::set_linear_campath: return sizeof(LinearCampathPayload);
        case MessageType::enable_override:
        case MessageType::disable_override:
        case MessageType::get_status:
        case MessageType::shutdown:
        case MessageType::clear_campath:
            return 0;
        case MessageType::status: return sizeof(StatusPayload);
    }
    return kMaxMessageBytes + 1;
}

} // namespace deadlock_mvm
