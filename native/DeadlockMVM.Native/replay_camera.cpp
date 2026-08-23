#include "replay_camera.hpp"

#include "campath_math.hpp"
#include "hook_lifecycle.hpp"
#include "manual_camera_math.hpp"
#include "pattern_scan.hpp"
#include "protocol.hpp"
#include "smvm_action_queue.hpp"
#include "smvm_overlay.hpp"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <type_traits>
#include <vector>

namespace deadlock_mvm {
namespace {

constexpr std::string_view kManagerPattern =
    "65 48 8B 04 25 58 00 00 00 48 8D 3D ?? ?? ?? ?? 8B D9 BA 68 00 00 00";
constexpr std::string_view kLocalPawnPattern =
    "33 D2 4C 8D 05 ?? ?? ?? ?? 83 F9 FF 8B C2 0F 45 C1 48 98 4D 8B 04 C0 "
    "4D 85 C0 74 ?? 45 8B 80 BC 06 00 00";
constexpr std::string_view kUpdatePattern =
    "4C 8B DC 55 48 83 EC 60 48 8B 05 ?? ?? ?? ?? 48 8B E9 80 78 40 00 "
    "0F 85 ?? ?? ?? ?? 0F 29 74 24 50 0F 57 F6 45 0F 29 4B B8 F3 44 0F "
    "10 48 30 41 0F 2F F1 0F 83 ?? ?? ?? ?? 8B 91 B0 00 00 00";

constexpr std::ptrdiff_t kManagerCurrentCamera = 0x28;
constexpr std::ptrdiff_t kCameraOrigin = 0x38;
constexpr std::ptrdiff_t kCameraAngles = 0x44;
constexpr std::ptrdiff_t kCameraFov = 0x50;
constexpr std::ptrdiff_t kCameraAnglesMirror = 0xBC;
constexpr std::ptrdiff_t kCameraOriginCache = 0xC8;
constexpr std::ptrdiff_t kCameraAnglesOutput = 0xD4;
constexpr std::ptrdiff_t kCameraRoamingPivot = 0x140;
constexpr std::ptrdiff_t kControllerPawnHandle = 0x6BC;
constexpr std::ptrdiff_t kPawnObserverServices = 0xF00;
constexpr std::ptrdiff_t kObserverMode = 0x48;
constexpr std::ptrdiff_t kGlobalVarsTick = 0x44;
constexpr std::size_t kUpdateVtableIndex = 3;
constexpr std::uint64_t kHeartbeatTimeoutMilliseconds = 1000;
constexpr std::uint64_t kCameraObservationFreshMilliseconds = 500;
constexpr std::uint64_t kSmvmSnapshotFreshMilliseconds = 500;
constexpr auto kClientModuleWaitTimeout = std::chrono::seconds(30);

using CameraUpdate = void*(__fastcall*)(void* camera);

struct SectionView final {
    std::uint8_t* begin{};
    std::size_t size{};

    [[nodiscard]] bool Contains(const std::uintptr_t address, const std::size_t length = 1) const noexcept {
        if (begin == nullptr || length > size)
            return false;
        const auto start = reinterpret_cast<std::uintptr_t>(begin);
        return address >= start && address - start <= size - length;
    }
};

struct ModuleView final {
    std::uint8_t* base{};
    std::size_t size{};
    SectionView text{};

    [[nodiscard]] bool Contains(const std::uintptr_t address, const std::size_t length = 1) const noexcept {
        if (base == nullptr || length > size)
            return false;
        const auto start = reinterpret_cast<std::uintptr_t>(base);
        return address >= start && address - start <= size - length;
    }
};

class AtomicSample final {
public:
    void Store(const CameraSample& sample, const std::uint64_t sequence) noexcept {
        generation_.fetch_add(1, std::memory_order_acq_rel);
        x_.store(std::bit_cast<std::uint64_t>(sample.x), std::memory_order_relaxed);
        y_.store(std::bit_cast<std::uint64_t>(sample.y), std::memory_order_relaxed);
        z_.store(std::bit_cast<std::uint64_t>(sample.z), std::memory_order_relaxed);
        pitch_.store(std::bit_cast<std::uint64_t>(sample.pitch), std::memory_order_relaxed);
        yaw_.store(std::bit_cast<std::uint64_t>(sample.yaw), std::memory_order_relaxed);
        roll_.store(std::bit_cast<std::uint64_t>(sample.roll), std::memory_order_relaxed);
        fov_.store(std::bit_cast<std::uint64_t>(sample.fov), std::memory_order_relaxed);
        sequence_.store(sequence, std::memory_order_relaxed);
        generation_.fetch_add(1, std::memory_order_release);
    }

    [[nodiscard]] bool Load(CameraSample& sample, std::uint64_t& sequence) const noexcept {
        for (int attempt = 0; attempt < 4; ++attempt) {
            const auto before = generation_.load(std::memory_order_acquire);
            if ((before & 1u) != 0)
                continue;
            CameraSample candidate{
                std::bit_cast<double>(x_.load(std::memory_order_relaxed)),
                std::bit_cast<double>(y_.load(std::memory_order_relaxed)),
                std::bit_cast<double>(z_.load(std::memory_order_relaxed)),
                std::bit_cast<double>(pitch_.load(std::memory_order_relaxed)),
                std::bit_cast<double>(yaw_.load(std::memory_order_relaxed)),
                std::bit_cast<double>(roll_.load(std::memory_order_relaxed)),
                std::bit_cast<double>(fov_.load(std::memory_order_relaxed)),
            };
            const auto candidate_sequence = sequence_.load(std::memory_order_relaxed);
            const auto after = generation_.load(std::memory_order_acquire);
            if (before == after && (after & 1u) == 0) {
                sample = candidate;
                sequence = candidate_sequence;
                return true;
            }
        }
        return false;
    }

private:
    static_assert(std::atomic<std::uint64_t>::is_always_lock_free);
    std::atomic<std::uint64_t> generation_{0};
    std::atomic<std::uint64_t> sequence_{0};
    std::atomic<std::uint64_t> x_{0};
    std::atomic<std::uint64_t> y_{0};
    std::atomic<std::uint64_t> z_{0};
    std::atomic<std::uint64_t> pitch_{0};
    std::atomic<std::uint64_t> yaw_{0};
    std::atomic<std::uint64_t> roll_{0};
    std::atomic<std::uint64_t> fov_{0};
};

class AtomicCameraFrame final {
public:
    void Store(const CameraSample& sample, const std::int64_t tick, const std::uint64_t sequence) noexcept {
        generation_.fetch_add(1, std::memory_order_acq_rel);
        sample_.Store(sample, sequence);
        tick_.store(tick, std::memory_order_relaxed);
        generation_.fetch_add(1, std::memory_order_release);
    }

    [[nodiscard]] bool Load(CameraSample& sample, std::int64_t& tick, std::uint64_t& sequence) const noexcept {
        for (int attempt = 0; attempt < 4; ++attempt) {
            const auto before = generation_.load(std::memory_order_acquire);
            if ((before & 1u) != 0)
                continue;
            CameraSample candidate{};
            std::uint64_t candidate_sequence = 0;
            if (!sample_.Load(candidate, candidate_sequence))
                continue;
            const auto candidate_tick = tick_.load(std::memory_order_relaxed);
            const auto after = generation_.load(std::memory_order_acquire);
            if (before == after && (after & 1u) == 0) {
                sample = candidate;
                tick = candidate_tick;
                sequence = candidate_sequence;
                return true;
            }
        }
        return false;
    }

private:
    std::atomic<std::uint64_t> generation_{0};
    std::atomic<std::int64_t> tick_{-1};
    AtomicSample sample_{};
};

class AtomicPathKeyframe final {
public:
    void Store(const CampathKeyframe& keyframe, const std::uint64_t sequence) noexcept {
        tick_.store(keyframe.demo_tick, std::memory_order_relaxed);
        sample_.Store(keyframe.camera, sequence);
    }

    [[nodiscard]] bool Load(CampathKeyframe& keyframe) const noexcept {
        std::uint64_t ignored_sequence = 0;
        keyframe.demo_tick = tick_.load(std::memory_order_relaxed);
        return sample_.Load(keyframe.camera, ignored_sequence);
    }

    [[nodiscard]] std::int64_t Tick() const noexcept {
        return tick_.load(std::memory_order_relaxed);
    }

private:
    std::atomic<std::int64_t> tick_{0};
    AtomicSample sample_{};
};

class AtomicCampath final {
public:
    void Store(
        const CampathPayloadHeader& header, const CampathKeyframe* keyframes,
        const std::uint64_t sequence) noexcept {
        generation_.fetch_add(1, std::memory_order_acq_rel);
        for (std::uint32_t index = 0; index < header.keyframe_count; ++index)
            keyframes_[index].Store(keyframes[index], sequence);
        count_.store(header.keyframe_count, std::memory_order_relaxed);
        interpolation_.store(header.interpolation, std::memory_order_relaxed);
        easing_.store(header.easing, std::memory_order_relaxed);
        end_behavior_.store(header.end_behavior, std::memory_order_relaxed);
        sequence_.store(sequence, std::memory_order_relaxed);
        generation_.fetch_add(1, std::memory_order_release);
    }

    [[nodiscard]] std::int64_t EndTick() const noexcept {
        const auto count = count_.load(std::memory_order_acquire);
        return count >= 2 && count <= kMaxCampathKeyframes ? keyframes_[count - 1].Tick() : -1;
    }

    [[nodiscard]] CampathEndBehavior EndBehavior() const noexcept {
        return end_behavior_.load(std::memory_order_acquire);
    }

