#pragma once
#include "look_settings.hpp"
#include <cstdint>

namespace deadlock_mvm {
// Chroma bypass applies to the physical preview mode and the active output
// transaction, including the interval while those asynchronous states settle.
inline bool ShouldApplyBeautyLook(const LookSettings& look, bool replay_active,
    bool chroma_preview, bool chroma_take) noexcept {
    return replay_active && !chroma_preview && !chroma_take &&
        ValidateLookSettings(look) && !IsNeutralLook(look);
}

struct LookTakeState final {
    LookSettings settings{};
    std::uint64_t session{};
    bool locked{};
    const LookSettings& Observe(const LookSettings& intended, std::uint64_t generation,
        bool transaction) noexcept {
        if (!transaction || !locked || session != generation) settings = intended;
        session = generation;
        locked = transaction;
        return settings;
    }
};
} // namespace deadlock_mvm
