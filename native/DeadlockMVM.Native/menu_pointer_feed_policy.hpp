#pragma once

namespace deadlock_mvm {

// A modal menu can learn button state from three routes: the in-process
// window procedure, the launcher fallback, and direct OS state sampled on the
// render frame. The physical sample is intentionally sufficient by itself so
// a delayed or silently removed low-level hook cannot disable held dragging.
[[nodiscard]] constexpr bool ResolveMenuPointerButtonDown(
    const bool menu_open,
    const bool routed_down,
    const bool forwarded_down,
    const bool physical_down) noexcept {
    return menu_open && (routed_down || forwarded_down || physical_down);
}

} // namespace deadlock_mvm