    [[nodiscard]] bool Copy(
        CampathPayloadHeader& header,
        CampathKeyframe* keyframes,
        const std::size_t capacity) const noexcept {
        if (keyframes == nullptr || capacity == 0)
            return false;
        for (int attempt = 0; attempt < 4; ++attempt) {
            const auto before = generation_.load(std::memory_order_acquire);
            if ((before & 1u) != 0)
                continue;
            const auto count = count_.load(std::memory_order_relaxed);
            if (count == 0 || count > capacity || count > kMaxCampathKeyframes)
                return false;
            header = CampathPayloadHeader{
                count,
                interpolation_.load(std::memory_order_relaxed),
                easing_.load(std::memory_order_relaxed),
                end_behavior_.load(std::memory_order_relaxed),
            };
            auto valid = true;
            for (std::uint32_t index = 0; index < count; ++index) {
                if (!keyframes_[index].Load(keyframes[index])) {
                    valid = false;
                    break;
                }
            }
            const auto after = generation_.load(std::memory_order_acquire);
            if (valid && before == after && (after & 1u) == 0)
                return true;
        }
        return false;
    }

    [[nodiscard]] bool Evaluate(
        const std::int64_t demo_tick, CameraSample& sample, std::uint64_t& sequence) const noexcept {
        for (int attempt = 0; attempt < 4; ++attempt) {
            const auto before = generation_.load(std::memory_order_acquire);
            if ((before & 1u) != 0)
                continue;
            const auto count = count_.load(std::memory_order_relaxed);
            const auto interpolation = interpolation_.load(std::memory_order_relaxed);
            const auto easing = easing_.load(std::memory_order_relaxed);
            if (count < 2 || count > kMaxCampathKeyframes)
                return false;

            std::uint32_t right = 1;
            if (demo_tick >= keyframes_[count - 1].Tick()) {
                right = count - 1;
            } else if (demo_tick > keyframes_[0].Tick()) {
                std::uint32_t low = 1;
                std::uint32_t high = count - 1;
                while (low < high) {
                    const auto middle = low + ((high - low) / 2);
                    if (keyframes_[middle].Tick() < demo_tick)
                        low = middle + 1;
                    else
                        high = middle;
                }
                right = low;
            }
            const auto left = right - 1;
            CampathKeyframe p1{};
            CampathKeyframe p2{};
            if (!keyframes_[left].Load(p1) || !keyframes_[right].Load(p2))
                continue;

            if (demo_tick <= p1.demo_tick) {
                sample = p1.camera;
            } else if (demo_tick >= p2.demo_tick && right == count - 1) {
                sample = p2.camera;
            } else {
                auto amount = static_cast<double>(demo_tick - p1.demo_tick) /
                              static_cast<double>(p2.demo_tick - p1.demo_tick);
                amount = ApplyCampathEasing(amount, easing);
                if (interpolation == CampathInterpolation::smooth) {
                    CampathKeyframe p0{};
                    CampathKeyframe p3{};
                    if (left > 0) {
                        if (!keyframes_[left - 1].Load(p0))
                            continue;
                    } else {
                        p0 = CampathKeyframe{p1.demo_tick, ReflectCamera(p1.camera, p2.camera)};
                    }
                    if (right + 1 < count) {
                        if (!keyframes_[right + 1].Load(p3))
                            continue;
                    } else {
                        p3 = CampathKeyframe{p2.demo_tick, ReflectCamera(p2.camera, p1.camera)};
                    }
                    sample = EvaluateSmoothCamera(p0.camera, p1.camera, p2.camera, p3.camera, amount);
                } else {
                    sample = EvaluateLinearCamera(p1.camera, p2.camera, amount);
                }
            }

            const auto candidate_sequence = sequence_.load(std::memory_order_relaxed);
            const auto after = generation_.load(std::memory_order_acquire);
            if (before == after && (after & 1u) == 0) {
                sequence = candidate_sequence;
                return ValidateSample(sample);
            }
        }
        return false;
    }

private:
    std::atomic<std::uint64_t> generation_{0};
    std::atomic<std::uint32_t> count_{0};
    std::atomic<CampathInterpolation> interpolation_{CampathInterpolation::linear};
    std::atomic<CampathEasing> easing_{CampathEasing::linear};
    std::atomic<CampathEndBehavior> end_behavior_{CampathEndBehavior::stop_and_release};
    std::atomic<std::uint64_t> sequence_{0};
    std::array<AtomicPathKeyframe, kMaxCampathKeyframes> keyframes_{};
};

template <typename T>
class AtomicPacket final {
public:
    static_assert(std::is_trivially_copyable_v<T>);
    static_assert(sizeof(T) % sizeof(std::uint64_t) == 0);

    void Store(const T& value) noexcept {
        generation_.fetch_add(1, std::memory_order_acq_rel);
        std::array<std::uint64_t, sizeof(T) / sizeof(std::uint64_t)> words{};
        std::memcpy(words.data(), &value, sizeof(value));
        for (std::size_t index = 0; index < words.size(); ++index)
            words_[index].store(words[index], std::memory_order_relaxed);
        generation_.fetch_add(1, std::memory_order_release);
    }

    [[nodiscard]] bool Load(T& value) const noexcept {
        for (int attempt = 0; attempt < 4; ++attempt) {
            const auto before = generation_.load(std::memory_order_acquire);
            if ((before & 1u) != 0)
                continue;
            std::array<std::uint64_t, sizeof(T) / sizeof(std::uint64_t)> words{};
            for (std::size_t index = 0; index < words.size(); ++index)
                words[index] = words_[index].load(std::memory_order_relaxed);
            const auto after = generation_.load(std::memory_order_acquire);
            if (before == after && (after & 1u) == 0) {
                std::memcpy(&value, words.data(), sizeof(value));
                return true;
            }
        }
        return false;
    }

private:
    std::atomic<std::uint64_t> generation_{0};
    std::array<std::atomic<std::uint64_t>, sizeof(T) / sizeof(std::uint64_t)> words_{};
};

struct Backend final {
    HMODULE self{};
    ModuleView client{};
    std::uintptr_t manager{};
    std::uintptr_t controllers{};
    std::uintptr_t entity_system_global{};
    std::uintptr_t update_target{};
    std::uintptr_t global_vars_slot{};
    // These are published before the vtable slot can point at CameraUpdateHook.
    // The hook and pipe threads access them concurrently, so they must remain
    // atomic even though hook installation itself is serialized by the pipe.
    std::atomic<void**> hooked_vtable{nullptr};
    std::atomic<CameraUpdate> original_update{nullptr};

