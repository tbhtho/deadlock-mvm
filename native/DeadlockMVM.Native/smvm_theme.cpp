#include "smvm_theme.hpp"
#include "optimistic_edit.hpp"

#include <algorithm>
#include <array>
#include <cstdio>

namespace deadlock_mvm::smvm_theme {
namespace {

Fonts g_fonts{};
float g_scale = 1.0F;

[[nodiscard]] ImFont* AddFont(
    const char* path,
    const float base_size,
    const float scale,
    const ImWchar* ranges) noexcept {
    ImFontConfig config{};
    config.SizePixels = base_size * scale;
    config.GlyphRanges = ranges;
    config.OversampleH = 2;
    config.OversampleV = 2;
    return ImGui::GetIO().Fonts->AddFontFromFileTTF(path, base_size * scale, &config);
}

[[nodiscard]] ImU32 PillColor(const PillKind kind) noexcept {
    switch (kind) {
        case PillKind::success: return colors::kSuccess;
        case PillKind::warning: return colors::kWarning;
        case PillKind::error: return colors::kError;
        case PillKind::accent: return colors::kAccent;
        case PillKind::muted:
        default: return colors::kMuted;
    }
}

void PushButtonColors(const ImU32 normal, const ImU32 hovered, const ImU32 active, const ImU32 text) noexcept {
    ImGui::PushStyleColor(ImGuiCol_Button, Vec4(normal));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, Vec4(hovered));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, Vec4(active));
    ImGui::PushStyleColor(ImGuiCol_Text, Vec4(text));
}

} // namespace

bool RebuildFontAtlas(Fonts& fonts, const float scale) noexcept {
    // Latin-1 plus general punctuation (em dash, bullets) for the editor copy.
    static const ImWchar ranges[] = {0x0020, 0x00FF, 0x2010, 0x205E, 0};
    auto& io = ImGui::GetIO();
    io.Fonts->Clear();
    constexpr auto kRegular = "C:\\Windows\\Fonts\\segoeui.ttf";
    constexpr auto kSemibold = "C:\\Windows\\Fonts\\segoeuib.ttf";
    constexpr auto kMono = "C:\\Windows\\Fonts\\consola.ttf";
    fonts.title = AddFont(kSemibold, 18.0F, scale, ranges);
    fonts.page_title = AddFont(kSemibold, 17.0F, scale, ranges);
    fonts.section = AddFont(kSemibold, 13.0F, scale, ranges);
    fonts.normal = AddFont(kRegular, 13.0F, scale, ranges);
    fonts.secondary = AddFont(kRegular, 12.0F, scale, ranges);
    fonts.hint = AddFont(kSemibold, 11.0F, scale, ranges);
    fonts.mono = AddFont(kMono, 13.0F, scale, ranges);
    if (fonts.normal == nullptr)
        fonts.normal = io.Fonts->AddFontDefault();
    if (fonts.normal == nullptr)
        return false;
    const auto fallback = [&io](ImFont*& font, ImFont* backup) noexcept {
        if (font == nullptr)
            font = backup != nullptr ? backup : io.Fonts->Fonts.back();
    };
    fallback(fonts.title, fonts.normal);
    fallback(fonts.page_title, fonts.normal);
    fallback(fonts.section, fonts.normal);
    fallback(fonts.secondary, fonts.normal);
    fallback(fonts.hint, fonts.normal);
    fallback(fonts.mono, fonts.normal);
    g_fonts = fonts;
    return true;
}

