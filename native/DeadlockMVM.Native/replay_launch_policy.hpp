#pragma once

#include <cwchar>

namespace deadlock_mvm {

[[nodiscard]] inline bool HasRequiredReplayLaunchArguments(
    const int argument_count,
    wchar_t* const* const arguments) noexcept {
    if (argument_count <= 0 || arguments == nullptr)
        return false;

    auto insecure = false;
    auto secure = false;
    auto replay = false;
    for (auto index = 0; index < argument_count; ++index) {
        const auto* const argument = arguments[index];
        if (argument == nullptr)
            continue;
        if (_wcsicmp(argument, L"-insecure") == 0)
            insecure = true;
        if (_wcsicmp(argument, L"-secure") == 0)
            secure = true;
        if (_wcsicmp(argument, L"+playdemo") != 0 || index + 1 >= argument_count)
            continue;
        const auto* const replay_path = arguments[index + 1];
        if (replay_path != nullptr && replay_path[0] != L'\0' &&
            replay_path[0] != L'+' && replay_path[0] != L'-')
            replay = true;
    }
    return insecure && !secure && replay;
}

} // namespace deadlock_mvm