    std::atomic<BackendState> state{BackendState::loading};
    std::atomic<ErrorCode> error{ErrorCode::none};
    std::atomic<bool> resolved{false};
    std::atomic<bool> hook_installed{false};
    std::atomic<bool> pipe_connected{false};
    std::atomic<bool> replay_active{false};
    std::atomic<bool> free_roam{false};
    std::atomic<bool> override_requested{false};
    std::atomic<bool> override_active{false};
    std::atomic<bool> roll_override_requested{false};
    std::atomic<bool> roll_override_active{false};
    std::atomic<std::uint64_t> roll_override_bits{std::bit_cast<std::uint64_t>(0.0)};
    std::atomic<bool> manual_camera_requested{false};
    std::atomic<bool> manual_camera_active{false};
    std::atomic<bool> manual_rebase_from_applied{false};
    std::atomic<std::uint64_t> manual_generation{0};
    std::atomic<bool> has_sample{false};
    std::atomic<bool> campath_active{false};
    std::atomic<bool> campath_completed{false};
    std::atomic<bool> command_line_replay{false};
    std::atomic<bool> observation_requested{false};
    std::atomic<bool> camera_observed{false};
    std::atomic<std::uint64_t> camera_observed_milliseconds{0};
    std::atomic<bool> smvm_snapshot_available{false};
    std::atomic<std::uint64_t> smvm_snapshot_milliseconds{0};
    std::atomic<bool> editor_campath_available{false};
    // Zero means no pending capture; otherwise this is action-queue epoch + 1.
    // Keeping request+epoch in one atomic prevents reconnect/new-click races.
    std::atomic<std::uint64_t> internal_capture_epoch_token{0};
    std::atomic<SmvmRendererBackend> renderer_backend{SmvmRendererBackend::none};
    std::atomic<SmvmRendererError> renderer_error{SmvmRendererError::renderer_not_loaded};
    std::atomic<std::uint32_t> overlay_flags{0};
    std::atomic<std::uint32_t> overlay_frame_microseconds{0};
    std::atomic<bool> shutdown{false};
    std::atomic<std::uint64_t> heartbeat_milliseconds{0};
    std::atomic<std::int64_t> replay_tick{0};
    std::atomic<std::int64_t> game_tick_offset{-1};
    std::atomic<std::int64_t> engine_tick{0};
    std::atomic<std::uint64_t> accepted_sequence{0};
    std::atomic<std::uint64_t> applied_sequence{0};
    std::atomic<std::uint64_t> hook_calls{0};
    std::atomic<std::uint32_t> active_hooks{0};
    AtomicSample desired_sample{};
    AtomicSample applied_sample{};
    AtomicSample manual_sample{};
    AtomicCameraFrame observed_frame{};
    AtomicCampath campath{};
    AtomicCampath editor_campath{};
    AtomicPacket<SmvmSnapshotPayload> smvm_snapshot{};
    SmvmActionQueue smvm_actions{};
    std::uint64_t manual_seen_generation{};
    std::int64_t manual_last_counter{};
    std::int64_t performance_frequency{};
    ManualCameraMotion manual_smoothed_motion{};
    SmvmSnapshotPayload manual_snapshot_cache{};
    std::atomic<bool> manual_snapshot_cached{false};
};

Backend* g_backend = nullptr;

[[nodiscard]] bool HasFreshSmvmSnapshot(const Backend& backend) noexcept {
    const auto received_at = backend.smvm_snapshot_milliseconds.load(std::memory_order_acquire);
    return backend.smvm_snapshot_available.load(std::memory_order_acquire) && received_at != 0 &&
           GetTickCount64() - received_at <= kSmvmSnapshotFreshMilliseconds;
}

void RevokeCameraOwnership(Backend& backend, const ErrorCode error) noexcept {
    backend.override_requested.store(false, std::memory_order_release);
    backend.override_active.store(false, std::memory_order_release);
    backend.has_sample.store(false, std::memory_order_release);
    backend.campath_active.store(false, std::memory_order_release);
    backend.campath_completed.store(false, std::memory_order_release);
    backend.roll_override_requested.store(false, std::memory_order_release);
    backend.roll_override_active.store(false, std::memory_order_release);
    const auto manual_was_owned = backend.manual_camera_requested.exchange(false, std::memory_order_acq_rel) |
                                  backend.manual_camera_active.exchange(false, std::memory_order_acq_rel);
    backend.manual_rebase_from_applied.store(false, std::memory_order_release);
    if (manual_was_owned)
        backend.manual_generation.fetch_add(1, std::memory_order_acq_rel);
    backend.manual_snapshot_cached.store(false, std::memory_order_release);
    ResetSmvmManualInput();
    backend.observation_requested.store(false, std::memory_order_release);
    backend.camera_observed.store(false, std::memory_order_release);
    backend.camera_observed_milliseconds.store(0, std::memory_order_release);
    backend.internal_capture_epoch_token.store(0, std::memory_order_release);
    const auto existing_error = backend.error.load(std::memory_order_acquire);
    if (backend.state.load(std::memory_order_acquire) != BackendState::failed ||
        existing_error == ErrorCode::none || error == ErrorCode::protocol_error)
        backend.error.store(error, std::memory_order_release);
}

[[nodiscard]] bool OverlayReadSnapshot(void* context, SmvmSnapshotPayload& snapshot) noexcept {
    auto* backend = static_cast<Backend*>(context);
    return backend != nullptr && backend->pipe_connected.load(std::memory_order_acquire) &&
           HasFreshSmvmSnapshot(*backend) &&
           backend->smvm_snapshot.Load(snapshot);
}

[[nodiscard]] bool OverlayReadEditorPath(
    void* context,
    CampathPayloadHeader& header,
    CampathKeyframe* keyframes,
    const std::size_t capacity) noexcept {
    auto* backend = static_cast<Backend*>(context);
    return backend != nullptr && backend->pipe_connected.load(std::memory_order_acquire) &&
           backend->editor_campath_available.load(std::memory_order_acquire) &&
           backend->editor_campath.Copy(header, keyframes, capacity);
}

[[nodiscard]] bool OverlayQueueAction(void* context, const SmvmActionPayload& action) noexcept {
    auto* backend = static_cast<Backend*>(context);
    const auto action_generation = backend == nullptr ? 0 : backend->smvm_actions.Generation();
    if (backend == nullptr || !backend->pipe_connected.load(std::memory_order_acquire) ||
        !backend->replay_active.load(std::memory_order_acquire) ||
        !HasFreshSmvmSnapshot(*backend))
        return false;
    return backend->smvm_actions.TryPush(action, action_generation);
}

void OverlayRequestCameraCapture(void* context) noexcept {
    auto* backend = static_cast<Backend*>(context);
    const auto action_generation = backend == nullptr ? 0 : backend->smvm_actions.Generation();
    const auto observed_at = backend == nullptr
        ? 0
        : backend->camera_observed_milliseconds.load(std::memory_order_acquire);
    if (backend != nullptr && backend->pipe_connected.load(std::memory_order_acquire) &&
        backend->replay_active.load(std::memory_order_acquire) &&
        backend->free_roam.load(std::memory_order_acquire) &&
        HasFreshSmvmSnapshot(*backend) &&
        backend->camera_observed.load(std::memory_order_acquire) &&
        observed_at != 0 && GetTickCount64() - observed_at <= kCameraObservationFreshMilliseconds &&
        !backend->campath_active.load(std::memory_order_acquire)) {
        backend->internal_capture_epoch_token.store(action_generation + 1, std::memory_order_release);
    }
}

void OverlayPublishStatus(
    void* context,
    const SmvmRendererBackend renderer,
    const SmvmRendererError error,
    const std::uint32_t flags,
    const std::uint32_t frame_microseconds) noexcept {
    auto* backend = static_cast<Backend*>(context);
    if (backend == nullptr)
        return;
    backend->renderer_backend.store(renderer, std::memory_order_release);
    backend->renderer_error.store(error, std::memory_order_release);
    backend->overlay_flags.store(flags, std::memory_order_release);
    backend->overlay_frame_microseconds.store(frame_microseconds, std::memory_order_release);
}

[[nodiscard]] std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](const wchar_t value) {
        return static_cast<wchar_t>(std::towlower(value));
    });
    return value;
}

[[nodiscard]] bool ValidateHostProcess(Backend& backend) noexcept {
    std::array<wchar_t, MAX_PATH> path{};
    const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) {
        backend.error.store(ErrorCode::wrong_process, std::memory_order_release);
        return false;
    }
    const auto lower_path = Lower(std::wstring(path.data(), length));
    const auto slash = lower_path.find_last_of(L"\\/");
    const auto name = lower_path.substr(slash == std::wstring::npos ? 0 : slash + 1);
    if (name != L"deadlock.exe" && name != L"project8.exe") {
        backend.error.store(ErrorCode::wrong_process, std::memory_order_release);
        return false;
    }

    const auto command_line = GetCommandLineW();
    const auto replay_launch = command_line != nullptr && Lower(command_line).find(L"+playdemo") != std::wstring::npos;
    backend.command_line_replay.store(replay_launch, std::memory_order_release);
    if (!replay_launch) {
        backend.error.store(ErrorCode::replay_launch_required, std::memory_order_release);
        return false;
    }
    return true;
}

[[nodiscard]] std::optional<ModuleView> InspectModule(const HMODULE module) noexcept {
    if (module == nullptr)
        return std::nullopt;
    const auto base = reinterpret_cast<std::uint8_t*>(module);
    __try {
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
            return std::nullopt;
        const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
            return std::nullopt;
        ModuleView view{base, nt->OptionalHeader.SizeOfImage, {}};
        const auto sections = IMAGE_FIRST_SECTION(nt);
        for (std::uint16_t index = 0; index < nt->FileHeader.NumberOfSections; ++index) {
            const auto& section = sections[index];
            if (std::memcmp(section.Name, ".text", 5) == 0) {
                view.text.begin = base + section.VirtualAddress;
                view.text.size = std::max<std::size_t>(section.Misc.VirtualSize, section.SizeOfRawData);
                break;
            }
        }
        if (view.text.begin == nullptr || !view.Contains(reinterpret_cast<std::uintptr_t>(view.text.begin), view.text.size))
            return std::nullopt;
        return view;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return std::nullopt;
    }
}

