#pragma once

#include "protocol.hpp"

namespace deadlock_mvm {

// The trooper health-particle hook remains installed for the native module's
// lifetime, but it suppresses only while SMVM owns replay presentation. F9,
// host loss, shutdown, and ordinary Deadlock presentation always pass through
// to the game's original predicate.
[[nodiscard]] constexpr bool ShouldSuppressTrooperHealthParticle(
    const DeadlockUiMode mode,
    const bool stop_requested) noexcept {
    return !stop_requested && mode != DeadlockUiMode::deadlock_ui;
}

} // namespace deadlock_mvm
