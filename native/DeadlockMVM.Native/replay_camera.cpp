#include "replay_camera.hpp"

#include "pattern_scan.hpp"
#include "protocol.hpp"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cwctype>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
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

class AtomicCampath final {
public:
    void Store(const LinearCampathPayload& path, const std::uint64_t sequence) noexcept {
        generation_.fetch_add(1, std::memory_order_acq_rel);
        from_tick_.store(path.from.demo_tick, std::memory_order_relaxed);
        to_tick_.store(path.to.demo_tick, std::memory_order_relaxed);
        from_.Store(path.from.camera, sequence);
        to_.Store(path.to.camera, sequence);
        sequence_.store(sequence, std::memory_order_relaxed);
        generation_.fetch_add(1, std::memory_order_release);
    }

    [[nodiscard]] bool Load(LinearCampathPayload& path, std::uint64_t& sequence) const noexcept {
        for (int attempt = 0; attempt < 4; ++attempt) {
            const auto before = generation_.load(std::memory_order_acquire);
            if ((before & 1u) != 0)
                continue;
            std::uint64_t ignored_from = 0;
            std::uint64_t ignored_to = 0;
            LinearCampathPayload candidate{};
            candidate.from.demo_tick = from_tick_.load(std::memory_order_relaxed);
            candidate.to.demo_tick = to_tick_.load(std::memory_order_relaxed);
            if (!from_.Load(candidate.from.camera, ignored_from) || !to_.Load(candidate.to.camera, ignored_to))
                continue;
            const auto candidate_sequence = sequence_.load(std::memory_order_relaxed);
            const auto after = generation_.load(std::memory_order_acquire);
            if (before == after && (after & 1u) == 0) {
                path = candidate;
                sequence = candidate_sequence;
                return true;
            }
        }
        return false;
    }

private:
    std::atomic<std::uint64_t> generation_{0};
    std::atomic<std::int64_t> from_tick_{0};
    std::atomic<std::int64_t> to_tick_{0};
    std::atomic<std::uint64_t> sequence_{0};
    AtomicSample from_{};
    AtomicSample to_{};
};

struct Backend final {
    HMODULE self{};
    ModuleView client{};
    std::uintptr_t manager{};
    std::uintptr_t controllers{};
    std::uintptr_t entity_system_global{};
    std::uintptr_t update_target{};
    std::uintptr_t global_vars_slot{};
    void** hooked_vtable{};
    CameraUpdate original_update{};

    std::atomic<BackendState> state{BackendState::loading};
    std::atomic<ErrorCode> error{ErrorCode::none};
    std::atomic<bool> resolved{false};
    std::atomic<bool> hook_installed{false};
    std::atomic<bool> pipe_connected{false};
    std::atomic<bool> replay_active{false};
    std::atomic<bool> free_roam{false};
    std::atomic<bool> override_requested{false};
    std::atomic<bool> override_active{false};
    std::atomic<bool> has_sample{false};
    std::atomic<bool> campath_active{false};
    std::atomic<bool> command_line_replay{false};
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
    AtomicCampath campath{};
};

Backend* g_backend = nullptr;

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

[[nodiscard]] double Lerp(const double from, const double to, const double amount) noexcept {
    return from + (to - from) * amount;
}

[[nodiscard]] double ShortestAngleDelta(const double from, const double to) noexcept {
    auto delta = std::fmod(to - from, 360.0);
    if (delta > 180.0) delta -= 360.0;
    if (delta < -180.0) delta += 360.0;
    return delta;
}

[[nodiscard]] double NormalizeAngle(double value) noexcept {
    value = std::fmod(value, 360.0);
    if (value > 180.0) value -= 360.0;
    if (value < -180.0) value += 360.0;
    return value;
}

