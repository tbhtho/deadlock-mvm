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
        // Sliders can produce a value every rendered frame while the host
        // consumes one action per IPC heartbeat. Replace the still-pending tail
        // value instead of filling the bounded queue with obsolete drag samples.
        // Only adjacent continuous edits coalesce, so buttons and reset actions
        // remain ordering barriers.
        if (read_ != write_ && IsContinuousEdit(action.type)) {
            const auto previous = (write_ + actions_.size() - 1) % actions_.size();
            auto& pending = actions_[previous];
            if (pending.generation == current_generation &&
                pending.payload.type == action.type &&
                ContinuousEditKeyMatches(pending.payload, action)) {
                pending.payload = action;
                lock_.clear(std::memory_order_release);
                return true;
            }
        }
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

    [[nodiscard]] static constexpr bool IsContinuousEdit(
        const SmvmActionType type) noexcept {
        switch (type) {
            case SmvmActionType::set_fov:
            case SmvmActionType::set_roll:
            case SmvmActionType::set_ui_scale:
            case SmvmActionType::set_menu_opacity:
            case SmvmActionType::set_movement_speed:
            case SmvmActionType::set_mouse_sensitivity:
            case SmvmActionType::set_smoothing:
            case SmvmActionType::set_path_label_scale:
            case SmvmActionType::set_replay_bar_scale:
            case SmvmActionType::set_replay_bar_opacity:
            case SmvmActionType::set_status_hud_scale:
            case SmvmActionType::set_status_hud_opacity:
            case SmvmActionType::set_look_value:
            case SmvmActionType::set_custom_fog_value:
            case SmvmActionType::set_custom_fog_color:
                return true;
            default:
                return false;
        }
    }

    [[nodiscard]] static constexpr bool ContinuousEditKeyMatches(
        const SmvmActionPayload& left,
        const SmvmActionPayload& right) noexcept {
        switch (left.type) {
            case SmvmActionType::set_look_value:
            case SmvmActionType::set_custom_fog_value:
                return left.index == right.index;
            default:
                return true;
        }
    }

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
