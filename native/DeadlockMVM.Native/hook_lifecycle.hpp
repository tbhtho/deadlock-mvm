#pragma once

#include <Windows.h>

#include <cstddef>

namespace deadlock_mvm {

// A hook slot is safe to withdraw only when the compare/exchange observed the
// hook being removed or the exact original target already restored. Any third
// value may be a later chain whose predecessor still points into this module.
[[nodiscard]] inline bool IsSafeHookSlotObservation(
    const void* observed,
    const void* hook,
    const void* original) noexcept {
    return observed == hook || observed == original;
}

// Complete ownership record for one vtable slot. A failed protection restore
// is retained as explicit cleanup debt even when the pointer CAS itself was
// successful (or never published our hook).
struct VtableHookSlotState final {
    void** address{};
    void* original{};
    void* hook{};
    bool published{};
    bool protection_debt{};
    DWORD original_protection{};
};

struct VtableHookMemoryOps final {
    bool (*protect)(void* address, std::size_t size, DWORD protection, DWORD& prior) noexcept;
    void* (*exchange)(void* volatile* address, void* replacement, void* expected) noexcept;
    void (*flush)() noexcept;
};

[[nodiscard]] inline bool ProtectVtableMemory(
    void* const address,
    const std::size_t size,
    const DWORD protection,
    DWORD& prior) noexcept {
    return VirtualProtect(address, size, protection, &prior) != FALSE;
}

[[nodiscard]] inline void* ExchangeVtablePointer(
    void* volatile* const address,
    void* const replacement,
    void* const expected) noexcept {
    return InterlockedCompareExchangePointer(address, replacement, expected);
}

inline void FlushVtableWrites() noexcept {
    FlushProcessWriteBuffers();
}

inline constexpr VtableHookMemoryOps kSystemVtableHookMemoryOps{
    &ProtectVtableMemory,
    &ExchangeVtablePointer,
    &FlushVtableWrites,
};

[[nodiscard]] inline bool NeedsVtableHookRecovery(
    const VtableHookSlotState& state) noexcept {
    return state.published || state.protection_debt;
}

[[nodiscard]] inline bool PublishVtableHook(
    VtableHookSlotState& state,
    const VtableHookMemoryOps& memory = kSystemVtableHookMemoryOps) noexcept {
    if (state.address == nullptr || state.original == nullptr || state.hook == nullptr ||
        NeedsVtableHookRecovery(state) || memory.protect == nullptr ||
        memory.exchange == nullptr || memory.flush == nullptr)
        return false;

    DWORD prior_protection = 0;
    if (!memory.protect(
            state.address, sizeof(void*), PAGE_READWRITE, prior_protection))
        return false;

    const auto observed = memory.exchange(
        reinterpret_cast<void* volatile*>(state.address),
        state.hook,
        state.original);
    memory.flush();

    DWORD ignored = 0;
    const auto protection_restored =
        memory.protect(
            state.address, sizeof(void*), prior_protection, ignored);
    const auto published_now = observed == state.original;
    // Observing our own hook is not a successful fresh publication, but it is
    // still a reachable code pointer that must be retained for rollback.
    state.published = published_now || observed == state.hook;
    if (!protection_restored) {
        state.protection_debt = true;
        state.original_protection = prior_protection;
    }
    return published_now && protection_restored;
}

// Removes this module's exact hook, restores any retained page-protection
// debt, and clears ownership only after both outcomes are proven safe. A
// foreign chain keeps the publication alive so unload must be refused.
[[nodiscard]] inline bool RestoreVtableHook(
    VtableHookSlotState& state,
    const VtableHookMemoryOps& memory = kSystemVtableHookMemoryOps) noexcept {
    if (!NeedsVtableHookRecovery(state))
        return true;
    if (state.address == nullptr || state.original == nullptr || state.hook == nullptr ||
        memory.protect == nullptr || memory.exchange == nullptr || memory.flush == nullptr)
        return false;

    DWORD current_protection = 0;
    if (!memory.protect(
            state.address, sizeof(void*), PAGE_READWRITE, current_protection))
        return false;

    auto slot_is_safe = true;
    if (state.published) {
        const auto observed = memory.exchange(
            reinterpret_cast<void* volatile*>(state.address),
            state.original,
            state.hook);
        slot_is_safe = IsSafeHookSlotObservation(
            observed, state.hook, state.original);
        memory.flush();
    }

    const auto target_protection = state.protection_debt
        ? state.original_protection
        : current_protection;
    DWORD ignored = 0;
    const auto protection_restored =
        memory.protect(
            state.address, sizeof(void*), target_protection, ignored);
    if (!protection_restored && !state.protection_debt) {
        state.protection_debt = true;
        state.original_protection = current_protection;
    }
    if (protection_restored) {
        state.protection_debt = false;
        state.original_protection = 0;
    }
    if (!slot_is_safe || !protection_restored)
        return false;

    state.published = false;
    return true;
}

} // namespace deadlock_mvm