[[nodiscard]] CameraSample EvaluateLinearCampath(
    const LinearCampathPayload& path, const std::int64_t demo_tick) noexcept {
    const auto span = static_cast<double>(path.to.demo_tick - path.from.demo_tick);
    const auto amount = std::clamp(static_cast<double>(demo_tick - path.from.demo_tick) / span, 0.0, 1.0);
    return CameraSample{
        Lerp(path.from.camera.x, path.to.camera.x, amount),
        Lerp(path.from.camera.y, path.to.camera.y, amount),
        Lerp(path.from.camera.z, path.to.camera.z, amount),
        Lerp(path.from.camera.pitch, path.to.camera.pitch, amount),
        NormalizeAngle(path.from.camera.yaw + ShortestAngleDelta(path.from.camera.yaw, path.to.camera.yaw) * amount),
        NormalizeAngle(path.from.camera.roll + ShortestAngleDelta(path.from.camera.roll, path.to.camera.roll) * amount),
        Lerp(path.from.camera.fov, path.to.camera.fov, amount),
    };
}

[[nodiscard]] bool CanApply(Backend& backend, void* camera) noexcept {
    if (!backend.override_requested.load(std::memory_order_acquire) ||
        !backend.has_sample.load(std::memory_order_acquire) ||
        !backend.pipe_connected.load(std::memory_order_acquire) ||
        !backend.replay_active.load(std::memory_order_acquire) ||
        !backend.free_roam.load(std::memory_order_acquire) ||
        !backend.command_line_replay.load(std::memory_order_acquire)) {
        backend.override_active.store(false, std::memory_order_release);
        backend.error.store(ErrorCode::replay_gate_closed, std::memory_order_release);
        return false;
    }

    const auto heartbeat = backend.heartbeat_milliseconds.load(std::memory_order_acquire);
    if (heartbeat == 0 || GetTickCount64() - heartbeat > kHeartbeatTimeoutMilliseconds) {
        backend.override_active.store(false, std::memory_order_release);
        backend.error.store(ErrorCode::heartbeat_stale, std::memory_order_release);
        return false;
    }

    void* current_camera = nullptr;
    void** current_vtable = nullptr;
    if (!ResolveCamera(backend, current_camera, current_vtable) || current_camera != camera ||
        current_vtable != backend.hooked_vtable || !IsObserverRoaming(backend)) {
        backend.override_active.store(false, std::memory_order_release);
        backend.error.store(ErrorCode::observer_not_roaming, std::memory_order_release);
        return false;
    }
    return true;
}

void* __fastcall CameraUpdateHook(void* camera) noexcept {
    auto* backend = g_backend;
    if (backend == nullptr)
        return nullptr;

    backend->active_hooks.fetch_add(1, std::memory_order_acq_rel);
    const auto original = backend->original_update;
    void* result = original != nullptr ? original(camera) : nullptr;
    backend->hook_calls.fetch_add(1, std::memory_order_relaxed);

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

    if (CanApply(*backend, camera)) {
        CameraSample sample{};
        std::uint64_t sequence = 0;
        auto loaded = false;
        if (backend->campath_active.load(std::memory_order_acquire)) {
            LinearCampathPayload path{};
            loaded = backend->campath.Load(path, sequence) && ValidateCampath(path);
            if (loaded) {
                const auto demo_tick = backend->replay_tick.load(std::memory_order_acquire);
                if (demo_tick < 0) {
                    loaded = false;
                    backend->override_active.store(false, std::memory_order_release);
                    backend->error.store(ErrorCode::replay_clock_unavailable, std::memory_order_release);
                } else {
                    sample = EvaluateLinearCampath(path, demo_tick);
                }
            }
        } else {
            loaded = backend->desired_sample.Load(sample, sequence);
        }
        if (loaded && ValidateSample(sample)) {
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
                backend->applied_sample.Store(sample, sequence);
                backend->applied_sequence.store(sequence, std::memory_order_release);
                backend->override_active.store(true, std::memory_order_release);
                backend->error.store(ErrorCode::none, std::memory_order_release);
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                backend->override_active.store(false, std::memory_order_release);
                backend->override_requested.store(false, std::memory_order_release);
                backend->error.store(ErrorCode::hook_runtime_invalid, std::memory_order_release);
            }
        }
    }

    backend->active_hooks.fetch_sub(1, std::memory_order_acq_rel);
    return result;
}

