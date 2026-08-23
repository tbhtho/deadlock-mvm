#pragma once

#include <cstddef>
#include <cstdint>

namespace deadlock_mvm {

// Source 2's public InputSystemVersion001 EnableInput implementation is a
// single typed state write in the current ABI. Decode the state offset from
// that validated method instead of hardcoding an object-layout offset.
[[nodiscard]] inline bool TryDecodeInputEnabledStateOffset(
    const std::uint8_t* code,
    const std::size_t size,
    std::uint8_t& offset) noexcept {
    if (code == nullptr || size < 4 || code[0] != 0x88 || code[1] != 0x51 ||
        code[3] != 0xC3 || code[2] < sizeof(void*)) {
        offset = 0;
        return false;
    }
    offset = code[2];
    return true;
}

} // namespace deadlock_mvm
