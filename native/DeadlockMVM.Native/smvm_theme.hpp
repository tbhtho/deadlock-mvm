#pragma once

// SMVM internal editor theme + shared component helpers (Dear ImGui).
// Design contract: tools/research/smvm_ui_v2.md section 4 (palette,
// typography, spacing) and section 5 (component system). All widget states
// (normal/hover/pressed/selected/disabled) derive from the constants here;
// page code must never hardcode colors.

#include "imgui.h"

#include <cstdint>

namespace deadlock_mvm::smvm_theme {

[[nodiscard]] constexpr ImU32 Rgba(
    const std::uint8_t red,
    const std::uint8_t green,
    const std::uint8_t blue,
    const std::uint8_t alpha = 255) noexcept {
    return static_cast<ImU32>(red) | (static_cast<ImU32>(green) << 8) |
           (static_cast<ImU32>(blue) << 16) | (static_cast<ImU32>(alpha) << 24);
}

namespace colors {
    // Palette per spec section 4.1 (warm dark shell, brass accent).
    inline constexpr ImU32 kBackdrop = Rgba(0, 0, 0, 89);           // black @ 35%
    inline constexpr ImU32 kShell = Rgba(0x1C, 0x1B, 0x19, 245);    // @ 96%
    inline constexpr ImU32 kCard = Rgba(0x24, 0x23, 0x20);
    inline constexpr ImU32 kControl = Rgba(0x2C, 0x2A, 0x26);
    inline constexpr ImU32 kControlHover = Rgba(0x33, 0x31, 0x2C);  // lighten ~6%
    inline constexpr ImU32 kControlActive = Rgba(0x26, 0x24, 0x21); // darken ~6%
    inline constexpr ImU32 kHairline = Rgba(0x3A, 0x37, 0x2F);
    inline constexpr ImU32 kText = Rgba(0xED, 0xE6, 0xD8);
    inline constexpr ImU32 kMuted = Rgba(0x9A, 0x93, 0x8A);
    inline constexpr ImU32 kDisabledText = Rgba(0x9A, 0x93, 0x8A, 115); // 45%
    inline constexpr ImU32 kAccent = Rgba(0xD2, 0xA4, 0x53);
    inline constexpr ImU32 kAccentSoft = Rgba(0xD2, 0xA4, 0x53, 36);    // @ 14%
    inline constexpr ImU32 kAccentStrong = Rgba(0xA8, 0x7F, 0x3E);
    inline constexpr ImU32 kSuccess = Rgba(0x7F, 0xA3, 0x6B);
    inline constexpr ImU32 kWarning = Rgba(0xD9, 0x9A, 0x45);
    inline constexpr ImU32 kError = Rgba(0xC2, 0x5B, 0x4E);
    inline constexpr ImU32 kShadow = Rgba(0, 0, 0, 70);
} // namespace colors

[[nodiscard]] constexpr ImVec4 Vec4(const ImU32 color) noexcept {
    return ImVec4(
        static_cast<float>(color & 0xFFu) / 255.0F,
        static_cast<float>((color >> 8) & 0xFFu) / 255.0F,
        static_cast<float>((color >> 16) & 0xFFu) / 255.0F,
        static_cast<float>((color >> 24) & 0xFFu) / 255.0F);
}

struct Fonts final {
    ImFont* title{};      // 18 semibold  - header "SMVM"
    ImFont* page_title{}; // 17 semibold
    ImFont* section{};    // 13 semibold  - section titles
    ImFont* normal{};     // 13 regular
    ImFont* secondary{};  // 12 regular
    ImFont* hint{};       // 11 semibold  - keybind hints, status pills
    ImFont* mono{};       // 13 Consolas  - camera numbers, ticks
};

// Rebuilds the font atlas at (spec base size * scale), where scale is
// dpiScale * uiScale. Falls back to Segoe UI regular and finally to the
// embedded default font so the overlay never fails on a missing font file.
// The caller must recreate renderer device objects afterwards so the atlas
// texture is re-uploaded.
bool RebuildFontAtlas(Fonts& fonts, float scale) noexcept;

// Applies the full SMVM style (colors, 6px/4px rounding, spec spacing) and
// scales every size by the effective scale.
void ApplySmvmStyle(float scale) noexcept;

// Fonts currently rasterized into the atlas (valid after RebuildFontAtlas).
[[nodiscard]] const Fonts& GetFonts() noexcept;
// Effective scale the style was last applied with.
[[nodiscard]] float GetScale() noexcept;

enum class PillKind : std::uint32_t {
    success,
    warning,
    error,
    muted,
    accent,
};

// Section title (13 semibold) followed by a card child region. Returns false
// when the card is clipped away; call EndSection only when it returned true.
bool BeginSection(const char* title, float height = 0.0F, float width = 0.0F) noexcept;
void EndSection() noexcept;

[[nodiscard]] ImVec2 StatusPillSize(const char* text) noexcept;
void StatusPill(const char* text, PillKind kind) noexcept;

bool Button(const char* label, bool enabled = true, const ImVec2& size = ImVec2(0.0F, 0.0F)) noexcept;
bool PrimaryButton(const char* label, bool enabled = true, const ImVec2& size = ImVec2(0.0F, 0.0F)) noexcept;
bool DangerButton(const char* label, bool enabled = true, const ImVec2& size = ImVec2(0.0F, 0.0F)) noexcept;

// Returns the newly selected index, or the unchanged selection.
int SegmentedControl(
    const char* id,
    const char* const* labels,
    int count,
    int selected,
    bool enabled = true) noexcept;

// Switch-style toggle; returns true when clicked (caller queues the action).
bool Toggle(const char* label, bool value, bool enabled = true) noexcept;

// Numeric field + slider pair with a visible caption on the right. Keeps the
// in-flight edit stable while the authoritative value catches up.
// Returns true and sets out_value on edit.
bool SliderInput(
    const char* id,
    float current,
    float minimum,
    float maximum,
    float& out_value,
    const char* format = "%.1f",
    bool enabled = true,
    const char* label = nullptr) noexcept;

void KeybindHint(const char* text) noexcept;
void ValueRow(const char* label, const char* value, bool mono = false, bool available = true) noexcept;
bool KeyframeRow(int index, std::int64_t tick, double fov, double roll, bool selected) noexcept;
void EmptyState(const char* title, const char* body) noexcept;
// Call directly after the item the tooltip belongs to (works on disabled items).
void Tooltip(const char* text) noexcept;

enum class ConfirmResult : std::uint32_t {
    none,
    confirm,
    cancel,
};

void OpenConfirmation(const char* id) noexcept;
// Renders the modal when open; report and close on either choice.
ConfirmResult ConfirmationModal(
    const char* id,
    const char* title,
    const char* body,
    const char* confirm_label = "Confirm") noexcept;

} // namespace deadlock_mvm::smvm_theme
