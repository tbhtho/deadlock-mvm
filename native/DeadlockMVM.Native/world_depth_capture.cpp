#include "world_depth_capture.hpp"

#include "engine_movie_audio.hpp"
#include "movie_capture_queue_policy.hpp"
#include "mp4_encoder.hpp"
#include "protocol.hpp"

#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <libgmavi.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <climits>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace deadlock_mvm {
namespace {

template <typename T>
void SafeRelease(T*& value) noexcept {
    if (value != nullptr) {
        value->Release();
        value = nullptr;
    }
}

class MappedResource final {
public:
    MappedResource(
        ID3D11DeviceContext* context,
        ID3D11Resource* resource,
        const UINT subresource) noexcept
        : context_(context), resource_(resource), subresource_(subresource) {}

    ~MappedResource() noexcept { Unmap(); }

    void Unmap() noexcept {
        if (context_ == nullptr || resource_ == nullptr)
            return;
        context_->Unmap(resource_, subresource_);
        context_ = nullptr;
        resource_ = nullptr;
    }

    MappedResource(const MappedResource&) = delete;
    MappedResource& operator=(const MappedResource&) = delete;

private:
    ID3D11DeviceContext* context_{};
    ID3D11Resource* resource_{};
    UINT subresource_{};
};

struct MovieFrame final {
    std::uint64_t index{};
    std::int64_t replay_tick{-1};
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t color_stride{};
    std::vector<std::uint8_t> color_bgr{};
    std::vector<float> depth{};
};

struct AviOutputs final {
    void* beauty{};
    void* depth{};
    void* depth_key{};
};

struct MovieCaptureState final {
    std::mutex mutex{};
    std::mutex depth_view_mutex{};
    std::condition_variable work_ready{};
    std::condition_variable queue_space{};
    std::deque<MovieFrame> queue{};
    std::deque<std::vector<float>> recycled_depth{};
    std::deque<std::vector<std::uint8_t>> recycled_color{};
    std::thread writer{};
    std::filesystem::path take_directory{};
    std::string capture_name{};
    bool writer_stop{};
    LookSettings look{}; // Immutable between writer start and join.
    std::uint64_t lut_content_hash{};

    std::atomic<bool> active{false};
    std::atomic<bool> frame_composition_ready{false};
    std::atomic<bool> depth_observation_enabled{false};
    std::atomic<bool> depth_observer_observed{false};
    std::atomic<bool> depth_available{false};
    std::atomic<bool> avi_available{false};
    // Small playable H.264 preview beside the uncompressed World AVI. This is
    // a convenience artifact; its failure never fails a take.
    std::atomic<bool> mp4_available{false};
    std::atomic<std::uint64_t> mp4_frames_written{0};
    std::atomic<bool> audio_started{false};
    std::atomic<bool> audio_failed{false};
    std::atomic<bool> fatal_failed{false};
    std::atomic<bool> pass_incomplete{false};
    std::atomic<std::uint64_t> observed_frames{0};
    std::atomic<std::uint64_t> written_frames{0};
    std::atomic<std::uint64_t> beauty_tga_frames_written{0};
    std::atomic<std::uint64_t> beauty_frames_written{0};
    std::atomic<std::uint64_t> depth_pfm_frames_written{0};
    std::atomic<std::uint64_t> depth_avi_frames_written{0};
    std::atomic<std::uint64_t> depth_key_frames_written{0};
    std::atomic<std::uint64_t> greenscreen_background_pixels{0};
    std::atomic<std::uint64_t> greenscreen_subject_pixels{0};
    std::atomic<std::uint64_t> greenscreen_frames_with_background{0};
    std::atomic<std::uint64_t> greenscreen_frames_with_subject{0};
    // First frame that produced no key background, kept so a failed Chroma pass
    // names the depth condition that rejected it and the range it observed.
    std::atomic<std::uint32_t> greenscreen_failure_reason{
        static_cast<std::uint32_t>(GreenscreenDepthReason::not_observed)};
    std::atomic<bool> greenscreen_failure_far_is_zero{false};
    std::atomic<float> greenscreen_failure_depth_min{0.0F};
    std::atomic<float> greenscreen_failure_depth_max{0.0F};
    std::atomic<std::uint64_t> present_calls{0};
    std::atomic<std::uint64_t> repeated_camera_sequences_captured{0};
    std::atomic<std::uint64_t> repeated_visual_samples_captured{0};
    std::atomic<std::uint64_t> last_visual_fingerprint{0};
    std::atomic<std::uint64_t> depth_frames_unavailable{0};
    std::atomic<std::uint64_t> queue_backpressure_events{0};
    std::atomic<std::uint64_t> queue_backpressure_microseconds{0};
    std::atomic<std::uint64_t> queue_backpressure_timeouts{0};
    std::atomic<std::uint64_t> maximum_queue_wait_microseconds{0};
    std::atomic<std::uint64_t> maximum_capture_microseconds{0};
    std::atomic<std::uint64_t> maximum_writer_microseconds{0};
    std::atomic<std::uint32_t> queue_capacity{
        static_cast<std::uint32_t>(kMinimumMovieCaptureQueuedFrames)};
    std::atomic<std::uint32_t> maximum_queue_depth{0};
    std::atomic<std::uint64_t> last_camera_frame_sequence{0};
    std::atomic<std::uint64_t> replay_timing_digest{14695981039346656037ULL};
    std::atomic<std::uint64_t> rendered_camera_digest{14695981039346656037ULL};
    std::atomic<std::uint64_t> rendered_camera_samples{0};
    std::atomic<std::uint64_t> expected_frame_count{0};
    std::atomic<std::int64_t> first_replay_tick{-1};
    std::atomic<std::int64_t> last_replay_tick{-1};
    std::atomic<std::uint32_t> fps{60};
    std::atomic<double> playback_speed{1.0};
    std::atomic<std::uint32_t> output_mode{0};
    std::atomic<std::uint32_t> pass_flags{movie_capture_pass_beauty};
    std::atomic<std::uint32_t> output_width{0};
    std::atomic<std::uint32_t> output_height{0};
    std::atomic<std::uint32_t> source_width{0};
    std::atomic<std::uint32_t> source_height{0};
    std::atomic<bool> greenscreen_active{false};
    std::atomic<bool> capture_audio{true};
    std::atomic<std::uint32_t> greenscreen_color_rgb{0x00FF00u};
    std::atomic<std::uint32_t> expected_depth_width{0};
    std::atomic<std::uint32_t> expected_depth_height{0};

    ID3D11DepthStencilView* retained_depth_view{};

    // Shader-readable view of the retained depth for the live depth-of-field
    // effect. Recreated when the observed resource changes; null when the
    // engine depth surface is not sampleable.
    ID3D11ShaderResourceView* depth_srv{};
    ID3D11Resource* depth_srv_resource{};
    DXGI_FORMAT depth_srv_format{DXGI_FORMAT_UNKNOWN};

    // Fallback: a typeless copy of the typed depth surface that can be bound as
    // a shader resource when the engine depth itself is not sampleable.
    ID3D11Texture2D* depth_copy{};
    ID3D11ShaderResourceView* depth_copy_srv{};
    ID3D11Resource* depth_copy_source{};
    D3D11_TEXTURE2D_DESC depth_copy_description{};
    DXGI_FORMAT depth_copy_format{DXGI_FORMAT_UNKNOWN};

    ID3D11Texture2D* depth_staging{};
    D3D11_TEXTURE2D_DESC depth_staging_description{};
    DXGI_FORMAT depth_view_format{DXGI_FORMAT_UNKNOWN};
    UINT depth_subresource{};
    ID3D11Texture2D* color_staging{};
    D3D11_TEXTURE2D_DESC color_staging_description{};
    ID3D11Texture2D* cadence_staging{};
    D3D11_TEXTURE2D_DESC cadence_source_description{};
};

void UpdateMaximum(
    std::atomic<std::uint64_t>& target,
    const std::uint64_t candidate) noexcept {
    auto current = target.load(std::memory_order_acquire);
    while (candidate > current && !target.compare_exchange_weak(
               current, candidate, std::memory_order_acq_rel)) {
    }
}

void UpdateMaximum(
    std::atomic<std::uint32_t>& target,
    const std::uint32_t candidate) noexcept {
    auto current = target.load(std::memory_order_acquire);
    while (candidate > current && !target.compare_exchange_weak(
               current, candidate, std::memory_order_acq_rel)) {
    }
}

[[nodiscard]] std::uint64_t ElapsedMicroseconds(
    const std::chrono::steady_clock::time_point start) noexcept {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::steady_clock::now() - start).count());
}

[[nodiscard]] std::uint64_t VisualFingerprint(
    const MovieFrame& frame, const std::uint64_t pre_effect_color = 0) noexcept {
    constexpr auto kOffsetBasis = 14695981039346656037ULL;
    constexpr auto kPrime = 1099511628211ULL;
    auto hash = pre_effect_color != 0 ? pre_effect_color : kOffsetBasis;
    const auto mix = [&hash](const std::uint8_t value) noexcept {
        hash ^= value;
        hash *= kPrime;
    };
    const auto step_x = std::max<std::uint32_t>(1, frame.width / 64u);
    const auto step_y = std::max<std::uint32_t>(1, frame.height / 36u);
    auto has_payload = pre_effect_color != 0;
    if (pre_effect_color == 0 && !frame.color_bgr.empty()) {
        has_payload = true;
        for (std::uint32_t y = 0; y < frame.height; y += step_y) {
            const auto* row = frame.color_bgr.data() +
                (static_cast<std::size_t>(y) * frame.color_stride);
            for (std::uint32_t x = 0; x < frame.width; x += step_x) {
                const auto* pixel = row + (static_cast<std::size_t>(x) * 3u);
                mix(pixel[0]);
                mix(pixel[1]);
                mix(pixel[2]);
            }
        }
    }
    if (!frame.depth.empty()) {
        has_payload = true;
        for (std::uint32_t y = 0; y < frame.height; y += step_y) {
            for (std::uint32_t x = 0; x < frame.width; x += step_x) {
                std::uint32_t bits{};
                std::memcpy(
                    &bits,
                    &frame.depth[static_cast<std::size_t>(y) * frame.width + x],
                    sizeof(bits));
                mix(static_cast<std::uint8_t>(bits));
                mix(static_cast<std::uint8_t>(bits >> 8));
                mix(static_cast<std::uint8_t>(bits >> 16));
                mix(static_cast<std::uint8_t>(bits >> 24));
            }
        }
    }
    return has_payload ? hash : 0;
}

MovieCaptureState& State() noexcept {
    static MovieCaptureState state{};
    return state;
}

void RecycleFrameBuffers(MovieFrame& frame) noexcept {
    auto& state = State();
    std::lock_guard lock(state.mutex);
    const auto recycle_limit = static_cast<std::size_t>(
        state.queue_capacity.load(std::memory_order_acquire)) + 2u;
    if (state.recycled_depth.size() < recycle_limit) {
        frame.depth.clear();
        state.recycled_depth.push_back(std::move(frame.depth));
    }
    if (state.recycled_color.size() < recycle_limit) {
        frame.color_bgr.clear();
        state.recycled_color.push_back(std::move(frame.color_bgr));
    }
}

void MarkFatalFailure() noexcept {
    auto& state = State();
    state.fatal_failed.store(true, std::memory_order_release);
    state.pass_incomplete.store(true, std::memory_order_release);
}

[[nodiscard]] bool IsSafeCaptureName(const std::string_view name) noexcept {
    if (name.empty() || name.size() >= 64)
        return false;
    return std::all_of(name.begin(), name.end(), [](const char value) noexcept {
        return (value >= 'a' && value <= 'z') ||
               (value >= 'A' && value <= 'Z') ||
               (value >= '0' && value <= '9') || value == '-' || value == '_';
    });
}

[[nodiscard]] std::filesystem::path Utf8Path(const std::string_view value) {
    if (value.empty() || value.size() > static_cast<std::size_t>(INT_MAX))
        return {};
    const auto required = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0);
    if (required <= 0)
        return {};
    std::wstring wide(static_cast<std::size_t>(required), L'\0');
    if (MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            wide.data(),
            required) != required) {
        return {};
    }
    return std::filesystem::path(wide);
}

[[nodiscard]] bool SameTextureDescription(
    const D3D11_TEXTURE2D_DESC& left,
    const D3D11_TEXTURE2D_DESC& right) noexcept {
    return left.Width == right.Width && left.Height == right.Height &&
           left.MipLevels == right.MipLevels && left.ArraySize == right.ArraySize &&
           left.Format == right.Format &&
           left.SampleDesc.Count == right.SampleDesc.Count &&
           left.SampleDesc.Quality == right.SampleDesc.Quality;
}