[[nodiscard]] bool ReadPointer(const std::uintptr_t address, std::uintptr_t& value) noexcept {
    value = 0;
    if (address == 0)
        return false;
    __try {
        value = *reinterpret_cast<const std::uintptr_t*>(address);
        return value != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] bool ResolveStaticTargets(Backend& backend) noexcept {
    const auto client_module = GetModuleHandleW(L"client.dll");
    const auto inspected = InspectModule(client_module);
    if (!inspected) {
        backend.error.store(ErrorCode::client_module_missing, std::memory_order_release);
        return false;
    }
    backend.client = *inspected;

    const auto manager_pattern = ParsePattern(kManagerPattern);
    const auto pawn_pattern = ParsePattern(kLocalPawnPattern);
    const auto update_pattern = ParsePattern(kUpdatePattern);
    if (!manager_pattern || !pawn_pattern || !update_pattern) {
        backend.error.store(ErrorCode::signature_missing, std::memory_order_release);
        return false;
    }

    const auto manager_hits = FindPattern(backend.client.text.begin, backend.client.text.size, *manager_pattern, 8);
    std::vector<std::uintptr_t> manager_targets;
    for (const auto hit : manager_hits) {
        const auto instruction = reinterpret_cast<std::uintptr_t>(backend.client.text.begin + hit + 9);
        const auto target = ResolveRipRelative(instruction, 3, 7);
        if (target)
            manager_targets.push_back(*target);
    }
    std::sort(manager_targets.begin(), manager_targets.end());
    manager_targets.erase(std::unique(manager_targets.begin(), manager_targets.end()), manager_targets.end());
    if (manager_targets.size() != 1 || !backend.client.Contains(manager_targets.front(), sizeof(void*) * 8)) {
        backend.error.store(manager_targets.empty() ? ErrorCode::signature_missing : ErrorCode::signature_ambiguous,
                            std::memory_order_release);
        return false;
    }

    const auto pawn_hits = FindPattern(backend.client.text.begin, backend.client.text.size, *pawn_pattern, 2);
    const auto update_hits = FindPattern(backend.client.text.begin, backend.client.text.size, *update_pattern, 2);
    if (pawn_hits.size() != 1 || update_hits.size() != 1) {
        backend.error.store((pawn_hits.size() > 1 || update_hits.size() > 1)
                                ? ErrorCode::signature_ambiguous
                                : ErrorCode::signature_missing,
                            std::memory_order_release);
        return false;
    }

    const auto pawn_start = reinterpret_cast<std::uintptr_t>(backend.client.text.begin + pawn_hits.front());
    const auto controllers = ResolveRipRelative(pawn_start + 2, 3, 7);
    std::optional<std::uintptr_t> entity_system;
    const auto* pawn_bytes = reinterpret_cast<const std::uint8_t*>(pawn_start);
    for (std::size_t offset = 0x20; offset <= 0x50; ++offset) {
        if (pawn_bytes[offset] == 0x4C && pawn_bytes[offset + 1] == 0x8B && pawn_bytes[offset + 2] == 0x0D) {
            entity_system = ResolveRipRelative(pawn_start + offset, 3, 7);
            break;
        }
    }
    if (!controllers || !entity_system ||
        !backend.client.Contains(*controllers, sizeof(void*)) ||
        !backend.client.Contains(*entity_system, sizeof(void*))) {
        backend.error.store(ErrorCode::signature_missing, std::memory_order_release);
        return false;
    }

    backend.manager = manager_targets.front();
    backend.controllers = *controllers;
    backend.entity_system_global = *entity_system;
    backend.update_target = reinterpret_cast<std::uintptr_t>(backend.client.text.begin + update_hits.front());
    const auto global_vars = ResolveRipRelative(backend.update_target + 8, 3, 7);
    if (!global_vars || !backend.client.Contains(*global_vars, sizeof(void*))) {
        backend.error.store(ErrorCode::signature_missing, std::memory_order_release);
        return false;
    }
    backend.global_vars_slot = *global_vars;
    backend.resolved.store(true, std::memory_order_release);
    backend.state.store(BackendState::connected, std::memory_order_release);
    backend.error.store(ErrorCode::none, std::memory_order_release);
    return true;
}

[[nodiscard]] bool WaitForClientModule(Backend& backend) noexcept {
    const auto deadline = std::chrono::steady_clock::now() + kClientModuleWaitTimeout;
    while (std::chrono::steady_clock::now() < deadline) {
        if (GetModuleHandleW(L"client.dll") != nullptr)
            return true;
        if (backend.shutdown.load(std::memory_order_acquire))
            break;
        Sleep(10);
    }
    backend.error.store(ErrorCode::client_module_missing, std::memory_order_release);
    return false;
}

[[nodiscard]] bool ResolveCamera(Backend& backend, void*& camera, void**& vtable) noexcept {
    camera = nullptr;
    vtable = nullptr;
    std::uintptr_t camera_address = 0;
    std::uintptr_t vtable_address = 0;
    if (!ReadPointer(backend.manager + kManagerCurrentCamera, camera_address) ||
        !ReadPointer(camera_address, vtable_address) ||
        !backend.client.Contains(vtable_address, sizeof(void*) * 20))
        return false;

    __try {
        const auto fov = *reinterpret_cast<const float*>(camera_address + kCameraFov);
        if (!std::isfinite(fov) || fov < 1.0f || fov > 179.0f)
            return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }

    camera = reinterpret_cast<void*>(camera_address);
    vtable = reinterpret_cast<void**>(vtable_address);
    return true;
}

[[nodiscard]] bool IsObserverRoaming(const Backend& backend) noexcept {
    std::uintptr_t controller = 0;
    std::uintptr_t entity_system = 0;
    if (!ReadPointer(backend.controllers, controller) || !ReadPointer(backend.entity_system_global, entity_system))
        return false;

    __try {
        const auto handle = *reinterpret_cast<const std::uint32_t*>(controller + kControllerPawnHandle);
        if (handle == 0xFFFFFFFFu || handle == 0xFFFFFFFEu)
            return false;
        const auto index = handle & 0x7FFFu;
        const auto chunk = *reinterpret_cast<const std::uintptr_t*>(entity_system + 8ull * (index >> 9));
        if (chunk == 0)
            return false;
        const auto entry = chunk + 112ull * (index & 0x1FFu);
        if (*reinterpret_cast<const std::uint32_t*>(entry + 0x10) != handle)
            return false;
        const auto pawn = *reinterpret_cast<const std::uintptr_t*>(entry);
        if (pawn == 0)
            return false;
        const auto observer = *reinterpret_cast<const std::uintptr_t*>(pawn + kPawnObserverServices);
        return observer != 0 && *reinterpret_cast<const std::uint8_t*>(observer + kObserverMode) == 4;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void WriteVector(const std::uintptr_t address, const float x, const float y, const float z) noexcept {
    auto* values = reinterpret_cast<float*>(address);
    values[0] = x;
    values[1] = y;
    values[2] = z;
}

[[nodiscard]] bool ReadCameraSample(void* camera, CameraSample& sample) noexcept {
    if (camera == nullptr)
        return false;
    __try {
        const auto address = reinterpret_cast<std::uintptr_t>(camera);
        const auto* origin = reinterpret_cast<const float*>(address + kCameraOrigin);
        const auto* angles = reinterpret_cast<const float*>(address + kCameraAngles);
        sample = CameraSample{
            origin[0], origin[1], origin[2], angles[0], angles[1], angles[2],
            *reinterpret_cast<const float*>(address + kCameraFov),
        };
        return ValidateSample(sample);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        sample = {};
        return false;
    }
}

[[nodiscard]] bool WriteCameraSample(void* camera, const CameraSample& sample) noexcept {
    if (camera == nullptr || !ValidateSample(sample))
        return false;
    __try {
        const auto address = reinterpret_cast<std::uintptr_t>(camera);
        const auto x = static_cast<float>(sample.x);
        const auto y = static_cast<float>(sample.y);
        const auto z = static_cast<float>(sample.z);
        const auto pitch = static_cast<float>(sample.pitch);
        const auto yaw = static_cast<float>(sample.yaw);
        const auto roll = static_cast<float>(sample.roll);
        WriteVector(address + kCameraRoamingPivot, x, y, z);
        WriteVector(address + kCameraOrigin, x, y, z);
        WriteVector(address + kCameraOriginCache, x, y, z);
        WriteVector(address + kCameraAngles, pitch, yaw, roll);
        WriteVector(address + kCameraAnglesMirror, pitch, yaw, roll);
        WriteVector(address + kCameraAnglesOutput, pitch, yaw, roll);
        *reinterpret_cast<float*>(address + kCameraFov) = static_cast<float>(sample.fov);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] bool CanApply(Backend& backend, void* camera) noexcept {
    if (!backend.override_requested.load(std::memory_order_acquire)) {
        backend.override_active.store(false, std::memory_order_release);
        return false;
    }
    if (!backend.has_sample.load(std::memory_order_acquire) ||
        !backend.pipe_connected.load(std::memory_order_acquire) ||
        !backend.replay_active.load(std::memory_order_acquire) ||
        !backend.free_roam.load(std::memory_order_acquire) ||
        !backend.command_line_replay.load(std::memory_order_acquire)) {
        RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
        return false;
    }

    const auto heartbeat = backend.heartbeat_milliseconds.load(std::memory_order_acquire);
    if (heartbeat == 0 || GetTickCount64() - heartbeat > kHeartbeatTimeoutMilliseconds) {
        RevokeCameraOwnership(backend, ErrorCode::heartbeat_stale);
        return false;
    }

    void* current_camera = nullptr;
    void** current_vtable = nullptr;
    if (!ResolveCamera(backend, current_camera, current_vtable) || current_camera != camera ||
        current_vtable != backend.hooked_vtable.load(std::memory_order_acquire) || !IsObserverRoaming(backend)) {
        RevokeCameraOwnership(backend, ErrorCode::observer_not_roaming);
        return false;
    }
    return true;
}

[[nodiscard]] bool CanApplyRoll(Backend& backend, void* camera) noexcept {
    if (!backend.roll_override_requested.load(std::memory_order_acquire)) {
        backend.roll_override_active.store(false, std::memory_order_release);
        return false;
    }
    if (!backend.pipe_connected.load(std::memory_order_acquire) ||
        !backend.replay_active.load(std::memory_order_acquire) ||
        !backend.free_roam.load(std::memory_order_acquire) ||
        !backend.command_line_replay.load(std::memory_order_acquire)) {
        RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
        return false;
    }

    const auto heartbeat = backend.heartbeat_milliseconds.load(std::memory_order_acquire);
    if (heartbeat == 0 || GetTickCount64() - heartbeat > kHeartbeatTimeoutMilliseconds) {
        RevokeCameraOwnership(backend, ErrorCode::heartbeat_stale);
        return false;
    }

    void* current_camera = nullptr;
    void** current_vtable = nullptr;
    if (!ResolveCamera(backend, current_camera, current_vtable) || current_camera != camera ||
        current_vtable != backend.hooked_vtable.load(std::memory_order_acquire) || !IsObserverRoaming(backend)) {
        RevokeCameraOwnership(backend, ErrorCode::observer_not_roaming);
        return false;
    }
    return true;
}

[[nodiscard]] bool CanApplyManualCamera(Backend& backend, void* camera) noexcept {
    if (!backend.manual_camera_requested.load(std::memory_order_acquire)) {
        backend.manual_camera_active.store(false, std::memory_order_release);
        return false;
    }
    if (!backend.pipe_connected.load(std::memory_order_acquire) ||
        !backend.replay_active.load(std::memory_order_acquire) ||
        !backend.free_roam.load(std::memory_order_acquire) ||
        !backend.command_line_replay.load(std::memory_order_acquire)) {
        RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
        return false;
    }
    if (!HasFreshSmvmSnapshot(backend)) {
        RevokeCameraOwnership(backend, ErrorCode::heartbeat_stale);
        return false;
    }
    const auto heartbeat = backend.heartbeat_milliseconds.load(std::memory_order_acquire);
    if (heartbeat == 0 || GetTickCount64() - heartbeat > kHeartbeatTimeoutMilliseconds) {
        RevokeCameraOwnership(backend, ErrorCode::heartbeat_stale);
        return false;
    }
    void* current_camera = nullptr;
    void** current_vtable = nullptr;
    if (!ResolveCamera(backend, current_camera, current_vtable) || current_camera != camera ||
        current_vtable != backend.hooked_vtable.load(std::memory_order_acquire) || !IsObserverRoaming(backend)) {
        RevokeCameraOwnership(backend, ErrorCode::observer_not_roaming);
        return false;
    }
    return true;
}

[[nodiscard]] ManualCameraMotion SmoothManualMotion(
    Backend& backend,
    const ManualCameraMotion& target,
    const double smoothing,
    const double delta_seconds) noexcept {
    if (smoothing <= 0.0001 || delta_seconds <= 0.0) {
        backend.manual_smoothed_motion = target;
        return target;
    }
    const auto time_constant = 0.01 + (std::clamp(smoothing, 0.0, 0.95) * 0.28);
    const auto blend = std::clamp(1.0 - std::exp(-delta_seconds / time_constant), 0.0, 1.0);
    auto& value = backend.manual_smoothed_motion;
    const auto approach = [blend](double& current, const double desired) noexcept {
        current += (desired - current) * blend;
    };
    approach(value.forward, target.forward);
    approach(value.right, target.right);
    approach(value.up, target.up);
    // Raw mouse deltas are per-frame impulses. Carrying them forward creates
    // a rotation tail after the user stops moving the mouse.
    value.mouse_x = target.mouse_x;
    value.mouse_y = target.mouse_y;
    value.roll = target.roll;
    value.wheel_steps = target.wheel_steps;
    value.reset_roll = target.reset_roll;
    return value;
}

[[nodiscard]] bool CanObserve(Backend& backend, void* camera) noexcept {
    if (!backend.observation_requested.load(std::memory_order_acquire))
        return false;
    if (!backend.pipe_connected.load(std::memory_order_acquire) ||
        !backend.replay_active.load(std::memory_order_acquire) ||
        !backend.free_roam.load(std::memory_order_acquire) ||
        !backend.command_line_replay.load(std::memory_order_acquire)) {
        RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
        return false;
    }

    const auto heartbeat = backend.heartbeat_milliseconds.load(std::memory_order_acquire);
    if (heartbeat == 0 || GetTickCount64() - heartbeat > kHeartbeatTimeoutMilliseconds) {
        RevokeCameraOwnership(backend, ErrorCode::heartbeat_stale);
        return false;
    }

    void* current_camera = nullptr;
    void** current_vtable = nullptr;
    if (!ResolveCamera(backend, current_camera, current_vtable) || current_camera != camera ||
        current_vtable != backend.hooked_vtable.load(std::memory_order_acquire) || !IsObserverRoaming(backend)) {
        RevokeCameraOwnership(backend, ErrorCode::observer_not_roaming);
        return false;
    }
    return true;
}

void* __fastcall CameraUpdateHook(void* camera) noexcept {
    auto* backend = g_backend;
    if (backend == nullptr)
        return nullptr;

    backend->active_hooks.fetch_add(1, std::memory_order_acq_rel);
    const auto original = backend->original_update.load(std::memory_order_acquire);
    void* result = original != nullptr ? original(camera) : nullptr;
    const auto hook_call = backend->hook_calls.fetch_add(1, std::memory_order_relaxed) + 1;

    std::uintptr_t global_vars = 0;
    if (ReadPointer(backend->global_vars_slot, global_vars)) {
        __try {
            const auto engine_tick = *reinterpret_cast<const std::int32_t*>(global_vars + kGlobalVarsTick);
            backend->engine_tick.store(engine_tick, std::memory_order_release);
            const auto offset = backend->game_tick_offset.load(std::memory_order_acquire);
            backend->replay_tick.store(offset >= 0 ? engine_tick - offset : -1, std::memory_order_release);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            backend->engine_tick.store(0, std::memory_order_release);
            backend->replay_tick.store(-1, std::memory_order_release);
        }
    }

    const auto full_override_requested = backend->override_requested.load(std::memory_order_acquire);
    if (full_override_requested && CanApply(*backend, camera)) {
        CameraSample sample{};
        std::uint64_t sequence = 0;
        auto loaded = false;
        if (backend->campath_active.load(std::memory_order_acquire)) {
            const auto demo_tick = backend->replay_tick.load(std::memory_order_acquire);
            if (demo_tick < 0) {
                RevokeCameraOwnership(*backend, ErrorCode::replay_clock_unavailable);
            } else {
                loaded = backend->campath.Evaluate(demo_tick, sample, sequence);
            }
        } else {
            loaded = backend->desired_sample.Load(sample, sequence);
        }
        if (loaded && ValidateSample(sample)) {
            if (WriteCameraSample(camera, sample)) {
                backend->applied_sample.Store(sample, sequence);
                backend->applied_sequence.store(sequence, std::memory_order_release);
                backend->override_active.store(true, std::memory_order_release);
                backend->error.store(ErrorCode::none, std::memory_order_release);
                if (backend->campath_active.load(std::memory_order_acquire) &&
                    backend->campath.EndBehavior() == CampathEndBehavior::stop_and_release &&
                    backend->replay_tick.load(std::memory_order_acquire) >= backend->campath.EndTick()) {
                    backend->campath_completed.store(true, std::memory_order_release);
                    backend->campath_active.store(false, std::memory_order_release);
                    backend->override_requested.store(false, std::memory_order_release);
                    backend->override_active.store(false, std::memory_order_release);
                    backend->manual_rebase_from_applied.store(true, std::memory_order_release);
                }
            } else {
                RevokeCameraOwnership(*backend, ErrorCode::hook_runtime_invalid);
            }
        } else {
            RevokeCameraOwnership(*backend, ErrorCode::invalid_sample);
        }
    }

    auto manual_applied = false;
    if (!full_override_requested &&
        backend->manual_camera_requested.load(std::memory_order_acquire) &&
        CanApplyManualCamera(*backend, camera)) {
        SmvmSnapshotPayload snapshot{};
        const auto loaded_snapshot = backend->smvm_snapshot.Load(snapshot) &&
                                     ValidateSmvmSnapshotPayload(snapshot);
        if (loaded_snapshot) {
            backend->manual_snapshot_cache = snapshot;
            backend->manual_snapshot_cached.store(true, std::memory_order_release);
        } else if (backend->manual_snapshot_cached.load(std::memory_order_acquire)) {
            snapshot = backend->manual_snapshot_cache;
        }
        if (loaded_snapshot || backend->manual_snapshot_cached.load(std::memory_order_acquire)) {
            LARGE_INTEGER counter{};
            QueryPerformanceCounter(&counter);
            const auto generation = backend->manual_generation.load(std::memory_order_acquire);
            const auto initialize = generation != backend->manual_seen_generation;
            CameraSample sample{};
            std::uint64_t ignored_sequence = 0;
            if (initialize) {
                auto loaded = false;
                if (backend->manual_rebase_from_applied.exchange(false, std::memory_order_acq_rel))
                    loaded = backend->applied_sample.Load(sample, ignored_sequence) && ValidateSample(sample);
                if (!loaded)
                    loaded = ReadCameraSample(camera, sample);
                if (loaded) {
                    backend->manual_sample.Store(sample, generation);
                    backend->manual_seen_generation = generation;
                    backend->manual_last_counter = counter.QuadPart;
                    backend->manual_smoothed_motion = {};
                }
            }
            if (backend->manual_sample.Load(sample, ignored_sequence) && ValidateSample(sample)) {
                auto delta_seconds = 0.0;
                if (backend->manual_last_counter > 0 && backend->performance_frequency > 0)
                    delta_seconds = static_cast<double>(counter.QuadPart - backend->manual_last_counter) /
                        static_cast<double>(backend->performance_frequency);
                backend->manual_last_counter = counter.QuadPart;
                SmvmManualInputFrame input{};
                ManualCameraMotion motion{};
                if (ConsumeSmvmManualInput(snapshot, input)) {
                    motion.forward = (input.forward ? 1.0 : 0.0) - (input.backward ? 1.0 : 0.0);
                    motion.right = (input.right ? 1.0 : 0.0) - (input.left ? 1.0 : 0.0);
                    motion.up = (input.up ? 1.0 : 0.0) - (input.down ? 1.0 : 0.0);
                    motion.mouse_x = input.mouse_delta_x;
                    motion.mouse_y = input.mouse_delta_y;
                    motion.roll = (input.roll_right ? 1.0 : 0.0) - (input.roll_left ? 1.0 : 0.0);
                    motion.wheel_steps = input.wheel_steps;
                    motion.reset_roll = input.reset_roll;
                    if (motion.roll != 0.0 || motion.reset_roll)
                        backend->roll_override_requested.store(false, std::memory_order_release);
                }
                motion = SmoothManualMotion(*backend, motion, snapshot.smoothing, delta_seconds);
                auto speed = snapshot.movement_speed;
                if (input.fast)
                    speed *= snapshot.boost_multiplier;
                if (input.precision)
                    speed *= snapshot.precision_multiplier;
                const ManualCameraTuning tuning{
                    speed,
                    snapshot.mouse_sensitivity,
                    45.0,
                    snapshot.fov_step,
                    (snapshot.flags & smvm_snapshot_invert_y) != 0,
                    (snapshot.flags & smvm_snapshot_fov_inverted) != 0,
                };
                sample = ApplyManualCameraMotion(sample, motion, tuning, delta_seconds);
                if (backend->roll_override_requested.load(std::memory_order_acquire))
                    sample.roll = std::bit_cast<double>(
                        backend->roll_override_bits.load(std::memory_order_acquire));
                if (WriteCameraSample(camera, sample)) {
                    backend->manual_sample.Store(sample, generation);
                    backend->manual_camera_active.store(true, std::memory_order_release);
                    backend->roll_override_active.store(
                        backend->roll_override_requested.load(std::memory_order_acquire),
                        std::memory_order_release);
                    backend->error.store(ErrorCode::none, std::memory_order_release);
                    manual_applied = true;
                } else {
                    RevokeCameraOwnership(*backend, ErrorCode::hook_runtime_invalid);
                }
            }
        } else {
            RevokeCameraOwnership(*backend, ErrorCode::heartbeat_stale);
        }
    } else {
        backend->manual_camera_active.store(false, std::memory_order_release);
    }

    if (!full_override_requested && !manual_applied &&
        backend->roll_override_requested.load(std::memory_order_acquire) &&
        CanApplyRoll(*backend, camera)) {
        const auto roll = std::bit_cast<double>(backend->roll_override_bits.load(std::memory_order_acquire));
        if (!ValidateRoll(roll)) {
            backend->roll_override_requested.store(false, std::memory_order_release);
            backend->roll_override_active.store(false, std::memory_order_release);
            backend->error.store(ErrorCode::invalid_sample, std::memory_order_release);
        } else {
            __try {
                const auto address = reinterpret_cast<std::uintptr_t>(camera);
                const auto rendered_roll = static_cast<float>(roll);
                *reinterpret_cast<float*>(address + kCameraAngles + (2 * sizeof(float))) = rendered_roll;
                *reinterpret_cast<float*>(address + kCameraAnglesMirror + (2 * sizeof(float))) = rendered_roll;
                *reinterpret_cast<float*>(address + kCameraAnglesOutput + (2 * sizeof(float))) = rendered_roll;
                backend->roll_override_active.store(true, std::memory_order_release);
                backend->error.store(ErrorCode::none, std::memory_order_release);
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                RevokeCameraOwnership(*backend, ErrorCode::hook_runtime_invalid);
            }
        }
    } else if (!manual_applied) {
        backend->roll_override_active.store(false, std::memory_order_release);
    }

    if (CanObserve(*backend, camera)) {
        __try {
            const auto address = reinterpret_cast<std::uintptr_t>(camera);
            const auto* origin = reinterpret_cast<const float*>(address + kCameraOrigin);
            const auto* angles = reinterpret_cast<const float*>(address + kCameraAngles);
            const auto fov = *reinterpret_cast<const float*>(address + kCameraFov);
            const CameraSample observed{
                origin[0], origin[1], origin[2],
                angles[0], angles[1], angles[2], fov,
            };
            const auto tick = backend->replay_tick.load(std::memory_order_acquire);
            if (tick >= 0 && ValidateSample(observed)) {
                backend->observed_frame.Store(observed, tick, hook_call);
                backend->camera_observed.store(true, std::memory_order_release);
                backend->camera_observed_milliseconds.store(GetTickCount64(), std::memory_order_release);
                const auto capture_token =
                    backend->internal_capture_epoch_token.exchange(0, std::memory_order_acq_rel);
                if (capture_token != 0) {
                    const SmvmActionPayload action{
                        SmvmActionType::add_keyframe, -1, tick, 0.0, observed};
                    const auto action_generation = capture_token - 1;
                    if (!backend->smvm_actions.TryPush(action, action_generation) &&
                        action_generation == backend->smvm_actions.Generation() &&
                        backend->pipe_connected.load(std::memory_order_acquire)) {
                        auto no_newer_capture = std::uint64_t{0};
                        static_cast<void>(backend->internal_capture_epoch_token.compare_exchange_strong(
                            no_newer_capture, capture_token, std::memory_order_acq_rel));
                    }
                }
            } else {
                backend->camera_observed.store(false, std::memory_order_release);
                backend->camera_observed_milliseconds.store(0, std::memory_order_release);
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            backend->camera_observed.store(false, std::memory_order_release);
            backend->camera_observed_milliseconds.store(0, std::memory_order_release);
        }
    } else {
        backend->camera_observed.store(false, std::memory_order_release);
        backend->camera_observed_milliseconds.store(0, std::memory_order_release);
    }

    backend->active_hooks.fetch_sub(1, std::memory_order_acq_rel);
    return result;
}

[[nodiscard]] bool WaitForHookDrain(Backend& backend) noexcept {
    const auto deadline = GetTickCount64() + 2000;
    auto stable_checks = 0;
    while (GetTickCount64() < deadline && stable_checks < 10) {
        if (backend.active_hooks.load(std::memory_order_acquire) == 0)
            ++stable_checks;
        else
            stable_checks = 0;
        Sleep(1);
    }
    return stable_checks == 10;
}

[[nodiscard]] bool DetachHook(Backend& backend) noexcept {
    if (!backend.hook_installed.load(std::memory_order_acquire))
        return true;

    auto* const vtable = backend.hooked_vtable.load(std::memory_order_acquire);
    const auto original = backend.original_update.load(std::memory_order_acquire);
    if (vtable == nullptr || original == nullptr)
        return false;

    auto* const slot = &vtable[kUpdateVtableIndex];
    if (!backend.client.Contains(reinterpret_cast<std::uintptr_t>(slot), sizeof(void*)))
        return false;

    DWORD prior_protection = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &prior_protection))
        return false;

    const auto observed_slot = InterlockedCompareExchangePointer(
        slot,
        reinterpret_cast<void*>(original),
        reinterpret_cast<void*>(&CameraUpdateHook));
    DWORD ignored = 0;
    const auto protection_restored =
        VirtualProtect(slot, sizeof(void*), prior_protection, &ignored) != FALSE;
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));

    // A third-party hook may have chained through SMVM after installation. In
    // that case the slot no longer equals either endpoint we can prove safe;
    // retain every publication and keep this DLL resident rather than unmapping
    // code that the foreign chain may still call.
    const auto slot_is_safe = IsSafeHookSlotObservation(
        observed_slot,
        reinterpret_cast<void*>(&CameraUpdateHook),
        reinterpret_cast<void*>(original));
    if (!slot_is_safe || !protection_restored || !WaitForHookDrain(backend))
        return false;

    backend.hook_installed.store(false, std::memory_order_release);
    backend.hooked_vtable.store(nullptr, std::memory_order_release);
    backend.original_update.store(nullptr, std::memory_order_release);
    backend.camera_observed.store(false, std::memory_order_release);
    backend.camera_observed_milliseconds.store(0, std::memory_order_release);
    return true;
}

[[nodiscard]] bool InstallHook(Backend& backend) noexcept {
    if (backend.hook_installed.load(std::memory_order_acquire)) {
        void* current_camera = nullptr;
        void** current_vtable = nullptr;
        auto* const published_vtable = backend.hooked_vtable.load(std::memory_order_acquire);
        if (ResolveCamera(backend, current_camera, current_vtable) &&
            current_vtable == published_vtable &&
            current_vtable[kUpdateVtableIndex] == reinterpret_cast<void*>(&CameraUpdateHook))
            return true;
        if (!DetachHook(backend)) {
            RevokeCameraOwnership(backend, ErrorCode::hook_runtime_invalid);
            return false;
        }
    }
    if (!backend.resolved.load(std::memory_order_acquire))
        return false;

    void* camera = nullptr;
    void** vtable = nullptr;
    if (!ResolveCamera(backend, camera, vtable)) {
        RevokeCameraOwnership(backend, ErrorCode::camera_unavailable);
        return false;
    }
    if (reinterpret_cast<std::uintptr_t>(vtable[kUpdateVtableIndex]) != backend.update_target) {
        RevokeCameraOwnership(backend, ErrorCode::hook_target_mismatch);
        return false;
    }

    auto* slot = &vtable[kUpdateVtableIndex];
    // Publish both values before the slot can invoke CameraUpdateHook. The
    // compare-exchange below is the release boundary visible to engine threads.
    backend.hooked_vtable.store(vtable, std::memory_order_release);
    backend.original_update.store(reinterpret_cast<CameraUpdate>(backend.update_target), std::memory_order_release);
    DWORD prior_protection = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &prior_protection)) {
        backend.hooked_vtable.store(nullptr, std::memory_order_release);
        backend.original_update.store(nullptr, std::memory_order_release);
        RevokeCameraOwnership(backend, ErrorCode::hook_install_failed);
        return false;
    }
    const auto previous = InterlockedCompareExchangePointer(
        slot,
        reinterpret_cast<void*>(&CameraUpdateHook),
        reinterpret_cast<void*>(backend.update_target));
    DWORD ignored = 0;
    const auto restored_protection = VirtualProtect(slot, sizeof(void*), prior_protection, &ignored) != FALSE;
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    if (previous != reinterpret_cast<void*>(backend.update_target)) {
        backend.hooked_vtable.store(nullptr, std::memory_order_release);
        backend.original_update.store(nullptr, std::memory_order_release);
        RevokeCameraOwnership(backend, ErrorCode::hook_target_mismatch);
        return false;
    }
    if (!restored_protection) {
        // The hook may already be running, so rollback uses a CAS and retains
        // the published original until both the slot and active-call count are
        // proven safe. If proof fails, hook_installed remains true and unload is
        // refused by UninstallHook.
        DWORD rollback_protection = 0;
        auto rollback_removed = false;
        auto rollback_protection_restored = false;
        if (VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &rollback_protection)) {
            const auto rollback_observed = InterlockedCompareExchangePointer(
                slot,
                reinterpret_cast<void*>(backend.update_target),
                reinterpret_cast<void*>(&CameraUpdateHook));
            // Only our hook or the proven original target are safe outcomes. A
            // foreign value may have retained SMVM as its predecessor.
            rollback_removed = IsSafeHookSlotObservation(
                rollback_observed,
                reinterpret_cast<void*>(&CameraUpdateHook),
                reinterpret_cast<void*>(backend.update_target));
            rollback_protection_restored =
                VirtualProtect(slot, sizeof(void*), prior_protection, &ignored) != FALSE;
            FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
        }
        if (rollback_removed && rollback_protection_restored && WaitForHookDrain(backend)) {
            backend.hooked_vtable.store(nullptr, std::memory_order_release);
            backend.original_update.store(nullptr, std::memory_order_release);
        } else {
            backend.hook_installed.store(true, std::memory_order_release);
        }
        RevokeCameraOwnership(backend, ErrorCode::hook_install_failed);
        return false;
    }

    backend.original_update.store(reinterpret_cast<CameraUpdate>(previous), std::memory_order_release);
    backend.hook_installed.store(true, std::memory_order_release);
    backend.state.store(BackendState::ready, std::memory_order_release);
    backend.error.store(ErrorCode::none, std::memory_order_release);
    return true;
}

