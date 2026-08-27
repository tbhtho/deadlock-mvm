#pragma once

#include <cstdint>

namespace deadlock_mvm {

struct TowerFadeValueMemoryOps final {
    bool (*is_writable)(float*) noexcept{};
    bool (*try_read)(float*, std::uint32_t&) noexcept{};
    bool (*try_write)(float*, std::uint32_t) noexcept{};
};

struct TowerFadeValueOwner final {
    float* active_value{};
    std::uint32_t original_bits{};
    bool original_captured{};
};

inline void ClearTowerFadeValueOwner(TowerFadeValueOwner& owner) noexcept {
    owner.active_value = nullptr;
    owner.original_bits = 0;
    owner.original_captured = false;
}

[[nodiscard]] inline bool CaptureTowerFadeValue(
    TowerFadeValueOwner& owner,
    float* const current_value,
    const TowerFadeValueMemoryOps& memory) noexcept {
    if (current_value == nullptr || memory.is_writable == nullptr ||
        memory.try_read == nullptr || !memory.is_writable(current_value))
        return false;
    std::uint32_t original_bits = 0;
    if (!memory.try_read(current_value, original_bits))
        return false;
    owner.active_value = current_value;
    owner.original_bits = original_bits;
    owner.original_captured = true;
    return true;
}

// The ConVar runtime storage can be replaced while Deadlock is running. Only
// restore through the pointer that the registration object still publishes.
// A different resolved pointer proves the old storage is detached, so retire
// its record without touching memory that Deadlock may already have reused.
[[nodiscard]] inline bool RestoreTowerFadeValue(
    TowerFadeValueOwner& owner,
    float* const current_value,
    const TowerFadeValueMemoryOps& memory) noexcept {
    if (!owner.original_captured)
        return true;
    if (current_value == nullptr)
        return false;
    if (current_value != owner.active_value) {
        ClearTowerFadeValueOwner(owner);
        return true;
    }
    if (memory.is_writable == nullptr || memory.try_write == nullptr ||
        !memory.is_writable(current_value))
        return false;
    if (!memory.try_write(current_value, owner.original_bits))
        return false;
    ClearTowerFadeValueOwner(owner);
    return true;
}

} // namespace deadlock_mvm
