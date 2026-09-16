#pragma once

#include "protocol.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <string_view>

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11DepthStencilView;
struct ID3D11Texture2D;
struct ID3D11ShaderResourceView;
struct IDXGISwapChain;

namespace deadlock_mvm {

[[nodiscard]] constexpr bool IsRepeatedMovieCameraSequence(
    const std::uint64_t previous_sequence,
    const std::uint64_t current_sequence) noexcept {
    return current_sequence != 0 && current_sequence == previous_sequence;
}

[[nodiscard]] constexpr bool ShouldCaptureMovieFrame(
    const std::uint64_t observed_frames,
    const std::uint64_t expected_frame_count) noexcept {
    return expected_frame_count == 0 || observed_frames < expected_frame_count;
}

// host_framerate fixes how many frames sample each game-time second. The AVI
// must declare the cinematic playback rate instead of that sampling rate, or a
// slowed-down preview is saved as a real-time clip that ends almost as soon as
// it starts. The artifact rate is the sampling rate scaled by the cinematic
// speed; an unusable speed fails back to real time.
[[nodiscard]] constexpr std::uint32_t OutputFrameRate(
    const std::uint32_t capture_fps,
    const double playback_speed) noexcept {
    if (capture_fps == 0)
        return 1;
    if (!(playback_speed > 0.0) || playback_speed > 16.0)
        return capture_fps;
    const auto scaled = static_cast<double>(capture_fps) * playback_speed + 0.5;
    if (scaled < 1.0)
        return 1;
    if (scaled > 1000.0)
        return 1000;
    return static_cast<std::uint32_t>(scaled);
}

// A tiny positive threshold is more reliable than an exact depth-equality draw
// at the reversed-Z clear plane while remaining far below useful scene depth.
inline constexpr auto kGreenscreenFarDepthTolerance = 1.0e-7F;

[[nodiscard]] constexpr bool IsGreenscreenFarPlaneDepth(
    const float device_depth,
    const bool far_depth_is_zero) noexcept {
    return far_depth_is_zero
        ? device_depth <= kGreenscreenFarDepthTolerance
        : device_depth >= 1.0F - kGreenscreenFarDepthTolerance;
}

// A Green Screen pass that produced no key background is reported as failed by
// the capture manifest, but the bare verdict does not say which comparison
// rejected the frame. These reasons name the observed depth condition together
// with the depth range, so an unusable plate identifies its own cause instead
// of only its symptom.
enum class GreenscreenDepthReason : std::uint32_t {
    not_observed = 0,
    depth_missing = 1,
    no_far_plane_samples = 2,
    whole_frame_is_far = 3,
    far_plane_present = 4,
};

[[nodiscard]] constexpr std::string_view DescribeGreenscreenDepthReason(
    const GreenscreenDepthReason reason) noexcept {
    switch (reason) {
        case GreenscreenDepthReason::depth_missing: return "depth_missing";
        case GreenscreenDepthReason::no_far_plane_samples: return "no_far_plane_samples";
        case GreenscreenDepthReason::whole_frame_is_far: return "whole_frame_is_far";
        case GreenscreenDepthReason::far_plane_present: return "far_plane_present";
        default: return "not_observed";
    }
}

struct GreenscreenDepthDiagnosis final {
    GreenscreenDepthReason reason{GreenscreenDepthReason::not_observed};
    bool far_depth_is_zero{};
    std::uint64_t samples{};
    float minimum{};
    float maximum{};
};

// Classifies the captured device depth on a coarse grid over the whole frame.
// `whole_frame_is_far` is the polarity-mismatch case: every sample matched the
// far plane that the border detection chose, so the detection picked the wrong
// end and the key fill keyed nothing. `no_far_plane_samples` is the opposite
// and by far the likelier live cause: depth was captured, but nothing in the
// frame matched the far plane the detection chose.
[[nodiscard]] inline GreenscreenDepthDiagnosis DiagnoseGreenscreenDepth(
    const float* const depth,
    const std::size_t width,
    const std::size_t height,
    const bool far_depth_is_zero) noexcept {
    GreenscreenDepthDiagnosis diagnosis{};
    diagnosis.far_depth_is_zero = far_depth_is_zero;
    if (depth == nullptr || width == 0 || height == 0) {
        diagnosis.reason = GreenscreenDepthReason::depth_missing;
        return diagnosis;
    }
    const auto step_x = std::max<std::size_t>(1, width / 64u);
    const auto step_y = std::max<std::size_t>(1, height / 64u);
    auto minimum = std::numeric_limits<float>::max();
    auto maximum = std::numeric_limits<float>::lowest();
    auto far_samples = std::uint64_t{};
    for (std::size_t y = 0; y < height; y += step_y) {
        for (std::size_t x = 0; x < width; x += step_x) {
            const auto value = depth[y * width + x];
            if (!std::isfinite(value))
                continue;
            minimum = std::min(minimum, value);
            maximum = std::max(maximum, value);
            ++diagnosis.samples;
            if (IsGreenscreenFarPlaneDepth(value, far_depth_is_zero))
                ++far_samples;
        }
    }
    if (diagnosis.samples == 0) {
        diagnosis.reason = GreenscreenDepthReason::depth_missing;
        return diagnosis;
    }
    diagnosis.minimum = minimum;
    diagnosis.maximum = maximum;
    if (far_samples == 0)
        diagnosis.reason = GreenscreenDepthReason::no_far_plane_samples;
    else if (far_samples == diagnosis.samples)
        diagnosis.reason = GreenscreenDepthReason::whole_frame_is_far;
    else
        diagnosis.reason = GreenscreenDepthReason::far_plane_present;
    return diagnosis;
}

[[nodiscard]] constexpr std::uint64_t AppendReplayTimingDigest(
    std::uint64_t digest,
    const std::int64_t replay_tick) noexcept {
    constexpr auto kPrime = 1099511628211ULL;
    const auto tick_bits = static_cast<std::uint64_t>(replay_tick);
    for (unsigned shift = 0; shift < 64; shift += 8) {
        digest ^= static_cast<std::uint8_t>(tick_bits >> shift);
        digest *= kPrime;
    }
    return digest;
}

[[nodiscard]] constexpr std::uint64_t AppendRenderedCameraDigest(
    std::uint64_t digest,
    const CameraSample& camera) noexcept {
    constexpr auto kPrime = 1099511628211ULL;
    const std::array values{
        camera.x,
        camera.y,
        camera.z,
        camera.pitch,
        camera.yaw,
        camera.roll,
        camera.fov,
    };
    for (const auto value : values) {
        const auto bits = std::bit_cast<std::uint64_t>(value);
        for (unsigned shift = 0; shift < 64; shift += 8) {
            digest ^= static_cast<std::uint8_t>(bits >> shift);
            digest *= kPrime;
        }
    }
    return digest;
}

struct MovieCaptureConfiguration final {
    bool active{};
    std::string_view capture_name{};
    std::string_view take_directory{};
    std::uint32_t fps{60};
    std::uint32_t output_mode{};
    std::uint32_t pass_flags{};
    std::uint32_t output_width{};
    std::uint32_t output_height{};
    double playback_speed{1.0};
    bool greenscreen_active{};
    bool capture_audio{true};
    std::uint32_t greenscreen_color_rgb{0x00FF00u};
    std::uint64_t expected_frame_count{};
    LookSettings look{};
    std::uint64_t lut_content_hash{};
};

struct WorldDepthCaptureStatus final {
    bool active{};
    bool frame_composition_ready{};
    bool depth_observer_active{};
    bool depth_available{};
    bool avi_available{};
    bool audio_active{};
    bool audio_failed{};
    bool failed{};
    bool incomplete{};
    std::uint64_t observed_frames{};
    std::uint64_t written_frames{};
    std::uint64_t beauty_tga_frames_written{};
    std::uint64_t beauty_frames_written{};
    std::uint64_t depth_pfm_frames_written{};
    std::uint64_t depth_avi_frames_written{};
    std::uint64_t depth_key_frames_written{};
    std::uint64_t present_calls{};
    std::uint64_t repeated_camera_sequences_captured{};
    std::uint64_t repeated_visual_samples_captured{};
    std::uint64_t depth_frames_unavailable{};
    std::uint64_t queue_backpressure_events{};
    std::uint64_t queue_backpressure_microseconds{};
    std::uint64_t maximum_queue_wait_microseconds{};
    std::uint64_t maximum_capture_microseconds{};
    std::uint64_t maximum_writer_microseconds{};
    std::uint32_t queue_capacity{};
    std::uint32_t maximum_queue_depth{};
};

// The immediate-context observer is enabled while a depth-dependent take is
// armed/active and throughout a synchronized World-to-Chroma batch. Retaining
// the validated full-resolution DSV across the inter-pass seek is required:
// blank-world mode may not expose another compatible binding before Chroma
// needs to paint its first plate.
void ConfigureWorldDepthObservation(
    bool enabled,
    std::uint32_t expected_width,
    std::uint32_t expected_height) noexcept;
void ObserveWorldDepthView(ID3D11DepthStencilView* depth_view) noexcept;

// Returns an AddRef'd full-resolution depth view retained by the immediate-
// context observer, or nullptr when the current frame has not exposed one yet.
// The caller owns the returned reference.
[[nodiscard]] ID3D11DepthStencilView* AcquireObservedWorldDepthView(
    std::uint32_t expected_width,
    std::uint32_t expected_height) noexcept;

// Returns an AddRef'd shader-resource view of the retained world depth for the
// live depth-of-field effect, or nullptr when the depth surface is not
// sampleable. The caller owns the returned reference.
[[nodiscard]] ID3D11ShaderResourceView* AcquireObservedWorldDepthShaderResourceView(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    std::uint32_t expected_width,
    std::uint32_t expected_height) noexcept;

// Synchronizes the native pass writer with the managed cinematic recording
// transaction. A configuration change finalizes the previous session before a
// replacement starts; no pass can leak across take names.
void SyncMovieCapture(const MovieCaptureConfiguration& configuration) noexcept;

// Read-only continuation check for readiness gates. An unrelated active take
// must not lend its initialized-writer status to a replacement capture.
[[nodiscard]] bool HasActiveMovieCaptureIdentity(
    std::string_view capture_name, std::string_view take_directory) noexcept;

// Captures the current backbuffer and world-depth view before DeadLockMVM draws
// its own composition guides or menu. The bounded queue blocks offline timing
// instead of dropping frames, preserving pass/frame alignment.
void CaptureMovieFrame(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    IDXGISwapChain* swapchain,
    std::int64_t replay_tick,
    const CameraSample* rendered_camera,
    std::uint64_t camera_frame_sequence,
    ID3D11Texture2D* cadence_color_source = nullptr) noexcept;

// The presentation compositor publishes readiness separately from writer
// startup. Green Screen is not allowed to resume replay until a compatible
// world-depth surface has actually produced the far-plane key fill.
void SetMovieFrameCompositionReady(bool ready) noexcept;

void NotifyWorldDepthDeviceLost() noexcept;
// Terminal GPU failure: stop audio/writer and mark the take failed/partial.
// Unlike ordinary resource invalidation this must not look like a clean stop.
void AbortMovieCaptureForDeviceFailure() noexcept;
void ShutdownWorldDepthCapture() noexcept;

[[nodiscard]] WorldDepthCaptureStatus GetWorldDepthCaptureStatus() noexcept;

} // namespace deadlock_mvm
