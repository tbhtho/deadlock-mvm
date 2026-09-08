#pragma once

#include <cstdint>
#include <filesystem>
#include <string_view>

namespace deadlock_mvm {

inline constexpr std::string_view kEngineMovieAudioServicePattern =
    "48 8B 0D ?? ?? ?? ?? 48 8B 01 FF 90 70 01 00 00 49 8B DE";

enum class EngineMovieAudioError : std::uint32_t {
    none = 0,
    already_active,
    path_unavailable,
    engine_module_unavailable,
    signature_missing,
    signature_ambiguous,
    engine_service_unavailable,
    recorder_unavailable,
    invalid_vtable,
    invocation_failed,
};

struct EngineMovieAudioStatus final {
    bool active{};
    bool started{};
    bool stop_invoked{};
    bool file_available{};
    EngineMovieAudioError error{EngineMovieAudioError::none};
};

// Uses Deadlock's own movie-audio recorder directly. The generic Source 2
// startmovie command is dormant in the installed Deadlock client, but its
// synchronized audio sink remains present in engine2.dll. Resolution is a
// unique, fail-closed signature transaction against the loaded image.
[[nodiscard]] bool StartEngineMovieAudio(
    const std::filesystem::path& wave_path) noexcept;
void StopEngineMovieAudio() noexcept;
[[nodiscard]] EngineMovieAudioStatus GetEngineMovieAudioStatus() noexcept;
[[nodiscard]] const char* DescribeEngineMovieAudioError(
    EngineMovieAudioError error) noexcept;

} // namespace deadlock_mvm
