#pragma once

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

} // namespace deadlock_mvm
