#include "pattern_scan.hpp"

#include <charconv>
#include <limits>

namespace deadlock_mvm {

std::optional<Pattern> ParsePattern(const std::string_view text) {
    Pattern result;
    std::size_t position = 0;
    while (position < text.size()) {
        while (position < text.size() && text[position] == ' ')
            ++position;
        if (position == text.size())
            break;

        const auto end = text.find(' ', position);
        const auto token = text.substr(position, end == std::string_view::npos ? text.size() - position : end - position);
        if (token == "?" || token == "??") {
            result.bytes.push_back(0);
            result.mask.push_back(0);
        } else {
            if (token.size() != 2)
                return std::nullopt;
            unsigned value = 0;
            const auto parsed = std::from_chars(token.data(), token.data() + token.size(), value, 16);
            if (parsed.ec != std::errc{} || parsed.ptr != token.data() + token.size() || value > 0xFF)
                return std::nullopt;
            result.bytes.push_back(static_cast<std::uint8_t>(value));
            result.mask.push_back(1);
        }
        position = end == std::string_view::npos ? text.size() : end + 1;
    }

    if (result.bytes.empty())
        return std::nullopt;
    return result;
}

std::vector<std::size_t> FindPattern(
    const std::uint8_t* data,
    const std::size_t size,
    const Pattern& pattern,
    const std::size_t max_results) {
    std::vector<std::size_t> results;
    if (data == nullptr || pattern.bytes.empty() || pattern.bytes.size() != pattern.mask.size() ||
        pattern.bytes.size() > size || max_results == 0)
        return results;

    const auto final = size - pattern.bytes.size();
    for (std::size_t offset = 0; offset <= final; ++offset) {
        bool matches = true;
        for (std::size_t index = 0; index < pattern.bytes.size(); ++index) {
            if (pattern.mask[index] != 0 && data[offset + index] != pattern.bytes[index]) {
                matches = false;
                break;
            }
        }
        if (matches) {
            results.push_back(offset);
            if (results.size() >= max_results)
                break;
        }
    }
    return results;
}

std::optional<std::uintptr_t> ResolveRipRelative(
    const std::uintptr_t instruction,
    const std::size_t displacement_offset,
    const std::size_t instruction_length) noexcept {
    if (instruction == 0 || displacement_offset > std::numeric_limits<std::size_t>::max() - sizeof(std::int32_t))
        return std::nullopt;
    const auto displacement = *reinterpret_cast<const std::int32_t*>(instruction + displacement_offset);
    return instruction + instruction_length + displacement;
}

} // namespace deadlock_mvm
