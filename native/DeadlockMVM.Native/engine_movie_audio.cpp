#include "engine_movie_audio.hpp"

#include "pattern_scan.hpp"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <mutex>
#include <string>
#include <string_view>

namespace deadlock_mvm {
namespace {

// engine2!CMovieRecorder::Start resolves its audio service through this exact
// sequence in the audited Deadlock build:
//   mov rcx,[rip+service]; mov rax,[rcx]; call [rax+170h]
constexpr std::size_t kGetAudioRecorderVtableIndex = 0x170u / sizeof(void*);
constexpr std::size_t kStartAudioVtableIndex = 0x18u / sizeof(void*);
constexpr std::size_t kStopAudioSubobjectOffset = 8;
constexpr std::size_t kStopAudioVtableIndex = 0x10u / sizeof(void*);

struct ModuleTextView final {
    std::uint8_t* base{};
    std::size_t image_size{};
    std::uint8_t* text{};
    std::size_t text_size{};

    [[nodiscard]] bool Contains(
        const std::uintptr_t address,
        const std::size_t length = 1) const noexcept {
        if (base == nullptr || length > image_size)
            return false;
        const auto start = reinterpret_cast<std::uintptr_t>(base);
        return address >= start && address - start <= image_size - length;
    }
};

struct AudioState final {
    std::mutex mutex{};
    void* recorder{};
    std::filesystem::path wave_path{};
    EngineMovieAudioStatus status{};
};

AudioState& State() noexcept {
    static AudioState state{};
    return state;
}

[[nodiscard]] bool IsReadableRange(
    const void* address,
    const std::size_t size) noexcept {
    if (address == nullptr || size == 0)
        return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) != sizeof(memory) ||
        memory.State != MEM_COMMIT ||
        (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
        return false;
    }
    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto region = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    return start >= region && start - region <= memory.RegionSize &&
           size <= memory.RegionSize - (start - region);
}

[[nodiscard]] bool IsExecutableFunction(const void* function) noexcept {
    if (function == nullptr)
        return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(function, &memory, sizeof(memory)) != sizeof(memory) ||
        memory.State != MEM_COMMIT || (memory.Protect & PAGE_GUARD) != 0) {
        return false;
    }
    const auto protection = memory.Protect & 0xFF;
    return protection == PAGE_EXECUTE || protection == PAGE_EXECUTE_READ ||
           protection == PAGE_EXECUTE_READWRITE ||
           protection == PAGE_EXECUTE_WRITECOPY;
}

[[nodiscard]] bool InspectModuleText(
    const HMODULE module,
    ModuleTextView& view) noexcept {
    view = {};
    if (module == nullptr)
        return false;
    auto* const base = reinterpret_cast<std::uint8_t*>(module);
    __try {
        const auto* const dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
            return false;
        const auto* const nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE ||
            nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
            return false;
        }
        view.base = base;
        view.image_size = nt->OptionalHeader.SizeOfImage;
        const auto* const sections = IMAGE_FIRST_SECTION(nt);
        for (std::uint16_t index = 0;
             index < nt->FileHeader.NumberOfSections;
             ++index) {
            const auto& section = sections[index];
            if (std::memcmp(section.Name, ".text", 5) != 0)
                continue;
            view.text = base + section.VirtualAddress;
            view.text_size = std::max<std::size_t>(
                section.Misc.VirtualSize,
                section.SizeOfRawData);
            break;
        }
        return view.text != nullptr && view.Contains(
            reinterpret_cast<std::uintptr_t>(view.text),
            view.text_size);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        view = {};
        return false;
    }
}

