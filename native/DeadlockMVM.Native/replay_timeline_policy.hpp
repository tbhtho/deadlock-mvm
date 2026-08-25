#pragma once

#include <cstdint>
#include <limits>

namespace deadlock_mvm {

struct ReplayTimelineGeometry {
    float x{};
    float y{};
    float width{};
    float height{};
};

[[nodiscard]] constexpr bool ShouldShowReplayTimeline(
    const bool internal_enabled,
    const bool replay_active,
    const bool smvm_replay_ui) noexcept {
    return internal_enabled && replay_active && smvm_replay_ui;
}

[[nodiscard]] constexpr bool ShouldHideEditorUiDuringCinematic(
    const bool hide_ui_enabled,
    const bool campath_playing) noexcept {
    return hide_ui_enabled && campath_playing;
}

[[nodiscard]] constexpr bool ShouldShowEditorUi(
    const bool timeline_available,
    const bool hidden_for_cinematic,
    const bool menu_open) noexcept {
    return timeline_available && (!hidden_for_cinematic || menu_open);
}

[[nodiscard]] constexpr ReplayTimelineGeometry ComputeReplayTimelineGeometry(
    const float viewport_width,
    const float viewport_height,
    const float requested_scale,
    const bool anchor_top) noexcept {
    const auto scale = requested_scale > 0.0F ? requested_scale : 1.0F;
    const auto margin = 16.0F * scale;
    const auto available_width = viewport_width > margin * 2.0F
        ? viewport_width - margin * 2.0F
        : 1.0F;
    const auto desired_width = 720.0F * scale;
    const auto width = desired_width < available_width ? desired_width : available_width;
    const auto available_height = viewport_height > margin * 2.0F
        ? viewport_height - margin * 2.0F
        : 1.0F;
    const auto desired_height = 96.0F * scale;
    const auto height = desired_height < available_height ? desired_height : available_height;
    const auto centered_x = (viewport_width - width) * 0.5F;
    const auto bottom_y = viewport_height - height - margin;
    return {
        centered_x > 0.0F ? centered_x : 0.0F,
        anchor_top ? margin : (bottom_y > 0.0F ? bottom_y : 0.0F),
        width,
        height,
    };
}

[[nodiscard]] constexpr std::int64_t ClampReplayTimelineTick(
    const std::int64_t requested_tick,
    const std::int64_t total_ticks) noexcept {
    const auto maximum = total_ticks > 0
        ? total_ticks
        : (std::numeric_limits<std::int64_t>::max)();
    if (requested_tick <= 0)
        return 0;
    return requested_tick > maximum ? maximum : requested_tick;
}

[[nodiscard]] constexpr std::int32_t DesiredReplayPauseActionIndex(
    const bool currently_paused) noexcept {
    return currently_paused ? 0 : 1;
}

[[nodiscard]] constexpr bool ShouldLockReplayTimelineInput(
    const bool menu_open,
    const bool replay_seek_in_progress) noexcept {
    return !menu_open || replay_seek_in_progress;
}

[[nodiscard]] constexpr bool ShouldOfferPlayCinematic(
    const std::uint32_t keyframe_count) noexcept {
    return keyframe_count >= 3;
}

[[nodiscard]] constexpr bool ShouldShowCampathPlacementList(
    const bool has_path,
    const std::uint32_t keyframe_count) noexcept {
    return has_path && keyframe_count > 0;
}

[[nodiscard]] constexpr bool ShouldDrawCampathPlacementGuides(
    const bool smvm_movie_ui,
    const bool clean_view,
    const bool has_path,
    const bool campath_playing,
    const bool camera_readable) noexcept {
    return smvm_movie_ui && !clean_view && has_path && !campath_playing && camera_readable;
}

[[nodiscard]] constexpr bool ReplayTimelineEditorOwnsKeyboard(
    const bool menu_open,
    const bool tick_editor_active,
    const bool timeline_visible) noexcept {
    return menu_open && tick_editor_active && timeline_visible;
}

[[nodiscard]] constexpr bool ReplayTimelineTextInputConsumesKey(
    const bool editor_owns_keyboard,
    const bool menu_binding_matches) noexcept {
    // The editor may own ordinary typing, but the configured menu key is the
    // unconditional escape hatch back to SMVM Free Camera.
    return editor_owns_keyboard && !menu_binding_matches;
}

[[nodiscard]] constexpr bool ShouldQueuePendingReplaySeek(
    const bool request_pending,
    const bool replay_active,
    const std::int64_t current_tick,
    const std::uint64_t now_ms,
    const std::uint64_t retry_at_ms) noexcept {
    return request_pending && replay_active && current_tick >= 0 && now_ms >= retry_at_ms;
}

[[nodiscard]] constexpr bool IsReplayUiActionSessionCurrent(
    const bool replay_active,
    const std::uint64_t current_replay_session_generation,
    const std::uint64_t source_replay_session_generation) noexcept {
    return replay_active && current_replay_session_generation > 0 &&
           source_replay_session_generation == current_replay_session_generation;
}

[[nodiscard]] constexpr bool IsCinematicStartPromptReady(
    const bool armed,
    const bool has_path,
    const std::uint32_t keyframe_count,
    const std::int64_t current_tick,
    const std::int64_t first_keyframe_tick,
    const std::int64_t target_tick,
    const bool free_camera_ready,
    const bool campath_playing) noexcept {
    if (!armed || !has_path || keyframe_count < 3 || current_tick < 0 ||
        first_keyframe_tick != target_tick || target_tick < 0 ||
        !free_camera_ready || campath_playing) {
        return false;
    }
    const auto distance = current_tick >= target_tick
        ? current_tick - target_tick
        : target_tick - current_tick;
    return distance <= 2;
}

[[nodiscard]] constexpr double PlaybackSpeedFromPercent(const double requested_percent) noexcept {
    const auto finite_percent = requested_percent == requested_percent
        ? requested_percent
        : 100.0;
    const auto clamped_percent = finite_percent < 1.0
        ? 1.0
        : (finite_percent > 1000.0 ? 1000.0 : finite_percent);
    return clamped_percent / 100.0;
}

[[nodiscard]] constexpr bool ShouldStartCinematicForSpaceEvent(
    const bool prompt_ready,
    const bool space_was_released,
    const bool key_down,
    const bool repeated,
    const bool already_consumed) noexcept {
    return prompt_ready && space_was_released && key_down &&
           !repeated && !already_consumed;
}

} // namespace deadlock_mvm