void ReleaseStaging() noexcept {
    auto& state = State();
    SafeRelease(state.depth_staging);
    SafeRelease(state.color_staging);
    SafeRelease(state.cadence_staging);
    state.cadence_source_description = {};
    state.depth_staging_description = {};
    state.color_staging_description = {};
    state.depth_view_format = DXGI_FORMAT_UNKNOWN;
    state.depth_subresource = 0;
    state.depth_available.store(false, std::memory_order_release);
}

void ReleaseRetainedDepthView() noexcept {
    auto& state = State();
    std::lock_guard lock(state.depth_view_mutex);
    SafeRelease(state.retained_depth_view);
    SafeRelease(state.depth_srv);
    SafeRelease(state.depth_srv_resource);
    state.depth_srv_format = DXGI_FORMAT_UNKNOWN;
    SafeRelease(state.depth_copy);
    SafeRelease(state.depth_copy_srv);
    SafeRelease(state.depth_copy_source);
    state.depth_copy_description = {};
    state.depth_copy_format = DXGI_FORMAT_UNKNOWN;
}

[[nodiscard]] bool EnsureDepthStaging(
    ID3D11Device* device,
    const D3D11_TEXTURE2D_DESC& source,
    const DXGI_FORMAT view_format,
    const UINT subresource) noexcept {
    auto& state = State();
    if (state.depth_staging != nullptr &&
        SameTextureDescription(state.depth_staging_description, source) &&
        state.depth_view_format == view_format &&
        state.depth_subresource == subresource) {
        return true;
    }
    SafeRelease(state.depth_staging);
    state.depth_staging_description = {};
    if (source.SampleDesc.Count != 1)
        return false;
    auto staging = source;
    staging.Usage = D3D11_USAGE_STAGING;
    staging.BindFlags = 0;
    staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    staging.MiscFlags = 0;
    if (FAILED(device->CreateTexture2D(&staging, nullptr, &state.depth_staging)) ||
        state.depth_staging == nullptr) {
        SafeRelease(state.depth_staging);
        return false;
    }
    state.depth_staging_description = source;
    state.depth_view_format = view_format;
    state.depth_subresource = subresource;
    return true;
}

[[nodiscard]] bool EnsureColorStaging(
    ID3D11Device* device,
    const D3D11_TEXTURE2D_DESC& source) noexcept {
    auto& state = State();
    if (state.color_staging != nullptr &&
        SameTextureDescription(state.color_staging_description, source)) {
        return true;
    }
    SafeRelease(state.color_staging);
    state.color_staging_description = {};
    if (source.SampleDesc.Count != 1)
        return false;
    auto staging = source;
    staging.Usage = D3D11_USAGE_STAGING;
    staging.BindFlags = 0;
    staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    staging.MiscFlags = 0;
    if (FAILED(device->CreateTexture2D(&staging, nullptr, &state.color_staging)) ||
        state.color_staging == nullptr) {
        SafeRelease(state.color_staging);
        return false;
    }
    state.color_staging_description = source;
    return true;
}

[[nodiscard]] std::size_t DepthPixelSize(const DXGI_FORMAT format) noexcept {
    switch (format) {
        case DXGI_FORMAT_D16_UNORM: return 2;
        case DXGI_FORMAT_D24_UNORM_S8_UINT: return 4;
        case DXGI_FORMAT_D32_FLOAT: return 4;
        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: return 8;
        default: return 0;
    }
}

[[nodiscard]] float ReadDepthValue(
    const std::byte* pixel,
    const DXGI_FORMAT format) noexcept {
    switch (format) {
        case DXGI_FORMAT_D16_UNORM: {
            std::uint16_t value{};
            std::memcpy(&value, pixel, sizeof(value));
            return static_cast<float>(value) /
                static_cast<float>(std::numeric_limits<std::uint16_t>::max());
        }
        case DXGI_FORMAT_D24_UNORM_S8_UINT: {
            std::uint32_t value{};
            std::memcpy(&value, pixel, sizeof(value));
            return static_cast<float>(value & 0x00FFFFFFu) / 16777215.0F;
        }
        case DXGI_FORMAT_D32_FLOAT:
        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: {
            float value{};
            std::memcpy(&value, pixel, sizeof(value));
            return std::isfinite(value) ? std::clamp(value, 0.0F, 1.0F) : 0.0F;
        }
        default: return 0.0F;
    }
}

[[nodiscard]] bool ResolveDepthSubresource(
    const D3D11_DEPTH_STENCIL_VIEW_DESC& view,
    const D3D11_TEXTURE2D_DESC& texture,
    UINT& subresource,
    std::uint32_t& width,
    std::uint32_t& height) noexcept {
    UINT mip_slice = 0;
    UINT array_slice = 0;
    switch (view.ViewDimension) {
        case D3D11_DSV_DIMENSION_TEXTURE2D:
            mip_slice = view.Texture2D.MipSlice;
            break;
        case D3D11_DSV_DIMENSION_TEXTURE2DARRAY:
            mip_slice = view.Texture2DArray.MipSlice;
            array_slice = view.Texture2DArray.FirstArraySlice;
            break;
        default: return false;
    }
    if (mip_slice >= texture.MipLevels || array_slice >= texture.ArraySize)
        return false;
    subresource = D3D11CalcSubresource(mip_slice, array_slice, texture.MipLevels);
    width = std::max<std::uint32_t>(1, texture.Width >> mip_slice);
    height = std::max<std::uint32_t>(1, texture.Height >> mip_slice);
    return true;
}

[[nodiscard]] bool IsSupportedWorldDepthView(
    ID3D11DepthStencilView* depth_view,
    const std::uint32_t expected_width,
    const std::uint32_t expected_height) noexcept {
    if (depth_view == nullptr || expected_width == 0 || expected_height == 0)
        return false;
    Microsoft::WRL::ComPtr<ID3D11Resource> depth_resource{};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> depth_texture{};
    D3D11_DEPTH_STENCIL_VIEW_DESC view_description{};
    depth_view->GetDesc(&view_description);
    if (DepthPixelSize(view_description.Format) == 0)
        return false;
    depth_view->GetResource(depth_resource.GetAddressOf());
    if (depth_resource == nullptr || FAILED(depth_resource.As(&depth_texture)) ||
        depth_texture == nullptr) {
        return false;
    }
    D3D11_TEXTURE2D_DESC texture_description{};
    depth_texture->GetDesc(&texture_description);
    UINT subresource = 0;
    std::uint32_t width = 0;
    std::uint32_t height = 0;
    return texture_description.SampleDesc.Count == 1 &&
           ResolveDepthSubresource(
               view_description, texture_description, subresource, width, height) &&
           width == expected_width && height == expected_height;
}

[[nodiscard]] bool CaptureDepthView(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    ID3D11DepthStencilView* depth_view,
    MovieFrame& frame) noexcept {
    auto& state = State();
    Microsoft::WRL::ComPtr<ID3D11Resource> depth_resource{};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> depth_texture{};
    if (depth_view == nullptr)
        return false;
    D3D11_DEPTH_STENCIL_VIEW_DESC view_description{};
    depth_view->GetDesc(&view_description);
    const auto pixel_size = DepthPixelSize(view_description.Format);
    if (pixel_size == 0)
        return false;
    depth_view->GetResource(depth_resource.GetAddressOf());
    if (depth_resource == nullptr || FAILED(depth_resource.As(&depth_texture)) ||
        depth_texture == nullptr) {
        return false;
    }
    D3D11_TEXTURE2D_DESC description{};
    depth_texture->GetDesc(&description);
    UINT subresource = 0;
    std::uint32_t width = 0;
    std::uint32_t height = 0;
    if (!ResolveDepthSubresource(
            view_description, description, subresource, width, height) ||
        !EnsureDepthStaging(device, description, view_description.Format, subresource)) {
        return false;
    }
    context->CopySubresourceRegion(
        state.depth_staging,
        subresource,
        0,
        0,
        0,
        depth_texture.Get(),
        subresource,
        nullptr);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(
            state.depth_staging, subresource, D3D11_MAP_READ, 0, &mapped))) {
        return false;
    }
    MappedResource mapped_resource(context, state.depth_staging, subresource);
    if (frame.width != 0 && (frame.width != width || frame.height != height))
        return false;
    frame.width = width;
    frame.height = height;
    {
        std::lock_guard lock(state.mutex);
        if (!state.recycled_depth.empty()) {
            frame.depth = std::move(state.recycled_depth.front());
            state.recycled_depth.pop_front();
        }
    }
    frame.depth.resize(static_cast<std::size_t>(width) * height);
    for (std::uint32_t row = 0; row < height; ++row) {
        const auto* source = static_cast<const std::byte*>(mapped.pData) +
            (static_cast<std::size_t>(row) * mapped.RowPitch);
        auto* destination = frame.depth.data() +
            (static_cast<std::size_t>(height - row - 1) * width);
        for (std::uint32_t column = 0; column < width; ++column) {
            destination[column] = ReadDepthValue(
                source + (static_cast<std::size_t>(column) * pixel_size),
                view_description.Format);
        }
    }
    mapped_resource.Unmap();
    state.depth_available.store(true, std::memory_order_release);
    return true;
}

[[nodiscard]] bool CaptureDepth(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    MovieFrame& frame) noexcept {
    Microsoft::WRL::ComPtr<ID3D11DepthStencilView> bound_depth_view{};
    context->OMGetRenderTargets(0, nullptr, bound_depth_view.GetAddressOf());
    if (CaptureDepthView(device, context, bound_depth_view.Get(), frame))
        return true;

    Microsoft::WRL::ComPtr<ID3D11DepthStencilView> retained_depth_view{};
    {
        auto& state = State();
        std::lock_guard lock(state.depth_view_mutex);
        if (state.retained_depth_view != nullptr) {
            state.retained_depth_view->AddRef();
            retained_depth_view.Attach(state.retained_depth_view);
        }
    }
    return retained_depth_view != nullptr &&
        CaptureDepthView(device, context, retained_depth_view.Get(), frame);
}

[[nodiscard]] bool CaptureColor(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    IDXGISwapChain* swapchain,
    MovieFrame& frame) noexcept {
    auto& state = State();
    if (swapchain == nullptr)
        return false;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> backbuffer{};
    if (FAILED(swapchain->GetBuffer(
            0,
            __uuidof(ID3D11Texture2D),
            reinterpret_cast<void**>(backbuffer.GetAddressOf()))) || backbuffer == nullptr) {
        return false;
    }
    D3D11_TEXTURE2D_DESC description{};
    backbuffer->GetDesc(&description);
    const auto supported =
        description.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
        description.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
        description.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
        description.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
        description.Format == DXGI_FORMAT_B8G8R8X8_UNORM ||
        description.Format == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB;
    if (!supported || !EnsureColorStaging(device, description))
        return false;
    context->CopyResource(state.color_staging, backbuffer.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(state.color_staging, 0, D3D11_MAP_READ, 0, &mapped)))
        return false;
    MappedResource mapped_resource(context, state.color_staging, 0);
    if (frame.width != 0 &&
        (frame.width != description.Width || frame.height != description.Height)) {
        return false;
    }
    frame.width = description.Width;
    frame.height = description.Height;
    {
        std::lock_guard lock(state.mutex);
        if (!state.recycled_color.empty()) {
            frame.color_bgr = std::move(state.recycled_color.front());
            state.recycled_color.pop_front();
        }
    }
    frame.color_stride = (description.Width * 3u + 3u) & ~3u;
    frame.color_bgr.resize(
        static_cast<std::size_t>(frame.color_stride) * description.Height);
    const auto rgba = description.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
        description.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    for (std::uint32_t row = 0; row < description.Height; ++row) {
        const auto* source = static_cast<const std::uint8_t*>(mapped.pData) +
            (static_cast<std::size_t>(row) * mapped.RowPitch);
        auto* destination = frame.color_bgr.data() +
            (static_cast<std::size_t>(description.Height - row - 1) * frame.color_stride);
        for (std::uint32_t column = 0; column < description.Width; ++column) {
            const auto* pixel = source + (static_cast<std::size_t>(column) * 4);
            auto* output = destination + (static_cast<std::size_t>(column) * 3);
            if (rgba) {
                output[0] = pixel[2];
                output[1] = pixel[1];
                output[2] = pixel[0];
            } else {
                output[0] = pixel[0];
                output[1] = pixel[1];
                output[2] = pixel[2];
            }
        }
        std::fill(
            destination + (static_cast<std::size_t>(description.Width) * 3u),
            destination + frame.color_stride,
            std::uint8_t{0});
    }
    mapped_resource.Unmap();
    return true;
}