[[nodiscard]] bool UninstallHook(Backend& backend) noexcept {
    RevokeCameraOwnership(backend, ErrorCode::none);
    const auto detached = DetachHook(backend);
    if (!detached)
        backend.error.store(ErrorCode::hook_runtime_invalid, std::memory_order_release);
    return detached;
}

[[nodiscard]] bool ReadExact(const HANDLE pipe, void* buffer, const DWORD size) noexcept {
    auto* output = static_cast<std::uint8_t*>(buffer);
    DWORD total = 0;
    while (total < size) {
        DWORD read = 0;
        if (!ReadFile(pipe, output + total, size - total, &read, nullptr) || read == 0)
            return false;
        total += read;
    }
    return true;
}

[[nodiscard]] bool WriteExact(const HANDLE pipe, const void* buffer, const DWORD size) noexcept {
    const auto* input = static_cast<const std::uint8_t*>(buffer);
    DWORD total = 0;
    while (total < size) {
        DWORD written = 0;
        if (!WriteFile(pipe, input + total, size - total, &written, nullptr) || written == 0)
            return false;
        total += written;
    }
    return true;
}

[[nodiscard]] StatusPayload BuildStatus(Backend& backend) noexcept {
    StatusPayload status{};
    status.state = backend.state.load(std::memory_order_acquire);
    status.error = backend.error.load(std::memory_order_acquire);
    status.process_id = GetCurrentProcessId();
    status.accepted_sequence = backend.accepted_sequence.load(std::memory_order_acquire);
    status.applied_sequence = backend.applied_sequence.load(std::memory_order_acquire);
    status.hook_calls = backend.hook_calls.load(std::memory_order_acquire);
    status.replay_tick = backend.replay_tick.load(std::memory_order_acquire);
    std::uint64_t ignored_sequence = 0;
    std::int64_t observed_tick = -1;
    const auto observed_at = backend.camera_observed_milliseconds.load(std::memory_order_acquire);
    if (backend.camera_observed.load(std::memory_order_acquire) &&
        observed_at != 0 && GetTickCount64() - observed_at <= kCameraObservationFreshMilliseconds &&
        backend.observed_frame.Load(status.camera, observed_tick, ignored_sequence)) {
        status.replay_tick = observed_tick;
        status.flags |= status_camera_observed;
    } else {
        static_cast<void>(backend.applied_sample.Load(status.camera, ignored_sequence));
    }
    status.renderer_backend = backend.renderer_backend.load(std::memory_order_acquire);
    status.renderer_error = backend.renderer_error.load(std::memory_order_acquire);
    status.overlay_flags = backend.overlay_flags.load(std::memory_order_acquire);
    status.overlay_frame_microseconds = backend.overlay_frame_microseconds.load(std::memory_order_acquire);
    static_cast<void>(backend.smvm_actions.TryPop(status.action));
    if (backend.resolved.load(std::memory_order_acquire)) status.flags |= status_resolved;
    if (backend.hook_installed.load(std::memory_order_acquire)) status.flags |= status_hook_installed;
    if (backend.pipe_connected.load(std::memory_order_acquire)) status.flags |= status_pipe_connected;
    if (backend.replay_active.load(std::memory_order_acquire) && backend.free_roam.load(std::memory_order_acquire)) status.flags |= status_replay_gate;
    if (backend.override_requested.load(std::memory_order_acquire)) status.flags |= status_override_requested;
    if (backend.override_active.load(std::memory_order_acquire)) status.flags |= status_override_active;
    if (backend.has_sample.load(std::memory_order_acquire)) status.flags |= status_has_sample;
    if (backend.command_line_replay.load(std::memory_order_acquire)) status.flags |= status_command_line_replay;
    if (backend.campath_active.load(std::memory_order_acquire)) status.flags |= status_campath_active;
    if (backend.campath_completed.load(std::memory_order_acquire)) status.flags |= status_campath_completed;
    if (backend.roll_override_active.load(std::memory_order_acquire)) status.flags |= status_roll_override_active;
    if (backend.manual_camera_requested.load(std::memory_order_acquire)) status.flags |= status_manual_camera_requested;
    if (backend.manual_camera_active.load(std::memory_order_acquire)) status.flags |= status_manual_camera_active;
    return status;
}

