#pragma once

#include <atomic>
#include <cstdint>

namespace deadlock_mvm {

// Coalesces one physical input delivered through both Raw Input and legacy
// window messages. The first channel owns the press; each channel still owns
// its matching release so neither Deadlock nor SMVM receives a stuck button.
class SmvmInputRoute final {
public:
    [[nodiscard]] bool Claim(const std::uint8_t route) noexcept {
        return routes_.fetch_or(route, std::memory_order_acq_rel) == 0;
    }

    [[nodiscard]] bool Release(const std::uint8_t route) noexcept {
        const auto previous = routes_.fetch_and(
            static_cast<std::uint8_t>(~route), std::memory_order_acq_rel);
        return (previous & route) != 0;
    }

    [[nodiscard]] bool HasAny() const noexcept {
        return routes_.load(std::memory_order_acquire) != 0;
    }

    void Reset() noexcept {
        routes_.store(0, std::memory_order_release);
    }

private:
    std::atomic<std::uint8_t> routes_{0};
};

} // namespace deadlock_mvm
