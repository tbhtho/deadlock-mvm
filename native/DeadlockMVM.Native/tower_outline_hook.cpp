#include "tower_outline_hook.hpp"

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

constexpr std::size_t kOutlinePolicyVtableIndex = 300;
constexpr std::string_view kOutlinePolicyPatternA =
    "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 30 48 8B 01 49 8B F8 "
    "48 8B F2 48 8B D9 FF 90 B0 04 00 00 84 C0 74 7E";
constexpr std::string_view kOutlinePolicyPatternB =
    "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 30 48 8B 05 ?? ?? ?? ?? "
    "49 8B F8 48 8B F2 48 8B D9 80 78 58 00 0F 85 ?? ?? ?? ?? 48 8B 01 "
    "FF 90 B0 04 00 00 84 C0 74 7B";
constexpr auto kCallbackDrainTimeout = std::chrono::milliseconds(1000);

enum class OutlinePolicyKind : std::uint8_t {
    standard,
    boss_disabled,
};

struct TowerClassSpec final {
    std::string_view rtti_name;
    OutlinePolicyKind policy;
};

constexpr std::array<TowerClassSpec, 5> kTowerClasses{{
    {".?AVC_NPC_Boss_Tier3@@", OutlinePolicyKind::standard},
    {".?AVC_NPC_TrooperBoss@@", OutlinePolicyKind::standard},
    {".?AVC_NPC_BarrackBoss@@", OutlinePolicyKind::standard},
    {".?AVC_NPC_Boss_Tier2@@", OutlinePolicyKind::boss_disabled},
    {".?AVC_NPC_Boss_Tier2_Sidelanes@@", OutlinePolicyKind::boss_disabled},
}};

using OutlinePolicyFunction = std::int64_t(__fastcall*)(
    void* npc,
    std::int32_t viewer_team,
    std::uint32_t* outline_range);

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

struct ResolvedTowerHooks final {
    OutlinePolicyFunction standard{};
    OutlinePolicyFunction boss_disabled{};
    std::array<void**, kTowerClasses.size()> slots{};
};

struct TowerOutlineHookState final {
    SRWLOCK lock = SRWLOCK_INIT;
    std::atomic<OutlinePolicyFunction> standard_original{nullptr};
    std::atomic<OutlinePolicyFunction> boss_disabled_original{nullptr};
    std::atomic<TowerOutlineSuppressionPredicate> should_suppress{nullptr};
    std::atomic<std::uint32_t> active_calls{0};
    std::atomic<bool> reachable{false};
    std::atomic<bool> installed{false};
    std::atomic<bool> suppression_observed{false};
    std::array<VtableHookSlotState, kTowerClasses.size()> slots{};
};

TowerOutlineHookState g_tower_outline_hook{};

class ActiveCallGuard final {
public:
    ActiveCallGuard() noexcept {
        g_tower_outline_hook.active_calls.fetch_add(1, std::memory_order_acq_rel);
    }

    ~ActiveCallGuard() noexcept {
        g_tower_outline_hook.active_calls.fetch_sub(1, std::memory_order_acq_rel);
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
            (kOutlinePolicyVtableIndex + 2) * sizeof(void*)))
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

[[nodiscard]] OutlinePolicyFunction ResolveUniqueFunction(
    const ModuleView& view,
    const std::string_view pattern_text) noexcept {
    const auto pattern = ParsePattern(pattern_text);
    if (!pattern)
        return nullptr;
    const auto matches = FindPattern(view.text.begin, view.text.size, *pattern, 2);
    return matches.size() == 1
        ? reinterpret_cast<OutlinePolicyFunction>(view.text.begin + matches.front())
        : nullptr;
}