[[nodiscard]] bool WriteDepthFrame(
    const std::filesystem::path& directory,
    const MovieFrame& frame) {
    std::array<wchar_t, 48> filename{};
    const auto count = swprintf_s(
        filename.data(),
        filename.size(),
        L"zdepth_%08llu.pfm",
        static_cast<unsigned long long>(frame.index));
    if (count <= 0)
        return false;
    std::ofstream stream(directory / filename.data(), std::ios::binary | std::ios::trunc);
    if (!stream)
        return false;
    stream << "Pf\n" << frame.width << ' ' << frame.height << "\n-1.0\n";
    stream.write(
        reinterpret_cast<const char*>(frame.depth.data()),
        static_cast<std::streamsize>(frame.depth.size() * sizeof(float)));
    return static_cast<bool>(stream);
}

[[nodiscard]] bool WriteTgaFrame(
    const std::filesystem::path& directory,
    const std::string& capture_name,
    const MovieFrame& frame) {
    if (frame.color_bgr.empty() || frame.width == 0 || frame.height == 0 ||
        frame.width > std::numeric_limits<std::uint16_t>::max() ||
        frame.height > std::numeric_limits<std::uint16_t>::max()) {
        return false;
    }
    std::array<wchar_t, 96> filename{};
    const auto count = swprintf_s(
        filename.data(),
        filename.size(),
        L"%hs_%08llu.tga",
        capture_name.c_str(),
        static_cast<unsigned long long>(frame.index));
    if (count <= 0)
        return false;

    std::array<std::uint8_t, 18> header{};
    header[2] = 2; // uncompressed true-color image
    header[12] = static_cast<std::uint8_t>(frame.width & 0xFFu);
    header[13] = static_cast<std::uint8_t>((frame.width >> 8u) & 0xFFu);
    header[14] = static_cast<std::uint8_t>(frame.height & 0xFFu);
    header[15] = static_cast<std::uint8_t>((frame.height >> 8u) & 0xFFu);
    header[16] = 24;
    // CaptureColor stores bottom-up BGR, matching TGA's default bottom-left origin.
    header[17] = 0;

    std::ofstream stream(directory / filename.data(), std::ios::binary | std::ios::trunc);
    if (!stream)
        return false;
    stream.write(
        reinterpret_cast<const char*>(header.data()),
        static_cast<std::streamsize>(header.size()));
    const auto row_bytes = static_cast<std::size_t>(frame.width) * 3u;
    if (frame.color_stride == row_bytes) {
        stream.write(
            reinterpret_cast<const char*>(frame.color_bgr.data()),
            static_cast<std::streamsize>(row_bytes * frame.height));
    } else {
        for (std::uint32_t row = 0; row < frame.height; ++row) {
            const auto* const pixels = frame.color_bgr.data() +
                (static_cast<std::size_t>(row) * frame.color_stride);
            stream.write(
                reinterpret_cast<const char*>(pixels),
                static_cast<std::streamsize>(row_bytes));
        }
    }
    return static_cast<bool>(stream);
}

[[nodiscard]] bool EnsureAvi(
    void*& writer,
    const std::filesystem::path& path,
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t fps) noexcept {
    if (writer != nullptr)
        return true;
    try {
        // libgmavi only accepts a narrow path, so the name goes through the
        // active ANSI code page. A capture root that code page cannot represent
        // would open a different file than the wide path this take later
        // renames: the rename then fails, the whole take is reported failed for
        // the wrong reason, and a stray mis-named AVI is left behind. Prove the
        // conversion round-trips before handing the path over.
        const auto narrow_path = path.string();
        if (std::filesystem::path(narrow_path) != path)
            return false;
        writer = gmav_open(narrow_path.c_str(), width, height, fps);
        return writer != nullptr;
    } catch (...) {
        return false;
    }
}

// Opens the playable H.264 preview with a bounded quality target. Media
// Foundation or an H.264 encoder may be absent; that only skips the preview.
[[nodiscard]] bool EnsureMp4(
    Mp4Encoder& encoder,
    const std::filesystem::path& path,
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t fps) noexcept {
    if (encoder.IsOpen())
        return true;
    try {
        const auto quality =
            static_cast<std::uint64_t>(width) * height * fps / 5u;
        const auto bitrate = static_cast<std::uint32_t>(
            std::clamp<std::uint64_t>(quality, 8'000'000u, 100'000'000u));
        return encoder.Open(path, width, height, fps, bitrate);
    } catch (...) {
        return false;
    }
}

[[nodiscard]] std::filesystem::path AviPath(
    const std::filesystem::path& directory,
    const std::string& capture_name,
    const std::string_view pass_name,
    const std::string_view ending) {
    const auto synchronized_leaf =
        (capture_name == "world" &&
         (pass_name == "world" || pass_name == "zdepth")) ||
        (capture_name == "chroma" && pass_name == "chroma");
    const auto stem = synchronized_leaf
        ? std::string(pass_name)
        : capture_name + "_" + std::string(pass_name);
    return directory / (stem + std::string(ending));
}

[[nodiscard]] bool FinishAndPublishAvi(
    void*& writer,
    const std::filesystem::path& working_path,
    const std::filesystem::path& complete_path,
    const std::filesystem::path& partial_path) noexcept {
    if (writer == nullptr)
        return true;
    const auto finished = gmav_finish(writer);
    writer = nullptr;
    if (!finished)
        return false;
    std::error_code error{};
    std::filesystem::rename(
        working_path,
        partial_path.empty() ? complete_path : partial_path,
        error);
    return !error;
}

[[nodiscard]] std::filesystem::path PartialAviPath(
    const std::filesystem::path& directory,
    const std::string& capture_name,
    const std::string_view pass_name,
    const std::uint64_t written,
    const std::uint64_t expected) {
    if (written == expected && expected != 0)
        return {};
    const auto synchronized_leaf =
        (capture_name == "world" &&
         (pass_name == "world" || pass_name == "zdepth")) ||
        (capture_name == "chroma" && pass_name == "chroma");
    const auto stem = synchronized_leaf
        ? std::string(pass_name)
        : capture_name + "_" + std::string(pass_name);
    return directory /
        (stem + "_PARTIAL_" + std::to_string(written) + "-of-" +
         std::to_string(expected) + ".avi");
}

[[nodiscard]] bool FarDepthIsZero(const MovieFrame& frame) noexcept {
    if (frame.depth.empty() || frame.width == 0 || frame.height == 0)
        return true;
    std::size_t near_zero = 0;
    std::size_t near_one = 0;
    const auto observe = [&frame, &near_zero, &near_one](
        const std::uint32_t x,
        const std::uint32_t y) noexcept {
        const auto value = frame.depth[static_cast<std::size_t>(y) * frame.width + x];
        if (value <= 0.0001F)
            ++near_zero;
        if (value >= 0.9999F)
            ++near_one;
    };
    const auto step_x = std::max<std::uint32_t>(1, frame.width / 64);
    const auto step_y = std::max<std::uint32_t>(1, frame.height / 64);
    for (std::uint32_t x = 0; x < frame.width; x += step_x) {
        observe(x, 0);
        observe(x, frame.height - 1);
    }
    for (std::uint32_t y = 0; y < frame.height; y += step_y) {
        observe(0, y);
        observe(frame.width - 1, y);
    }
    return near_zero >= near_one;
}

struct GreenscreenFillResult final {
    std::uint64_t background_pixels{};
    std::uint64_t subject_pixels{};
};

[[nodiscard]] GreenscreenFillResult FillGreenscreenFarPlane(
    MovieFrame& frame,
    const std::uint32_t color_rgb) noexcept {
    GreenscreenFillResult result{};
    if (frame.color_bgr.empty() || frame.depth.empty() || frame.width == 0 ||
        frame.height == 0 || frame.color_stride < frame.width * 3u ||
        frame.depth.size() < static_cast<std::size_t>(frame.width) * frame.height) {
        return result;
    }

    const auto far_is_zero = FarDepthIsZero(frame);
    const std::array<std::uint8_t, 3> key_bgr{
        static_cast<std::uint8_t>(color_rgb & 0xFFu),
        static_cast<std::uint8_t>((color_rgb >> 8u) & 0xFFu),
        static_cast<std::uint8_t>((color_rgb >> 16u) & 0xFFu),
    };
    for (std::uint32_t y = 0; y < frame.height; ++y) {
        auto* row = frame.color_bgr.data() +
            static_cast<std::size_t>(y) * frame.color_stride;
        const auto depth_row = static_cast<std::size_t>(y) * frame.width;
        for (std::uint32_t x = 0; x < frame.width; ++x) {
            if (!IsGreenscreenFarPlaneDepth(frame.depth[depth_row + x], far_is_zero)) {
                ++result.subject_pixels;
                continue;
            }
            auto* pixel = row + static_cast<std::size_t>(x) * 3u;
            std::copy(key_bgr.begin(), key_bgr.end(), pixel);
            ++result.background_pixels;
        }
    }
    return result;
}

struct LinearResizeSample final {
    std::uint32_t lower{};
    std::uint32_t upper{};
    std::uint32_t weight{};
};

void BuildLinearResizeSamples(
    const std::uint32_t source_extent,
    const std::uint32_t target_extent,
    std::vector<LinearResizeSample>& samples) {
    samples.resize(target_extent);
    if (source_extent == 0 || target_extent == 0)
        return;
    for (std::uint32_t target = 0; target < target_extent; ++target) {
        const auto position =
            ((static_cast<double>(target) + 0.5) * source_extent / target_extent) - 0.5;
        const auto clamped = std::clamp(position, 0.0, static_cast<double>(source_extent - 1u));
        const auto lower = static_cast<std::uint32_t>(clamped);
        const auto upper = std::min(source_extent - 1u, lower + 1u);
        const auto fraction = clamped - lower;
        samples[target] = {
            lower,
            upper,
            static_cast<std::uint32_t>(fraction * 65536.0 + 0.5),
        };
    }
}

[[nodiscard]] std::uint8_t InterpolateByte(
    const std::uint8_t lower,
    const std::uint8_t upper,
    const std::uint32_t weight) noexcept {
    const auto inverse = 65536u - weight;
    return static_cast<std::uint8_t>(
        (static_cast<std::uint32_t>(lower) * inverse +
         static_cast<std::uint32_t>(upper) * weight + 32768u) >> 16u);
}

void ResizeColorFrame(
    const MovieFrame& source,
    MovieFrame& target,
    std::vector<std::uint8_t>& horizontal,
    std::vector<LinearResizeSample>& horizontal_samples,
    std::vector<LinearResizeSample>& vertical_samples,
    const MovieResizeRegion region,
    const std::uint32_t greenscreen_color_rgb,
    const bool greenscreen_active) {
    target.color_stride = (target.width * 3u + 3u) & ~3u;
    target.color_bgr.assign(
        static_cast<std::size_t>(target.color_stride) * target.height,
        std::uint8_t{0});
    if (source.color_bgr.empty() || region.width == 0 || region.height == 0)
        return;

    const std::array<std::uint8_t, 3> background{
        static_cast<std::uint8_t>(greenscreen_color_rgb & 0xFFu),
        static_cast<std::uint8_t>((greenscreen_color_rgb >> 8u) & 0xFFu),
        static_cast<std::uint8_t>((greenscreen_color_rgb >> 16u) & 0xFFu),
    };
    if (greenscreen_active &&
        (region.width != target.width || region.height != target.height)) {
        for (std::uint32_t y = 0; y < target.height; ++y) {
            auto* row = target.color_bgr.data() +
                static_cast<std::size_t>(y) * target.color_stride;
            for (std::uint32_t x = 0; x < target.width; ++x) {
                auto* pixel = row + static_cast<std::size_t>(x) * 3u;
                std::copy(background.begin(), background.end(), pixel);
            }
        }
    }

    BuildLinearResizeSamples(source.width, region.width, horizontal_samples);
    BuildLinearResizeSamples(source.height, region.height, vertical_samples);
    const auto horizontal_stride = static_cast<std::size_t>(region.width) * 3u;
    horizontal.resize(horizontal_stride * source.height);
    for (std::uint32_t y = 0; y < source.height; ++y) {
        const auto* source_row = source.color_bgr.data() +
            static_cast<std::size_t>(y) * source.color_stride;
        auto* horizontal_row = horizontal.data() +
            static_cast<std::size_t>(y) * horizontal_stride;
        for (std::uint32_t x = 0; x < region.width; ++x) {
            const auto sample = horizontal_samples[x];
            const auto* lower = source_row + static_cast<std::size_t>(sample.lower) * 3u;
            const auto* upper = source_row + static_cast<std::size_t>(sample.upper) * 3u;
            auto* output = horizontal_row + static_cast<std::size_t>(x) * 3u;
            for (std::size_t channel = 0; channel < 3; ++channel)
                output[channel] = InterpolateByte(lower[channel], upper[channel], sample.weight);
        }
    }
    for (std::uint32_t y = 0; y < region.height; ++y) {
        const auto sample = vertical_samples[y];
        const auto* lower = horizontal.data() +
            static_cast<std::size_t>(sample.lower) * horizontal_stride;
        const auto* upper = horizontal.data() +
            static_cast<std::size_t>(sample.upper) * horizontal_stride;
        auto* output = target.color_bgr.data() +
            static_cast<std::size_t>(region.y + y) * target.color_stride +
            static_cast<std::size_t>(region.x) * 3u;
        for (std::size_t byte = 0; byte < horizontal_stride; ++byte)
            output[byte] = InterpolateByte(lower[byte], upper[byte], sample.weight);
    }
}

