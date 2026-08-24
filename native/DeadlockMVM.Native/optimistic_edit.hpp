#pragma once

#include <algorithm>
#include <cmath>

namespace deadlock_mvm {

enum class OptimisticEditResult {
    synchronized,
    pending,
    acknowledged,
    timed_out,
};

// Keeps an editor value stable while the managed snapshot catches up with an
// action emitted by the native UI. A negative pending_since value means that
// no acknowledgement is outstanding.
[[nodiscard]] inline OptimisticEditResult ReconcileOptimisticEdit(
    const float authoritative,
    const double now,
    const double timeout_seconds,
    const float epsilon,
    float& display_value,
    double& pending_since) noexcept {
    if (pending_since < 0.0) {
        display_value = authoritative;
        return OptimisticEditResult::synchronized;
    }
    if (std::abs(authoritative - display_value) <= epsilon) {
        display_value = authoritative;
        pending_since = -1.0;
        return OptimisticEditResult::acknowledged;
    }
    if (now - pending_since >= timeout_seconds) {
        display_value = authoritative;
        pending_since = -1.0;
        return OptimisticEditResult::timed_out;
    }
    return OptimisticEditResult::pending;
}

inline void MarkOptimisticEdit(
    const float edited,
    const float minimum,
    const float maximum,
    const double now,
    float& display_value,
    double& pending_since) noexcept {
    display_value = std::clamp(edited, minimum, maximum);
    pending_since = now;
}

} // namespace deadlock_mvm
