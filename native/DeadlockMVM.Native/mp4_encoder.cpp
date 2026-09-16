#include "mp4_encoder.hpp"

#include <Windows.h>

#include <codecapi.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <objbase.h>

#include <wrl/client.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <new>
#include <vector>

namespace deadlock_mvm {
namespace {

using Microsoft::WRL::ComPtr;

constexpr LONGLONG kSecondInHns = 10'000'000LL;

[[nodiscard]] HRESULT SetVideoType(
    IMFMediaType* type,
    const GUID& subtype,
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t fps) noexcept {
    HRESULT result = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(result))
        return result;
    result = type->SetGUID(MF_MT_SUBTYPE, subtype);
    if (FAILED(result))
        return result;
    result = type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(result))
        return result;
    result = MFSetAttributeSize(type, MF_MT_FRAME_SIZE, width, height);
    if (FAILED(result))
        return result;
    result = MFSetAttributeRatio(type, MF_MT_FRAME_RATE, fps, 1);
    if (FAILED(result))
        return result;
    return MFSetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
}

// Output-codec description is advisory for a convenience preview. A missing
// attribute must never cost the take its playable file, so the writes are
// best-effort while the required type setup above stays fatal.
void SetAdvisoryUINT32(
    IMFMediaType* type,
    const GUID& key,
    const std::uint32_t value) noexcept {
    static_cast<void>(type->SetUINT32(key, value));
}

} // namespace

struct Mp4Encoder::Impl final {
    ComPtr<IMFSinkWriter> writer{};
    DWORD stream{};
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t fps{};
    std::uint64_t frames{};
    bool com_initialized{};
    bool media_foundation_started{};
    std::vector<std::uint8_t> bgra{};
};

Mp4Encoder::~Mp4Encoder() {
    static_cast<void>(Close());
    delete impl_;
    impl_ = nullptr;
}