[[nodiscard]] bool SendStatus(const HANDLE pipe, Backend& backend, const std::uint64_t sequence) noexcept {
    const auto status = BuildStatus(backend);
    const MessageHeader header{kProtocolMagic, kProtocolVersion, MessageType::status,
                               static_cast<std::uint32_t>(sizeof(status)), sequence};
    return WriteExact(pipe, &header, static_cast<DWORD>(sizeof(header))) &&
           WriteExact(pipe, &status, static_cast<DWORD>(sizeof(status)));
}

void ResetConnectionGate(Backend& backend) noexcept {
    backend.pipe_connected.store(false, std::memory_order_release);
    // Editor actions are scoped to an IPC connection, not to camera ownership.
    // Camera-gate revocation is expected outside Free Roam and must not discard
    // actions such as FREE ROAM or PAUSE before the host can consume them.
    backend.smvm_actions.Invalidate();
    backend.replay_active.store(false, std::memory_order_release);
    backend.free_roam.store(false, std::memory_order_release);
    backend.override_requested.store(false, std::memory_order_release);
    backend.override_active.store(false, std::memory_order_release);
    backend.has_sample.store(false, std::memory_order_release);
    backend.roll_override_requested.store(false, std::memory_order_release);
    backend.roll_override_active.store(false, std::memory_order_release);
    backend.manual_camera_requested.store(false, std::memory_order_release);
    backend.manual_camera_active.store(false, std::memory_order_release);
    backend.manual_rebase_from_applied.store(false, std::memory_order_release);
    backend.manual_generation.fetch_add(1, std::memory_order_acq_rel);
    backend.manual_snapshot_cached.store(false, std::memory_order_release);
    ResetSmvmManualInput();
    backend.heartbeat_milliseconds.store(0, std::memory_order_release);
    backend.campath_active.store(false, std::memory_order_release);
    backend.campath_completed.store(false, std::memory_order_release);
    backend.observation_requested.store(false, std::memory_order_release);
    backend.camera_observed.store(false, std::memory_order_release);
    backend.camera_observed_milliseconds.store(0, std::memory_order_release);
    backend.smvm_snapshot_available.store(false, std::memory_order_release);
    backend.smvm_snapshot_milliseconds.store(0, std::memory_order_release);
    backend.editor_campath_available.store(false, std::memory_order_release);
    backend.internal_capture_epoch_token.store(0, std::memory_order_release);
}

