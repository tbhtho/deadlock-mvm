#pragma once

#include "protocol.hpp"

namespace deadlock_mvm {

// Tower outline hooks stay installed for the native module lifetime, but the
// game's original class policy is restored logically whenever Deadlock owns
// presentation or shutdown has started.
[[nodiscard]] constexpr bool ShouldSuppressTowerOutline(
    const DeadlockUiMode mode,
    const bool stop_requested) noexcept {
    return !stop_requested && mode != DeadlockUiMode::deadlock_ui;
}

} // namespace deadlock_mvm