void ApplySmvmStyle(const float scale) noexcept {
    g_scale = scale;
    auto& style = ImGui::GetStyle();
    style = ImGuiStyle{};
    style.Alpha = 1.0F;
    style.DisabledAlpha = 0.45F;
    style.WindowPadding = ImVec2(12.0F, 12.0F);
    style.WindowRounding = 6.0F;
    style.WindowBorderSize = 1.0F;
    style.WindowMinSize = ImVec2(64.0F, 44.0F);
    style.ChildRounding = 6.0F;
    style.ChildBorderSize = 1.0F;
    style.PopupRounding = 6.0F;
    style.PopupBorderSize = 1.0F;
    style.FramePadding = ImVec2(10.0F, 5.0F);
    style.FrameRounding = 4.0F;
    style.FrameBorderSize = 1.0F;
    style.ItemSpacing = ImVec2(8.0F, 8.0F);
    style.ItemInnerSpacing = ImVec2(8.0F, 6.0F);
    style.CellPadding = ImVec2(6.0F, 4.0F);
    style.IndentSpacing = 12.0F;
    style.ScrollbarSize = 10.0F;
    style.ScrollbarRounding = 4.0F;
    style.GrabMinSize = 8.0F;
    style.GrabRounding = 4.0F;
    style.SeparatorTextBorderSize = 1.0F;
    style.MouseCursorScale = 1.0F;

    auto* palette = style.Colors;
    palette[ImGuiCol_Text] = Vec4(colors::kText);
    palette[ImGuiCol_TextDisabled] = Vec4(colors::kDisabledText);
    palette[ImGuiCol_WindowBg] = Vec4(colors::kShell);
    palette[ImGuiCol_ChildBg] = Vec4(colors::kCard);
    palette[ImGuiCol_PopupBg] = Vec4(colors::kShell);
    palette[ImGuiCol_Border] = Vec4(colors::kHairline);
    palette[ImGuiCol_BorderShadow] = ImVec4(0.0F, 0.0F, 0.0F, 0.0F);
    palette[ImGuiCol_FrameBg] = Vec4(colors::kControl);
    palette[ImGuiCol_FrameBgHovered] = Vec4(colors::kControlHover);
    palette[ImGuiCol_FrameBgActive] = Vec4(colors::kControlActive);
    palette[ImGuiCol_TitleBg] = Vec4(colors::kShell);
    palette[ImGuiCol_TitleBgActive] = Vec4(colors::kShell);
    palette[ImGuiCol_TitleBgCollapsed] = Vec4(colors::kShell);
    palette[ImGuiCol_MenuBarBg] = Vec4(colors::kCard);
    palette[ImGuiCol_ScrollbarBg] = ImVec4(0.0F, 0.0F, 0.0F, 0.0F);
    palette[ImGuiCol_ScrollbarGrab] = Vec4(colors::kHairline);
    palette[ImGuiCol_ScrollbarGrabHovered] = Vec4(colors::kMuted);
    palette[ImGuiCol_ScrollbarGrabActive] = Vec4(colors::kMuted);
    palette[ImGuiCol_CheckMark] = Vec4(colors::kAccent);
    palette[ImGuiCol_SliderGrab] = Vec4(colors::kAccent);
    palette[ImGuiCol_SliderGrabActive] = Vec4(colors::kAccentStrong);
    palette[ImGuiCol_Button] = Vec4(colors::kControl);
    palette[ImGuiCol_ButtonHovered] = Vec4(colors::kControlHover);
    palette[ImGuiCol_ButtonActive] = Vec4(colors::kControlActive);
    palette[ImGuiCol_Header] = Vec4(colors::kAccentSoft);
    palette[ImGuiCol_HeaderHovered] = Vec4(Rgba(0xD2, 0xA4, 0x53, 56));
    palette[ImGuiCol_HeaderActive] = Vec4(Rgba(0xD2, 0xA4, 0x53, 72));
    palette[ImGuiCol_Separator] = Vec4(colors::kHairline);
    palette[ImGuiCol_SeparatorHovered] = Vec4(colors::kMuted);
    palette[ImGuiCol_SeparatorActive] = Vec4(colors::kMuted);
    palette[ImGuiCol_ResizeGrip] = Vec4(colors::kHairline);
    palette[ImGuiCol_ResizeGripHovered] = Vec4(colors::kAccent);
    palette[ImGuiCol_ResizeGripActive] = Vec4(colors::kAccent);
    palette[ImGuiCol_TabHovered] = Vec4(colors::kControlHover);
    palette[ImGuiCol_Tab] = Vec4(colors::kControl);
    palette[ImGuiCol_TabSelected] = Vec4(colors::kAccentSoft);
    palette[ImGuiCol_PlotLines] = Vec4(colors::kAccent);
    palette[ImGuiCol_PlotHistogram] = Vec4(colors::kAccent);
    palette[ImGuiCol_TextSelectedBg] = Vec4(Rgba(0xD2, 0xA4, 0x53, 90));
    palette[ImGuiCol_NavCursor] = Vec4(colors::kAccent);
    palette[ImGuiCol_ModalWindowDimBg] = ImVec4(0.0F, 0.0F, 0.0F, 0.35F);
    style.ScaleAllSizes(scale);
}

const Fonts& GetFonts() noexcept {
    return g_fonts;
}

float GetScale() noexcept {
    return g_scale;
}

