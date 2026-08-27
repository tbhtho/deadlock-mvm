#pragma once

#include <Windows.h>

namespace deadlock_mvm {

using TowerFadeOverridePredicate = bool(*)() noexcept;

// Resolves the current-client citadel_camera_fade_other_near_opacity object
// from its unique registration code without changing the mapped image.
[[nodiscard]] bool CanResolveTowerFadeOverride(HMODULE client_module) noexcept;

// Owns only the tower/objective near-opacity ConVar while SMVM presentation is
// active. The original float is captured before the first force and restored
// on F9, host loss, shutdown, or removal.
[[nodiscard]] bool InstallTowerFadeOverride(
    HMODULE client_module,
    TowerFadeOverridePredicate should_force_opaque) noexcept;

// Bounded, allocation-free reconciliation. Safe from the renderer and the
// cold installer pump; returns false only when the current ConVar storage is
// temporarily unavailable.
[[nodiscard]] bool PumpTowerFadeOverride() noexcept;

[[nodiscard]] bool IsTowerFadeOverrideInstalled() noexcept;
[[nodiscard]] bool HasTowerFadeOverrideBeenEnforced() noexcept;

// Restores the exact pre-SMVM float before dropping ownership.
[[nodiscard]] bool RemoveTowerFadeOverride() noexcept;

} // namespace deadlock_mvm