void ResizeDepthFrame(
    const MovieFrame& source,
    MovieFrame& target,
    const MovieResizeRegion region) {
    if (source.depth.empty() || region.width == 0 || region.height == 0)
        return;
    target.depth.assign(
        static_cast<std::size_t>(target.width) * target.height,
        FarDepthIsZero(source) ? 0.0F : 1.0F);
    for (std::uint32_t y = 0; y < region.height; ++y) {
        const auto source_y = MovieNearestSourceCoordinate(
            y, region.height, source.height);
        for (std::uint32_t x = 0; x < region.width; ++x) {
            const auto source_x = MovieNearestSourceCoordinate(
                x, region.width, source.width);
            target.depth[
                static_cast<std::size_t>(region.y + y) * target.width + region.x + x] =
                source.depth[static_cast<std::size_t>(source_y) * source.width + source_x];
        }
    }
}

[[nodiscard]] MovieFrame& PrepareOutputFrame(
    MovieFrame& source,
    MovieFrame& resized,
    std::vector<std::uint8_t>& horizontal,
    std::vector<LinearResizeSample>& horizontal_samples,
    std::vector<LinearResizeSample>& vertical_samples,
    const std::uint32_t target_width,
    const std::uint32_t target_height,
    const std::uint32_t greenscreen_color_rgb,
    const bool greenscreen_active) {
    if (source.width == target_width && source.height == target_height)
        return source;
    resized.index = source.index;
    resized.replay_tick = source.replay_tick;
    resized.width = target_width;
    resized.height = target_height;
    const auto region = FitMovieResizeRegion(
        source.width, source.height, target_width, target_height);
    if (!source.color_bgr.empty()) {
        ResizeColorFrame(
            source,
            resized,
            horizontal,
            horizontal_samples,
            vertical_samples,
            region,
            greenscreen_color_rgb,
            greenscreen_active);
    } else {
        resized.color_bgr.clear();
        resized.color_stride = 0;
    }
    if (!source.depth.empty())
        ResizeDepthFrame(source, resized, region);
    else
        resized.depth.clear();
    return resized;
}

void BuildDepthPreview(const MovieFrame& frame, std::vector<std::uint8_t>& output) {
    const auto stride = (frame.width * 3u + 3u) & ~3u;
    output.assign(static_cast<std::size_t>(stride) * frame.height, std::uint8_t{0});
    const auto far_is_zero = FarDepthIsZero(frame);
    for (std::uint32_t y = 0; y < frame.height; ++y) {
        for (std::uint32_t x = 0; x < frame.width; ++x) {
            const auto pixel = static_cast<std::size_t>(y) * frame.width + x;
            const auto target = static_cast<std::size_t>(y) * stride + x * 3u;
            const auto near_weight = far_is_zero
                ? std::clamp(frame.depth[pixel], 0.0F, 1.0F)
                : 1.0F - std::clamp(frame.depth[pixel], 0.0F, 1.0F);
            // Raw reversed-Z values in the owner's take occupied roughly
            // 0..0.18, making a linear 8-bit AVI almost black. Preserve exact
            // device depth in PFM and use a stable gamma curve only for this
            // explicitly non-metric preview.
            // pow(x, 0.25) is sqrt(sqrt(x)). The equivalent square-root form
            // avoids millions of general exponent calls per captured frame.
            const auto display = std::sqrt(std::sqrt(near_weight));
            const auto value = static_cast<std::uint8_t>(display * 255.0F + 0.5F);
            output[target] = value;
            output[target + 1] = value;
            output[target + 2] = value;
        }
    }
}

// A background worker that throws would terminate the game process, so the
// whole body (including the startup and finalization allocation paths) is
// guarded by the caller rather than only the per-frame work.
void WriterMainBody() {
    auto& state = State();
    static_cast<void>(SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL));
    AviOutputs avi{};
    Mp4Encoder mp4_preview{};
    auto mp4_preview_failed = false;
    std::vector<std::uint8_t> converted{};
    MovieFrame resized{};
    std::vector<std::uint8_t> horizontal{};
    std::vector<LinearResizeSample> horizontal_samples{};
    std::vector<LinearResizeSample> vertical_samples{};
    std::filesystem::path take_directory{};
    std::string capture_name{};
    {
        std::lock_guard lock(state.mutex);
        take_directory = state.take_directory;
        capture_name = state.capture_name;
    }
    const auto avi_directory = take_directory / L"avi";
    const auto color_directory = take_directory / L"color";
    for (;;) {
        MovieFrame frame{};
        {
            std::unique_lock lock(state.mutex);
            state.work_ready.wait(lock, [&state]() noexcept {
                return state.writer_stop || !state.queue.empty();
            });
            if (state.queue.empty() && state.writer_stop)
                break;
            frame = std::move(state.queue.front());
            state.queue.pop_front();
            state.queue_space.notify_one();
        }

        const auto writer_started = std::chrono::steady_clock::now();
        try {
            const auto passes = state.pass_flags.load(std::memory_order_acquire);
            const auto output_mode = state.output_mode.load(std::memory_order_acquire);
            const auto fps = OutputFrameRate(
                state.fps.load(std::memory_order_acquire),
                state.playback_speed.load(std::memory_order_acquire));
            if ((passes & movie_capture_pass_greenscreen_free_camera) != 0) {
                const auto fill = FillGreenscreenFarPlane(
                    frame,
                    state.greenscreen_color_rgb.load(std::memory_order_acquire));
                state.greenscreen_background_pixels.fetch_add(
                    fill.background_pixels,
                    std::memory_order_acq_rel);
                state.greenscreen_subject_pixels.fetch_add(
                    fill.subject_pixels,
                    std::memory_order_acq_rel);
                if (fill.background_pixels != 0) {
                    state.greenscreen_frames_with_background.fetch_add(
                        1,
                        std::memory_order_acq_rel);
                } else {
                    // A Chroma frame with no physical key-color region is not a
                    // usable Green Screen plate even when libgmavi can encode it.
                    state.pass_incomplete.store(true, std::memory_order_release);
                    // The verdict alone does not say which depth comparison
                    // rejected the frame. Record the first failing frame's
                    // condition and observed depth range so the manifest can
                    // name the cause instead of only the symptom.
                    auto unobserved = static_cast<std::uint32_t>(
                        GreenscreenDepthReason::not_observed);
                    const auto diagnosis = DiagnoseGreenscreenDepth(
                        frame.depth.data(),
                        frame.width,
                        frame.height,
                        FarDepthIsZero(frame));
                    if (state.greenscreen_failure_reason.compare_exchange_strong(
                            unobserved,
                            static_cast<std::uint32_t>(diagnosis.reason),
                            std::memory_order_acq_rel)) {
                        state.greenscreen_failure_far_is_zero.store(
                            diagnosis.far_depth_is_zero,
                            std::memory_order_release);
                        state.greenscreen_failure_depth_min.store(
                            diagnosis.minimum,
                            std::memory_order_release);
                        state.greenscreen_failure_depth_max.store(
                            diagnosis.maximum,
                            std::memory_order_release);
                    }
                }
                if (fill.subject_pixels != 0) {
                    state.greenscreen_frames_with_subject.fetch_add(
                        1,
                        std::memory_order_acq_rel);
                }
            }
            auto& output_frame = PrepareOutputFrame(
                frame,
                resized,
                horizontal,
                horizontal_samples,
                vertical_samples,
                state.output_width.load(std::memory_order_acquire),
                state.output_height.load(std::memory_order_acquire),
                state.greenscreen_color_rgb.load(std::memory_order_acquire),
                state.greenscreen_active.load(std::memory_order_acquire));
            const auto wants_beauty_tga = output_mode != 1 &&
                (passes & movie_capture_pass_beauty) != 0;
            const auto wants_native_beauty = output_mode != 0 &&
                (passes & movie_capture_pass_beauty) != 0;
            auto output_failed = false;
            auto wrote_any = false;
            auto wrote_avi = false;
            if ((passes & movie_capture_pass_world_depth_pfm) != 0) {
                if (output_frame.depth.empty()) {
                    state.pass_incomplete.store(true, std::memory_order_release);
                } else {
                    const auto wrote = WriteDepthFrame(take_directory / L"depth", output_frame);
                    output_failed = output_failed || !wrote;
                    wrote_any = wrote_any || wrote;
                    if (wrote)
                        state.depth_pfm_frames_written.fetch_add(1, std::memory_order_acq_rel);
                }
            }

            if (wants_beauty_tga) {
                const auto wrote = WriteTgaFrame(
                    color_directory,
                    capture_name,
                    output_frame);
                output_failed = output_failed || !wrote;
                wrote_any = wrote_any || wrote;
                if (wrote) {
                    state.beauty_tga_frames_written.fetch_add(
                        1,
                        std::memory_order_acq_rel);
                }
            }

            if (wants_native_beauty) {
                const auto wrote = !output_frame.color_bgr.empty() &&
                    EnsureAvi(
                        avi.beauty,
                        AviPath(avi_directory, capture_name, "world", ".partial.avi"),
                        output_frame.width,
                        output_frame.height,
                        fps) &&
                    gmav_add(avi.beauty, output_frame.color_bgr.data());
                output_failed = output_failed || !wrote;
                wrote_any = wrote_any || wrote;
                wrote_avi = wrote_avi || wrote;
                if (wrote) {
                    state.beauty_frames_written.fetch_add(1, std::memory_order_acq_rel);
                    // Playable preview: the uncompressed AVI cannot stream from
                    // the capture disk, so a small H.264 sibling is written with
                    // the same frames. Encoder absence is not a take failure.
                    if (!mp4_preview_failed) {
                        const auto ready = EnsureMp4(
                            mp4_preview,
                            AviPath(avi_directory, capture_name, "world", ".partial.mp4"),
                            output_frame.width,
                            output_frame.height,
                            fps) &&
                            mp4_preview.WriteBgrFrame(
                                output_frame.color_bgr.data(),
                                output_frame.color_stride);
                        if (ready) {
                            state.mp4_frames_written.fetch_add(1, std::memory_order_acq_rel);
                        } else {
                            mp4_preview_failed = true;
                            static_cast<void>(mp4_preview.Close());
                        }
                    }
                }
            }
            if ((passes & movie_capture_pass_world_depth_avi) != 0) {
                if (output_frame.depth.empty()) {
                    state.pass_incomplete.store(true, std::memory_order_release);
                } else {
                    BuildDepthPreview(output_frame, converted);
                    const auto wrote = EnsureAvi(
                            avi.depth,
                            AviPath(avi_directory, capture_name, "zdepth", ".partial.avi"),
                            output_frame.width,
                            output_frame.height,
                            fps) &&
                        gmav_add(avi.depth, converted.data());
                    output_failed = output_failed || !wrote;
                    wrote_any = wrote_any || wrote;
                    wrote_avi = wrote_avi || wrote;
                    if (wrote)
                        state.depth_avi_frames_written.fetch_add(1, std::memory_order_acq_rel);
                }
            }
            if ((passes & movie_capture_pass_greenscreen_free_camera) != 0) {
                if (output_frame.color_bgr.empty()) {
                    state.pass_incomplete.store(true, std::memory_order_release);
                } else {
                    const auto wrote = EnsureAvi(
                            avi.depth_key,
                            AviPath(avi_directory, capture_name, "chroma", ".partial.avi"),
                            output_frame.width,
                            output_frame.height,
                            fps) &&
                        gmav_add(avi.depth_key, output_frame.color_bgr.data());
                    output_failed = output_failed || !wrote;
                    wrote_any = wrote_any || wrote;
                    wrote_avi = wrote_avi || wrote;
                    if (wrote)
                        state.depth_key_frames_written.fetch_add(1, std::memory_order_acq_rel);
                }
            }

            // A pass that failed for this frame must not hide a complete AVI that
            // the same frame successfully wrote: the artifact exists on disk and
            // the manifest has to agree with it.
            if (wrote_avi)
                state.avi_available.store(true, std::memory_order_release);
            if (output_failed) {
                MarkFatalFailure();
            } else if (wrote_any) {
                state.written_frames.fetch_add(1, std::memory_order_acq_rel);
            }
        } catch (...) {
            MarkFatalFailure();
        }
        UpdateMaximum(state.maximum_writer_microseconds, ElapsedMicroseconds(writer_started));

        RecycleFrameBuffers(frame);
    }
    const auto expected = state.observed_frames.load(std::memory_order_acquire);
    const auto passes = state.pass_flags.load(std::memory_order_acquire);
    const auto output_mode = state.output_mode.load(std::memory_order_acquire);
    const auto requested_pass_is_partial = [expected](
        const bool requested,
        const std::uint64_t written) noexcept {
        return requested && (expected == 0 || written != expected);
    };
    if (requested_pass_is_partial(
            output_mode != 1 && (passes & movie_capture_pass_beauty) != 0,
            state.beauty_tga_frames_written.load(std::memory_order_acquire)) ||
        requested_pass_is_partial(
            output_mode != 0 && (passes & movie_capture_pass_beauty) != 0,
            state.beauty_frames_written.load(std::memory_order_acquire)) ||
        requested_pass_is_partial(
            (passes & movie_capture_pass_world_depth_pfm) != 0,
            state.depth_pfm_frames_written.load(std::memory_order_acquire)) ||
        requested_pass_is_partial(
            (passes & movie_capture_pass_world_depth_avi) != 0,
            state.depth_avi_frames_written.load(std::memory_order_acquire)) ||
        requested_pass_is_partial(
            (passes & movie_capture_pass_greenscreen_free_camera) != 0,
            state.depth_key_frames_written.load(std::memory_order_acquire))) {
        state.pass_incomplete.store(true, std::memory_order_release);
    }
    const auto finish = [&avi_directory, &capture_name, expected](
        void*& writer,
        const std::string_view pass_name,
        const std::uint64_t written) noexcept {
        return FinishAndPublishAvi(
            writer,
            AviPath(avi_directory, capture_name, pass_name, ".partial.avi"),
            AviPath(avi_directory, capture_name, pass_name, ".avi"),
            PartialAviPath(avi_directory, capture_name, pass_name, written, expected));
    };
    const auto beauty_finished = finish(
        avi.beauty,
        "world",
        state.beauty_frames_written.load(std::memory_order_acquire));
    const auto depth_finished = finish(
        avi.depth,
        "zdepth",
        state.depth_avi_frames_written.load(std::memory_order_acquire));
    const auto key_finished = finish(
        avi.depth_key,
        "chroma",
        state.depth_key_frames_written.load(std::memory_order_acquire));
    // The preview never blocks a take: it is finalized on its own and an
    // incomplete encode is discarded.
    const auto mp4_partial = AviPath(avi_directory, capture_name, "world", ".partial.mp4");
    const auto mp4_complete = AviPath(avi_directory, capture_name, "world", ".mp4");
    const auto mp4_frames = mp4_preview.FramesWritten();
    const auto mp4_closed = mp4_preview.Close();
    if (mp4_closed && !mp4_preview_failed && mp4_frames > 0) {
        std::error_code rename_error{};
        std::filesystem::rename(mp4_partial, mp4_complete, rename_error);
        if (!rename_error) {
            state.mp4_available.store(true, std::memory_order_release);
        } else {
            // A finished encode that cannot be published must not leave its
            // partial beside the take either: the preview is either complete
            // under its published name or absent.
            std::error_code remove_error{};
            std::filesystem::remove(mp4_partial, remove_error);
        }
    } else {
        std::error_code remove_error{};
        std::filesystem::remove(mp4_partial, remove_error);
    }
    if (!beauty_finished || !depth_finished || !key_finished)
        MarkFatalFailure();
}