bool BeginSection(const char* title, const float height, const float width) noexcept {
    // Treat the heading and card as one layout item. Several pages place
    // sections side by side with SameLine(); without a group only the heading
    // stays on that line and the child card wraps beneath the previous column.
    ImGui::BeginGroup();
    ImGui::PushFont(g_fonts.section);
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(ImGui::GetStyle().ItemSpacing.x, 3.0F * g_scale));
    ImGui::TextUnformatted(title);
    ImGui::PopStyleVar();
    ImGui::PopFont();
    ImGui::PushStyleColor(ImGuiCol_ChildBg, Vec4(colors::kCard));
    ImGui::PushStyleColor(ImGuiCol_Border, Vec4(colors::kHairline));
    const auto open = ImGui::BeginChild(
        title,
        ImVec2(width, height),
        ImGuiChildFlags_Borders | ImGuiChildFlags_AutoResizeY,
        ImGuiWindowFlags_None);
    if (!open) {
        // EndChild is still required for a clipped child; callers may skip the
        // contents and EndSection in this case.
        ImGui::EndChild();
        ImGui::PopStyleColor(2);
        ImGui::EndGroup();
        return false;
    }
    ImGui::Dummy(ImVec2(1.0F, 1.0F));
    return true;
}

void EndSection() noexcept {
    ImGui::Dummy(ImVec2(1.0F, 1.0F));
    ImGui::EndChild();
    ImGui::PopStyleColor(2);
    ImGui::EndGroup();
}

ImVec2 StatusPillSize(const char* text) noexcept {
    const auto scale = g_scale;
    ImGui::PushFont(g_fonts.hint);
    const auto text_size = ImGui::CalcTextSize(text);
    ImGui::PopFont();
    return ImVec2(
        text_size.x + (26.0F * scale),
        text_size.y + (10.0F * scale));
}

void StatusPill(const char* text, const PillKind kind) noexcept {
    const auto accent = PillColor(kind);
    const auto scale = g_scale;
    const auto size = StatusPillSize(text);
    auto* draw_list = ImGui::GetWindowDrawList();
    const auto pos = ImGui::GetCursorScreenPos();
    draw_list->AddRectFilled(
        pos,
        ImVec2(pos.x + size.x, pos.y + size.y),
        (accent & 0x00FFFFFFu) | (36u << 24),
        size.y * 0.5F);
    draw_list->AddRect(
        pos,
        ImVec2(pos.x + size.x, pos.y + size.y),
        (accent & 0x00FFFFFFu) | (140u << 24),
        size.y * 0.5F);
    const auto dot_radius = 2.5F * scale;
    const auto dot_center = ImVec2(pos.x + (11.0F * scale), pos.y + (size.y * 0.5F));
    draw_list->AddCircleFilled(dot_center, dot_radius, accent);
    ImGui::PushFont(g_fonts.hint);
    const auto text_size = ImGui::CalcTextSize(text);
    draw_list->AddText(
        g_fonts.hint,
        g_fonts.hint->FontSize,
        ImVec2(pos.x + (19.0F * scale), pos.y + ((size.y - text_size.y) * 0.5F)),
        accent,
        text);
    ImGui::PopFont();
    ImGui::Dummy(size);
}

namespace {

bool ThemedButton(
    const char* label,
    const bool enabled,
    const ImVec2& size,
    const ImU32 normal,
    const ImU32 hovered,
    const ImU32 active,
    const ImU32 text) noexcept {
    ImGui::BeginDisabled(!enabled);
    PushButtonColors(normal, hovered, active, text);
    const auto pressed = ImGui::Button(label, size);
    ImGui::PopStyleColor(4);
    ImGui::EndDisabled();
    return pressed;
}

} // namespace

bool Button(const char* label, const bool enabled, const ImVec2& size) noexcept {
    return ThemedButton(
        label,
        enabled,
        size,
        colors::kControl,
        colors::kControlHover,
        colors::kControlActive,
        colors::kText);
}

bool PrimaryButton(const char* label, const bool enabled, const ImVec2& size) noexcept {
    return ThemedButton(
        label,
        enabled,
        size,
        colors::kAccent,
        Rgba(0xDE, 0xB4, 0x68),
        colors::kAccentStrong,
        Rgba(0x1C, 0x1B, 0x19));
}

bool DangerButton(const char* label, const bool enabled, const ImVec2& size) noexcept {
    return ThemedButton(
        label,
        enabled,
        size,
        Rgba(0xC2, 0x5B, 0x4E, 48),
        Rgba(0xC2, 0x5B, 0x4E, 80),
        Rgba(0xC2, 0x5B, 0x4E, 110),
        colors::kError);
}