[[nodiscard]] bool ValidateSmvmSnapshot(const SmvmSnapshotPayload& snapshot) noexcept {
    return ValidateSmvmSnapshotPayload(snapshot);
}

[[nodiscard]] bool ServePipeConnection(Backend& backend) noexcept {
    const auto name = L"\\\\.\\pipe\\DeadlockMVM.Native." + std::to_wstring(GetCurrentProcessId());
    const auto pipe = CreateNamedPipeW(
        name.c_str(),
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1,
        4096,
        4096,
        0,
        nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
        backend.state.store(BackendState::failed, std::memory_order_release);
        backend.error.store(ErrorCode::protocol_error, std::memory_order_release);
        backend.shutdown.store(true, std::memory_order_release);
        return false;
    }

    const auto connected = ConnectNamedPipe(pipe, nullptr) != FALSE || GetLastError() == ERROR_PIPE_CONNECTED;
    if (!connected) {
        CloseHandle(pipe);
        return false;
    }

    bool hello_received = false;
    backend.pipe_connected.store(true, std::memory_order_release);
    while (!backend.shutdown.load(std::memory_order_acquire)) {
        MessageHeader header{};
        if (!ReadExact(pipe, &header, static_cast<DWORD>(sizeof(header))))
            break;
        if (!ValidateHeader(header) || !ValidatePayloadSize(header.type, header.payload_size)) {
            backend.error.store(ErrorCode::protocol_error, std::memory_order_release);
            break;
        }

        std::array<std::uint8_t, kMaxMessageBytes> payload{};
        if (header.payload_size != 0 && !ReadExact(pipe, payload.data(), header.payload_size))
            break;

        switch (header.type) {
            case MessageType::hello: {
                const auto request = *reinterpret_cast<const HelloPayload*>(payload.data());
                hello_received = request.expected_process_id == GetCurrentProcessId() &&
                                 request.client_build == kProtocolVersion;
                if (!hello_received)
                    backend.error.store(ErrorCode::protocol_error, std::memory_order_release);
                // A successful protocol handshake must not erase a startup
                // signature/host failure that occurred before the pipe opened.
                break;
            }
            case MessageType::heartbeat: {
                if (!hello_received) {
                    backend.error.store(ErrorCode::protocol_error, std::memory_order_release);
                    break;
                }
                const auto request = *reinterpret_cast<const HeartbeatPayload*>(payload.data());
                backend.replay_active.store(request.replay_active != 0, std::memory_order_release);
                backend.free_roam.store(request.free_roam != 0, std::memory_order_release);
                backend.replay_tick.store(request.replay_tick, std::memory_order_release);
                backend.game_tick_offset.store(request.game_tick_offset, std::memory_order_release);
                backend.heartbeat_milliseconds.store(GetTickCount64(), std::memory_order_release);
                if (request.replay_active == 0) {
                    RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
                } else if (request.free_roam == 0) {
                    // Observer/seek transitions revoke camera ownership. Managed
                    // Campath seek recovery explicitly retransfers and rearms.
                    RevokeCameraOwnership(backend, ErrorCode::observer_not_roaming);
                } else if ((backend.observation_requested.load(std::memory_order_acquire) ||
                            backend.override_requested.load(std::memory_order_acquire) ||
                            backend.manual_camera_requested.load(std::memory_order_acquire) ||
                            backend.roll_override_requested.load(std::memory_order_acquire)) &&
                           !InstallHook(backend)) {
                    const auto hook_error = backend.error.load(std::memory_order_acquire);
                    RevokeCameraOwnership(backend, hook_error == ErrorCode::none
                        ? ErrorCode::hook_install_failed
                        : hook_error);
                }
                break;
            }
            case MessageType::enable_manual_camera:
                if (hello_received && backend.replay_active.load(std::memory_order_acquire) &&
                    backend.free_roam.load(std::memory_order_acquire) &&
                    HasFreshSmvmSnapshot(backend) && InstallHook(backend)) {
                    backend.manual_rebase_from_applied.store(false, std::memory_order_release);
                    backend.manual_generation.fetch_add(1, std::memory_order_acq_rel);
                    backend.manual_camera_requested.store(true, std::memory_order_release);
                    backend.manual_camera_active.store(false, std::memory_order_release);
                    backend.observation_requested.store(true, std::memory_order_release);
                    backend.error.store(ErrorCode::none, std::memory_order_release);
                } else {
                    RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
                }
                break;
            case MessageType::disable_manual_camera:
                backend.manual_camera_requested.store(false, std::memory_order_release);
                backend.manual_camera_active.store(false, std::memory_order_release);
                backend.roll_override_requested.store(false, std::memory_order_release);
                backend.roll_override_active.store(false, std::memory_order_release);
                backend.manual_generation.fetch_add(1, std::memory_order_acq_rel);
                ResetSmvmManualInput();
                break;
            case MessageType::set_roll_override: {
                const auto request = *reinterpret_cast<const RollPayload*>(payload.data());
                if (hello_received && ValidateRoll(request.roll) &&
                    backend.replay_active.load(std::memory_order_acquire) &&
                    backend.free_roam.load(std::memory_order_acquire) && InstallHook(backend)) {
                    backend.roll_override_bits.store(
                        std::bit_cast<std::uint64_t>(request.roll), std::memory_order_release);
                    backend.roll_override_requested.store(true, std::memory_order_release);
                    backend.roll_override_active.store(false, std::memory_order_release);
                    backend.error.store(ErrorCode::none, std::memory_order_release);
                } else {
                    RevokeCameraOwnership(backend, ValidateRoll(request.roll)
                        ? ErrorCode::replay_gate_closed
                        : ErrorCode::invalid_sample);
                }
                break;
            }
            case MessageType::set_camera_sample: {
                const auto request = *reinterpret_cast<const CameraSample*>(payload.data());
                if (!hello_received || !ValidateSample(request)) {
                    RevokeCameraOwnership(backend, ErrorCode::invalid_sample);
                    break;
                }
                backend.desired_sample.Store(request, header.sequence);
                backend.accepted_sequence.store(header.sequence, std::memory_order_release);
                backend.has_sample.store(true, std::memory_order_release);
                backend.campath_active.store(false, std::memory_order_release);
                break;
            }
            case MessageType::set_campath: {
                const auto request = *reinterpret_cast<const CampathPayloadHeader*>(payload.data());
                const auto* keyframes = reinterpret_cast<const CampathKeyframe*>(
                    payload.data() + sizeof(CampathPayloadHeader));
                const auto expected_size = sizeof(CampathPayloadHeader) +
                    (static_cast<std::size_t>(request.keyframe_count) * sizeof(CampathKeyframe));
                if (!hello_received || expected_size != header.payload_size ||
                    !ValidateCampath(request, keyframes)) {
                    RevokeCameraOwnership(backend, ErrorCode::invalid_sample);
                    break;
                }
                if (backend.game_tick_offset.load(std::memory_order_acquire) < 0) {
                    RevokeCameraOwnership(backend, ErrorCode::replay_clock_unavailable);
                    break;
                }
                backend.campath.Store(request, keyframes, header.sequence);
                backend.desired_sample.Store(keyframes[0].camera, header.sequence);
                backend.accepted_sequence.store(header.sequence, std::memory_order_release);
                backend.has_sample.store(true, std::memory_order_release);
                backend.campath_active.store(true, std::memory_order_release);
                backend.campath_completed.store(false, std::memory_order_release);
                break;
            }
            case MessageType::enable_override:
                if (hello_received && backend.replay_active.load(std::memory_order_acquire) &&
                    backend.free_roam.load(std::memory_order_acquire) && backend.has_sample.load(std::memory_order_acquire) &&
                    InstallHook(backend)) {
                    backend.override_requested.store(true, std::memory_order_release);
                } else {
                    RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
                }
                break;
            case MessageType::disable_override:
                backend.override_requested.store(false, std::memory_order_release);
                backend.override_active.store(false, std::memory_order_release);
                backend.campath_active.store(false, std::memory_order_release);
                backend.campath_completed.store(false, std::memory_order_release);
                backend.roll_override_requested.store(false, std::memory_order_release);
                backend.roll_override_active.store(false, std::memory_order_release);
                if (backend.manual_camera_requested.load(std::memory_order_acquire)) {
                    backend.manual_rebase_from_applied.store(true, std::memory_order_release);
                    backend.manual_generation.fetch_add(1, std::memory_order_acq_rel);
                }
                break;
            case MessageType::clear_campath:
                backend.campath_active.store(false, std::memory_order_release);
                backend.campath_completed.store(false, std::memory_order_release);
                break;
            case MessageType::prepare_camera_observation:
                if (hello_received && backend.replay_active.load(std::memory_order_acquire) &&
                    backend.free_roam.load(std::memory_order_acquire) &&
                    backend.game_tick_offset.load(std::memory_order_acquire) >= 0 && InstallHook(backend)) {
                    backend.observation_requested.store(true, std::memory_order_release);
                    backend.error.store(ErrorCode::none, std::memory_order_release);
                } else {
                    RevokeCameraOwnership(backend, ErrorCode::replay_gate_closed);
                }
                break;
            case MessageType::update_smvm_snapshot: {
                const auto request = *reinterpret_cast<const SmvmSnapshotPayload*>(payload.data());
                if (!hello_received || !ValidateSmvmSnapshot(request)) {
                    RevokeCameraOwnership(backend, ErrorCode::protocol_error);
                    break;
                }
                backend.smvm_snapshot.Store(request);
                backend.smvm_snapshot_milliseconds.store(GetTickCount64(), std::memory_order_release);
                backend.smvm_snapshot_available.store(true, std::memory_order_release);
                if ((request.flags & smvm_snapshot_internal_enabled) != 0 &&
                    backend.replay_active.load(std::memory_order_acquire) &&
                    backend.free_roam.load(std::memory_order_acquire) && InstallHook(backend)) {
                    backend.observation_requested.store(true, std::memory_order_release);
                    backend.error.store(ErrorCode::none, std::memory_order_release);
                }
                break;
            }
            case MessageType::set_editor_campath: {
                const auto request = *reinterpret_cast<const CampathPayloadHeader*>(payload.data());
                const auto* keyframes = reinterpret_cast<const CampathKeyframe*>(
                    payload.data() + sizeof(CampathPayloadHeader));
                const auto expected_size = sizeof(CampathPayloadHeader) +
                    (static_cast<std::size_t>(request.keyframe_count) * sizeof(CampathKeyframe));
                if (!hello_received || expected_size != header.payload_size ||
                    !ValidateEditorCampath(request, keyframes)) {
                    backend.error.store(ErrorCode::invalid_sample, std::memory_order_release);
                    break;
                }
                backend.editor_campath.Store(request, keyframes, header.sequence);
                backend.editor_campath_available.store(true, std::memory_order_release);
                break;
            }
            case MessageType::clear_editor_campath:
                backend.editor_campath_available.store(false, std::memory_order_release);
                break;
            case MessageType::get_status:
                break;
            case MessageType::shutdown:
                backend.shutdown.store(true, std::memory_order_release);
                break;
            case MessageType::status:
                backend.error.store(ErrorCode::protocol_error, std::memory_order_release);
                break;
        }

        if (!SendStatus(pipe, backend, header.sequence))
            break;
    }

    FlushFileBuffers(pipe);
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    ResetConnectionGate(backend);
    return true;
}

