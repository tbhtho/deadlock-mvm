#pragma once

#include <algorithm>
#include <cstdint>

namespace deadlock_mvm {

struct MovieRecordingTickProgress final {
    std::int64_t completed{};
    std::int64_t total{};
    std::int64_t remaining{};
};

[[nodiscard]] constexpr MovieRecordingTickProgress ComputeMovieRecordingTickProgress(
    std::int64_t start_tick,
    std::int64_t end_tick,
    const std::int64_t current_tick) noexcept {
    if (end_tick < start_tick)
        std::swap(start_tick, end_tick);
    const auto total = end_tick - start_tick;
    const auto completed = std::clamp(current_tick - start_tick, std::int64_t{0}, total);
    return {completed, total, total - completed};
}

} // namespace deadlock_mvm