[[nodiscard]] void** ReadVtable(
    void* object,
    const std::size_t entry_count) noexcept {
    if (!IsReadableRange(object, sizeof(void*)))
        return nullptr;
    __try {
        auto** const vtable = *reinterpret_cast<void***>(object);
        return IsReadableRange(vtable, entry_count * sizeof(void*))
            ? vtable
            : nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

[[nodiscard]] void* ReadPointer(const std::uintptr_t address) noexcept {
    if (address == 0 ||
        !IsReadableRange(reinterpret_cast<const void*>(address), sizeof(void*))) {
        return nullptr;
    }
    __try {
        return *reinterpret_cast<void**>(address);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

[[nodiscard]] void* InvokeGetRecorder(
    void* service,
    void* function) noexcept {
    using GetRecorder = void*(__fastcall*)(void*);
    __try {
        return reinterpret_cast<GetRecorder>(function)(service);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

[[nodiscard]] bool InvokeStart(
    void* recorder,
    void* function,
    const char* path) noexcept {
    using StartRecorder = void(__fastcall*)(void*, const char*);
    __try {
        reinterpret_cast<StartRecorder>(function)(recorder, path);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] bool InvokeStop(void* recorder) noexcept {
    auto* const stop_object = static_cast<std::uint8_t*>(recorder) +
        kStopAudioSubobjectOffset;
    auto** const vtable = ReadVtable(
        stop_object,
        kStopAudioVtableIndex + 1);
    if (vtable == nullptr ||
        !IsExecutableFunction(vtable[kStopAudioVtableIndex])) {
        return false;
    }
    using StopRecorder = void(__fastcall*)(void*);
    __try {
        reinterpret_cast<StopRecorder>(vtable[kStopAudioVtableIndex])(
            stop_object);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] std::string Utf8Path(
    const std::filesystem::path& path) {
    const auto value = path.u8string();
    return {
        reinterpret_cast<const char*>(value.data()),
        value.size(),
    };
}

// Walks the RIFF chunk list rather than trusting the file size: the engine's
// sink writes a valid 44-byte header before any audio arrives, so a header-only
// file is indistinguishable from a real take unless the data size is checked.
[[nodiscard]] bool ReadWaveDataBytes(
    const std::filesystem::path& path,
    std::uint32_t& data_bytes) noexcept {
    data_bytes = 0;
    try {
        std::ifstream stream(path, std::ios::binary);
        if (!stream)
            return false;
        std::array<std::uint8_t, 12> header{};
        stream.read(
            reinterpret_cast<char*>(header.data()),
            static_cast<std::streamsize>(header.size()));
        if (stream.gcount() != static_cast<std::streamsize>(header.size()) ||
            std::memcmp(header.data(), "RIFF", 4) != 0 ||
            std::memcmp(header.data() + 8, "WAVE", 4) != 0) {
            return false;
        }
        stream.seekg(0, std::ios::end);
        const auto file_size = stream.tellg();
        stream.seekg(static_cast<std::streamoff>(header.size()), std::ios::beg);
        if (file_size < static_cast<std::streamoff>(header.size()))
            return false;
        for (;;) {
            const auto chunk_start = stream.tellg();
            std::array<std::uint8_t, 8> chunk{};
            stream.read(
                reinterpret_cast<char*>(chunk.data()),
                static_cast<std::streamsize>(chunk.size()));
            if (stream.gcount() != static_cast<std::streamsize>(chunk.size()))
                return false;
            const auto chunk_bytes =
                static_cast<std::uint32_t>(chunk[4]) |
                (static_cast<std::uint32_t>(chunk[5]) << 8) |
                (static_cast<std::uint32_t>(chunk[6]) << 16) |
                (static_cast<std::uint32_t>(chunk[7]) << 24);
            if (std::memcmp(chunk.data(), "data", 4) == 0) {
                // A declared size larger than the file means the header is
                // lying. Reporting it as samples would tell the take report that
                // audio exists when nothing was written past the 44-byte header.
                if (static_cast<std::streamoff>(chunk_bytes) >
                    file_size - chunk_start - static_cast<std::streamoff>(chunk.size())) {
                    return false;
                }
                data_bytes = chunk_bytes;
                return true;
            }
            stream.seekg(
                static_cast<std::streamoff>(chunk_bytes + (chunk_bytes & 1u)),
                std::ios::cur);
            if (!stream)
                return false;
        }
    } catch (...) {
        return false;
    }
}

} // namespace

std::uint32_t WaveDataChunkBytes(
    const std::uint8_t* bytes,
    const std::size_t size) noexcept {
    if (bytes == nullptr || size < 12 ||
        std::memcmp(bytes, "RIFF", 4) != 0 ||
        std::memcmp(bytes + 8, "WAVE", 4) != 0) {
        return 0;
    }
    std::size_t offset = 12;
    while (offset + 8 <= size) {
        const auto chunk_bytes =
            static_cast<std::uint32_t>(bytes[offset + 4]) |
            (static_cast<std::uint32_t>(bytes[offset + 5]) << 8) |
            (static_cast<std::uint32_t>(bytes[offset + 6]) << 16) |
            (static_cast<std::uint32_t>(bytes[offset + 7]) << 24);
        if (std::memcmp(bytes + offset, "data", 4) == 0) {
            // The declared size has to fit in the buffer that was read. A
            // truncated header-only file otherwise reports its header's promise
            // rather than what is actually there.
            if (static_cast<std::size_t>(chunk_bytes) > size - offset - 8)
                return 0;
            return chunk_bytes;
        }
        const std::uint64_t advance =
            8ull + chunk_bytes + (chunk_bytes & 1u);
        if (advance > size - offset)
            break;
        offset += static_cast<std::size_t>(advance);
    }
    return 0;
}

bool StartEngineMovieAudio(
    const std::filesystem::path& wave_path) noexcept {
    auto& state = State();
    std::lock_guard lock(state.mutex);
    if (state.recorder != nullptr || state.status.active) {
        state.status.error = EngineMovieAudioError::already_active;
        return false;
    }
    state.status = {};
    state.wave_path.clear();

    try {
        std::error_code file_error{};
        if (wave_path.empty() || !wave_path.is_absolute() ||
            std::filesystem::exists(wave_path, file_error) || file_error) {
            state.status.error = EngineMovieAudioError::path_unavailable;
            return false;
        }

        const auto module = GetModuleHandleW(L"engine2.dll");
        if (module == nullptr) {
            state.status.error = EngineMovieAudioError::engine_module_unavailable;
            return false;
        }
        ModuleTextView view{};
        if (!InspectModuleText(module, view)) {
            state.status.error = EngineMovieAudioError::engine_module_unavailable;
            return false;
        }
        const auto pattern = ParsePattern(kEngineMovieAudioServicePattern);
        if (!pattern) {
            state.status.error = EngineMovieAudioError::signature_missing;
            return false;
        }
        const auto hits = FindPattern(view.text, view.text_size, *pattern, 2);
        if (hits.empty()) {
            state.status.error = EngineMovieAudioError::signature_missing;
            return false;
        }
        if (hits.size() != 1) {
            state.status.error = EngineMovieAudioError::signature_ambiguous;
            return false;
        }
        const auto instruction = reinterpret_cast<std::uintptr_t>(
            view.text + hits.front());
        const auto service_global = ResolveRipRelative(instruction, 3, 7);
        if (!service_global || !view.Contains(*service_global, sizeof(void*)) ||
            !IsReadableRange(reinterpret_cast<const void*>(*service_global), sizeof(void*))) {
            state.status.error = EngineMovieAudioError::engine_service_unavailable;
            return false;
        }

        auto* const service = ReadPointer(*service_global);
        auto** const service_vtable = ReadVtable(
            service,
            kGetAudioRecorderVtableIndex + 1);
        if (service_vtable == nullptr ||
            !IsExecutableFunction(service_vtable[kGetAudioRecorderVtableIndex])) {
            state.status.error = EngineMovieAudioError::engine_service_unavailable;
            return false;
        }
        auto* const recorder = InvokeGetRecorder(
            service,
            service_vtable[kGetAudioRecorderVtableIndex]);
        if (recorder == nullptr) {
            state.status.error = EngineMovieAudioError::recorder_unavailable;
            return false;
        }
        auto** const recorder_vtable = ReadVtable(
            recorder,
            kStartAudioVtableIndex + 1);
        auto** const stop_vtable = ReadVtable(
            static_cast<std::uint8_t*>(recorder) + kStopAudioSubobjectOffset,
            kStopAudioVtableIndex + 1);
        if (recorder_vtable == nullptr || stop_vtable == nullptr ||
            !IsExecutableFunction(recorder_vtable[kStartAudioVtableIndex]) ||
            !IsExecutableFunction(stop_vtable[kStopAudioVtableIndex])) {
            state.status.error = EngineMovieAudioError::invalid_vtable;
            return false;
        }
        const auto narrow_path = Utf8Path(wave_path);
        if (!InvokeStart(
                recorder,
                recorder_vtable[kStartAudioVtableIndex],
                narrow_path.c_str())) {
            state.status.error = EngineMovieAudioError::invocation_failed;
            return false;
        }
        state.recorder = recorder;
        state.wave_path = wave_path;
        state.status.active = true;
        state.status.started = true;
        return true;
    } catch (...) {
        state.recorder = nullptr;
        state.wave_path.clear();
        state.status.error = EngineMovieAudioError::invocation_failed;
        return false;
    }
}

void StopEngineMovieAudio() noexcept {
    auto& state = State();
    std::lock_guard lock(state.mutex);
    if (state.recorder == nullptr) {
        state.status.active = false;
        return;
    }
    const auto stopped = InvokeStop(state.recorder);
    state.recorder = nullptr;
    state.status.active = false;
    state.status.stop_invoked = stopped;
    if (!stopped)
        state.status.error = EngineMovieAudioError::invocation_failed;
    std::uint32_t data_bytes = 0;
    state.status.file_available =
        !state.wave_path.empty() &&
        ReadWaveDataBytes(state.wave_path, data_bytes) &&
        data_bytes > 0;
    if (!state.status.file_available &&
        state.status.error == EngineMovieAudioError::none) {
        state.status.error = EngineMovieAudioError::output_empty;
    }
}

EngineMovieAudioStatus GetEngineMovieAudioStatus() noexcept {
    auto& state = State();
    std::lock_guard lock(state.mutex);
    return state.status;
}

const char* DescribeEngineMovieAudioError(
    const EngineMovieAudioError error) noexcept {
    switch (error) {
        case EngineMovieAudioError::none: return "none";
        case EngineMovieAudioError::already_active: return "already active";
        case EngineMovieAudioError::path_unavailable: return "output path unavailable";
        case EngineMovieAudioError::engine_module_unavailable: return "engine2 unavailable";
        case EngineMovieAudioError::signature_missing: return "audio signature missing";
        case EngineMovieAudioError::signature_ambiguous: return "audio signature ambiguous";
        case EngineMovieAudioError::engine_service_unavailable: return "engine audio service unavailable";
        case EngineMovieAudioError::recorder_unavailable: return "movie audio recorder unavailable";
        case EngineMovieAudioError::invalid_vtable: return "movie audio vtable rejected";
        case EngineMovieAudioError::invocation_failed: return "movie audio invocation failed";
        case EngineMovieAudioError::output_empty: return "movie audio output has no samples";
    }
    return "unknown";
}

} // namespace deadlock_mvm
