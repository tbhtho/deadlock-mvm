#pragma once
#include <dxgi.h>
namespace deadlock_mvm {
// Occlusion, invalid-call resize contention, and ordinary success are not
// device removal. Only these terminal Present results retire GPU ownership.
[[nodiscard]] constexpr bool IsTerminalD3D11PresentFailure(HRESULT result) noexcept {
    return result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET;
}
} // namespace deadlock_mvm
