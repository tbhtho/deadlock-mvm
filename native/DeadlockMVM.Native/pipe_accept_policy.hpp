#pragma once

#include <cstdint>

namespace deadlock_mvm {

// Early injection deliberately precedes the launcher's replay confirmation,
// whose slow-start allowance is 150 seconds. Keep cleanup bounded without
// unloading a healthy backend before that managed workflow can connect.
constexpr std::uint64_t kInitialPipeAcceptTimeoutMilliseconds = 180'000;
constexpr std::uint32_t kInitialPipeAcceptPollMilliseconds = 100;

enum class PipeAcceptProgress : std::uint8_t {
    pending,
    connected,
    failed,
};

enum class InitialPipeAcceptAction : std::uint8_t {
    wait,
    accept,
    cancel_for_shutdown,
    cancel_for_timeout,
    fail,
};

[[nodiscard]] constexpr InitialPipeAcceptAction ResolveInitialPipeAcceptAction(
    const PipeAcceptProgress progress,
    const bool shutdown_requested,
    const std::uint64_t elapsed_milliseconds,
    const std::uint64_t timeout_milliseconds = kInitialPipeAcceptTimeoutMilliseconds) noexcept {
    if (progress == PipeAcceptProgress::connected)
        return InitialPipeAcceptAction::accept;
    if (progress == PipeAcceptProgress::failed)
        return InitialPipeAcceptAction::fail;
    if (shutdown_requested)
        return InitialPipeAcceptAction::cancel_for_shutdown;
    if (elapsed_milliseconds >= timeout_milliseconds)
        return InitialPipeAcceptAction::cancel_for_timeout;
    return InitialPipeAcceptAction::wait;
}

[[nodiscard]] constexpr std::uint32_t InitialPipeAcceptWaitMilliseconds(
    const std::uint64_t elapsed_milliseconds,
    const std::uint64_t timeout_milliseconds = kInitialPipeAcceptTimeoutMilliseconds) noexcept {
    if (elapsed_milliseconds >= timeout_milliseconds)
        return 0;
    const auto remaining = timeout_milliseconds - elapsed_milliseconds;
    return remaining < kInitialPipeAcceptPollMilliseconds
        ? static_cast<std::uint32_t>(remaining)
        : kInitialPipeAcceptPollMilliseconds;
}

} // namespace deadlock_mvm
