#include "npc_healthbar_hook.hpp"

#include "hook_lifecycle.hpp"
#include "pattern_scan.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string_view>

namespace deadlock_mvm {
namespace {

constexpr std::size_t kHealthParticleActiveVtableIndex = 302;
constexpr std::string_view kHealthParticleActivePattern =
    "48 8B 81 90 03 00 00 F7 80 C8 09 00 00 FF FF FF 3F 0F 95 C0 C3";
constexpr std::array<std::string_view, 3> kCreepRttiNames{
    ".?AVC_NPC_Trooper@@",
    ".?AVC_NPC_TrooperNeutral@@",
    ".?AVC_NPC_TrooperNeutralNodeMover@@",
};
constexpr auto kCallbackDrainTimeout = std::chrono::milliseconds(1000);

using HealthParticleActiveFunction = bool(__fastcall*)(void* npc);

struct SectionView final {
    std::uint8_t* begin{};
    std::size_t size{};

    [[nodiscard]] bool Contains(
        const std::uintptr_t address,
        const std::size_t length = 1) const noexcept {
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

struct RttiCompleteObjectLocator final {
    std::uint32_t signature{};
    std::uint32_t offset{};
    std::uint32_t constructor_displacement{};
    std::uint32_t type_descriptor_rva{};
    std::uint32_t class_descriptor_rva{};
    std::uint32_t self_rva{};
};

static_assert(sizeof(RttiCompleteObjectLocator) == 24);

struct HealthbarHookState final {
    SRWLOCK lock = SRWLOCK_INIT;
    std::atomic<HealthParticleActiveFunction> original{nullptr};
    std::atomic<TrooperHealthParticleSuppressionPredicate> should_suppress{nullptr};
    std::atomic<std::uint32_t> active_calls{0};
    std::atomic<bool> reachable{false};
    std::atomic<bool> installed{false};
    std::atomic<bool> suppression_observed{false};
    std::array<VtableHookSlotState, kCreepRttiNames.size()> slots{};
};

HealthbarHookState g_healthbar_hook{};

class ActiveCallGuard final {
public:
    ActiveCallGuard() noexcept {
        g_healthbar_hook.active_calls.fetch_add(1, std::memory_order_acq_rel);
    }

    ~ActiveCallGuard() noexcept {
        g_healthbar_hook.active_calls.fetch_sub(1, std::memory_order_acq_rel);
    }

    ActiveCallGuard(const ActiveCallGuard&) = delete;
    ActiveCallGuard& operator=(const ActiveCallGuard&) = delete;
};

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
            const auto section_size = std::max<std::size_t>(
                section.Misc.VirtualSize, section.SizeOfRawData);
            if (std::memcmp(section.Name, ".text", 5) == 0)
                view.text = {base + section.VirtualAddress, section_size};
            else if (std::memcmp(section.Name, ".rdata", 6) == 0)
                view.rdata = {base + section.VirtualAddress, section_size};
        }
        return view.text.begin != nullptr && view.rdata.begin != nullptr &&
               view.Contains(reinterpret_cast<std::uintptr_t>(view.text.begin), view.text.size) &&
               view.Contains(reinterpret_cast<std::uintptr_t>(view.rdata.begin), view.rdata.size);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        view = {};
        return false;
    }
}

[[nodiscard]] bool IsPrimaryVtableForClass(
    const ModuleView& view,
    void** const vtable,
    const std::string_view expected_name) noexcept {
    if (vtable == nullptr ||
        !view.rdata.Contains(
            reinterpret_cast<std::uintptr_t>(vtable) - sizeof(void*),
            (kHealthParticleActiveVtableIndex + 2) * sizeof(void*)))
        return false;

    __try {
        const auto locator_address = reinterpret_cast<std::uintptr_t>(vtable[-1]);
        if (!view.Contains(locator_address, sizeof(RttiCompleteObjectLocator)))
            return false;
        const auto* const locator =
            reinterpret_cast<const RttiCompleteObjectLocator*>(locator_address);
        if (locator->signature != 1 || locator->offset != 0 ||
            reinterpret_cast<std::uintptr_t>(view.base) + locator->self_rva != locator_address)
            return false;

        const auto type_address = reinterpret_cast<std::uintptr_t>(view.base) +
            locator->type_descriptor_rva;
        constexpr std::size_t kTypeDescriptorHeaderSize = sizeof(void*) * 2;
        if (!view.Contains(
                type_address,
                kTypeDescriptorHeaderSize + expected_name.size() + 1))
            return false;
        const auto* const name = reinterpret_cast<const char*>(
            type_address + kTypeDescriptorHeaderSize);
        return std::memcmp(name, expected_name.data(), expected_name.size()) == 0 &&
               name[expected_name.size()] == '\0';
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] bool ResolveCreepHealthbarSlots(
    const HMODULE client_module,
    std::array<void**, kCreepRttiNames.size()>& slots,
    HealthParticleActiveFunction& original) noexcept {
    slots = {};
    original = nullptr;
    ModuleView view{};
    if (!InspectModule(client_module, view))
        return false;
    const auto pattern = ParsePattern(kHealthParticleActivePattern);
    if (!pattern)
        return false;
    const auto targets = FindPattern(view.text.begin, view.text.size, *pattern, 2);
    if (targets.size() != 1)
        return false;

    const auto target = reinterpret_cast<HealthParticleActiveFunction>(
        view.text.begin + targets.front());
    std::array<std::size_t, kCreepRttiNames.size()> candidate_counts{};
    for (std::size_t offset = 0;
         offset + sizeof(void*) <= view.rdata.size;
         offset += alignof(void*)) {
        void* candidate{};
        std::memcpy(&candidate, view.rdata.begin + offset, sizeof(candidate));
        if (candidate != reinterpret_cast<void*>(target) ||
            offset < (kHealthParticleActiveVtableIndex + 1) * sizeof(void*))
            continue;
        auto** const vtable = reinterpret_cast<void**>(
            view.rdata.begin + offset - kHealthParticleActiveVtableIndex * sizeof(void*));
        for (std::size_t class_index = 0; class_index < kCreepRttiNames.size(); ++class_index) {
            if (!IsPrimaryVtableForClass(view, vtable, kCreepRttiNames[class_index]))
                continue;
            slots[class_index] = &vtable[kHealthParticleActiveVtableIndex];
            ++candidate_counts[class_index];
        }
    }
    for (std::size_t index = 0; index < slots.size(); ++index) {
        if (candidate_counts[index] != 1 || slots[index] == nullptr)
            return false;
        for (std::size_t prior = 0; prior < index; ++prior) {
            if (slots[index] == slots[prior])
                return false;
        }
    }
    original = target;
    return true;
}

[[nodiscard]] bool CallOriginalHealthParticleActive(
    HealthParticleActiveFunction original,
    void* npc) noexcept;

[[nodiscard]] bool __fastcall CreepHealthParticleActiveHook(void* npc) noexcept {
    ActiveCallGuard guard;
    const auto original = g_healthbar_hook.original.load(std::memory_order_acquire);
    const auto should_suppress =
        g_healthbar_hook.should_suppress.load(std::memory_order_acquire);
    if (should_suppress != nullptr && should_suppress()) {
        g_healthbar_hook.suppression_observed.store(true, std::memory_order_release);
        return false;
    }
    if (original == nullptr)
        return false;
    return CallOriginalHealthParticleActive(original, npc);
}

[[nodiscard]] bool CallOriginalHealthParticleActive(
    const HealthParticleActiveFunction original,
    void* const npc) noexcept {
    __try {
        return original(npc);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] bool WaitForCallsToDrain() noexcept {
    const auto deadline = std::chrono::steady_clock::now() + kCallbackDrainTimeout;
    std::uint32_t quiet_samples = 0;
    while (quiet_samples < 3) {
        if (std::chrono::steady_clock::now() >= deadline)
            return false;
        if (g_healthbar_hook.active_calls.load(std::memory_order_acquire) == 0)
            ++quiet_samples;
        else
            quiet_samples = 0;
        Sleep(10);
    }
    return true;
}

void ClearState() noexcept {
    g_healthbar_hook.slots = {};
    g_healthbar_hook.should_suppress.store(nullptr, std::memory_order_release);
    g_healthbar_hook.original.store(nullptr, std::memory_order_release);
    g_healthbar_hook.suppression_observed.store(false, std::memory_order_release);
    g_healthbar_hook.installed.store(false, std::memory_order_release);
}

[[nodiscard]] bool RecoverPublishedHooksLocked() noexcept {
    g_healthbar_hook.installed.store(false, std::memory_order_release);
    auto restored_all = true;
    for (std::size_t index = g_healthbar_hook.slots.size(); index-- > 0;) {
        if (!NeedsVtableHookRecovery(g_healthbar_hook.slots[index]))
            continue;
        restored_all = RestoreVtableHook(g_healthbar_hook.slots[index]) && restored_all;
    }
    if (!restored_all || !WaitForCallsToDrain())
        return false;
    g_healthbar_hook.reachable.store(false, std::memory_order_release);
    ClearState();
    return true;
}

} // namespace

bool CanResolveTrooperHealthbarHook(const HMODULE client_module) noexcept {
    std::array<void**, kCreepRttiNames.size()> slots{};
    HealthParticleActiveFunction original = nullptr;
    return ResolveCreepHealthbarSlots(client_module, slots, original) && original != nullptr;
}

bool InstallTrooperHealthbarHook(
    const HMODULE client_module,
    const TrooperHealthParticleSuppressionPredicate should_suppress) noexcept {
    if (client_module == nullptr || should_suppress == nullptr)
        return false;
    AcquireSRWLockExclusive(&g_healthbar_hook.lock);
    if (g_healthbar_hook.reachable.load(std::memory_order_acquire)) {
        const auto same_predicate =
            g_healthbar_hook.should_suppress.load(std::memory_order_acquire) == should_suppress;
        if (g_healthbar_hook.installed.load(std::memory_order_acquire)) {
            ReleaseSRWLockExclusive(&g_healthbar_hook.lock);
            return same_predicate;
        }
        if (!RecoverPublishedHooksLocked()) {
            ReleaseSRWLockExclusive(&g_healthbar_hook.lock);
            return false;
        }
    }

    std::array<void**, kCreepRttiNames.size()> slots{};
    HealthParticleActiveFunction original = nullptr;
    if (!ResolveCreepHealthbarSlots(client_module, slots, original) || original == nullptr) {
        ReleaseSRWLockExclusive(&g_healthbar_hook.lock);
        return false;
    }
    for (std::size_t index = 0; index < slots.size(); ++index) {
        g_healthbar_hook.slots[index] = {
            slots[index],
            reinterpret_cast<void*>(original),
            reinterpret_cast<void*>(&CreepHealthParticleActiveHook),
        };
    }
    g_healthbar_hook.original.store(original, std::memory_order_release);
    g_healthbar_hook.should_suppress.store(should_suppress, std::memory_order_release);
    g_healthbar_hook.suppression_observed.store(false, std::memory_order_release);

    for (std::size_t index = 0; index < slots.size(); ++index) {
        if (PublishVtableHook(g_healthbar_hook.slots[index])) {
            g_healthbar_hook.reachable.store(true, std::memory_order_release);
            continue;
        }
        auto rollback_complete = true;
        for (std::size_t rollback = index + 1; rollback-- > 0;) {
            if (!NeedsVtableHookRecovery(g_healthbar_hook.slots[rollback]))
                continue;
            const auto restored = RestoreVtableHook(g_healthbar_hook.slots[rollback]);
            rollback_complete = rollback_complete && restored;
        }
        if (rollback_complete && WaitForCallsToDrain()) {
            g_healthbar_hook.reachable.store(false, std::memory_order_release);
            ClearState();
        } else {
            g_healthbar_hook.reachable.store(true, std::memory_order_release);
        }
        ReleaseSRWLockExclusive(&g_healthbar_hook.lock);
        return false;
    }
    g_healthbar_hook.reachable.store(true, std::memory_order_release);
    g_healthbar_hook.installed.store(true, std::memory_order_release);
    ReleaseSRWLockExclusive(&g_healthbar_hook.lock);
    return true;
}

bool IsTrooperHealthbarHookInstalled() noexcept {
    return g_healthbar_hook.installed.load(std::memory_order_acquire);
}

bool HasTrooperHealthbarSuppressionBeenObserved() noexcept {
    return g_healthbar_hook.suppression_observed.load(std::memory_order_acquire);
}

bool RemoveTrooperHealthbarHook() noexcept {
    AcquireSRWLockExclusive(&g_healthbar_hook.lock);
    if (!g_healthbar_hook.reachable.load(std::memory_order_acquire)) {
        ReleaseSRWLockExclusive(&g_healthbar_hook.lock);
        return true;
    }
    const auto recovered = RecoverPublishedHooksLocked();
    ReleaseSRWLockExclusive(&g_healthbar_hook.lock);
    return recovered;
}

} // namespace deadlock_mvm
