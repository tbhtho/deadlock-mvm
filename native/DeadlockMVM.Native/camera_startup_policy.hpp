#pragma once

#include "protocol.hpp"

namespace deadlock_mvm {

[[nodiscard]] constexpr bool ShouldRetryCameraStartup(
    const bool resolved,
    const ErrorCode error,
    const bool handshake_complete,
    const bool client_loaded) noexcept {
    return !resolved && error == ErrorCode::client_module_missing &&
        handshake_complete && client_loaded;
}

} // namespace deadlock_mvm