int SegmentedControl(
    const char* id,
    const char* const* labels,
    const int count,
    const int selected,
    const bool enabled) noexcept {
    auto result = selected;
    ImGui::PushID(id);
    ImGui::BeginDisabled(!enabled);
    for (auto index = 0; index < count; ++index) {
        if (index > 0)
            ImGui::SameLine(0.0F, 1.0F);
        const auto active = index == selected;
        ImGui::PushID(index);
        PushButtonColors(
            active ? colors::kAccentSoft : colors::kControl,
            active ? Rgba(0xD2, 0xA4, 0x53, 56) : colors::kControlHover,
            active ? Rgba(0xD2, 0xA4, 0x53, 72) : colors::kControlActive,
            active ? colors::kAccent : colors::kMuted);
        if (ImGui::Button(labels[index]))
            result = index;
        ImGui::PopStyleColor(4);
        ImGui::PopID();
    }
    ImGui::EndDisabled();
    ImGui::PopID();
    return result;
}

bool Toggle(const char* label, const bool value, const bool enabled) noexcept {
    const auto scale = g_scale;
    const auto width = 34.0F * scale;
    const auto height = 18.0F * scale;
    ImGui::PushID(label);
    ImGui::BeginDisabled(!enabled);
    const auto pos = ImGui::GetCursorScreenPos();
    const auto clicked = ImGui::InvisibleButton("##toggle", ImVec2(width, height));
    const auto hovered = ImGui::IsItemHovered();
    auto* draw_list = ImGui::GetWindowDrawList();
    const auto track = value ? colors::kAccentSoft : (hovered ? colors::kControlHover : colors::kControl);
    draw_list->AddRectFilled(
        pos,
        ImVec2(pos.x + width, pos.y + height),
        track,
        height * 0.5F);
    draw_list->AddRect(
        pos,
        ImVec2(pos.x + width, pos.y + height),
        value ? colors::kAccent : colors::kHairline,
        height * 0.5F);
    const auto knob_radius = (height * 0.5F) - (3.0F * scale);
    const auto knob_x = value ? pos.x + width - (height * 0.5F) : pos.x + (height * 0.5F);
    draw_list->AddCircleFilled(
        ImVec2(knob_x, pos.y + (height * 0.5F)),
        knob_radius,
        value ? colors::kAccent : colors::kMuted);
    ImGui::SameLine(0.0F, ImGui::GetStyle().ItemInnerSpacing.x);
    ImGui::AlignTextToFramePadding();
    ImGui::TextUnformatted(label);
    ImGui::EndDisabled();
    ImGui::PopID();
    return clicked;
}