bool Mp4Encoder::Open(
    const std::filesystem::path& path,
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t fps,
    const std::uint32_t bitrate_bps) noexcept {
    if (impl_ != nullptr || path.empty() || width == 0 || height == 0 || fps == 0 ||
        bitrate_bps == 0) {
        return false;
    }
    auto* const impl = new (std::nothrow) Impl{};
    if (impl == nullptr)
        return false;
    impl_ = impl;

    // Media Foundation creates the output file as soon as the sink writer
    // exists, and a failed Open() used to return with impl_ still set. That made
    // every later Open() silently fail (the guard rejects a non-null impl_) and
    // left a stray partial MP4 behind. Every failure below therefore tears the
    // attempt down completely and removes whatever the sink writer created, so
    // the encoder is exactly as usable as it was before the call.
    const auto abandon = [this, impl, &path]() noexcept {
        if (impl->writer != nullptr) {
            static_cast<void>(impl->writer->Finalize());
            impl->writer.Reset();
        }
        if (impl->media_foundation_started) {
            static_cast<void>(MFShutdown());
            impl->media_foundation_started = false;
        }
        if (impl->com_initialized) {
            CoUninitialize();
            impl->com_initialized = false;
        }
        std::error_code error{};
        std::filesystem::remove(path, error);
        delete impl;
        impl_ = nullptr;
    };

    // The native writer runs on a raw thread without COM. Media Foundation
    // requires an apartment; MTA is compatible with the game's own MF usage.
    const auto com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    impl->com_initialized = SUCCEEDED(com_result);
    if (FAILED(com_result) && com_result != RPC_E_CHANGED_MODE) {
        std::fprintf(stderr, "mp4: CoInitializeEx=0x%08lX\n", static_cast<unsigned long>(com_result));
        abandon();
        return false;
    }

    const auto mf_result = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(mf_result)) {
        std::fprintf(stderr, "mp4: MFStartup=0x%08lX\n", static_cast<unsigned long>(mf_result));
        abandon();
        return false;
    }
    impl->media_foundation_started = true;

    ComPtr<IMFSinkWriter> writer;
    const auto create_result = MFCreateSinkWriterFromURL(path.c_str(), nullptr, nullptr, &writer);
    if (FAILED(create_result)) {
        std::fprintf(stderr, "mp4: MFCreateSinkWriterFromURL=0x%08lX\n", static_cast<unsigned long>(create_result));
        abandon();
        return false;
    }

    ComPtr<IMFMediaType> output_type;
    if (FAILED(MFCreateMediaType(&output_type)) ||
        FAILED(SetVideoType(output_type.Get(), MFVideoFormat_H264, width, height, fps)) ||
        FAILED(output_type->SetUINT32(MF_MT_AVG_BITRATE, bitrate_bps))) {
        std::fprintf(stderr, "mp4: output media type rejected\n");
        abandon();
        return false;
    }
    // The capture backbuffer is SDR sRGB, so the preview is tagged BT.709
    // limited range instead of leaving players to guess. Baseline is the most
    // widely decodable H.264 profile, matching what a preview needs.
    SetAdvisoryUINT32(output_type.Get(), MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
    SetAdvisoryUINT32(output_type.Get(), MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
    SetAdvisoryUINT32(output_type.Get(), MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
    SetAdvisoryUINT32(output_type.Get(), MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709);
    SetAdvisoryUINT32(output_type.Get(), MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709);
    const auto stream_result = writer->AddStream(output_type.Get(), &impl->stream);
    if (FAILED(stream_result)) {
        std::fprintf(stderr, "mp4: AddStream=0x%08lX\n", static_cast<unsigned long>(stream_result));
        abandon();
        return false;
    }

    ComPtr<IMFMediaType> input_type;
    // BGRA rows are copied into the encoder; the sink writer inserts the color
    // converter to the encoder's native format.
    if (FAILED(MFCreateMediaType(&input_type)) ||
        FAILED(SetVideoType(input_type.Get(), MFVideoFormat_RGB32, width, height, fps))) {
        std::fprintf(stderr, "mp4: input media type rejected\n");
        abandon();
        return false;
    }
    const auto input_result = writer->SetInputMediaType(impl->stream, input_type.Get(), nullptr);
    if (FAILED(input_result)) {
        std::fprintf(stderr, "mp4: SetInputMediaType=0x%08lX\n", static_cast<unsigned long>(input_result));
        abandon();
        return false;
    }
    const auto begin_result = writer->BeginWriting();
    if (FAILED(begin_result)) {
        std::fprintf(stderr, "mp4: BeginWriting=0x%08lX\n", static_cast<unsigned long>(begin_result));
        abandon();
        return false;
    }

    impl->writer = writer;
    impl->width = width;
    impl->height = height;
    impl->fps = fps;
    impl->bgra.resize(static_cast<std::size_t>(width) * 4u * height);
    return true;
}

bool Mp4Encoder::WriteBgrFrame(
    const std::uint8_t* bgr,
    const std::uint32_t stride_bytes) noexcept {
    auto* const impl = impl_;
    if (impl == nullptr || impl->writer == nullptr || bgr == nullptr ||
        stride_bytes < impl->width * 3u) {
        return false;
    }
    const auto row_bytes = static_cast<std::size_t>(impl->width) * 4u;
    for (std::uint32_t y = 0; y < impl->height; ++y) {
        const auto* const source = bgr + static_cast<std::size_t>(y) * stride_bytes;
        auto* const target = impl->bgra.data() + static_cast<std::size_t>(y) * row_bytes;
        for (std::uint32_t x = 0; x < impl->width; ++x) {
            target[x * 4u + 0u] = source[x * 3u + 0u];
            target[x * 4u + 1u] = source[x * 3u + 1u];
            target[x * 4u + 2u] = source[x * 3u + 2u];
            target[x * 4u + 3u] = 0xFFu;
        }
    }

    ComPtr<IMFMediaBuffer> buffer;
    if (FAILED(MFCreateMemoryBuffer(static_cast<DWORD>(impl->bgra.size()), &buffer)))
        return false;
    BYTE* destination = nullptr;
    if (FAILED(buffer->Lock(&destination, nullptr, nullptr)))
        return false;
    std::memcpy(destination, impl->bgra.data(), impl->bgra.size());
    static_cast<void>(buffer->Unlock());
    if (FAILED(buffer->SetCurrentLength(static_cast<DWORD>(impl->bgra.size()))))
        return false;

    ComPtr<IMFSample> sample;
    if (FAILED(MFCreateSample(&sample)))
        return false;
    if (FAILED(sample->AddBuffer(buffer.Get())))
        return false;
    const auto duration = kSecondInHns / impl->fps;
    if (FAILED(sample->SetSampleTime(static_cast<LONGLONG>(impl->frames) * duration)))
        return false;
    if (FAILED(sample->SetSampleDuration(duration)))
        return false;
    if (FAILED(impl->writer->WriteSample(impl->stream, sample.Get())))
        return false;
    impl->frames += 1;
    return true;
}

bool Mp4Encoder::Close() noexcept {
    auto* const impl = impl_;
    if (impl == nullptr)
        return true;
    auto succeeded = true;
    if (impl->writer != nullptr) {
        succeeded = SUCCEEDED(impl->writer->Finalize());
        impl->writer.Reset();
    }
    if (impl->media_foundation_started) {
        static_cast<void>(MFShutdown());
        impl->media_foundation_started = false;
    }
    if (impl->com_initialized) {
        CoUninitialize();
        impl->com_initialized = false;
    }
    return succeeded;
}

bool Mp4Encoder::IsOpen() const noexcept {
    return impl_ != nullptr && impl_->writer != nullptr;
}

std::uint64_t Mp4Encoder::FramesWritten() const noexcept {
    return impl_ == nullptr ? 0 : impl_->frames;
}

} // namespace deadlock_mvm