void WriterMain() noexcept {
    try {
        WriterMainBody();
    } catch (...) {
        // Starting the take and finalizing its files both allocate; an escaped
        // exception here used to call std::terminate and take Deadlock with it.
        MarkFatalFailure();
    }
}

void WriteManifest(
    const std::filesystem::path& take_directory,
    const bool depth_was_available,
    const bool avi_was_available) noexcept {
    if (take_directory.empty())
        return;
    auto& state = State();
    try {
        std::ofstream manifest(take_directory / L"deadlockmvm_capture.txt", std::ios::trunc);
        if (!manifest)
            return;
        const auto expected = state.observed_frames.load(std::memory_order_acquire);
        const auto passes = state.pass_flags.load(std::memory_order_acquire);
        const auto output_mode = state.output_mode.load(std::memory_order_acquire);
        const auto mp4_requested = output_mode != 0 &&
            (passes & movie_capture_pass_beauty) != 0;
        const auto audio = GetEngineMovieAudioStatus();
        const auto& look = state.look;
        manifest << std::setprecision(std::numeric_limits<float>::max_digits10)
            << "Reshade schema/revision: " << look.schema_version << '/' << look.revision << '\n'
            << "Reshade enabled/strength: " << look.enabled << '/' << look.strength << '\n'
            << "Reshade color domain: post-tonemap SDR; explicit sRGB decode/encode; no HDR recovery\n"
            << "Reshade capture policy: frozen for take; pre-effect cadence; raw depth/chroma bypass\n"
            << "Reshade exposure/contrast/saturation/vibrance: " << look.exposure << '/' << look.contrast << '/' << look.saturation << '/' << look.vibrance << '\n'
            << "Reshade temperature/tint: " << look.temperature << '/' << look.tint << '\n'
            << "Reshade lift RGB: " << look.lift_r << '/' << look.lift_g << '/' << look.lift_b << '\n'
            << "Reshade gamma RGB: " << look.gamma_r << '/' << look.gamma_g << '/' << look.gamma_b << '\n'
            << "Reshade gain RGB: " << look.gain_r << '/' << look.gain_g << '/' << look.gain_b << '\n'
            << "Reshade shadows/highlights: " << look.shadows << '/' << look.highlights << '\n'
            << "Reshade bloom threshold/knee/intensity/radius/quality: " << look.bloom_threshold << '/' << look.bloom_knee << '/' << look.bloom_intensity << '/' << look.bloom_radius << '/' << look.bloom_quality << '\n'
            << "Reshade sharpen/vignette/grain/seed: " << look.sharpen << '/' << look.vignette << '/' << look.grain_strength << '/' << look.grain_seed << '\n'
            << "Reshade grain clock: accepted cinematic frame index; preview replay tick\n"
            << "Reshade LUT size/intensity/revision: " << look.lut_size << '/' << look.lut_intensity << '/' << look.lut_revision << '\n'
            << "Reshade LUT RGB float32 LE FNV1a64: " << std::hex << state.lut_content_hash << std::dec << '\n';
        const auto write_pass = [&manifest, expected](
            const char* name,
            const bool requested,
            const std::uint64_t written) {
            manifest << name << ": ";
            if (!requested) {
                manifest << "not requested\n";
            } else if (expected != 0 && written == expected) {
                manifest << "complete (" << written << '/' << expected << ")\n";
            } else {
                manifest << "PARTIAL (" << written << '/' << expected << ")\n";
            }
        };
        manifest <<
            "DeadLockMVM cinematic capture\n"
            "Frame rate: " << OutputFrameRate(
                state.fps.load(std::memory_order_acquire),
                state.playback_speed.load(std::memory_order_acquire)) << " FPS\n"
            "Capture sampling rate: " <<
                state.fps.load(std::memory_order_acquire) << " FPS\n"
            "Cinematic playback speed: " <<
                state.playback_speed.load(std::memory_order_acquire) << "x\n"
            "TGA output: DeadLockMVM native 24-bit BGR writer\n"
            "WAV output: Deadlock internal synchronized movie-audio sink\n"
            "AVI output: libgmavi uncompressed 24-bit BGR / OpenDML\n"
            "Depth: normalized D3D11 device depth, not linear camera-space depth\n"
            "Source resolution: " << state.source_width.load(std::memory_order_acquire) << 'x' <<
                state.source_height.load(std::memory_order_acquire) << '\n'
            << "Output resolution: " << state.output_width.load(std::memory_order_acquire) << 'x' <<
                state.output_height.load(std::memory_order_acquire) << '\n'
            << "Color resize: separable bilinear; depth resize: nearest-neighbor\n"
            "Pass flags: " << passes << '\n'
            << "Frame limit target: " <<
                state.expected_frame_count.load(std::memory_order_acquire) << '\n'
            << "Frames observed: " << expected << '\n'
            << "Frames finalized: " << state.written_frames.load(std::memory_order_acquire) << '\n'
            << "World TGA frames: " <<
                state.beauty_tga_frames_written.load(std::memory_order_acquire) << '\n'
            << "World AVI frames: " <<
                state.beauty_frames_written.load(std::memory_order_acquire) << '\n'
            << "Z-Depth PFM frames: " <<
                state.depth_pfm_frames_written.load(std::memory_order_acquire) << '\n'
            << "Z-Depth AVI frames: " <<
                state.depth_avi_frames_written.load(std::memory_order_acquire) << '\n'
            << "Chroma AVI frames: " <<
                state.depth_key_frames_written.load(std::memory_order_acquire) << '\n'
            << "Present calls observed: " << state.present_calls.load(std::memory_order_acquire) << '\n'
            << "Repeated camera-sequence Presents observed: " <<
                state.repeated_camera_sequences_captured.load(std::memory_order_acquire) << '\n'
            << "Repeated sampled visual frames observed: " <<
                state.repeated_visual_samples_captured.load(std::memory_order_acquire) << '\n'
            << "Cadence status: " <<
                (state.repeated_visual_samples_captured.load(std::memory_order_acquire) == 0
                    ? "no repeated sampled visual frame observed"
                    : "WARNING - repeated sampled visual frames require rendered review") << '\n'
            << "Depth frames unavailable at Present: " <<
                state.depth_frames_unavailable.load(std::memory_order_acquire) << '\n'
            << "Capture queue capacity: " <<
                state.queue_capacity.load(std::memory_order_acquire) << " frames\n"
            << "Maximum capture queue depth: " <<
                state.maximum_queue_depth.load(std::memory_order_acquire) << " frames\n"
            << "Queue backpressure waits: " <<
                state.queue_backpressure_events.load(std::memory_order_acquire) << '\n'
            << "Queue backpressure total: " <<
                state.queue_backpressure_microseconds.load(std::memory_order_acquire) / 1000u << " ms\n"
            << "Queue backpressure wait-budget failures: " <<
                state.queue_backpressure_timeouts.load(std::memory_order_acquire) << '\n'
            << "Longest queue backpressure wait: " <<
                state.maximum_queue_wait_microseconds.load(std::memory_order_acquire) / 1000u << " ms\n"
            << "Slowest GPU readback/conversion: " <<
                state.maximum_capture_microseconds.load(std::memory_order_acquire) / 1000u << " ms\n"
            << "Slowest frame write: " <<
                state.maximum_writer_microseconds.load(std::memory_order_acquire) / 1000u << " ms\n"
            << "First replay tick: " << state.first_replay_tick.load(std::memory_order_acquire) << '\n'
            << "Last replay tick: " << state.last_replay_tick.load(std::memory_order_acquire) << '\n'
            << "Replay timing digest: 0x" << std::hex <<
                state.replay_timing_digest.load(std::memory_order_acquire) << std::dec << '\n'
            << "Rendered camera samples: " <<
                state.rendered_camera_samples.load(std::memory_order_acquire) << '\n'
            << "Rendered camera digest: 0x" << std::hex <<
                state.rendered_camera_digest.load(std::memory_order_acquire) << std::dec << '\n'
            << "Green Screen isolation: " <<
                (state.greenscreen_active.load(std::memory_order_acquire)
                    ? "Deadlock blank-world plus D3D11 and writer-side far-plane key fill"
                    : "not requested") << '\n'
            << "Green Screen background pixels: " <<
                state.greenscreen_background_pixels.load(std::memory_order_acquire) << '\n'
            << "Green Screen subject pixels: " <<
                state.greenscreen_subject_pixels.load(std::memory_order_acquire) << '\n'
            << "Green Screen frames with key background: " <<
                state.greenscreen_frames_with_background.load(std::memory_order_acquire) << '\n'
            << "Green Screen frames with isolated subject: " <<
                state.greenscreen_frames_with_subject.load(std::memory_order_acquire) << '\n'
            << "Green Screen validation: " <<
                (!state.greenscreen_active.load(std::memory_order_acquire)
                    ? "not requested"
                    : state.greenscreen_background_pixels.load(std::memory_order_acquire) == 0
                        ? "FAILED - no key background was produced"
                        : state.greenscreen_subject_pixels.load(std::memory_order_acquire) == 0
                            ? "REVIEW - no isolated subject pixels were observed"
                            : "ready for rendered artifact review") << '\n'
            << "Movie frame composition ready: " <<
                (state.frame_composition_ready.load(std::memory_order_acquire)
                    ? "yes" : "no") << '\n'
            << "Depth available: " << (depth_was_available ? "yes" : "no") << '\n'
            << "Depth observer armed during take: " <<
                (state.depth_observer_observed.load(std::memory_order_acquire)
                    ? "yes" : "no") << '\n'
            << "All requested visual passes complete: " <<
                (state.pass_incomplete.load(std::memory_order_acquire) ? "no" : "yes") << '\n'
            << "Movie audio requested: " <<
                (state.capture_audio.load(std::memory_order_acquire) ? "yes" : "no") << '\n'
            << "Movie audio recorder started: " <<
                (state.capture_audio.load(std::memory_order_acquire)
                    ? (audio.started ? "yes" : "no")
                    : "not requested") << '\n'
            << "Movie audio stop invoked: " <<
                (state.capture_audio.load(std::memory_order_acquire)
                    ? (audio.stop_invoked ? "yes" : "no")
                    : "not requested") << '\n'
            << "WAV present at native finalization: " <<
                (state.capture_audio.load(std::memory_order_acquire)
                    ? (audio.file_available ? "yes" : "no")
                    : "not requested") << '\n'
            << "Movie audio error: " <<
                (state.capture_audio.load(std::memory_order_acquire)
                    ? DescribeEngineMovieAudioError(audio.error)
                    : "not requested") << '\n'
            << "AVI available: " << (avi_was_available ? "yes" : "no") << '\n'
            << "Playable MP4: " <<
                (mp4_requested
                    ? (state.mp4_available.load(std::memory_order_acquire)
                        ? "yes" : "no")
                    : "not requested") << '\n'
            << "Playable MP4 frames: " <<
                state.mp4_frames_written.load(std::memory_order_acquire) << '\n'
            << "Artifact rule: completed AVIs use .avi; incomplete AVIs are named "
               "_PARTIAL_<written>-of-<observed>.avi\n"
            << "Failure observed: " <<
                (state.fatal_failed.load(std::memory_order_acquire) ? "yes" : "no") << '\n';
        // A failed plate names its own cause: the depth condition that rejected
        // the first failing frame and the device-depth range it observed, so
        // the far-plane polarity is visible without a debugger. A take with no
        // failed plate keeps its manifest unchanged.
        const auto greenscreen_reason = static_cast<GreenscreenDepthReason>(
            state.greenscreen_failure_reason.load(std::memory_order_acquire));
        if (greenscreen_reason != GreenscreenDepthReason::not_observed) {
            manifest
                << "Green Screen failure reason: "
                << DescribeGreenscreenDepthReason(greenscreen_reason) << '\n'
                << "Green Screen failure depth range: "
                << state.greenscreen_failure_depth_min.load(std::memory_order_acquire) << '/'
                << state.greenscreen_failure_depth_max.load(std::memory_order_acquire) << '\n'
                << "Green Screen failure far plane at: "
                << (state.greenscreen_failure_far_is_zero.load(std::memory_order_acquire)
                        ? "0" : "1")
                << '\n';
        }
        write_pass(
            "World TGA",
            output_mode != 1 && (passes & movie_capture_pass_beauty) != 0,
            state.beauty_tga_frames_written.load(std::memory_order_acquire));
        write_pass(
            "World AVI",
            output_mode != 0 && (passes & movie_capture_pass_beauty) != 0,
            state.beauty_frames_written.load(std::memory_order_acquire));
        write_pass(
            "Z-Depth PFM",
            (passes & movie_capture_pass_world_depth_pfm) != 0,
            state.depth_pfm_frames_written.load(std::memory_order_acquire));
        write_pass(
            "Z-Depth preview AVI",
            (passes & movie_capture_pass_world_depth_avi) != 0,
            state.depth_avi_frames_written.load(std::memory_order_acquire));
        write_pass(
            "Chroma AVI",
            (passes & movie_capture_pass_greenscreen_free_camera) != 0,
            state.depth_key_frames_written.load(std::memory_order_acquire));
    } catch (...) {
        MarkFatalFailure();
    }
}