void ServePipe(Backend& backend) noexcept {
    while (!backend.shutdown.load(std::memory_order_acquire)) {
        const auto served = ServePipeConnection(backend);
        if (!served && !backend.shutdown.load(std::memory_order_acquire))
            Sleep(100);
    }
}

} // namespace

[[noreturn]] void RunReplayCameraBackend(const HMODULE self_module) noexcept {
    Backend backend{};
    backend.self = self_module;
    LARGE_INTEGER performance_frequency{};
    if (QueryPerformanceFrequency(&performance_frequency))
        backend.performance_frequency = performance_frequency.QuadPart;
    g_backend = &backend;

    if (!ValidateHostProcess(backend)) {
        backend.state.store(BackendState::failed, std::memory_order_release);
    } else {
        // The launcher loads this component immediately after process creation
        // so the renderer hook can be installed before Deadlock publishes its
        // real swapchain. Camera signatures depend on client.dll, which appears
        // later; keep the two readiness chains independent and bounded.
        const SmvmOverlayCallbacks callbacks{
            &backend,
            &OverlayReadSnapshot,
            &OverlayReadEditorPath,
            &OverlayQueueAction,
            &OverlayRequestCameraCapture,
            &OverlayPublishStatus,
        };
        if (!StartSmvmOverlay(self_module, callbacks)) {
            backend.renderer_backend.store(SmvmRendererBackend::none, std::memory_order_release);
            backend.renderer_error.store(SmvmRendererError::hook_install_failed, std::memory_order_release);
        }
        if (!WaitForClientModule(backend) || !ResolveStaticTargets(backend))
            backend.state.store(BackendState::failed, std::memory_order_release);
    }

    ServePipe(backend);
    const auto overlay_stopped = StopSmvmOverlay();
    const auto camera_detached = UninstallHook(backend);
    if (!overlay_stopped || !camera_detached) {
        // CameraUpdateHook can still be reachable or in flight. Keep both this
        // module and the stack-backed Backend resident if either hook cannot be
        // proven restored and drained. A process restart is the safe recovery
        // boundary rather than unloading reachable executable code.
        backend.state.store(BackendState::failed, std::memory_order_release);
        backend.error.store(ErrorCode::hook_runtime_invalid, std::memory_order_release);
        for (;;)
            Sleep(1000);
    }
    g_backend = nullptr;
    FreeLibraryAndExitThread(self_module, 0);
}

} // namespace deadlock_mvm