[[nodiscard]] bool ResolveTowerHooks(
    const HMODULE client_module,
    ResolvedTowerHooks& resolved) noexcept {
    resolved = {};
    ModuleView view{};
    if (!InspectModule(client_module, view))
        return false;
    resolved.standard = ResolveUniqueFunction(view, kOutlinePolicyPatternA);
    resolved.boss_disabled = ResolveUniqueFunction(view, kOutlinePolicyPatternB);
    if (resolved.standard == nullptr || resolved.boss_disabled == nullptr ||
        resolved.standard == resolved.boss_disabled)
        return false;

    std::array<std::size_t, kTowerClasses.size()> candidate_counts{};
    for (std::size_t offset = 0;
         offset + sizeof(void*) <= view.rdata.size;
         offset += alignof(void*)) {
        void* candidate{};
        std::memcpy(&candidate, view.rdata.begin + offset, sizeof(candidate));
        if (candidate != reinterpret_cast<void*>(resolved.standard) &&
            candidate != reinterpret_cast<void*>(resolved.boss_disabled))
            continue;
        if (offset < (kOutlinePolicyVtableIndex + 1) * sizeof(void*))
            continue;
        auto** const vtable = reinterpret_cast<void**>(
            view.rdata.begin + offset - kOutlinePolicyVtableIndex * sizeof(void*));
        for (std::size_t class_index = 0; class_index < kTowerClasses.size(); ++class_index) {
            const auto& spec = kTowerClasses[class_index];
            const auto expected = spec.policy == OutlinePolicyKind::standard
                ? resolved.standard
                : resolved.boss_disabled;
            if (candidate != reinterpret_cast<void*>(expected) ||
                !IsPrimaryVtableForClass(view, vtable, spec.rtti_name))
                continue;
            resolved.slots[class_index] = &vtable[kOutlinePolicyVtableIndex];
            ++candidate_counts[class_index];
        }
    }
    for (std::size_t index = 0; index < resolved.slots.size(); ++index) {
        if (candidate_counts[index] != 1 || resolved.slots[index] == nullptr)
            return false;
        for (std::size_t prior = 0; prior < index; ++prior) {
            if (resolved.slots[index] == resolved.slots[prior])
                return false;
        }
    }
    return true;
}