void StopSession() noexcept {
    auto& state = State();
    if (!state.active.exchange(false, std::memory_order_acq_rel) &&
        !state.writer.joinable()) {
        state.frame_composition_ready.store(false, std::memory_order_release);
        ReleaseStaging();
        return;
    }
    std::filesystem::path take_directory{};
    {
        std::lock_guard lock(state.mutex);
        take_directory = state.take_directory;
        state.writer_stop = true;
    }
    const auto depth_was_available =
        state.depth_available.load(std::memory_order_acquire);
    StopEngineMovieAudio();
    ReleaseStaging();
    state.work_ready.notify_all();
    state.queue_space.notify_all();
    if (state.writer.joinable())
        state.writer.join();
    const auto avi_was_available = state.avi_available.load(std::memory_order_acquire);
    WriteManifest(take_directory, depth_was_available, avi_was_available);
    {
        std::lock_guard lock(state.mutex);
        state.queue.clear();
        state.recycled_depth.clear();
        state.recycled_color.clear();
        state.writer_stop = false;
        state.take_directory.clear();
        state.capture_name.clear();
    }
    state.depth_available.store(false, std::memory_order_release);
    state.avi_available.store(false, std::memory_order_release);
    state.mp4_available.store(false, std::memory_order_release);
    state.mp4_frames_written.store(0, std::memory_order_release);
    state.audio_started.store(false, std::memory_order_release);
    state.greenscreen_active.store(false, std::memory_order_release);
    state.frame_composition_ready.store(false, std::memory_order_release);
}

[[nodiscard]] bool StartSession(const MovieCaptureConfiguration& configuration) noexcept {
    auto& state = State();
    try {
        if (!IsSafeCaptureName(configuration.capture_name) ||
            configuration.take_directory.empty() ||
            configuration.fps < 1 || configuration.fps > 1000 ||
            configuration.output_mode > 2 ||
            configuration.output_width == 0 || configuration.output_width > 7680 ||
            configuration.output_height == 0 || configuration.output_height > 4320 ||
            !std::isfinite(configuration.playback_speed) ||
            configuration.playback_speed <= 0.0 ||
            configuration.pass_flags == movie_capture_pass_none ||
            (configuration.pass_flags & ~kKnownMovieCapturePassFlags) != 0 ||
            ((configuration.pass_flags & movie_capture_pass_greenscreen_free_camera) != 0 &&
             !configuration.greenscreen_active) ||
            (configuration.expected_frame_count != 0 &&
             configuration.pass_flags != movie_capture_pass_greenscreen_free_camera) ||
            (configuration.greenscreen_color_rgb & 0xFF000000u) != 0) {
            return false;
        }
        const auto take = Utf8Path(configuration.take_directory).lexically_normal();
        if (take.empty() || !take.is_absolute() ||
            take.filename() != std::filesystem::path(std::string(configuration.capture_name)))
            return false;
        std::filesystem::create_directories(take);
        if (configuration.capture_audio)
            std::filesystem::create_directories(take / L"audio");
        if (configuration.output_mode != 1 &&
            (configuration.pass_flags & movie_capture_pass_beauty) != 0) {
            std::filesystem::create_directories(take / L"color");
        }
        if ((configuration.pass_flags & movie_capture_pass_world_depth_pfm) != 0)
            std::filesystem::create_directories(take / L"depth");
        if (configuration.output_mode != 0 ||
            (configuration.pass_flags & (movie_capture_pass_world_depth_avi |
                                         movie_capture_pass_greenscreen_free_camera)) != 0) {
            std::filesystem::create_directories(take / L"avi");
        }
        if ((configuration.pass_flags & movie_capture_pass_world_depth_pfm) != 0) {
            std::ofstream metadata(take / L"depth" / L"README.txt", std::ios::trunc);
            if (!metadata)
                return false;
            metadata <<
                "DeadLockMVM World Depth\n"
                "Format: little-endian PFM (32-bit float, one channel)\n"
                "Values: raw normalized D3D11 device depth before the SMVM overlay\n"
                "Rows: PFM bottom-to-top order\n";
        }
        {
            std::lock_guard lock(state.mutex);
            state.take_directory = take;
            state.capture_name.assign(configuration.capture_name);
            state.look = configuration.look;
            state.lut_content_hash = configuration.lut_content_hash;
            state.writer_stop = false;
            state.queue.clear();
            state.recycled_depth.clear();
            state.recycled_color.clear();
        }
        state.fps.store(configuration.fps, std::memory_order_release);
        state.playback_speed.store(configuration.playback_speed, std::memory_order_release);
        state.output_mode.store(configuration.output_mode, std::memory_order_release);
        state.pass_flags.store(configuration.pass_flags, std::memory_order_release);
        state.output_width.store(configuration.output_width, std::memory_order_release);
        state.output_height.store(configuration.output_height, std::memory_order_release);
        state.source_width.store(0, std::memory_order_release);
        state.source_height.store(0, std::memory_order_release);
        state.greenscreen_active.store(configuration.greenscreen_active, std::memory_order_release);
        state.frame_composition_ready.store(
            !configuration.greenscreen_active,
            std::memory_order_release);
        state.capture_audio.store(configuration.capture_audio, std::memory_order_release);
        state.greenscreen_color_rgb.store(
            configuration.greenscreen_color_rgb,
            std::memory_order_release);
        state.expected_frame_count.store(
            configuration.expected_frame_count,
            std::memory_order_release);
        state.fatal_failed.store(false, std::memory_order_release);
        state.pass_incomplete.store(false, std::memory_order_release);
        state.depth_available.store(false, std::memory_order_release);
        state.depth_observer_observed.store(false, std::memory_order_release);
        state.avi_available.store(false, std::memory_order_release);
        state.mp4_available.store(false, std::memory_order_release);
        state.mp4_frames_written.store(0, std::memory_order_release);
        state.audio_started.store(false, std::memory_order_release);
        state.audio_failed.store(false, std::memory_order_release);
        state.observed_frames.store(0, std::memory_order_release);
        state.written_frames.store(0, std::memory_order_release);
        state.beauty_tga_frames_written.store(0, std::memory_order_release);
        state.beauty_frames_written.store(0, std::memory_order_release);
        state.depth_pfm_frames_written.store(0, std::memory_order_release);
        state.depth_avi_frames_written.store(0, std::memory_order_release);
        state.depth_key_frames_written.store(0, std::memory_order_release);
        state.greenscreen_background_pixels.store(0, std::memory_order_release);
        state.greenscreen_subject_pixels.store(0, std::memory_order_release);
        state.greenscreen_frames_with_background.store(0, std::memory_order_release);
        state.greenscreen_frames_with_subject.store(0, std::memory_order_release);
        state.greenscreen_failure_reason.store(
            static_cast<std::uint32_t>(GreenscreenDepthReason::not_observed),
            std::memory_order_release);
        state.greenscreen_failure_far_is_zero.store(false, std::memory_order_release);
        state.greenscreen_failure_depth_min.store(0.0F, std::memory_order_release);
        state.greenscreen_failure_depth_max.store(0.0F, std::memory_order_release);
        state.present_calls.store(0, std::memory_order_release);
        state.repeated_camera_sequences_captured.store(0, std::memory_order_release);
        state.repeated_visual_samples_captured.store(0, std::memory_order_release);
        state.last_visual_fingerprint.store(0, std::memory_order_release);
        state.depth_frames_unavailable.store(0, std::memory_order_release);
        state.queue_backpressure_events.store(0, std::memory_order_release);
        state.queue_backpressure_microseconds.store(0, std::memory_order_release);
        state.queue_backpressure_timeouts.store(0, std::memory_order_release);
        state.maximum_queue_wait_microseconds.store(0, std::memory_order_release);
        state.maximum_capture_microseconds.store(0, std::memory_order_release);
        state.maximum_writer_microseconds.store(0, std::memory_order_release);
        state.queue_capacity.store(
            static_cast<std::uint32_t>(kMinimumMovieCaptureQueuedFrames),
            std::memory_order_release);
        state.maximum_queue_depth.store(0, std::memory_order_release);
        state.last_camera_frame_sequence.store(0, std::memory_order_release);
        state.replay_timing_digest.store(14695981039346656037ULL, std::memory_order_release);
        state.rendered_camera_digest.store(14695981039346656037ULL, std::memory_order_release);
        state.rendered_camera_samples.store(0, std::memory_order_release);
        state.first_replay_tick.store(-1, std::memory_order_release);
        state.last_replay_tick.store(-1, std::memory_order_release);
        const auto wave_path = take / L"audio" /
            (std::string(configuration.capture_name) + ".wav");
        const auto audio_started = configuration.capture_audio && StartEngineMovieAudio(wave_path);
        state.audio_started.store(audio_started, std::memory_order_release);
        state.audio_failed.store(
            configuration.capture_audio && !audio_started,
            std::memory_order_release);
        state.writer = std::thread(&WriterMain);
        state.active.store(true, std::memory_order_release);
        return true;
    } catch (...) {
        StopEngineMovieAudio();
        return false;
    }
}

