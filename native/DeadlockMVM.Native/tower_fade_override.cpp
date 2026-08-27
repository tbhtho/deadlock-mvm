#include "tower_fade_override.hpp"

#include "pattern_scan.hpp"
#include "tower_fade_value_owner.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string_view>

namespace deadlock_mvm {
namespace {

constexpr std::string_view kOtherNearOpacityName =
    "citadel_camera_fade_other_near_opacity";
constexpr auto kTowerFadeOpaqueBits = std::bit_cast<std::uint32_t>(1.0F);
// At the unique `lea rdx, name` xref, the ConVar object instruction begins
// nine bytes later after `xor eax,eax`.
constexpr std::size_t kConVarObjectInstructionOffset = 9;
constexpr std::size_t kConVarObjectDisplacementOffset = 3;
constexpr std::size_t kConVarObjectInstructionLength = 7;
constexpr std::size_t kConVarRuntimePointerOffset = 8;
constexpr std::size_t kConVarValueOffset = 0x58;

struct SectionView final {
    std::uint8_t* begin{};
    std::size_t size{};
};

struct ModuleView final {
    std::uint8_t* base{};
    std::size_t size{};
    SectionView text{};
    SectionView rdata{};

    [[nodiscard]] bool Contains(
        const std::uintptr_t address,
        const std::size_t length = 1) const noexcept {
        if (base == nullptr || length > size)
            return false;
        const auto start = reinterpret_cast<std::uintptr_t>(base);
        return address >= start && address - start <= size - length;
    }
};

struct TowerFadeOverrideState final {
    SRWLOCK lock = SRWLOCK_INIT;
    std::atomic<std::uintptr_t> convar_object{0};
    std::atomic<TowerFadeOverridePredicate> should_force_opaque{nullptr};
    std::atomic<bool> installed{false};
    std::atomic<bool> enforcement_observed{false};
    TowerFadeValueOwner value_owner{};
};

TowerFadeOverrideState g_tower_fade_override{};

[[nodiscard]] bool InspectModule(const HMODULE module, ModuleView& view) noexcept {
    view = {};
    if (module == nullptr)
        return false;
    auto* const base = reinterpret_cast<std::uint8_t*>(module);
    __try {
        const auto* const dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
            return false;
        const auto* const nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE ||
            nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
            return false;
        view.base = base;
        view.size = nt->OptionalHeader.SizeOfImage;
        const auto* const sections = IMAGE_FIRST_SECTION(nt);
        for (std::uint16_t index = 0; index < nt->FileHeader.NumberOfSections; ++index) {
            const auto& section = sections[index];
            if (std::memcmp(section.Name, ".text", 5) != 0)
            {
                if (std::memcmp(section.Name, ".rdata", 6) != 0)
                    continue;
                view.rdata.begin = base + section.VirtualAddress;
                view.rdata.size = std::max<std::size_t>(
                    section.Misc.VirtualSize, section.SizeOfRawData);
            }
            else {
                view.text.begin = base + section.VirtualAddress;
                view.text.size = std::max<std::size_t>(
                    section.Misc.VirtualSize, section.SizeOfRawData);
            }
        }
        return view.text.begin != nullptr && view.rdata.begin != nullptr &&
               view.Contains(
                   reinterpret_cast<std::uintptr_t>(view.text.begin), view.text.size) &&
               view.Contains(
                   reinterpret_cast<std::uintptr_t>(view.rdata.begin), view.rdata.size);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        view = {};
        return false;
    }
}

[[nodiscard]] bool ResolveConVarObject(
    const HMODULE client_module,
    std::uintptr_t& convar_object) noexcept {
    convar_object = 0;
    ModuleView view{};
    if (!InspectModule(client_module, view) ||
        view.rdata.size < kOtherNearOpacityName.size() + 1)
        return false;

    std::uintptr_t name_xref = 0;
    std::size_t xref_count = 0;
    constexpr std::array<std::uint8_t, 14> kRegistrationHead{
        0x4C, 0x8B, 0xDC, 0x48, 0x81, 0xEC, 0xB8,
        0x00, 0x00, 0x00, 0xF3, 0x0F, 0x10, 0x15,
    };
    constexpr std::array<std::uint8_t, 5> kAfterNameXref{
        0x33, 0xC0, 0x48, 0x8D, 0x0D,
    };
    for (std::size_t offset = 18; offset + 16 <= view.text.size; ++offset) {
        auto* const candidate = view.text.begin + offset;
        if (candidate[0] != 0x48 || candidate[1] != 0x8D || candidate[2] != 0x15)
            continue;
        const auto candidate_address = reinterpret_cast<std::uintptr_t>(candidate);
        const auto target = ResolveRipRelative(candidate_address, 3, 7);
        const auto rdata_start = reinterpret_cast<std::uintptr_t>(view.rdata.begin);
        const auto target_in_rdata = target && *target >= rdata_start &&
            *target - rdata_start <=
                view.rdata.size - (kOtherNearOpacityName.size() + 1);
        if (!target_in_rdata ||
            std::memcmp(
                reinterpret_cast<const void*>(*target),
                kOtherNearOpacityName.data(),
                kOtherNearOpacityName.size()) != 0 ||
            *reinterpret_cast<const char*>(*target + kOtherNearOpacityName.size()) != '\0' ||
            std::memcmp(candidate - 18, kRegistrationHead.data(),
                        kRegistrationHead.size()) != 0 ||
            std::memcmp(candidate + 7, kAfterNameXref.data(),
                        kAfterNameXref.size()) != 0)
            continue;
        name_xref = candidate_address;
        ++xref_count;
        if (xref_count > 1)
            return false;
    }
    if (xref_count != 1 || name_xref == 0)
        return false;
    const auto instruction = name_xref + kConVarObjectInstructionOffset;
    const auto resolved = ResolveRipRelative(
        instruction,
        kConVarObjectDisplacementOffset,
        kConVarObjectInstructionLength);
    if (!resolved || !view.Contains(*resolved, kConVarRuntimePointerOffset + sizeof(void*)))
        return false;
    convar_object = *resolved;
    return true;
}

[[nodiscard]] bool IsWritableFloat(float* const value) noexcept {
    if (value == nullptr ||
        (reinterpret_cast<std::uintptr_t>(value) % alignof(std::uint32_t)) != 0)
        return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(value, &memory, sizeof(memory)) != sizeof(memory) ||
        memory.State != MEM_COMMIT || (memory.Protect & PAGE_GUARD) != 0 ||
        memory.RegionSize < sizeof(std::uint32_t))
        return false;
    const auto protection = memory.Protect & 0xFF;
    if (protection != PAGE_READWRITE && protection != PAGE_WRITECOPY &&
        protection != PAGE_EXECUTE_READWRITE && protection != PAGE_EXECUTE_WRITECOPY)
        return false;
    const auto start = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    const auto address = reinterpret_cast<std::uintptr_t>(value);
    return address >= start && address - start <= memory.RegionSize - sizeof(std::uint32_t);
}

[[nodiscard]] float* ResolveCurrentValue(const std::uintptr_t object) noexcept {
    if (object == 0)
        return nullptr;
    __try {
        const auto runtime = *reinterpret_cast<const std::uintptr_t*>(
            object + kConVarRuntimePointerOffset);
        if (runtime == 0)
            return nullptr;
        auto* const value = reinterpret_cast<float*>(runtime + kConVarValueOffset);
        return IsWritableFloat(value) ? value : nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

[[nodiscard]] bool TryAtomicRead(
    float* const value,
    std::uint32_t& bits) noexcept {
    __try {
        bits = static_cast<std::uint32_t>(InterlockedCompareExchange(
            reinterpret_cast<volatile LONG*>(value), 0, 0));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] bool TryAtomicWrite(
    float* const value,
    const std::uint32_t bits) noexcept {
    __try {
        static_cast<void>(InterlockedExchange(
            reinterpret_cast<volatile LONG*>(value), static_cast<LONG>(bits)));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

constexpr TowerFadeValueMemoryOps kTowerFadeMemoryOps{
    &IsWritableFloat,
    &TryAtomicRead,
    &TryAtomicWrite,
};

[[nodiscard]] bool RestoreLocked() noexcept {
    auto& state = g_tower_fade_override;
    if (!state.value_owner.original_captured)
        return true;
    auto* const current_value = ResolveCurrentValue(
        state.convar_object.load(std::memory_order_acquire));
    return RestoreTowerFadeValue(
        state.value_owner, current_value, kTowerFadeMemoryOps);
}

void ClearStateLocked() noexcept {
    auto& state = g_tower_fade_override;
    ClearTowerFadeValueOwner(state.value_owner);
    state.enforcement_observed.store(false, std::memory_order_release);
    state.should_force_opaque.store(nullptr, std::memory_order_release);
    state.convar_object.store(0, std::memory_order_release);
    state.installed.store(false, std::memory_order_release);
}

} // namespace

bool CanResolveTowerFadeOverride(const HMODULE client_module) noexcept {
    std::uintptr_t object = 0;
    return ResolveConVarObject(client_module, object) && object != 0;
}

bool InstallTowerFadeOverride(
    const HMODULE client_module,
    const TowerFadeOverridePredicate should_force_opaque) noexcept {
    if (client_module == nullptr || should_force_opaque == nullptr)
        return false;
    auto& state = g_tower_fade_override;
    AcquireSRWLockExclusive(&state.lock);
    if (state.installed.load(std::memory_order_acquire)) {
        const auto same_predicate = state.should_force_opaque.load(
            std::memory_order_acquire) == should_force_opaque;
        ReleaseSRWLockExclusive(&state.lock);
        return same_predicate;
    }
    std::uintptr_t object = 0;
    if (!ResolveConVarObject(client_module, object) || object == 0) {
        ReleaseSRWLockExclusive(&state.lock);
        return false;
    }
    state.convar_object.store(object, std::memory_order_release);
    state.should_force_opaque.store(should_force_opaque, std::memory_order_release);
    state.enforcement_observed.store(false, std::memory_order_release);
    state.installed.store(true, std::memory_order_release);
    ReleaseSRWLockExclusive(&state.lock);
    return true;
}

bool PumpTowerFadeOverride() noexcept {
    auto& state = g_tower_fade_override;
    AcquireSRWLockExclusive(&state.lock);
    if (!state.installed.load(std::memory_order_acquire)) {
        ReleaseSRWLockExclusive(&state.lock);
        return false;
    }
    const auto predicate = state.should_force_opaque.load(std::memory_order_acquire);
    const auto force = predicate != nullptr && predicate();
    if (!force) {
        const auto restored = RestoreLocked();
        ReleaseSRWLockExclusive(&state.lock);
        return restored;
    }

    auto* const value = ResolveCurrentValue(
        state.convar_object.load(std::memory_order_acquire));
    if (value == nullptr) {
        ReleaseSRWLockExclusive(&state.lock);
        return false;
    }
    if (!state.value_owner.original_captured ||
        state.value_owner.active_value != value) {
        if (state.value_owner.original_captured &&
            !RestoreTowerFadeValue(
                state.value_owner, value, kTowerFadeMemoryOps)) {
            ReleaseSRWLockExclusive(&state.lock);
            return false;
        }
        if (!CaptureTowerFadeValue(
                state.value_owner, value, kTowerFadeMemoryOps)) {
            ReleaseSRWLockExclusive(&state.lock);
            return false;
        }
    }
    if (!TryAtomicWrite(value, kTowerFadeOpaqueBits)) {
        ReleaseSRWLockExclusive(&state.lock);
        return false;
    }
    state.enforcement_observed.store(true, std::memory_order_release);
    ReleaseSRWLockExclusive(&state.lock);
    return true;
}

bool IsTowerFadeOverrideInstalled() noexcept {
    return g_tower_fade_override.installed.load(std::memory_order_acquire);
}

bool HasTowerFadeOverrideBeenEnforced() noexcept {
    return g_tower_fade_override.enforcement_observed.load(std::memory_order_acquire);
}

bool RemoveTowerFadeOverride() noexcept {
    auto& state = g_tower_fade_override;
    AcquireSRWLockExclusive(&state.lock);
    if (!state.installed.load(std::memory_order_acquire)) {
        ReleaseSRWLockExclusive(&state.lock);
        return true;
    }
    state.should_force_opaque.store(nullptr, std::memory_order_release);
    const auto restored = RestoreLocked();
    if (restored)
        ClearStateLocked();
    ReleaseSRWLockExclusive(&state.lock);
    return restored;
}

} // namespace deadlock_mvm
