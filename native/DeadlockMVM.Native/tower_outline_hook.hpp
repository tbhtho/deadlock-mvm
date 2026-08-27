#pragma once

#include <Windows.h>

namespace deadlock_mvm {

using TowerOutlineSuppressionPredicate = bool(*)() noexcept;

// Read-only current-build validation. All five exact tower/objective RTTI
// tables and both outline-policy signatures must resolve uniquely.
[[nodiscard]] bool CanResolveTowerOutlineHooks(HMODULE client_module) noexcept;

// Transactionally replaces only the tower/objective outline-policy slots.
// Other NPC outlines and every original policy outside SMVM pass through.
[[nodiscard]] bool InstallTowerOutlineHooks(
    HMODULE client_module,
    TowerOutlineSuppressionPredicate should_suppress) noexcept;

// Runtime evidence exported through the overlay status flags. Installed means
// every exact class slot is still owned; observed means a live tower query was
// actually suppressed.
[[nodiscard]] bool AreTowerOutlineHooksInstalled() noexcept;
[[nodiscard]] bool HasTowerOutlineSuppressionBeenObserved() noexcept;

// Restores every published class slot and drains in-flight callbacks. False
// means the native module must remain resident.
[[nodiscard]] bool RemoveTowerOutlineHooks() noexcept;

} // namespace deadlock_mvm
