#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string_view>
#include <vector>

namespace deadlock_mvm {

struct Pattern final {
    std::vector<std::uint8_t> bytes;
    std::vector<std::uint8_t> mask;
};

[[nodiscard]] std::optional<Pattern> ParsePattern(std::string_view text);

[[nodiscard]] std::vector<std::size_t> FindPattern(
    const std::uint8_t* data,
    std::size_t size,
    const Pattern& pattern,
    std::size_t max_results = static_cast<std::size_t>(-1));

[[nodiscard]] std::optional<std::uintptr_t> ResolveRipRelative(
    std::uintptr_t instruction,
    std::size_t displacement_offset,
    std::size_t instruction_length) noexcept;

} // namespace deadlock_mvm
