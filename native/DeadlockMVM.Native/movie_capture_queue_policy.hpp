#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace deadlock_mvm {

// Keep enough completed frames off the render thread to absorb normal NTFS and
// OpenDML write bursts, without allowing a high-resolution take to consume
// unbounded memory. At 1600x900, Beauty + float depth uses about 9.6 MiB per
// queued frame, so the eight-frame cap is roughly 77 MiB of queued payload.
constexpr std::size_t kMovieCaptureQueueBudgetBytes = 96u * 1024u * 1024u;
constexpr std::size_t kMinimumMovieCaptureQueuedFrames = 1;
constexpr std::size_t kMaximumMovieCaptureQueuedFrames = 8;

[[nodiscard]] constexpr std::size_t MovieCaptureFramePayloadBytes(
    const std::uint32_t width,
    const std::uint32_t height,
    const bool has_color,
    const bool has_depth) noexcept {
    if (width == 0 || height == 0 || (!has_color && !has_depth))
        return 0;
    const auto pixels = static_cast<std::uint64_t>(width) * height;
    const auto bytes_per_pixel = static_cast<std::uint64_t>(
        (has_color ? 3u : 0u) + (has_depth ? sizeof(float) : 0u));
    return pixels > std::numeric_limits<std::size_t>::max() / bytes_per_pixel
        ? std::numeric_limits<std::size_t>::max()
        : static_cast<std::size_t>(pixels * bytes_per_pixel);
}

[[nodiscard]] constexpr std::size_t MovieCaptureQueueFrameCapacity(
    const std::size_t frame_payload_bytes) noexcept {
    if (frame_payload_bytes == 0)
        return kMinimumMovieCaptureQueuedFrames;
    return std::clamp(
        kMovieCaptureQueueBudgetBytes / frame_payload_bytes,
        kMinimumMovieCaptureQueuedFrames,
        kMaximumMovieCaptureQueuedFrames);
}

// The render thread waits for queue space when the writer falls behind. That
// wait must be bounded: the writer can block on a removed or saturated capture
// disk, and an unbounded wait freezes the game for as long as the I/O lasts.
// The budget is far above the slowest frame write observed on a saturated SATA
// disk (about 1.4 s), so it only fires when the writer is genuinely wedged and
// the take is finalized instead of the game hanging.
constexpr std::uint64_t kMovieCaptureQueueWaitBudgetMilliseconds = 15000;

[[nodiscard]] constexpr bool MovieCaptureQueueWaitExceededBudget(
    const std::uint64_t waited_milliseconds) noexcept {
    return waited_milliseconds >= kMovieCaptureQueueWaitBudgetMilliseconds;
}

} // namespace deadlock_mvm
