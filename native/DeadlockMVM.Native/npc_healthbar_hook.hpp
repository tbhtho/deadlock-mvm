#pragma once

#include <Windows.h>

namespace deadlock_mvm {

using TrooperHealthParticleSuppressionPredicate = bool(*)() noexcept;

// Read-only current-build validation used by the native acceptance gate. It
// performs the same unique pattern + RTTI resolution as installation without
// changing the mapped client image.
[[nodiscard]] bool CanResolveTrooperHealthbarHook(HMODULE client_module) noexcept;

// Installs current-build, class-specific hooks on lane and neutral creep
// health_particle_active vtable predicates. Derived neutral classes copy this
// slot instead of inheriting the base table, so all three exact class tables
// must resolve before any slot is changed. Resolution is fail-closed: an
// updated or ambiguous client.dll is left untouched.
[[nodiscard]] bool InstallTrooperHealthbarHook(
    HMODULE client_module,
    TrooperHealthParticleSuppressionPredicate should_suppress) noexcept;

// Runtime evidence exported through the overlay status flags. Installed means
// every exact class slot is still owned; observed means a live creep query was
// actually suppressed, not merely that an isolated image accepted the patch.
[[nodiscard]] bool IsTrooperHealthbarHookInstalled() noexcept;
[[nodiscard]] bool HasTrooperHealthbarSuppressionBeenObserved() noexcept;

// Restores the exact original vtable target and waits for in-flight predicate
// calls to drain. False means the native module must remain resident.
[[nodiscard]] bool RemoveTrooperHealthbarHook() noexcept;

} // namespace deadlock_mvm
