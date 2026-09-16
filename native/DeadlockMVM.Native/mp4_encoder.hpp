#pragma once

#include <cstdint>
#include <filesystem>

namespace deadlock_mvm {

// The pass AVIs are uncompressed 24-bit BGR: about 466 MB/s at 1080p75, which
// no hard disk can stream and no consumer player will play. Every recorded
// Beauty pass therefore also gets a small playable H.264 MP4 through the
// Windows Media Foundation encoder. Failure is never fatal for a take; the AVI
// remains the authoritative artifact and the preview is simply absent.
class Mp4Encoder final {
public:
    Mp4Encoder() = default;
    ~Mp4Encoder();

    Mp4Encoder(const Mp4Encoder&) = delete;
    Mp4Encoder& operator=(const Mp4Encoder&) = delete;

    [[nodiscard]] bool Open(
        const std::filesystem::path& path,
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t fps,
        std::uint32_t bitrate_bps) noexcept;
    [[nodiscard]] bool WriteBgrFrame(
        const std::uint8_t* bgr,
        std::uint32_t stride_bytes) noexcept;
    [[nodiscard]] bool Close() noexcept;

    [[nodiscard]] bool IsOpen() const noexcept;
    [[nodiscard]] std::uint64_t FramesWritten() const noexcept;

private:
    struct Impl;
    Impl* impl_{};
};

} // namespace deadlock_mvm
