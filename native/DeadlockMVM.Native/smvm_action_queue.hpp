#pragma once

#include "protocol.hpp"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace deadlock_mvm {

// A bounded non-blocking handoff from the renderer/window callbacks to the IPC
// thread. Connection resets advance an epoch without waiting on either callback;
// entries from an earlier epoch are discarded instead of crossing a reconnect.
class SmvmActionQueue final {
public:
    [[nodiscard]] std::uint64_t Generation() const noexcept {
        return generation_.load(std::memory_order_acquire);
    }

    [[nodiscard]] bool TryPush(
        const SmvmActionPayload& action,
        const std::uint64_t expected_generation) noexcept {
        if (action.type == SmvmActionType::none || lock_.test_and_set(std::memory_order_acquire))
            return false;

        const auto current_generation = generation_.load(std::memory_order_acquire);
        if (current_generation != expected_generation) {
            lock_.clear(std::memory_order_release);
            return false;
        }
        DiscardStale(current_generation);
        const auto next = (write_ + 1) % actions_.size();
        if (next == read_) {
            lock_.clear(std::memory_order_release);
            return false;
        }
        actions_[write_] = QueuedAction{action, current_generation};
        write_ = next;
        lock_.clear(std::memory_order_release);
        return true;
    }

    [[nodiscard]] bool TryPop(SmvmActionPayload& action) noexcept {
        action = {};
        if (lock_.test_and_set(std::memory_order_acquire))
            return false;

        const auto current_generation = generation_.load(std::memory_order_acquire);
        DiscardStale(current_generation);
        if (read_ == write_) {
            lock_.clear(std::memory_order_release);
            return false;
        }
        action = actions_[read_].payload;
        read_ = (read_ + 1) % actions_.size();
        lock_.clear(std::memory_order_release);
        return true;
    }

    void Invalidate() noexcept {
        generation_.fetch_add(1, std::memory_order_acq_rel);
    }

private:
    struct QueuedAction final {
        SmvmActionPayload payload{};
        std::uint64_t generation{};
    };

    void DiscardStale(const std::uint64_t current_generation) noexcept {
        while (read_ != write_ && actions_[read_].generation != current_generation)
            read_ = (read_ + 1) % actions_.size();
    }

    std::atomic_flag lock_ = ATOMIC_FLAG_INIT;
    std::atomic<std::uint64_t> generation_{0};
    std::array<QueuedAction, 64> actions_{};
    std::size_t read_{};
    std::size_t write_{};
};

} // namespace deadlock_mvm