[[nodiscard]] std::int64_t CallOriginal(
    const OutlinePolicyFunction original,
    void* const npc,
    const std::int32_t viewer_team,
    std::uint32_t* const outline_range) noexcept {
    if (original == nullptr)
        return 0;
    __try {
        return original(npc, viewer_team, outline_range);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

[[nodiscard]] bool ShouldSuppress() noexcept {
    const auto predicate =
        g_tower_outline_hook.should_suppress.load(std::memory_order_acquire);
    return predicate != nullptr && predicate();
}

void ObserveSuppression() noexcept {
    g_tower_outline_hook.suppression_observed.store(true, std::memory_order_release);
}

[[nodiscard]] std::int64_t __fastcall StandardOutlineHook(
    void* const npc,
    const std::int32_t viewer_team,
    std::uint32_t* const outline_range) noexcept {
    ActiveCallGuard guard;
    if (ShouldSuppress()) {
        ObserveSuppression();
        return 0;
    }
    return CallOriginal(
        g_tower_outline_hook.standard_original.load(std::memory_order_acquire),
        npc,
        viewer_team,
        outline_range);
}

[[nodiscard]] std::int64_t __fastcall BossDisabledOutlineHook(
    void* const npc,
    const std::int32_t viewer_team,
    std::uint32_t* const outline_range) noexcept {
    ActiveCallGuard guard;
    if (ShouldSuppress()) {
        ObserveSuppression();
        return 0;
    }
    return CallOriginal(
        g_tower_outline_hook.boss_disabled_original.load(std::memory_order_acquire),
        npc,
        viewer_team,
        outline_range);
}

[[nodiscard]] OutlinePolicyFunction OriginalForClass(
    const std::size_t index) noexcept {
    return kTowerClasses[index].policy == OutlinePolicyKind::standard
        ? g_tower_outline_hook.standard_original.load(std::memory_order_acquire)
        : g_tower_outline_hook.boss_disabled_original.load(std::memory_order_acquire);
}

[[nodiscard]] void* HookForClass(const std::size_t index) noexcept {
    return kTowerClasses[index].policy == OutlinePolicyKind::standard
        ? reinterpret_cast<void*>(&StandardOutlineHook)
        : reinterpret_cast<void*>(&BossDisabledOutlineHook);
}

[[nodiscard]] bool PublishHook(const std::size_t index) noexcept {
    return PublishVtableHook(g_tower_outline_hook.slots[index]);
}

[[nodiscard]] bool RestoreHook(const std::size_t index) noexcept {
    return RestoreVtableHook(g_tower_outline_hook.slots[index]);
}

[[nodiscard]] bool WaitForCallsToDrain() noexcept {
    const auto deadline = std::chrono::steady_clock::now() + kCallbackDrainTimeout;
    std::uint32_t quiet_samples = 0;
    while (quiet_samples < 3) {
        if (std::chrono::steady_clock::now() >= deadline)
            return false;
        if (g_tower_outline_hook.active_calls.load(std::memory_order_acquire) == 0)
            ++quiet_samples;
        else
            quiet_samples = 0;
        Sleep(10);
    }
    return true;
}

void ClearState() noexcept {
    g_tower_outline_hook.slots = {};
    g_tower_outline_hook.should_suppress.store(nullptr, std::memory_order_release);
    g_tower_outline_hook.standard_original.store(nullptr, std::memory_order_release);
    g_tower_outline_hook.boss_disabled_original.store(nullptr, std::memory_order_release);
    g_tower_outline_hook.suppression_observed.store(false, std::memory_order_release);
    g_tower_outline_hook.installed.store(false, std::memory_order_release);
}

[[nodiscard]] bool RecoverPublishedHooksLocked() noexcept {
    g_tower_outline_hook.installed.store(false, std::memory_order_release);
    auto restored_all = true;
    for (std::size_t index = g_tower_outline_hook.slots.size(); index-- > 0;) {
        if (!NeedsVtableHookRecovery(g_tower_outline_hook.slots[index]))
            continue;
        restored_all = RestoreHook(index) && restored_all;
    }
    if (!restored_all || !WaitForCallsToDrain())
        return false;
    g_tower_outline_hook.reachable.store(false, std::memory_order_release);
    ClearState();
    return true;
}

} // namespace

bool CanResolveTowerOutlineHooks(const HMODULE client_module) noexcept {
    ResolvedTowerHooks resolved{};
    return ResolveTowerHooks(client_module, resolved);
}

bool InstallTowerOutlineHooks(
    const HMODULE client_module,
    const TowerOutlineSuppressionPredicate should_suppress) noexcept {
    if (client_module == nullptr || should_suppress == nullptr)
        return false;
    AcquireSRWLockExclusive(&g_tower_outline_hook.lock);
    if (g_tower_outline_hook.reachable.load(std::memory_order_acquire)) {
        const auto same_predicate =
            g_tower_outline_hook.should_suppress.load(std::memory_order_acquire) == should_suppress;
        if (g_tower_outline_hook.installed.load(std::memory_order_acquire)) {
            ReleaseSRWLockExclusive(&g_tower_outline_hook.lock);
            return same_predicate;
        }
        if (!RecoverPublishedHooksLocked()) {
            ReleaseSRWLockExclusive(&g_tower_outline_hook.lock);
            return false;
        }
    }

    ResolvedTowerHooks resolved{};
    if (!ResolveTowerHooks(client_module, resolved)) {
        ReleaseSRWLockExclusive(&g_tower_outline_hook.lock);
        return false;
    }
    g_tower_outline_hook.standard_original.store(resolved.standard, std::memory_order_release);
    g_tower_outline_hook.boss_disabled_original.store(
        resolved.boss_disabled, std::memory_order_release);
    g_tower_outline_hook.should_suppress.store(should_suppress, std::memory_order_release);
    g_tower_outline_hook.suppression_observed.store(false, std::memory_order_release);
    for (std::size_t index = 0; index < resolved.slots.size(); ++index) {
        g_tower_outline_hook.slots[index] = {
            resolved.slots[index],
            reinterpret_cast<void*>(OriginalForClass(index)),
            HookForClass(index),
        };
    }

    for (std::size_t index = 0; index < resolved.slots.size(); ++index) {
        if (PublishHook(index)) {
            g_tower_outline_hook.reachable.store(true, std::memory_order_release);
            continue;
        }
        auto rollback_complete = true;
        for (std::size_t rollback = index + 1; rollback-- > 0;) {
            if (!NeedsVtableHookRecovery(g_tower_outline_hook.slots[rollback]))
                continue;
            const auto restored = RestoreHook(rollback);
            rollback_complete = rollback_complete && restored;
        }
        if (rollback_complete && WaitForCallsToDrain()) {
            g_tower_outline_hook.reachable.store(false, std::memory_order_release);
            ClearState();
        } else {
            g_tower_outline_hook.reachable.store(true, std::memory_order_release);
        }
        ReleaseSRWLockExclusive(&g_tower_outline_hook.lock);
        return false;
    }
    g_tower_outline_hook.reachable.store(true, std::memory_order_release);
    g_tower_outline_hook.installed.store(true, std::memory_order_release);
    ReleaseSRWLockExclusive(&g_tower_outline_hook.lock);
    return true;
}

bool AreTowerOutlineHooksInstalled() noexcept {
    return g_tower_outline_hook.installed.load(std::memory_order_acquire);
}

bool HasTowerOutlineSuppressionBeenObserved() noexcept {
    return g_tower_outline_hook.suppression_observed.load(std::memory_order_acquire);
}

bool RemoveTowerOutlineHooks() noexcept {
    AcquireSRWLockExclusive(&g_tower_outline_hook.lock);
    if (!g_tower_outline_hook.reachable.load(std::memory_order_acquire)) {
        ReleaseSRWLockExclusive(&g_tower_outline_hook.lock);
        return true;
    }
    const auto recovered = RecoverPublishedHooksLocked();
    ReleaseSRWLockExclusive(&g_tower_outline_hook.lock);
    return recovered;
}

} // namespace deadlock_mvm