[[nodiscard]] bool ConfigurationMatches(
    const MovieCaptureConfiguration& configuration) noexcept {
    auto& state = State();
    if (!state.active.load(std::memory_order_acquire) ||
        state.fps.load(std::memory_order_acquire) != configuration.fps ||
        state.output_mode.load(std::memory_order_acquire) != configuration.output_mode ||
        state.pass_flags.load(std::memory_order_acquire) != configuration.pass_flags ||
        state.output_width.load(std::memory_order_acquire) != configuration.output_width ||
        state.output_height.load(std::memory_order_acquire) != configuration.output_height ||
        state.capture_audio.load(std::memory_order_acquire) != configuration.capture_audio ||
        state.greenscreen_active.load(std::memory_order_acquire) !=
            configuration.greenscreen_active ||
        state.greenscreen_color_rgb.load(std::memory_order_acquire) !=
            configuration.greenscreen_color_rgb ||
        state.expected_frame_count.load(std::memory_order_acquire) !=
            configuration.expected_frame_count) {
        return false;
    }
    std::lock_guard lock(state.mutex);
    return state.capture_name == configuration.capture_name &&
           state.take_directory == Utf8Path(configuration.take_directory).lexically_normal();
}

} // namespace

void ConfigureWorldDepthObservation(
    const bool enabled,
    const std::uint32_t expected_width,
    const std::uint32_t expected_height) noexcept {
    auto& state = State();
    if (!enabled || expected_width == 0 || expected_height == 0) {
        state.depth_observation_enabled.store(false, std::memory_order_release);
        state.expected_depth_width.store(0, std::memory_order_release);
        state.expected_depth_height.store(0, std::memory_order_release);
        ReleaseRetainedDepthView();
        return;
    }

    const auto previous_width =
        state.expected_depth_width.exchange(expected_width, std::memory_order_acq_rel);
    const auto previous_height =
        state.expected_depth_height.exchange(expected_height, std::memory_order_acq_rel);
    const auto dimensions_changed =
        previous_width != expected_width || previous_height != expected_height;
    if (dimensions_changed)
        ReleaseRetainedDepthView();
    state.depth_observation_enabled.store(true, std::memory_order_release);
}

void ObserveWorldDepthView(ID3D11DepthStencilView* depth_view) noexcept {
    auto& state = State();
    if (!state.depth_observation_enabled.load(std::memory_order_acquire) ||
        depth_view == nullptr) {
        return;
    }
    const auto expected_width =
        state.expected_depth_width.load(std::memory_order_acquire);
    const auto expected_height =
        state.expected_depth_height.load(std::memory_order_acquire);
    if (!IsSupportedWorldDepthView(depth_view, expected_width, expected_height))
        return;

    ID3D11DepthStencilView* previous = nullptr;
    {
        std::lock_guard lock(state.depth_view_mutex);
        if (!state.depth_observation_enabled.load(std::memory_order_acquire) ||
            state.expected_depth_width.load(std::memory_order_acquire) != expected_width ||
            state.expected_depth_height.load(std::memory_order_acquire) != expected_height ||
            state.retained_depth_view == depth_view) {
            return;
        }
        depth_view->AddRef();
        previous = state.retained_depth_view;
        state.retained_depth_view = depth_view;
    }
    SafeRelease(previous);
}

ID3D11DepthStencilView* AcquireObservedWorldDepthView(
    const std::uint32_t expected_width,
    const std::uint32_t expected_height) noexcept {
    auto& state = State();
    if (!state.depth_observation_enabled.load(std::memory_order_acquire) ||
        state.expected_depth_width.load(std::memory_order_acquire) != expected_width ||
        state.expected_depth_height.load(std::memory_order_acquire) != expected_height) {
        return nullptr;
    }
    std::lock_guard lock(state.depth_view_mutex);
    if (state.retained_depth_view == nullptr)
        return nullptr;
    state.retained_depth_view->AddRef();
    return state.retained_depth_view;
}

ID3D11ShaderResourceView* AcquireObservedWorldDepthShaderResourceView(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    const std::uint32_t expected_width,
    const std::uint32_t expected_height) noexcept {
    auto& state = State();
    if (device == nullptr)
        return nullptr;
    std::lock_guard lock(state.depth_view_mutex);
    if (state.retained_depth_view == nullptr ||
        state.expected_depth_width.load(std::memory_order_acquire) != expected_width ||
        state.expected_depth_height.load(std::memory_order_acquire) != expected_height)
        return nullptr;

    Microsoft::WRL::ComPtr<ID3D11Resource> resource{};
    state.retained_depth_view->GetResource(resource.GetAddressOf());
    if (resource == nullptr)
        return nullptr;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> depth_texture{};
    if (FAILED(resource.As(&depth_texture)) || depth_texture == nullptr)
        return nullptr;

    D3D11_DEPTH_STENCIL_VIEW_DESC view_desc{};
    state.retained_depth_view->GetDesc(&view_desc);
    DXGI_FORMAT srv_format = DXGI_FORMAT_UNKNOWN;
    DXGI_FORMAT copy_format = DXGI_FORMAT_UNKNOWN;
    switch (view_desc.Format) {
        case DXGI_FORMAT_D32_FLOAT:
            srv_format = DXGI_FORMAT_R32_FLOAT;
            copy_format = DXGI_FORMAT_R32_TYPELESS;
            break;
        case DXGI_FORMAT_D16_UNORM:
            srv_format = DXGI_FORMAT_R16_UNORM;
            copy_format = DXGI_FORMAT_R16_TYPELESS;
            break;
        case DXGI_FORMAT_D24_UNORM_S8_UINT:
            srv_format = DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
            copy_format = DXGI_FORMAT_R24G8_TYPELESS;
            break;
        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
            srv_format = DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
            copy_format = DXGI_FORMAT_R32G8X24_TYPELESS;
            break;
        default: return nullptr;
    }

    // Fast path: a typeless engine depth surface can be sampled directly. The
    // resolved view format is part of the cache key: the same resource can be
    // bound through a depth view of another format, and the cached SRV would
    // then sample it with the wrong interpretation.
    if (state.depth_srv != nullptr && state.depth_srv_resource == resource.Get() &&
        state.depth_srv_format == srv_format) {
        state.depth_srv->AddRef();
        return state.depth_srv;
    }
    {
        D3D11_SHADER_RESOURCE_VIEW_DESC srv_desc{};
        srv_desc.Format = srv_format;
        srv_desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        srv_desc.Texture2D.MostDetailedMip = 0;
        srv_desc.Texture2D.MipLevels = 1;
        ID3D11ShaderResourceView* direct = nullptr;
        if (SUCCEEDED(device->CreateShaderResourceView(resource.Get(), &srv_desc, &direct)) &&
            direct != nullptr) {
            SafeRelease(state.depth_srv);
            SafeRelease(state.depth_srv_resource);
            resource.CopyTo(&state.depth_srv_resource);
            state.depth_srv = direct;
            state.depth_srv_format = srv_format;
            state.depth_srv->AddRef();
            return state.depth_srv;
        }
    }

    // Fallback: copy the typed depth into a typeless surface we can sample. The
    // copy is refreshed every call because the depth changes per frame.
    if (context == nullptr)
        return nullptr;
    D3D11_TEXTURE2D_DESC desc{};
    depth_texture->GetDesc(&desc);
    const auto copy_reusable =
        state.depth_copy != nullptr && state.depth_copy_srv != nullptr &&
        state.depth_copy_source == resource.Get() &&
        state.depth_copy_format == copy_format &&
        state.depth_copy_description.Width == desc.Width &&
        state.depth_copy_description.Height == desc.Height &&
        state.depth_copy_description.ArraySize == desc.ArraySize;
    if (!copy_reusable) {
        SafeRelease(state.depth_copy);
        SafeRelease(state.depth_copy_srv);
        SafeRelease(state.depth_copy_source);
        state.depth_copy_description = {};
        state.depth_copy_format = DXGI_FORMAT_UNKNOWN;
        D3D11_TEXTURE2D_DESC copy = desc;
        copy.Format = copy_format;
        copy.Usage = D3D11_USAGE_DEFAULT;
        copy.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        copy.CPUAccessFlags = 0;
        copy.MipLevels = 1;
        copy.MiscFlags = 0;
        if (FAILED(device->CreateTexture2D(&copy, nullptr, &state.depth_copy)) ||
            state.depth_copy == nullptr) {
            SafeRelease(state.depth_copy);
            return nullptr;
        }
        D3D11_SHADER_RESOURCE_VIEW_DESC srv_desc{};
        srv_desc.Format = srv_format;
        srv_desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        srv_desc.Texture2D.MostDetailedMip = 0;
        srv_desc.Texture2D.MipLevels = 1;
        if (FAILED(device->CreateShaderResourceView(state.depth_copy, &srv_desc, &state.depth_copy_srv)) ||
            state.depth_copy_srv == nullptr) {
            SafeRelease(state.depth_copy);
            return nullptr;
        }
        resource.CopyTo(&state.depth_copy_source);
        state.depth_copy_description = copy;
        state.depth_copy_format = copy_format;
    }
    context->CopyResource(state.depth_copy, resource.Get());
    state.depth_copy_srv->AddRef();
    return state.depth_copy_srv;
}

bool HasActiveMovieCaptureIdentity(const std::string_view capture_name,
    const std::string_view take_directory) noexcept {
    auto& state = State();
    if (!state.active.load(std::memory_order_acquire)) return false;
    try {
        const auto directory = Utf8Path(take_directory).lexically_normal();
        std::lock_guard lock(state.mutex);
        return state.active.load(std::memory_order_acquire) &&
            state.capture_name == capture_name && state.take_directory == directory;
    } catch (...) {
        return false;
    }
}

void SyncMovieCapture(const MovieCaptureConfiguration& configuration) noexcept {
    if (!configuration.active) {
        StopSession();
        return;
    }
    if (ConfigurationMatches(configuration))
        return;
    StopSession();
    if (!StartSession(configuration))
        MarkFatalFailure();
}

namespace {
// Only active Beauty takes use this bounded strip readback. Queue it before
// the existing Beauty readback so its Map does not add a separate GPU fence.
// Noise/grades must never manufacture new cadence identities.
bool QueueCadenceSample(ID3D11Device* device, ID3D11DeviceContext* context,
    ID3D11Texture2D* source) noexcept {
    auto& state = State();
    D3D11_TEXTURE2D_DESC desc{};
    source->GetDesc(&desc);
    const auto supported = desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
        desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
        desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
        desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    if (!supported || desc.SampleDesc.Count != 1 || desc.Width == 0 || desc.Height == 0)
        return false;
    const auto rows = std::min(desc.Height, 36u);
    if (state.cadence_staging == nullptr ||
        state.cadence_source_description.Width != desc.Width ||
        state.cadence_source_description.Height != desc.Height ||
        state.cadence_source_description.Format != desc.Format) {
        SafeRelease(state.cadence_staging);
        D3D11_TEXTURE2D_DESC staging{};
        staging.Width = desc.Width;
        staging.Height = rows;
        staging.MipLevels = staging.ArraySize = staging.SampleDesc.Count = 1;
        staging.Format = desc.Format;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(device->CreateTexture2D(&staging, nullptr, &state.cadence_staging)))
            return false;
        state.cadence_source_description = desc;
    }
    for (UINT row = 0; row < rows; ++row) {
        const auto source_y = rows > 1 ? row * (desc.Height - 1) / (rows - 1) : 0;
        const D3D11_BOX box{0, source_y, 0, desc.Width, source_y + 1, 1};
        context->CopySubresourceRegion(state.cadence_staging, 0, 0, row, 0, source, 0, &box);
    }
    return true;
}