bool SliderInput(
    const char* id,
    const float current,
    const float minimum,
    const float maximum,
    float& out_value,
    const char* format,
    const bool enabled,
    const char* label) noexcept {
    auto changed = false;
    ImGui::PushID(id);
    ImGui::BeginDisabled(!enabled);
    auto* storage = ImGui::GetStateStorage();
    const auto width = ImGui::GetContentRegionAvail().x;
    const auto field_width = 84.0F * g_scale;
    const auto label_width = label != nullptr
        ? ImGui::CalcTextSize(label).x + ImGui::GetStyle().ItemSpacing.x
        : 0.0F;
    ImGui::SetNextItemWidth(field_width);
    // The numeric field and track share one optimistic value. Retain it after
    // release until the authoritative managed snapshot acknowledges the edit;
    // this prevents a visible snap-back during the IPC round trip.
    const auto value_id = ImGui::GetID("##edit_value");
    const auto pending_id = ImGui::GetID("##edit_pending_since");
    const auto initialized_id = ImGui::GetID("##edit_initialized");
    auto value = storage->GetFloat(value_id, current);
    auto pending_since = static_cast<double>(storage->GetFloat(pending_id, -1.0F));
    if (storage->GetInt(initialized_id, 0) == 0) {
        value = current;
        pending_since = -1.0;
        storage->SetInt(initialized_id, 1);
    }
    constexpr auto kPendingTimeoutSeconds = 2.0;
    const auto epsilon = std::max(0.0005F, (maximum - minimum) * 0.00001F);
    static_cast<void>(ReconcileOptimisticEdit(
        current, ImGui::GetTime(), kPendingTimeoutSeconds, epsilon, value, pending_since));
    value = std::clamp(value, minimum, maximum);
    storage->SetFloat(value_id, value);
    storage->SetFloat(pending_id, static_cast<float>(pending_since));

    auto edited = false;
    ImGui::PushFont(g_fonts.mono);
    ImGui::InputFloat("##field", &value, 0.0F, 0.0F, format);
    ImGui::PopFont();
    if (ImGui::IsItemEdited()) {
        edited = true;
    }
    ImGui::SameLine();
    // Match the proven archived in-process-menu pattern: the whole visible
    // track is one InvisibleButton and an active hold maps the cursor's
    // absolute X position to the value range. This does not depend on drag
    // deltas surviving a slow or collapsed render frame.
    const auto track_width = std::max(
        80.0F * g_scale,
        width - field_width - label_width - ImGui::GetStyle().ItemSpacing.x * 2.0F);
    const auto track_height = ImGui::GetFrameHeight();
    const auto track_position = ImGui::GetCursorScreenPos();
    static_cast<void>(ImGui::InvisibleButton(
        "##slider",
        ImVec2(track_width, track_height)));
    const auto track_hovered = ImGui::IsItemHovered();
    const auto track_active = enabled && ImGui::IsItemActive();
    if (track_active && track_width > 0.0F && maximum > minimum) {
        const auto fraction = std::clamp(
            (ImGui::GetIO().MousePos.x - track_position.x) / track_width,
            0.0F,
            1.0F);
        const auto next = minimum + fraction * (maximum - minimum);
        if (std::fabs(next - value) > epsilon) {
            value = next;
            edited = true;
        }
    }
    auto* draw_list = ImGui::GetWindowDrawList();
    const auto track_max = ImVec2(
        track_position.x + track_width,
        track_position.y + track_height);
    const auto track_color = track_active
        ? colors::kControlActive
        : track_hovered ? colors::kControlHover : colors::kControl;
    draw_list->AddRectFilled(
        track_position,
        track_max,
        track_color,
        ImGui::GetStyle().FrameRounding);
    draw_list->AddRect(
        track_position,
        track_max,
        track_active ? colors::kAccent : colors::kHairline,
        ImGui::GetStyle().FrameRounding);
    const auto fraction = maximum > minimum
        ? std::clamp((value - minimum) / (maximum - minimum), 0.0F, 1.0F)
        : 0.0F;
    const auto knob_radius = std::max(3.0F * g_scale, track_height * 0.22F);
    const auto knob_x = std::clamp(
        track_position.x + fraction * track_width,
        track_position.x + knob_radius,
        track_max.x - knob_radius);
    draw_list->AddLine(
        ImVec2(track_position.x + 3.0F * g_scale, track_position.y + track_height * 0.5F),
        ImVec2(knob_x, track_position.y + track_height * 0.5F),
        colors::kAccent,
        std::max(2.0F, 2.0F * g_scale));
    draw_list->AddCircleFilled(
        ImVec2(knob_x, track_position.y + track_height * 0.5F),
        knob_radius,
        colors::kAccent);
    if (edited) {
        MarkOptimisticEdit(value, minimum, maximum, ImGui::GetTime(), value, pending_since);
        storage->SetFloat(value_id, value);
        storage->SetFloat(pending_id, static_cast<float>(pending_since));
        out_value = value;
        changed = true;
    }
    if (label != nullptr) {
        ImGui::SameLine();
        ImGui::PushStyleColor(ImGuiCol_Text, Vec4(colors::kMuted));
        ImGui::TextUnformatted(label);
        ImGui::PopStyleColor();
    }
    ImGui::EndDisabled();
    ImGui::PopID();
    return changed;
}

void KeybindHint(const char* text) noexcept {
    ImGui::PushFont(g_fonts.hint);
    ImGui::PushStyleColor(ImGuiCol_Text, Vec4(colors::kAccent));
    ImGui::TextUnformatted(text);
    ImGui::PopStyleColor();
    ImGui::PopFont();
}

void ValueRow(const char* label, const char* value, const bool mono, const bool available) noexcept {
    const auto label_width = 110.0F * g_scale;
    ImGui::PushStyleColor(ImGuiCol_Text, Vec4(colors::kMuted));
    ImGui::TextUnformatted(label);
    ImGui::PopStyleColor();
    ImGui::SameLine(label_width);
    if (mono)
        ImGui::PushFont(g_fonts.mono);
    ImGui::PushStyleColor(ImGuiCol_Text, Vec4(available ? colors::kText : colors::kDisabledText));
    ImGui::TextUnformatted(available ? value : "--");
    ImGui::PopStyleColor();
    if (mono)
        ImGui::PopFont();
}