[[nodiscard]] bool InstallHook(Backend& backend) noexcept {
    if (backend.hook_installed.load(std::memory_order_acquire))
        return true;
    if (!backend.resolved.load(std::memory_order_acquire))
        return false;

    void* camera = nullptr;
    void** vtable = nullptr;
    if (!ResolveCamera(backend, camera, vtable)) {
        backend.error.store(ErrorCode::camera_unavailable, std::memory_order_release);
        return false;
    }
    if (reinterpret_cast<std::uintptr_t>(vtable[kUpdateVtableIndex]) != backend.update_target) {
        backend.error.store(ErrorCode::hook_target_mismatch, std::memory_order_release);
        return false;
    }

    auto* slot = &vtable[kUpdateVtableIndex];
    DWORD prior_protection = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &prior_protection)) {
        backend.error.store(ErrorCode::hook_install_failed, std::memory_order_release);
        return false;
    }
    const auto previous = InterlockedExchangePointer(slot, reinterpret_cast<void*>(&CameraUpdateHook));
    DWORD ignored = 0;
    const auto restored_protection = VirtualProtect(slot, sizeof(void*), prior_protection, &ignored) != FALSE;
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    if (previous != reinterpret_cast<void*>(backend.update_target) || !restored_protection) {
        DWORD writable = 0;
        if (VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &writable)) {
            InterlockedExchangePointer(slot, previous);
            VirtualProtect(slot, sizeof(void*), writable, &ignored);
        }
        backend.error.store(ErrorCode::hook_install_failed, std::memory_order_release);
        return false;
    }

    backend.hooked_vtable = vtable;
    backend.original_update = reinterpret_cast<CameraUpdate>(previous);
    backend.hook_installed.store(true, std::memory_order_release);
    backend.state.store(BackendState::ready, std::memory_order_release);
    backend.error.store(ErrorCode::none, std::memory_order_release);
    return true;
}

void UninstallHook(Backend& backend) noexcept {
    backend.override_requested.store(false, std::memory_order_release);
    backend.override_active.store(false, std::memory_order_release);
    if (!backend.hook_installed.exchange(false, std::memory_order_acq_rel) ||
        backend.hooked_vtable == nullptr || backend.original_update == nullptr)
        return;

    auto* slot = &backend.hooked_vtable[kUpdateVtableIndex];
    DWORD prior_protection = 0;
    if (VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &prior_protection)) {
        InterlockedCompareExchangePointer(
            slot,
            reinterpret_cast<void*>(backend.original_update),
            reinterpret_cast<void*>(&CameraUpdateHook));
        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void*), prior_protection, &ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    }

    int stable_checks = 0;
    while (stable_checks < 10) {
        if (backend.active_hooks.load(std::memory_order_acquire) == 0)
            ++stable_checks;
        else
            stable_checks = 0;
        Sleep(10);
    }
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
    const auto has_applied_sample = backend.applied_sample.Load(status.camera, ignored_sequence);
    static_cast<void>(has_applied_sample);
    if (backend.resolved.load(std::memory_order_acquire)) status.flags |= status_resolved;
    if (backend.hook_installed.load(std::memory_order_acquire)) status.flags |= status_hook_installed;
    if (backend.pipe_connected.load(std::memory_order_acquire)) status.flags |= status_pipe_connected;
    if (backend.replay_active.load(std::memory_order_acquire) && backend.free_roam.load(std::memory_order_acquire)) status.flags |= status_replay_gate;
    if (backend.override_requested.load(std::memory_order_acquire)) status.flags |= status_override_requested;
    if (backend.override_active.load(std::memory_order_acquire)) status.flags |= status_override_active;
    if (backend.has_sample.load(std::memory_order_acquire)) status.flags |= status_has_sample;
    if (backend.command_line_replay.load(std::memory_order_acquire)) status.flags |= status_command_line_replay;
    if (backend.campath_active.load(std::memory_order_acquire)) status.flags |= status_campath_active;
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
    backend.replay_active.store(false, std::memory_order_release);
    backend.free_roam.store(false, std::memory_order_release);
    backend.override_requested.store(false, std::memory_order_release);
    backend.override_active.store(false, std::memory_order_release);
    backend.heartbeat_milliseconds.store(0, std::memory_order_release);
    backend.campath_active.store(false, std::memory_order_release);
}