std::uint64_t ReadCadenceSample(ID3D11DeviceContext* context) noexcept {
    auto& state = State();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(state.cadence_staging, 0, D3D11_MAP_READ, 0, &mapped)))
        return 0;
    MappedResource unmap(context, state.cadence_staging, 0);
    auto hash = std::uint64_t{14695981039346656037ULL};
    const auto width = state.cadence_source_description.Width;
    const auto columns = std::min(width, 64u);
    const auto rows = std::min(state.cadence_source_description.Height, 36u);
    for (UINT y = 0; y < rows; ++y) {
        const auto* row = static_cast<const std::uint8_t*>(mapped.pData) + y * mapped.RowPitch;
        for (UINT x = 0; x < columns; ++x) {
            const auto offset = (columns > 1 ? x * (width - 1) / (columns - 1) : 0) * 4u;
            for (UINT channel = 0; channel < 3; ++channel) {
                hash ^= row[offset + channel];
                hash *= 1099511628211ULL;
            }
        }
    }
    return hash != 0 ? hash : 1;
}
} // namespace

void CaptureMovieFrame(
    ID3D11Device* device,
    ID3D11DeviceContext* context,
    IDXGISwapChain* swapchain,
    const std::int64_t replay_tick,
    const CameraSample* rendered_camera,
    const std::uint64_t camera_frame_sequence,
    ID3D11Texture2D* cadence_color_source) noexcept {
    auto& state = State();
    if (!state.active.load(std::memory_order_acquire))
        return;
    const auto expected_frame_count =
        state.expected_frame_count.load(std::memory_order_acquire);
    if (!ShouldCaptureMovieFrame(
            state.observed_frames.load(std::memory_order_acquire),
            expected_frame_count)) {
        return;
    }
    if (state.depth_observation_enabled.load(std::memory_order_acquire))
        state.depth_observer_observed.store(true, std::memory_order_release);
    state.present_calls.fetch_add(1, std::memory_order_acq_rel);
    const auto previous_sequence = state.last_camera_frame_sequence.exchange(
        camera_frame_sequence,
        std::memory_order_acq_rel);
    const auto repeated_camera_sequence =
        IsRepeatedMovieCameraSequence(previous_sequence, camera_frame_sequence);
    if (state.fatal_failed.load(std::memory_order_acquire) || device == nullptr ||
        context == nullptr || swapchain == nullptr) {
        return;
    }

    const auto passes = state.pass_flags.load(std::memory_order_acquire);
    const auto greenscreen_pass =
        (passes & movie_capture_pass_greenscreen_free_camera) != 0;
    const auto need_depth =
        (passes & (movie_capture_pass_world_depth_pfm |
                   movie_capture_pass_world_depth_avi |
                   movie_capture_pass_greenscreen_free_camera)) != 0;
    const auto need_color =
        (passes & movie_capture_pass_beauty) != 0 ||
        (passes & movie_capture_pass_greenscreen_free_camera) != 0;
    if (!need_depth && !need_color)
        return;

    try {
        const auto capture_started = std::chrono::steady_clock::now();
        struct CapturePredicateScope final {
            ID3D11DeviceContext* context;
            Microsoft::WRL::ComPtr<ID3D11Predicate> previous;
            BOOL value{};
            explicit CapturePredicateScope(ID3D11DeviceContext* c) : context(c) {
                context->GetPredication(&previous, &value);
                context->SetPredication(nullptr, FALSE);
            }
            ~CapturePredicateScope() { context->SetPredication(previous.Get(), value); }
        } predicate_scope(context);
        MovieFrame frame{};
        frame.replay_tick = replay_tick;
        if (need_color && cadence_color_source != nullptr &&
            !QueueCadenceSample(device, context, cadence_color_source)) {
            state.pass_incomplete.store(true, std::memory_order_release);
            return;
        }
        if (need_color && !CaptureColor(device, context, swapchain, frame)) {
            state.pass_incomplete.store(true, std::memory_order_release);
            return;
        }
        if (need_depth && !CaptureDepth(device, context, frame)) {
            state.depth_frames_unavailable.fetch_add(1, std::memory_order_acq_rel);
            state.pass_incomplete.store(true, std::memory_order_release);
            // Presentation-time depth is not bound on every Deadlock Present.
            // Keep an independent beauty AVI complete instead of allowing the
            // optional depth pass to collapse the whole take to a few frames.
            if (!need_color || greenscreen_pass)
                return;
        }
        if (frame.width != 0 && frame.height != 0) {
            state.source_width.store(frame.width, std::memory_order_release);
            state.source_height.store(frame.height, std::memory_order_release);
        }
        const auto cadence_fingerprint = need_color && cadence_color_source != nullptr
            ? ReadCadenceSample(context) : 0;
        if (need_color && cadence_color_source != nullptr && cadence_fingerprint == 0) {
            state.pass_incomplete.store(true, std::memory_order_release);
            RecycleFrameBuffers(frame);
            return;
        }
        const auto visual_fingerprint = VisualFingerprint(frame, cadence_fingerprint);
        const auto previous_visual_fingerprint = state.last_visual_fingerprint.exchange(
            visual_fingerprint,
            std::memory_order_acq_rel);
        const auto repeated_visual =
            visual_fingerprint != 0 && visual_fingerprint == previous_visual_fingerprint;
        if (repeated_camera_sequence)
            state.repeated_camera_sequences_captured.fetch_add(1, std::memory_order_acq_rel);
        if (repeated_visual)
            state.repeated_visual_samples_captured.fetch_add(1, std::memory_order_acq_rel);
        UpdateMaximum(state.maximum_capture_microseconds, ElapsedMicroseconds(capture_started));
        // Never drop a Present while a take is active. Under host_framerate the
        // replay clock advances on every Present, so suppressing repeated
        // Presents removed real time from the AVI and shifted every later
        // frame. Duplicate Presents are part of the capture timebase.
        const auto frame_index = state.observed_frames.fetch_add(1, std::memory_order_acq_rel);
        frame.index = frame_index;
        state.replay_timing_digest.store(
            AppendReplayTimingDigest(
                state.replay_timing_digest.load(std::memory_order_acquire),
                replay_tick),
            std::memory_order_release);
        if (rendered_camera != nullptr) {
            state.rendered_camera_digest.store(
                AppendRenderedCameraDigest(
                    state.rendered_camera_digest.load(std::memory_order_acquire),
                    *rendered_camera),
                std::memory_order_release);
            state.rendered_camera_samples.fetch_add(1, std::memory_order_acq_rel);
        }
        if (replay_tick >= 0) {
            auto no_tick = std::int64_t{-1};
            static_cast<void>(state.first_replay_tick.compare_exchange_strong(
                no_tick,
                replay_tick,
                std::memory_order_acq_rel));
            state.last_replay_tick.store(replay_tick, std::memory_order_release);
        }
        {
            std::unique_lock lock(state.mutex);
            const auto frame_payload_bytes = MovieCaptureFramePayloadBytes(
                frame.width,
                frame.height,
                !frame.color_bgr.empty(),
                !frame.depth.empty());
            const auto queue_capacity = static_cast<std::uint32_t>(
                MovieCaptureQueueFrameCapacity(frame_payload_bytes));
            state.queue_capacity.store(queue_capacity, std::memory_order_release);
            const auto queue_was_full = state.queue.size() >= queue_capacity;
            const auto queue_wait_started = std::chrono::steady_clock::now();
            // A stalled writer must not pin the render thread. The writer can
            // block on a removed or saturated capture disk, and the previous
            // unbounded wait froze the game for the whole I/O stall. The wait
            // is now bounded: past the budget the take is finalized as failed
            // instead of the game hanging indefinitely.
            while (state.queue.size() >= queue_capacity &&
                   !state.writer_stop &&
                   !state.fatal_failed.load(std::memory_order_acquire)) {
                state.queue_space.wait_for(lock, std::chrono::milliseconds(50));
                if (!MovieCaptureQueueWaitExceededBudget(
                        ElapsedMicroseconds(queue_wait_started) / 1000u)) {
                    continue;
                }
                state.queue_backpressure_timeouts.fetch_add(1, std::memory_order_acq_rel);
                MarkFatalFailure();
                // Signal the wedged writer so it drains and exits instead of
                // blocking on this queue for the rest of the process. The take
                // itself is still finalized by StopSession; joining the writer
                // here would deadlock the render thread against it.
                state.writer_stop = true;
                state.work_ready.notify_all();
                state.queue_space.notify_all();
                break;
            }
            if (queue_was_full) {
                const auto waited = ElapsedMicroseconds(queue_wait_started);
                state.queue_backpressure_events.fetch_add(1, std::memory_order_acq_rel);
                state.queue_backpressure_microseconds.fetch_add(waited, std::memory_order_acq_rel);
                UpdateMaximum(state.maximum_queue_wait_microseconds, waited);
            }
            if (!state.writer_stop && !state.fatal_failed.load(std::memory_order_acquire)) {
                state.queue.push_back(std::move(frame));
                UpdateMaximum(
                    state.maximum_queue_depth,
                    static_cast<std::uint32_t>(state.queue.size()));
                state.work_ready.notify_one();
            }
        }
    } catch (...) {
        MarkFatalFailure();
    }
}

void SetMovieFrameCompositionReady(const bool ready) noexcept {
    auto& state = State();
    if (!state.active.load(std::memory_order_acquire)) {
        state.frame_composition_ready.store(false, std::memory_order_release);
        return;
    }
    const auto was_ready = state.frame_composition_ready.exchange(
        ready,
        std::memory_order_acq_rel);
    if (was_ready && !ready)
        state.pass_incomplete.store(true, std::memory_order_release);
}

void NotifyWorldDepthDeviceLost() noexcept {
    ConfigureWorldDepthObservation(false, 0, 0);
    ReleaseStaging();
}

void AbortMovieCaptureForDeviceFailure() noexcept {
    if (State().active.load(std::memory_order_acquire)) MarkFatalFailure();
    StopSession();
    NotifyWorldDepthDeviceLost();
}

void ShutdownWorldDepthCapture() noexcept {
    ConfigureWorldDepthObservation(false, 0, 0);
    StopSession();
}

WorldDepthCaptureStatus GetWorldDepthCaptureStatus() noexcept {
    const auto& state = State();
    const auto audio = GetEngineMovieAudioStatus();
    return {
        state.active.load(std::memory_order_acquire),
        state.frame_composition_ready.load(std::memory_order_acquire),
        state.depth_observation_enabled.load(std::memory_order_acquire),
        state.depth_available.load(std::memory_order_acquire),
        state.avi_available.load(std::memory_order_acquire),
        audio.active,
        state.audio_failed.load(std::memory_order_acquire),
        state.fatal_failed.load(std::memory_order_acquire),
        state.pass_incomplete.load(std::memory_order_acquire),
        state.observed_frames.load(std::memory_order_acquire),
        state.written_frames.load(std::memory_order_acquire),
        state.beauty_tga_frames_written.load(std::memory_order_acquire),
        state.beauty_frames_written.load(std::memory_order_acquire),
        state.depth_pfm_frames_written.load(std::memory_order_acquire),
        state.depth_avi_frames_written.load(std::memory_order_acquire),
        state.depth_key_frames_written.load(std::memory_order_acquire),
        state.present_calls.load(std::memory_order_acquire),
        state.repeated_camera_sequences_captured.load(std::memory_order_acquire),
        state.repeated_visual_samples_captured.load(std::memory_order_acquire),
        state.depth_frames_unavailable.load(std::memory_order_acquire),
        state.queue_backpressure_events.load(std::memory_order_acquire),
        state.queue_backpressure_microseconds.load(std::memory_order_acquire),
        state.maximum_queue_wait_microseconds.load(std::memory_order_acquire),
        state.maximum_capture_microseconds.load(std::memory_order_acquire),
        state.maximum_writer_microseconds.load(std::memory_order_acquire),
        state.queue_capacity.load(std::memory_order_acquire),
        state.maximum_queue_depth.load(std::memory_order_acquire),
    };
}

} // namespace deadlock_mvm