bool KeyframeRow(
    const int index,
    const std::int64_t tick,
    const double fov,
    const double roll,
    const bool selected) noexcept {
    std::array<char, 96> text{};
    static_cast<void>(std::snprintf(
        text.data(),
        text.size(),
        "%02d  \xC2\xB7  Tick %lld  \xC2\xB7  FOV %.0f\xC2\xB0  \xC2\xB7  Roll %.0f\xC2\xB0",
        index + 1,
        static_cast<long long>(tick),
        fov,
        roll));
    ImGui::PushID(index);
    if (selected) {
        ImGui::PushStyleColor(ImGuiCol_Header, Vec4(colors::kAccentSoft));
        ImGui::PushStyleColor(ImGuiCol_HeaderHovered, Vec4(Rgba(0xD2, 0xA4, 0x53, 56)));
        ImGui::PushStyleColor(ImGuiCol_Text, Vec4(colors::kText));
    } else {
        ImGui::PushStyleColor(ImGuiCol_Text, Vec4(colors::kMuted));
    }
    ImGui::PushFont(g_fonts.mono);
    const auto clicked = ImGui::Selectable(text.data(), selected, 0, ImVec2(0.0F, 0.0F));
    ImGui::PopFont();
    if (selected) {
        const auto pos = ImGui::GetItemRectMin();
        const auto max = ImGui::GetItemRectMax();
        ImGui::GetWindowDrawList()->AddRectFilled(
            pos,
            ImVec2(pos.x + (2.0F * g_scale), max.y),
            colors::kAccent);
        ImGui::PopStyleColor(3);
    } else {
        ImGui::PopStyleColor();
    }
    ImGui::PopID();
    return clicked;
}

void EmptyState(const char* title, const char* body) noexcept {
    const auto width = ImGui::GetContentRegionAvail().x;
    ImGui::Dummy(ImVec2(1.0F, 18.0F * g_scale));
    ImGui::PushFont(g_fonts.page_title);
    const auto title_width = ImGui::CalcTextSize(title).x;
    ImGui::SetCursorPosX(ImGui::GetCursorPosX() + std::max(0.0F, (width - title_width) * 0.5F));
    ImGui::TextUnformatted(title);
    ImGui::PopFont();
    ImGui::PushFont(g_fonts.secondary);
    ImGui::PushStyleColor(ImGuiCol_Text, Vec4(colors::kMuted));
    const auto body_width = ImGui::CalcTextSize(body).x;
    ImGui::SetCursorPosX(ImGui::GetCursorPosX() + std::max(0.0F, (width - body_width) * 0.5F));
    ImGui::TextUnformatted(body);
    ImGui::PopStyleColor();
    ImGui::PopFont();
}

void Tooltip(const char* text) noexcept {
    if (ImGui::IsItemHovered(
            ImGuiHoveredFlags_ForTooltip | ImGuiHoveredFlags_AllowWhenDisabled |
            ImGuiHoveredFlags_DelayNormal))
        ImGui::SetTooltip("%s", text);
}

void OpenConfirmation(const char* id) noexcept {
    ImGui::OpenPopup(id);
}

ConfirmResult ConfirmationModal(
    const char* id,
    const char* title,
    const char* body,
    const char* confirm_label) noexcept {
    auto result = ConfirmResult::none;
    const auto scale = g_scale;
    ImGui::SetNextWindowSizeConstraints(
        ImVec2(280.0F * scale, 0.0F),
        ImVec2(360.0F * scale, 240.0F * scale));
    if (ImGui::BeginPopupModal(id, nullptr, ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove)) {
        ImGui::PushFont(g_fonts.section);
        ImGui::TextUnformatted(title);
        ImGui::PopFont();
        ImGui::Spacing();
        ImGui::PushStyleColor(ImGuiCol_Text, Vec4(colors::kMuted));
        ImGui::TextWrapped("%s", body);
        ImGui::PopStyleColor();
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        if (DangerButton(confirm_label, true, ImVec2(110.0F * scale, 0.0F))) {
            result = ConfirmResult::confirm;
            ImGui::CloseCurrentPopup();
        }
        ImGui::SameLine();
        if (Button("Cancel", true, ImVec2(80.0F * scale, 0.0F))) {
            result = ConfirmResult::cancel;
            ImGui::CloseCurrentPopup();
        }
        ImGui::EndPopup();
    }
    return result;
}

} // namespace deadlock_mvm::smvm_theme