void ServePipe(Backend& backend) noexcept {
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
        return;
    }

    const auto connected = ConnectNamedPipe(pipe, nullptr) != FALSE || GetLastError() == ERROR_PIPE_CONNECTED;
    if (!connected) {
        CloseHandle(pipe);
        backend.shutdown.store(true, std::memory_order_release);
        return;
    }

    bool hello_received = false;
    backend.pipe_connected.store(true, std::memory_order_release);
    while (!backend.shutdown.load(std::memory_order_acquire)) {
        MessageHeader header{};
        if (!ReadExact(pipe, &header, static_cast<DWORD>(sizeof(header))))
            break;
        if (!ValidateHeader(header) || header.payload_size != ExpectedPayloadSize(header.type)) {
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
                if (request.replay_active == 0 || request.free_roam == 0) {
                    backend.override_requested.store(false, std::memory_order_release);
                    backend.override_active.store(false, std::memory_order_release);
                    backend.campath_active.store(false, std::memory_order_release);
                }
                break;
            }
            case MessageType::set_camera_sample: {
                const auto request = *reinterpret_cast<const CameraSample*>(payload.data());
                if (!hello_received || !ValidateSample(request)) {
                    backend.error.store(ErrorCode::invalid_sample, std::memory_order_release);
                    break;
                }
                backend.desired_sample.Store(request, header.sequence);
                backend.accepted_sequence.store(header.sequence, std::memory_order_release);
                backend.has_sample.store(true, std::memory_order_release);
                backend.campath_active.store(false, std::memory_order_release);
                break;
            }
            case MessageType::set_linear_campath: {
                const auto request = *reinterpret_cast<const LinearCampathPayload*>(payload.data());
                if (!hello_received || !ValidateCampath(request)) {
                    backend.error.store(ErrorCode::invalid_sample, std::memory_order_release);
                    break;
                }
                if (backend.game_tick_offset.load(std::memory_order_acquire) < 0) {
                    backend.error.store(ErrorCode::replay_clock_unavailable, std::memory_order_release);
                    break;
                }
                backend.campath.Store(request, header.sequence);
                backend.desired_sample.Store(request.from.camera, header.sequence);
                backend.accepted_sequence.store(header.sequence, std::memory_order_release);
                backend.has_sample.store(true, std::memory_order_release);
                backend.campath_active.store(true, std::memory_order_release);
                break;
            }
            case MessageType::enable_override:
                if (hello_received && backend.replay_active.load(std::memory_order_acquire) &&
                    backend.free_roam.load(std::memory_order_acquire) && backend.has_sample.load(std::memory_order_acquire) &&
                    InstallHook(backend)) {
                    backend.override_requested.store(true, std::memory_order_release);
                } else {
                    backend.error.store(ErrorCode::replay_gate_closed, std::memory_order_release);
                }
                break;
            case MessageType::disable_override:
                backend.override_requested.store(false, std::memory_order_release);
                backend.override_active.store(false, std::memory_order_release);
                backend.campath_active.store(false, std::memory_order_release);
                break;
            case MessageType::clear_campath:
                backend.campath_active.store(false, std::memory_order_release);
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
    backend.shutdown.store(true, std::memory_order_release);
}

} // namespace

[[noreturn]] void RunReplayCameraBackend(const HMODULE self_module) noexcept {
    Backend backend{};
    backend.self = self_module;
    g_backend = &backend;

    if (!ValidateHostProcess(backend) || !ResolveStaticTargets(backend))
        backend.state.store(BackendState::failed, std::memory_order_release);

    ServePipe(backend);
    UninstallHook(backend);
    g_backend = nullptr;
    FreeLibraryAndExitThread(self_module, 0);
}

} // namespace deadlock_mvm
