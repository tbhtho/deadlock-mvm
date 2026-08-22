#pragma once

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace deadlock_mvm {

constexpr std::uint32_t kProtocolMagic = 0x4D564D43; // "CMVM" little-endian
constexpr std::uint16_t kProtocolVersion = 3;
constexpr std::size_t kMaxCampathKeyframes = 128;
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
    set_campath = 8,
    clear_campath = 9,
    prepare_camera_observation = 10,
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
    status_camera_observed = 1u << 9,
};

enum class CampathInterpolation : std::uint32_t {
    linear = 0,
    smooth = 1,
};

enum class CampathEasing : std::uint32_t {
    linear = 0,
    ease_in = 1,
    ease_out = 2,
    ease_in_out = 3,
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

struct CampathPayloadHeader final {
    std::uint32_t keyframe_count;
    CampathInterpolation interpolation;
    CampathEasing easing;
    std::uint32_t reserved;
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
static_assert(sizeof(CampathPayloadHeader) == 16);
static_assert(sizeof(StatusPayload) == 104);

constexpr std::size_t kMaxMessageBytes =
    sizeof(CampathPayloadHeader) + (sizeof(CampathKeyframe) * kMaxCampathKeyframes);

[[nodiscard]] inline bool IsKnownMessageType(const MessageType type) noexcept {
    switch (type) {
        case MessageType::hello:
        case MessageType::heartbeat:
        case MessageType::enable_override:
        case MessageType::disable_override:
        case MessageType::set_camera_sample:
        case MessageType::get_status:
        case MessageType::shutdown:
        case MessageType::set_campath:
        case MessageType::clear_campath:
        case MessageType::prepare_camera_observation:
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

[[nodiscard]] inline bool ValidateCampath(
    const CampathPayloadHeader& header, const CampathKeyframe* keyframes) noexcept {
    if (keyframes == nullptr || header.keyframe_count < 2 ||
        header.keyframe_count > kMaxCampathKeyframes || header.reserved != 0 ||
        (header.interpolation != CampathInterpolation::linear &&
         header.interpolation != CampathInterpolation::smooth) ||
        (header.easing != CampathEasing::linear && header.easing != CampathEasing::ease_in &&
         header.easing != CampathEasing::ease_out && header.easing != CampathEasing::ease_in_out))
        return false;

    for (std::uint32_t index = 0; index < header.keyframe_count; ++index) {
        if (keyframes[index].demo_tick < 0 || !ValidateSample(keyframes[index].camera) ||
            (index > 0 && keyframes[index - 1].demo_tick >= keyframes[index].demo_tick))
            return false;
    }
    return true;
}

[[nodiscard]] inline std::size_t ExpectedPayloadSize(const MessageType type) noexcept {
    switch (type) {
        case MessageType::hello: return sizeof(HelloPayload);
        case MessageType::heartbeat: return sizeof(HeartbeatPayload);
        case MessageType::set_camera_sample: return sizeof(CameraSample);
        case MessageType::set_campath: return kMaxMessageBytes + 1;
        case MessageType::enable_override:
        case MessageType::disable_override:
        case MessageType::get_status:
        case MessageType::shutdown:
        case MessageType::clear_campath:
        case MessageType::prepare_camera_observation:
            return 0;
        case MessageType::status: return sizeof(StatusPayload);
    }
    return kMaxMessageBytes + 1;
}

[[nodiscard]] inline bool ValidatePayloadSize(
    const MessageType type, const std::size_t payload_size) noexcept {
    if (type == MessageType::set_campath) {
        return payload_size >= sizeof(CampathPayloadHeader) + (2 * sizeof(CampathKeyframe)) &&
               payload_size <= kMaxMessageBytes &&
               (payload_size - sizeof(CampathPayloadHeader)) % sizeof(CampathKeyframe) == 0;
    }
    return payload_size == ExpectedPayloadSize(type);
}

} // namespace deadlock_mvm
