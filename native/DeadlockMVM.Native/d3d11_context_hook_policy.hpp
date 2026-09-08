#pragma once

#include <cstddef>
#include <cstdint>

namespace deadlock_mvm {

enum class D3D11ContextInterfaceVersion : std::uint32_t {
    base = 0,
    context1 = 1,
    context2 = 2,
    context3 = 3,
    context4 = 4,
};

// ID3D11DeviceContext4 inherits every earlier method in one vtable. A shadow
// table must preserve the complete interface exposed by the concrete object;
// cloning only the 115 base entries makes calls to Context1-4 run past it.
[[nodiscard]] constexpr std::size_t D3D11ContextVtableEntryCount(
    const D3D11ContextInterfaceVersion version) noexcept {
    switch (version) {
        case D3D11ContextInterfaceVersion::context4: return 149;
        case D3D11ContextInterfaceVersion::context3: return 147;
        case D3D11ContextInterfaceVersion::context2: return 144;
        case D3D11ContextInterfaceVersion::context1: return 134;
        case D3D11ContextInterfaceVersion::base:
        default: return 115;
    }
}

inline constexpr std::size_t kD3D11OmSetRenderTargetsVtableIndex = 33;

[[nodiscard]] constexpr bool NeedsDepthContextObservation(
    const bool cinematic_armed,
    const bool recording_active,
    const bool synchronized_compositing_active,
    const std::uint32_t selected_pass_flags,
    const std::uint32_t pass_flags,
    const std::uint32_t depth_pass_mask) noexcept {
    const auto transactional_pass_needs_depth =
        (cinematic_armed || recording_active) &&
        (pass_flags & depth_pass_mask) != 0;
    const auto compositing_batch_needs_depth =
        synchronized_compositing_active &&
        (selected_pass_flags & depth_pass_mask) != 0;
    return transactional_pass_needs_depth || compositing_batch_needs_depth;
}

static_assert(
    kD3D11OmSetRenderTargetsVtableIndex <
    D3D11ContextVtableEntryCount(D3D11ContextInterfaceVersion::base));

} // namespace deadlock_mvm
