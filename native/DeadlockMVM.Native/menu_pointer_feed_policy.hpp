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

// Deadlock is a first-person shooter, so a pointer message that is not
// forwarded must be consumed rather than handed to the game. The camera
// takeover and the open modal menu are the two states that own the pointer;
// both must swallow the messages ImGui never consumes (double-clicks and the
// tilt wheel), otherwise a double-click inside the menu fires the weapon and a
// tilt gesture cycles the game's own weapon/zoom bindings.
[[nodiscard]] constexpr bool OwnsMenuPointerMessages(
    const bool menu_open,
    const bool camera_input_takeover) noexcept {
    return menu_open || camera_input_takeover;
}

} // namespace deadlock_mvm
