#include "smvm_overlay.hpp"

#include "free_camera_input.hpp"
#include "manual_camera_input_policy.hpp"
#include "manual_mouse_fallback.hpp"
#include "recording_visual_policy.hpp"
#include "render_camera_policy.hpp"
#include "replay_timeline_policy.hpp"
#include "smvm_input_route.hpp"

#include "campath_math.hpp"
#include "pattern_scan.hpp"
#include "smvm_input_gate.hpp"
#include "smvm_theme.hpp"
#include "smvm_ui.hpp"

#include "imgui.h"
#include "imgui_impl_dx11.h"

#include <WinSock2.h>
#include <WS2tcpip.h>
#include <Windows.h>
#include <windowsx.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <optional>
#include <span>
#include <string_view>

namespace deadlock_mvm {
namespace {

constexpr std::size_t kPresentVtableIndex = 8;
constexpr std::size_t kResizeBuffersVtableIndex = 13;
constexpr std::size_t kCreateSwapchainVtableIndex = 10;
constexpr std::size_t kFactoryVtableEntryCount = 14;
constexpr std::size_t kSwapchainVtableEntryCount = 18;
constexpr std::size_t kInputSystemEnableVtableIndex = 13;
constexpr std::size_t kInputSystemRequiredVtableEntries = kInputSystemEnableVtableIndex + 1;
constexpr auto kInputSystemInterfaceName = "InputSystemVersion001";
constexpr std::ptrdiff_t kRenderFactoryOffset = 0xA8;
constexpr std::string_view kRenderFactoryPattern =
    "4C 8B 81 A8 ED 01 00 49 8B C9 48 8B 05 ?? ?? ?? ?? "
    "48 8B 90 A8 00 00 00 41 0F 11 41 18";
constexpr std::size_t kMaxVertices = 65520;
constexpr float kNearPlane = 1.0F;
constexpr std::uint32_t kSmoothVisualizationSamplesPerSegment = 24;
constexpr std::size_t kMaxCachedPathLines =
    (kMaxCampathKeyframes - 1) * kSmoothVisualizationSamplesPerSegment;
constexpr std::size_t kCameraMarkerLineCount = 9;
// Cold replay launches can load the Direct3D render module well after the
// native backend connects. Keep polling without blocking shutdown.
constexpr auto kInstallTimeout = std::chrono::seconds(120);
constexpr auto kInstallRetryInterval = std::chrono::milliseconds(1);
constexpr std::uint8_t kManualRawKeyRoute = 1u << 0;
constexpr std::uint8_t kManualWindowKeyRoute = 1u << 1;
constexpr std::uint8_t kManualPolledKeyRoute = 1u << 2;
constexpr std::uint8_t kMenuRawKeyRoute = 1u << 0;
constexpr std::uint8_t kMenuWindowKeyRoute = 1u << 1;
constexpr std::int32_t kFirstManualBindingAction = kSmvmFirstManualBindingAction;
constexpr std::int32_t kFirstEditorBindingAction = kSmvmFirstEditorBindingAction;
constexpr std::int32_t kLastBindingAction = kSmvmLastBindingAction;
constexpr auto kBindingResponseTimeoutMs = 1500ULL;
constexpr auto kBindingFeedbackDurationMs = 2200ULL;
constexpr std::uint32_t kRecordingProfileSoftRestoreDebt = 1u << 0;
constexpr std::uint32_t kRecordingProfileHardRestoreDebt = 1u << 1;
constexpr std::uint32_t kRecordingProfileAllRestoreDebt =
    kRecordingProfileSoftRestoreDebt | kRecordingProfileHardRestoreDebt;
enum class RecordingProfileRecoveryKind : std::uint32_t {
    none = 0,
    explicit_restore = 1,
    reconnect_reassert = 2,
    owner_transition = 3,
    replay_end_restore = 4,
};
// Lock-free SPSC ring from the window thread to the render thread. The render
// thread replays the events into ImGui before NewFrame; overflow drops input
// rather than blocking Present.
constexpr std::size_t kUiInputEventCapacity = 64;
constexpr UINT kSmvmCursorTransitionMessage = WM_APP + 0x4D0;
constexpr UINT kSmvmRestoreWindowProcedureMessage = WM_APP + 0x4D1;
constexpr UINT kSmvmPointerActionMessage = WM_APP + 0x4D2;
constexpr UINT kSmvmManualPointerTransitionMessage = WM_APP + 0x4D3;
constexpr UINT_PTR kSmvmModalInputTimerId = 0x4D564D01;
constexpr UINT kSmvmModalInputTimerIntervalMs = 16;
constexpr auto kCallbackDrainTimeout = std::chrono::milliseconds(1000);
constexpr std::size_t kMaxRegisteredRawInputDevices = 32;
constexpr std::size_t kMaxSuspendedRawMouseRegistrations = 4;
constexpr USHORT kGenericDesktopUsagePage = 0x01;
constexpr USHORT kMouseUsage = 0x02;

enum class SmvmPointerAction : std::uint16_t {
    none = 0,
    left_click = 1,
    right_click = 2,
    middle_click = 3,
    x1_click = 4,
    x2_click = 5,
    wheel = 6,
    validate = 7,
};

enum class ManualInputFailure : std::uint32_t {
    none = 0,
    invalid_window_thread = 1,
    sdl_api_unavailable = 2,
    sdl_window_unavailable = 3,
    menu_or_foreground = 4,
    sdl_enable_failed = 5,
    sdl_verify_failed = 6,
    raw_registration_missing = 7,
    readiness_incomplete = 8,
    camera_snapshot_not_ready = 9,
    menu_restore_failed = 10,
};

enum class RawMouseRegistrationDisposition : std::uint32_t {
    query_failed = 0,
    none = 1,
    null_target = 2,
    exact_window = 3,
    other_window = 4,
};

// UI input event kinds queued from the window thread for ImGui.
enum SmvmUiEventKind : std::uint32_t {
    smvm_ui_event_key = 1,   // a = virtual key, b = down
    smvm_ui_event_char = 2,  // a = codepoint
    smvm_ui_event_wheel = 3, // a = wheel delta
    smvm_ui_event_click = 4, // a = button index (down+up pair)
};

struct SmvmUiInputEvent final {
    std::uint32_t kind{};
    std::int32_t a{};
    std::int32_t b{};
};

struct WorldLabel final {
    float x{};
    float y{};
    float scale{1.0F};
    bool selected{};
    std::array<char, 48> text{};
};

[[nodiscard]] constexpr std::uint32_t RequiredInputModifiers(const std::uint32_t binding) noexcept {
    return (binding & kSmvmInputModifierMask) >> 16;
}

[[nodiscard]] constexpr bool RequiredInputModifiersAreActive(
    const std::uint32_t binding,
    const std::uint32_t active_modifiers) noexcept {
    const auto required = RequiredInputModifiers(binding);
    return (active_modifiers & required) == required;
}

[[nodiscard]] constexpr bool BindingOwnsKeyboardKey(
    const std::uint32_t binding,
    const std::uint32_t key,
    const std::uint32_t active_modifiers) noexcept {
    const auto base = binding & kSmvmInputBaseMask;
    return base > 0 && base <= 0xFF && base == key &&
           RequiredInputModifiersAreActive(binding, active_modifiers);
}

[[nodiscard]] constexpr bool MenuMayClaimKeyDown(
    const bool menu_open,
    const bool delivered_before_menu_open) noexcept {
    return menu_open && !delivered_before_menu_open;
}

static_assert(BindingOwnsKeyboardKey('W', 'W', 2u | 4u));
static_assert(BindingOwnsKeyboardKey('A', 'A', 2u | 4u));
static_assert(BindingOwnsKeyboardKey('S', 'S', 2u | 4u));
static_assert(BindingOwnsKeyboardKey('D', 'D', 2u | 4u));
static_assert(BindingOwnsKeyboardKey((1u << 16) | 'W', 'W', 1u | 4u));
static_assert(!BindingOwnsKeyboardKey((1u << 16) | 'W', 'W', 4u));
static_assert(!BindingOwnsKeyboardKey('W', 'A', 2u | 4u));
static_assert(MenuMayClaimKeyDown(true, false));
static_assert(!MenuMayClaimKeyDown(true, true));
static_assert(!MenuMayClaimKeyDown(false, false));

using PresentFunction = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using ResizeBuffersFunction = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using CreateSwapchainFunction = HRESULT(STDMETHODCALLTYPE*)(
    IDXGIFactory*,
    IUnknown*,
    DXGI_SWAP_CHAIN_DESC*,
    IDXGISwapChain**);
using SdlWindow = void;
using SdlPropertiesId = std::uint32_t;
using SdlGetWindowsFunction = SdlWindow**(__cdecl*)(int*);
using SdlGetWindowPropertiesFunction = SdlPropertiesId(__cdecl*)(SdlWindow*);
using SdlGetPointerPropertyFunction = void*(__cdecl*)(SdlPropertiesId, const char*, void*);
using SdlFreeFunction = void(__cdecl*)(void*);
using SdlGetWindowRelativeMouseModeFunction = bool(__cdecl*)(SdlWindow*);
using SdlSetWindowRelativeMouseModeFunction = bool(__cdecl*)(SdlWindow*, bool);
using CreateInterfaceFunction = void*(__cdecl*)(const char*, int*);
using InputSystemEnableFunction = void(__fastcall*)(void*, bool);

template <typename T>
void SafeRelease(T*& value) noexcept {
    if (value != nullptr) {
        value->Release();
        value = nullptr;
    }
}

struct Vertex final {
    float x{};
    float y{};
    float u{};
    float v{};
    std::uint32_t color{};
};

struct SavedD3D11State final {
    std::array<ID3D11RenderTargetView*, D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT> render_targets{};
    ID3D11DepthStencilView* depth_stencil_view{};
    ID3D11BlendState* blend_state{};
    FLOAT blend_factor[4]{};
    UINT sample_mask{};
    ID3D11DepthStencilState* depth_stencil_state{};
    UINT stencil_reference{};
    ID3D11RasterizerState* rasterizer_state{};
    ID3D11InputLayout* input_layout{};
    ID3D11Buffer* vertex_buffer{};
    UINT vertex_stride{};
    UINT vertex_offset{};
    D3D11_PRIMITIVE_TOPOLOGY topology{};
    ID3D11VertexShader* vertex_shader{};
    ID3D11PixelShader* pixel_shader{};
    ID3D11Buffer* vertex_constant_buffer{};
    ID3D11ShaderResourceView* pixel_resource{};
    ID3D11SamplerState* pixel_sampler{};
    UINT viewport_count{D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE};
    std::array<D3D11_VIEWPORT, D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE> viewports{};
};

struct OverlayState final {
    HMODULE self{};
    SmvmOverlayCallbacks callbacks{};
    HANDLE installer_thread{};
    std::atomic<bool> started{false};
    std::atomic<bool> stop_requested{false};
    std::atomic<bool> hooks_installed{false};
    std::atomic<bool> present_observed{false};
    std::atomic<bool> ready{false};
    std::atomic<bool> menu_open{false};
    std::atomic<bool> replay_tick_input_active{false};
    std::atomic<bool> cinematic_start_armed{false};
    std::atomic<bool> cinematic_start_ready{false};
    std::atomic<bool> cinematic_space_released{false};
    std::atomic<bool> cinematic_space_consumed{false};
    std::atomic<std::int64_t> cinematic_start_tick{-1};
    std::atomic<std::uint64_t> cinematic_replay_session_generation{0};
    std::atomic<bool> clean_view{false};
    std::atomic<std::uint32_t> presentation_mode{
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui)};
    std::atomic<std::uint32_t> previous_visible_mode{
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui)};
    std::atomic<std::uint32_t> last_vconsole_port{29000};
    std::atomic<bool> recording_profile_may_be_active{false};
    std::atomic<std::uint32_t> recording_profile_restore_debt{0};
    std::atomic<bool> recording_profile_native_restore_satisfied{false};
    std::atomic<bool> recording_profile_replay_observed{false};
    std::atomic<std::uint64_t> recording_profile_restore_request_epoch{0};
    std::atomic<std::uint64_t> recording_profile_recovery_counter{0};
    std::atomic<std::uint64_t> recording_profile_recovery_generation{0};
    std::atomic<std::uint32_t> recording_profile_recovery_kind{
        static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none)};
    std::atomic<std::uint32_t> recording_profile_recovery_target_mode{
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui)};
    std::atomic<bool> recording_profile_recovery_action_queued{false};
    std::atomic<std::uint64_t> recording_profile_recovery_action_last_attempt_ms{0};
    std::atomic<std::uint32_t> recording_profile_last_managed_mode{
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui)};
    std::atomic<bool> recording_profile_snapshot_observed{false};
    std::atomic<bool> recording_profile_disconnect_restore_guard{false};
    std::atomic<bool> recording_profile_lease_lost{false};
    std::atomic<std::uint64_t> recording_profile_last_ack_generation{0};
    std::atomic<bool> emergency_restore_attempted{false};
    std::atomic<std::uint64_t> emergency_restore_last_attempt_ms{0};
    std::atomic<std::uint32_t> active_hooks{0};
    std::atomic<std::uint32_t> active_window_procedures{0};
    std::atomic<bool> factory_hook_reachable{false};
    std::atomic<bool> present_hook_reachable{false};
    std::atomic<bool> resize_hook_reachable{false};
    std::atomic<std::int32_t> mouse_x{0};
    std::atomic<std::int32_t> mouse_y{0};
    std::atomic<std::int64_t> manual_look_right_delta{0};
    std::atomic<std::int64_t> manual_look_up_delta{0};
    std::atomic<std::int32_t> manual_wheel_delta{0};
    std::atomic<bool> manual_pointer_requested{false};
    std::atomic<bool> manual_pointer_active{false};
    std::atomic<std::uint64_t> manual_pointer_next_health_check_ms{0};
    std::atomic<bool> manual_mouse_observed{false};
    std::atomic<std::uint64_t> manual_raw_mouse_observed_ms{0};
    std::atomic<std::uint64_t> manual_fallback_mouse_observed_ms{0};
    std::atomic<bool> manual_fallback_mouse_observed{false};
    std::atomic<bool> manual_legacy_mouse_seeded{false};
    std::atomic<bool> manual_discard_next_legacy_sample{true};
    std::atomic<std::uint64_t> manual_fallback_settle_until_ms{0};
    std::atomic<std::int32_t> manual_legacy_mouse_x{0};
    std::atomic<std::int32_t> manual_legacy_mouse_y{0};
    std::atomic<bool> manual_keyboard_ready{false};
    std::atomic<bool> manual_relative_mouse_ready{false};
    std::atomic<bool> manual_raw_input_ready{false};
    std::atomic<bool> manual_cursor_ready{false};
    std::atomic<bool> manual_foreground_ready{false};
    std::atomic<bool> manual_window_procedure_ready{false};
    std::atomic<bool> manual_engine_input_ready{false};
    std::atomic<bool> manual_input_error{false};
    std::atomic<std::uint32_t> manual_input_failure{0};
    std::atomic<std::uint32_t> manual_raw_registration_disposition{0};
    std::array<std::atomic<std::uint8_t>, 256> key_down{};
    std::array<std::atomic<std::uint8_t>, 256> manual_key_routes{};
    std::array<std::atomic<bool>, 256> manual_poll_armed{};
    std::array<std::atomic<bool>, 256> manual_shortcut_queued{};
    std::array<std::atomic<std::uint8_t>, 256> menu_key_routes{};
    std::array<std::atomic<std::uint8_t>, 256> menu_preheld_keys{};
    std::array<SmvmInputRoute, 5> menu_mouse_routes{};
    std::atomic<std::uint64_t> last_wheel_action_ms{0};
    std::atomic<std::uint64_t> raw_wheel_consumed_ms{0};
    std::atomic<std::uint32_t> frame_microseconds{0};
    std::atomic<std::uint32_t> last_renderer_error{0};
    std::atomic<std::int32_t> binding_capture_action{-1};
    std::atomic<std::uint32_t> binding_capture_original{0};
    std::atomic<std::int32_t> binding_pending_action{-1};
    std::atomic<std::uint32_t> binding_pending_value{0};
    std::atomic<std::uint32_t> binding_pending_original{0};
    std::atomic<std::uint64_t> binding_pending_since_ms{0};
    std::atomic<std::int32_t> binding_feedback_action{-1};
    std::atomic<std::uint32_t> binding_feedback_kind{0};
    std::atomic<std::uint64_t> binding_feedback_until_ms{0};
    std::atomic<std::int32_t> binding_conflict_action{-1};
    std::atomic<std::uint32_t> binding_modifier_chord_used{0};
    std::atomic_flag render_lock = ATOMIC_FLAG_INIT;
    SRWLOCK hook_lifecycle_lock = SRWLOCK_INIT;
    SRWLOCK recording_profile_recovery_lock = SRWLOCK_INIT;

    IDXGIFactory* target_factory{};
    void** factory_original_vtable{};
    void** factory_hook_vtable{};
    CreateSwapchainFunction original_create_swapchain{};
    IDXGISwapChain* hooked_swapchain{};
    void** swapchain_original_vtable{};
    void** swapchain_hook_vtable{};
    PresentFunction original_present{};
    ResizeBuffersFunction original_resize{};
    IDXGISwapChain* target_swapchain{};
    HWND output_window{};
    WNDPROC original_window_proc{};
    RECT previous_clip{};
    bool previous_clip_valid{};
    bool cursor_shown{};
    int cursor_show_adjustments{};
    bool sdl_relative_mouse_restore_pending{};
    bool manual_relative_mouse_restore_pending{};
    void* input_system{};
    InputSystemEnableFunction input_system_enable{};
    std::uint8_t input_enabled_state_offset{};
    bool input_was_enabled{};
    std::atomic<bool> input_restore_pending{false};
    std::array<RAWINPUTDEVICE, kMaxSuspendedRawMouseRegistrations>
        suspended_raw_mouse_registrations{};
    UINT suspended_raw_mouse_registration_count{};
    std::atomic<bool> raw_mouse_restore_pending{false};
    std::array<RAWINPUTDEVICE, kMaxSuspendedRawMouseRegistrations>
        manual_raw_mouse_registrations{};
    UINT manual_raw_mouse_registration_count{};
    std::atomic<bool> manual_raw_mouse_restore_pending{false};

    ID3D11Device* device{};
    ID3D11DeviceContext* context{};
    ID3D11RenderTargetView* render_target{};
    ID3D11Buffer* vertex_buffer{};
    ID3D11Buffer* constant_buffer{};
    ID3D11VertexShader* vertex_shader{};
    ID3D11PixelShader* pixel_shader{};
    ID3D11InputLayout* input_layout{};
    ID3D11ShaderResourceView* atlas_view{};
    ID3D11SamplerState* sampler{};
    ID3D11BlendState* blend_state{};
    ID3D11RasterizerState* rasterizer_state{};
    ID3D11DepthStencilState* depth_state{};
    float viewport_width{};
    float viewport_height{};
    std::array<Vertex, kMaxVertices> vertices{};
    std::size_t vertex_count{};

    // Dear ImGui overlay UI (context owned by the render thread).
    ImGuiContext* imgui{};
    std::atomic<bool> ui_ready{false};
    smvm_theme::Fonts fonts{};
    float font_scale{};
    SmvmUiState ui{};
    std::array<SmvmUiInputEvent, kUiInputEventCapacity> ui_events{};
    std::atomic<std::uint32_t> ui_events_write{0};
    std::atomic<std::uint32_t> ui_events_read{0};
    std::array<bool, 5> imgui_button_state{};
    bool imgui_mouse_outside{true};
    std::chrono::steady_clock::time_point last_frame_time{};
    std::array<WorldLabel, kMaxCampathKeyframes> world_labels{};
    std::size_t world_label_count{};
};

OverlayState g_overlay{};

class ActiveCallbackGuard final {
public:
    explicit ActiveCallbackGuard(std::atomic<std::uint32_t>& counter) noexcept
        : counter_(counter) {
        counter_.fetch_add(1, std::memory_order_acq_rel);
    }

    ~ActiveCallbackGuard() noexcept {
        counter_.fetch_sub(1, std::memory_order_acq_rel);
    }

    ActiveCallbackGuard(const ActiveCallbackGuard&) = delete;
    ActiveCallbackGuard& operator=(const ActiveCallbackGuard&) = delete;

private:
    std::atomic<std::uint32_t>& counter_;
};

constexpr std::uint32_t Color(
    const std::uint8_t red,
    const std::uint8_t green,
    const std::uint8_t blue,
    const std::uint8_t alpha = 255) noexcept {
    return static_cast<std::uint32_t>(red) |
           (static_cast<std::uint32_t>(green) << 8) |
           (static_cast<std::uint32_t>(blue) << 16) |
           (static_cast<std::uint32_t>(alpha) << 24);
}

// World visualization colors, re-themed to the v2 palette (spec section 11):
// brass accent for the selected node, muted for everything else.
constexpr auto kPathLine = Color(0xD2, 0xA4, 0x53, 150);
constexpr auto kSelectedMarker = Color(0xD2, 0xA4, 0x53, 255);
constexpr auto kMarker = Color(0x9A, 0x93, 0x8A, 200);

[[nodiscard]] std::uint32_t ManualInputOverlayFlags() noexcept {
    const auto& state = g_overlay;
    std::uint32_t flags = 0;
    if (state.manual_pointer_requested.load(std::memory_order_acquire))
        flags |= smvm_overlay_manual_pointer_requested;
    if (state.manual_keyboard_ready.load(std::memory_order_acquire))
        flags |= smvm_overlay_keyboard_ready;
    if (state.manual_relative_mouse_ready.load(std::memory_order_acquire))
        flags |= smvm_overlay_relative_mouse_ready;
    if (state.manual_raw_input_ready.load(std::memory_order_acquire))
        flags |= smvm_overlay_raw_input_ready;
    if (state.manual_cursor_ready.load(std::memory_order_acquire))
        flags |= smvm_overlay_cursor_ready;
    if (state.manual_foreground_ready.load(std::memory_order_acquire))
        flags |= smvm_overlay_foreground_ready;
    if (state.manual_window_procedure_ready.load(std::memory_order_acquire))
        flags |= smvm_overlay_window_procedure_ready;
    if (state.manual_engine_input_ready.load(std::memory_order_acquire))
        flags |= smvm_overlay_engine_input_ready;
    if (state.manual_fallback_mouse_observed.load(std::memory_order_acquire))
        flags |= smvm_overlay_fallback_mouse_observed;
    return flags;
}

void PublishStatus(
    const SmvmRendererBackend backend,
    const SmvmRendererError error,
    const std::uint32_t frame_microseconds = 0) noexcept {
    auto& state = g_overlay;
    std::uint32_t flags = 0;
    if (state.hooks_installed.load(std::memory_order_acquire)) flags |= smvm_overlay_hook_installed;
    if (state.present_observed.load(std::memory_order_acquire)) flags |= smvm_overlay_present_observed;
    if (state.ready.load(std::memory_order_acquire)) flags |= smvm_overlay_ready;
    if (state.menu_open.load(std::memory_order_acquire)) flags |= smvm_overlay_menu_open;
    if (state.clean_view.load(std::memory_order_acquire)) flags |= smvm_overlay_clean_view;
    if (state.manual_pointer_active.load(std::memory_order_acquire))
        flags |= smvm_overlay_manual_pointer_active;
    if (state.manual_mouse_observed.load(std::memory_order_acquire))
        flags |= smvm_overlay_manual_mouse_observed;
    flags |= ManualInputOverlayFlags();
    if (frame_microseconds > 0)
        state.frame_microseconds.store(frame_microseconds, std::memory_order_release);
    state.last_renderer_error.store(static_cast<std::uint32_t>(error), std::memory_order_release);
    if (state.callbacks.publish_status != nullptr)
        state.callbacks.publish_status(state.callbacks.context, backend, error, flags, frame_microseconds);
}

void ObserveRecordingVisualSnapshotState(const SmvmSnapshotPayload& snapshot) noexcept {
    auto& state = g_overlay;
    const auto active_replay =
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
        (snapshot.flags & smvm_snapshot_replay_active) != 0;
    const auto managed_restore_pending =
        (snapshot.flags & smvm_snapshot_recording_profile_restore_pending) != 0;
    const auto managed_transaction_in_progress =
        (snapshot.flags & smvm_snapshot_recording_profile_transaction_in_progress) != 0;
    const auto deadlock_mode = snapshot.deadlock_ui_mode == DeadlockUiMode::deadlock_ui;
    const auto restore_debt =
        state.recording_profile_restore_debt.load(std::memory_order_acquire);
    const auto hard_restore_pending =
        (restore_debt & kRecordingProfileHardRestoreDebt) != 0;
    state.recording_profile_replay_observed.store(active_replay, std::memory_order_release);
    state.last_vconsole_port.store(snapshot.vconsole_port, std::memory_order_release);
    state.recording_profile_last_managed_mode.store(
        static_cast<std::uint32_t>(snapshot.deadlock_ui_mode), std::memory_order_release);
    state.recording_profile_last_ack_generation.store(
        snapshot.recording_profile_ack_generation, std::memory_order_release);
    state.recording_profile_snapshot_observed.store(true, std::memory_order_release);

    if (ManagedSnapshotMayHaveRecordingVisualProfile(
            active_replay,
            deadlock_mode,
            managed_restore_pending,
            managed_transaction_in_progress)) {
        state.recording_profile_may_be_active.store(true, std::memory_order_release);
    }

    if (!managed_transaction_in_progress && deadlock_mode && !managed_restore_pending) {
        // Managed mode changes to this stable state only after the complete
        // inverse batch succeeds. It clears managed/soft recovery. An explicit
        // F9 generation is retired separately only by its matching snapshot ack.
        state.recording_profile_may_be_active.store(false, std::memory_order_release);
        state.recording_profile_restore_debt.fetch_and(
            ~kRecordingProfileSoftRestoreDebt, std::memory_order_acq_rel);
    } else if (managed_transaction_in_progress || !deadlock_mode) {
        state.recording_profile_native_restore_satisfied.store(false, std::memory_order_release);
        if (ShouldClearObservedRecordingVisualRestoreDebt(
                hard_restore_pending,
                managed_restore_pending,
                managed_transaction_in_progress,
                deadlock_mode)) {
            // A healthy managed transaction or stable active SMVM profile owns
            // soft recovery. Hard F9/host-loss debt is never cleared here.
            state.recording_profile_restore_debt.fetch_and(
                ~kRecordingProfileSoftRestoreDebt, std::memory_order_acq_rel);
        }
    }

    if (ShouldRequestManagedRecordingVisualRestore(
            managed_restore_pending,
            managed_transaction_in_progress,
            deadlock_mode,
            state.recording_profile_native_restore_satisfied.load(std::memory_order_acquire))) {
        state.recording_profile_may_be_active.store(true, std::memory_order_release);
        state.recording_profile_restore_debt.fetch_or(
            kRecordingProfileSoftRestoreDebt, std::memory_order_acq_rel);
    }
}

[[nodiscard]] bool ReadSnapshot(SmvmSnapshotPayload& snapshot) noexcept {
    const auto& callbacks = g_overlay.callbacks;
    const auto read = callbacks.read_snapshot != nullptr &&
        callbacks.read_snapshot(callbacks.context, snapshot);
    if (read) {
        const auto recovery_kind = static_cast<RecordingProfileRecoveryKind>(
            g_overlay.recording_profile_recovery_kind.load(std::memory_order_acquire));
        const auto owner_transition_pending =
            recovery_kind == RecordingProfileRecoveryKind::owner_transition;
        const auto explicit_restore_pending =
            recovery_kind == RecordingProfileRecoveryKind::explicit_restore;
        const auto connection_recovery_pending =
            recovery_kind == RecordingProfileRecoveryKind::reconnect_reassert ||
            recovery_kind == RecordingProfileRecoveryKind::replay_end_restore;
        const auto hard_restore_pending =
            (g_overlay.recording_profile_restore_debt.load(std::memory_order_acquire) &
             kRecordingProfileHardRestoreDebt) != 0;
        const auto owner_target = static_cast<DeadlockUiMode>(
            g_overlay.recording_profile_recovery_target_mode.load(std::memory_order_acquire));
        const auto current_mode_value = ResolveLocalRecordingPresentationMode(
            snapshot.deadlock_ui_mode,
            explicit_restore_pending,
            hard_restore_pending,
            connection_recovery_pending,
            owner_transition_pending,
            owner_target,
            g_overlay.recording_profile_recovery_action_queued.load(std::memory_order_acquire),
            (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
                (snapshot.flags & smvm_snapshot_replay_active) != 0,
            snapshot.deadlock_ui_error != DeadlockUiError::none);
        const auto current_mode = static_cast<std::uint32_t>(current_mode_value);
        const auto previous_mode = g_overlay.presentation_mode.exchange(
            current_mode, std::memory_order_acq_rel);
        if (current_mode_value == DeadlockUiMode::clean_footage) {
            if (previous_mode == static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui) ||
                previous_mode == static_cast<std::uint32_t>(DeadlockUiMode::smvm_replay_ui)) {
                g_overlay.previous_visible_mode.store(previous_mode, std::memory_order_release);
            }
            g_overlay.clean_view.store(true, std::memory_order_release);
        } else if (current_mode_value == DeadlockUiMode::deadlock_ui ||
                   current_mode_value == DeadlockUiMode::smvm_replay_ui) {
            g_overlay.previous_visible_mode.store(current_mode, std::memory_order_release);
            g_overlay.clean_view.store(false, std::memory_order_release);
        } else {
            g_overlay.presentation_mode.store(
                static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui),
                std::memory_order_release);
            g_overlay.previous_visible_mode.store(
                static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui),
                std::memory_order_release);
            g_overlay.clean_view.store(false, std::memory_order_release);
        }
        // Every render/input consumer must see the locally resolved mode. A
        // stale managed SMVM snapshot must not redraw the timeline after F9,
        // host loss, queue failure, or another hard inverse has already made
        // Deadlock UI the authoritative local presentation.
        snapshot.deadlock_ui_mode = static_cast<DeadlockUiMode>(
            g_overlay.presentation_mode.load(std::memory_order_acquire));
    }
    return read;
}

[[nodiscard]] bool ReadPath(
    CampathPayloadHeader& header,
    CampathKeyframe* keyframes,
    const std::size_t capacity) noexcept {
    const auto& callbacks = g_overlay.callbacks;
    return callbacks.read_editor_path != nullptr &&
           callbacks.read_editor_path(callbacks.context, header, keyframes, capacity);
}

[[nodiscard]] bool QueueAction(
    const SmvmActionType type,
    const std::int32_t index = -1,
    const std::int64_t tick = -1,
    const double value = 0.0,
    const CameraSample camera = {},
    const std::string_view text = {}) noexcept {
    const auto& callbacks = g_overlay.callbacks;
    if (callbacks.queue_action == nullptr)
        return false;
    SmvmSnapshotPayload snapshot{};
    if (!ReadSnapshot(snapshot))
        return false;
    SmvmActionPayload action{type, index, tick, value, camera, {}};
    action.replay_session_generation = snapshot.replay_session_generation;
    const auto text_length = std::min(text.size(), action.text.size() - 1);
    if (text_length > 0)
        std::memcpy(action.text.data(), text.data(), text_length);
    return callbacks.queue_action(callbacks.context, action);
}

[[nodiscard]] bool QueueActionForSnapshot(
    const SmvmSnapshotPayload& snapshot,
    const SmvmActionType type,
    const std::int32_t index = -1,
    const std::int64_t tick = -1,
    const double value = 0.0,
    const CameraSample camera = {},
    const std::string_view text = {}) noexcept {
    const auto& callbacks = g_overlay.callbacks;
    if (callbacks.queue_action == nullptr)
        return false;
    SmvmActionPayload action{type, index, tick, value, camera, {}};
    action.replay_session_generation = snapshot.replay_session_generation;
    const auto text_length = std::min(text.size(), action.text.size() - 1);
    if (text_length > 0)
        std::memcpy(action.text.data(), text.data(), text_length);
    return callbacks.queue_action(callbacks.context, action);
}

void SetMenuOpen(bool open) noexcept;
[[nodiscard]] bool CanUseManualCamera(const SmvmSnapshotPayload& snapshot) noexcept;
[[nodiscard]] bool CanConsumeManualCameraInput(const SmvmSnapshotPayload& snapshot) noexcept;
[[nodiscard]] bool CanConsumeManualCameraKeyboardInput(
    const SmvmSnapshotPayload& snapshot) noexcept;
[[nodiscard]] bool CanConsumeManualCameraMouseInput(
    const SmvmSnapshotPayload& snapshot) noexcept;
void RequestEmergencyDeadlockUiRestore(bool hard_restore) noexcept;
void ArmEmergencyDeadlockUiRestore(bool hard_restore) noexcept;
void PumpEmergencyDeadlockUiRestore() noexcept;
[[nodiscard]] std::uint64_t BeginRecordingProfileRecovery(
    RecordingProfileRecoveryKind kind,
    DeadlockUiMode target_mode) noexcept;
void MarkRecordingProfileRecoveryActionQueued(std::uint64_t generation) noexcept;
[[nodiscard]] bool RequestOwnerPresentationMode(
    DeadlockUiMode target_mode,
    const SmvmSnapshotPayload& snapshot) noexcept;
void ReconcileRecordingProfileRecovery(const SmvmSnapshotPayload& snapshot) noexcept;

[[nodiscard]] DeadlockUiMode PreviousVisibleMode() noexcept {
    const auto raw = g_overlay.previous_visible_mode.load(std::memory_order_acquire);
    return raw == static_cast<std::uint32_t>(DeadlockUiMode::smvm_replay_ui)
        ? DeadlockUiMode::smvm_replay_ui
        : DeadlockUiMode::deadlock_ui;
}

void SetLocalPresentationMode(const DeadlockUiMode mode) noexcept {
    auto& state = g_overlay;
    if (mode != DeadlockUiMode::deadlock_ui) {
        state.recording_profile_may_be_active.store(true, std::memory_order_release);
        state.recording_profile_native_restore_satisfied.store(false, std::memory_order_release);
    }
    state.presentation_mode.store(static_cast<std::uint32_t>(mode), std::memory_order_release);
    if (mode != DeadlockUiMode::smvm_replay_ui &&
        state.replay_tick_input_active.exchange(false, std::memory_order_acq_rel)) {
        ResetSmvmManualInput();
    }
    if (mode == DeadlockUiMode::deadlock_ui || mode == DeadlockUiMode::smvm_replay_ui) {
        state.previous_visible_mode.store(static_cast<std::uint32_t>(mode), std::memory_order_release);
        state.clean_view.store(false, std::memory_order_release);
    } else {
        state.clean_view.store(mode == DeadlockUiMode::clean_footage, std::memory_order_release);
    }
}

[[nodiscard]] bool ToggleCleanFootage(
    const bool restore_and_open_menu,
    const SmvmSnapshotPayload& snapshot) noexcept {
    auto& state = g_overlay;
    const auto clean = state.clean_view.load(std::memory_order_acquire);
    if (!clean) {
        const auto current = state.presentation_mode.load(std::memory_order_acquire);
        if (current == static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui) ||
            current == static_cast<std::uint32_t>(DeadlockUiMode::smvm_replay_ui)) {
            state.previous_visible_mode.store(current, std::memory_order_release);
        }
        SetMenuOpen(false);
    }
    const auto target = clean ? PreviousVisibleMode() : DeadlockUiMode::clean_footage;
    if (!RequestOwnerPresentationMode(target, snapshot)) {
        return false;
    }
    if (clean && restore_and_open_menu)
        SetMenuOpen(true);
    PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::none);
    return true;
}

[[nodiscard]] bool SendEmergencyVConsoleCommands(
    const std::uint32_t port,
    const std::span<const std::string_view> commands) noexcept {
    if (port == 0 || port > 65535 || commands.empty())
        return false;
    for (const auto command : commands) {
        if (command.empty() || command.size() > 1024)
            return false;
    }
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock) != 0)
        return false;
    const auto socket_handle = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socket_handle == INVALID_SOCKET) {
        WSACleanup();
        return false;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(static_cast<u_short>(port));
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    u_long nonblocking = 1;
    static_cast<void>(ioctlsocket(socket_handle, FIONBIO, &nonblocking));
    auto connected = connect(
        socket_handle, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0;
    if (!connected && WSAGetLastError() == WSAEWOULDBLOCK) {
        fd_set write_set{};
        FD_ZERO(&write_set);
        FD_SET(socket_handle, &write_set);
        timeval timeout{0, 150000};
        connected = select(0, nullptr, &write_set, nullptr, &timeout) > 0;
        if (connected) {
            int socket_error = 0;
            int error_size = sizeof(socket_error);
            connected = getsockopt(
                socket_handle, SOL_SOCKET, SO_ERROR,
                reinterpret_cast<char*>(&socket_error), &error_size) == 0 && socket_error == 0;
        }
    }
    nonblocking = 0;
    static_cast<void>(ioctlsocket(socket_handle, FIONBIO, &nonblocking));

    auto sent_all = false;
    if (connected) {
        const auto send_command = [&](const std::string_view text) noexcept {
            if (text.empty() || text.size() > 1024)
                return false;
            std::array<char, 1037> message{};
            const auto total = static_cast<std::uint16_t>(12 + text.size() + 1);
            std::memcpy(message.data(), "CMND", 4);
            message[4] = 0x00;
            message[5] = static_cast<char>(0xD4);
            message[8] = static_cast<char>((total >> 8) & 0xFF);
            message[9] = static_cast<char>(total & 0xFF);
            std::memcpy(message.data() + 12, text.data(), text.size());
            auto sent = 0;
            while (sent < total) {
                const auto result = send(socket_handle, message.data() + sent, total - sent, 0);
                if (result <= 0)
                    return false;
                sent += result;
            }
            return true;
        };
        const auto wait_for_marker = [&](const std::string_view marker) noexcept {
            auto matched = std::size_t{0};
            const auto deadline = GetTickCount64() + 2000;
            std::array<char, 8192> buffer{};
            while (GetTickCount64() < deadline) {
                fd_set read_set{};
                FD_ZERO(&read_set);
                FD_SET(socket_handle, &read_set);
                const auto remaining = deadline - GetTickCount64();
                timeval timeout{
                    static_cast<long>(remaining / 1000),
                    static_cast<long>((remaining % 1000) * 1000)};
                if (select(0, &read_set, nullptr, nullptr, &timeout) <= 0)
                    return false;
                const auto received = recv(
                    socket_handle, buffer.data(), static_cast<int>(buffer.size()), 0);
                if (received <= 0)
                    return false;
                for (auto index = 0; index < received; ++index) {
                    const auto byte = buffer[static_cast<std::size_t>(index)];
                    if (byte == marker[matched]) {
                        if (++matched == marker.size())
                            return true;
                    } else {
                        matched = byte == marker.front() ? 1u : 0u;
                    }
                }
            }
            return false;
        };

        std::array<char, 72> sync_marker{};
        std::array<char, 72> done_marker{};
        std::array<char, 80> sync_command{};
        std::array<char, 80> done_command{};
        const auto token = GetTickCount64();
        static_cast<void>(std::snprintf(
            sync_marker.data(), sync_marker.size(), "SMVM_RESTORE_SYNC_%lu_%llu",
            GetCurrentProcessId(), static_cast<unsigned long long>(token)));
        static_cast<void>(std::snprintf(
            done_marker.data(), done_marker.size(), "SMVM_RESTORE_DONE_%lu_%llu",
            GetCurrentProcessId(), static_cast<unsigned long long>(token)));
        static_cast<void>(std::snprintf(
            sync_command.data(), sync_command.size(), "echo %s", sync_marker.data()));
        static_cast<void>(std::snprintf(
            done_command.data(), done_command.size(), "echo %s", done_marker.data()));

        const auto synchronized = send_command(sync_command.data()) &&
            wait_for_marker(sync_marker.data());
        sent_all = synchronized;
        for (const auto command : commands)
            sent_all = sent_all && send_command(command);
        sent_all = sent_all && send_command(done_command.data()) &&
            wait_for_marker(done_marker.data());
    }
    closesocket(socket_handle);
    WSACleanup();
    return sent_all;
}

[[nodiscard]] std::uint64_t NextRecordingProfileRecoveryGeneration() noexcept {
    auto& state = g_overlay;
    const auto acknowledged = std::min(
        state.recording_profile_last_ack_generation.load(std::memory_order_acquire),
        0x7FFFFFFFFFFFFFFEULL);
    auto counter = state.recording_profile_recovery_counter.load(std::memory_order_acquire);
    while (counter < acknowledged &&
           !state.recording_profile_recovery_counter.compare_exchange_weak(
               counter, acknowledged, std::memory_order_acq_rel, std::memory_order_acquire)) {
    }
    auto generation = state.recording_profile_recovery_counter.fetch_add(
        1, std::memory_order_acq_rel) + 1;
    generation &= 0x7FFFFFFFFFFFFFFFULL;
    if (generation == 0) {
        generation = state.recording_profile_recovery_counter.fetch_add(
            1, std::memory_order_acq_rel) + 1;
        generation &= 0x7FFFFFFFFFFFFFFFULL;
    }
    return generation;
}

[[nodiscard]] std::uint64_t BeginRecordingProfileRecovery(
    const RecordingProfileRecoveryKind kind,
    const DeadlockUiMode target_mode) noexcept {
    auto& state = g_overlay;
    AcquireSRWLockExclusive(&state.recording_profile_recovery_lock);
    const auto current_kind = static_cast<RecordingProfileRecoveryKind>(
        state.recording_profile_recovery_kind.load(std::memory_order_relaxed));
    if ((current_kind == RecordingProfileRecoveryKind::explicit_restore ||
         current_kind == RecordingProfileRecoveryKind::owner_transition ||
         current_kind == RecordingProfileRecoveryKind::replay_end_restore) &&
        kind == RecordingProfileRecoveryKind::reconnect_reassert) {
        const auto existing = state.recording_profile_recovery_generation.load(
            std::memory_order_relaxed);
        state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
        state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
        ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
        return existing;
    }

    const auto generation = state.recording_profile_snapshot_observed.load(
        std::memory_order_acquire)
        ? NextRecordingProfileRecoveryGeneration()
        : 0;
    state.recording_profile_recovery_target_mode.store(
        static_cast<std::uint32_t>(target_mode), std::memory_order_relaxed);
    state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
    state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
    state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
    state.recording_profile_recovery_kind.store(
        static_cast<std::uint32_t>(kind), std::memory_order_release);
    ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
    return generation;
}

void MarkRecordingProfileRecoveryActionQueued(const std::uint64_t generation) noexcept {
    auto& state = g_overlay;
    AcquireSRWLockExclusive(&state.recording_profile_recovery_lock);
    if (state.recording_profile_recovery_generation.load(std::memory_order_relaxed) == generation) {
        state.recording_profile_recovery_action_queued.store(true, std::memory_order_relaxed);
        state.recording_profile_recovery_action_last_attempt_ms.store(
            GetTickCount64(), std::memory_order_relaxed);
    }
    ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
}

[[nodiscard]] bool RequestOwnerPresentationMode(
    const DeadlockUiMode target_mode,
    const SmvmSnapshotPayload& snapshot) noexcept {
    auto& state = g_overlay;
    AcquireSRWLockExclusive(&state.recording_profile_recovery_lock);
    const auto kind = static_cast<RecordingProfileRecoveryKind>(
        state.recording_profile_recovery_kind.load(std::memory_order_relaxed));
    if (kind == RecordingProfileRecoveryKind::explicit_restore) {
        ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
        return false;
    }
    const auto generation = NextRecordingProfileRecoveryGeneration();
    state.recording_profile_recovery_target_mode.store(
        static_cast<std::uint32_t>(target_mode), std::memory_order_relaxed);
    state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
    state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
    state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
    state.recording_profile_recovery_kind.store(
        static_cast<std::uint32_t>(RecordingProfileRecoveryKind::owner_transition),
        std::memory_order_release);
    const auto forward_target = target_mode != DeadlockUiMode::deadlock_ui;
    const auto hard_restore_pending =
        (state.recording_profile_restore_debt.load(std::memory_order_acquire) &
         kRecordingProfileHardRestoreDebt) != 0;
    const auto queued = ShouldQueueInitialOwnerPresentationAction(
            forward_target, hard_restore_pending) &&
        QueueActionForSnapshot(
            snapshot,
            SmvmActionType::set_deadlock_ui_mode,
            static_cast<std::int32_t>(target_mode),
            -static_cast<std::int64_t>(generation));
    if (queued) {
        state.recording_profile_recovery_action_queued.store(true, std::memory_order_relaxed);
        state.recording_profile_recovery_action_last_attempt_ms.store(
            GetTickCount64(), std::memory_order_relaxed);
        SetLocalPresentationMode(target_mode);
    } else {
        // The new logical owner intent remains armed and will retry after the
        // queue/pipe recovers. Fail physically to Deadlock in the meantime so
        // an already-delivered older forward batch cannot become the last word.
        SetLocalPresentationMode(DeadlockUiMode::deadlock_ui);
    }
    const auto needs_physical_restore =
        !queued || target_mode == DeadlockUiMode::deadlock_ui;
    if (needs_physical_restore)
        ArmEmergencyDeadlockUiRestore(true);
    ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
    if (needs_physical_restore)
        PumpEmergencyDeadlockUiRestore();
    // A queue-full or inverse-in-progress result is deferred, not dropped: the
    // generation/target above remains the durable owner intent for Reconcile.
    return true;
}

void ArmEmergencyDeadlockUiRestore(const bool hard_restore) noexcept {
    auto& state = g_overlay;
    state.recording_profile_restore_request_epoch.fetch_add(1, std::memory_order_acq_rel);
    state.recording_profile_restore_debt.fetch_or(
        hard_restore
            ? kRecordingProfileAllRestoreDebt
            : kRecordingProfileSoftRestoreDebt,
        std::memory_order_acq_rel);
}

void RequestEmergencyDeadlockUiRestore(const bool hard_restore) noexcept {
    ArmEmergencyDeadlockUiRestore(hard_restore);
    PumpEmergencyDeadlockUiRestore();
}

void PumpEmergencyDeadlockUiRestore() noexcept {
    auto& state = g_overlay;
    const auto now = GetTickCount64();
    const auto previous = state.emergency_restore_last_attempt_ms.load(std::memory_order_acquire);
    if (!ShouldAttemptRecordingVisualRestore(
            state.recording_profile_restore_debt.load(std::memory_order_acquire) != 0,
            state.emergency_restore_attempted.load(std::memory_order_acquire),
            now,
            previous))
        return;
    if (state.emergency_restore_attempted.exchange(true, std::memory_order_acq_rel))
        return;
    const auto attempted_debt =
        state.recording_profile_restore_debt.load(std::memory_order_acquire);
    const auto request_epoch =
        state.recording_profile_restore_request_epoch.load(std::memory_order_acquire);
    if (attempted_debt == 0) {
        state.emergency_restore_attempted.store(false, std::memory_order_release);
        return;
    }
    state.emergency_restore_last_attempt_ms.store(now, std::memory_order_release);
    const auto restored = SendEmergencyVConsoleCommands(
        state.last_vconsole_port.load(std::memory_order_acquire),
        kDeadlockPresentationRestoreCommands);
    if (restored) {
        state.recording_profile_may_be_active.store(false, std::memory_order_release);
        if (CanRetireRecordingVisualRestoreAttempt(
                request_epoch,
                state.recording_profile_restore_request_epoch.load(std::memory_order_acquire),
                state.recording_profile_disconnect_restore_guard.load(std::memory_order_acquire),
                state.stop_requested.load(std::memory_order_acquire))) {
            state.recording_profile_restore_debt.fetch_and(
                ~attempted_debt, std::memory_order_acq_rel);
        }
        state.recording_profile_native_restore_satisfied.store(true, std::memory_order_release);
        state.emergency_restore_attempted.store(false, std::memory_order_release);
        SetLocalPresentationMode(DeadlockUiMode::deadlock_ui);
    } else {
        state.emergency_restore_attempted.store(false, std::memory_order_release);
    }
}

void ReconcileRecordingProfileRecovery(const SmvmSnapshotPayload& snapshot) noexcept {
    auto& state = g_overlay;
    const auto managed_transaction_in_progress =
        (snapshot.flags & smvm_snapshot_recording_profile_transaction_in_progress) != 0;
    const auto managed_restore_pending =
        (snapshot.flags & smvm_snapshot_recording_profile_restore_pending) != 0;
    const auto active_replay =
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
        (snapshot.flags & smvm_snapshot_replay_active) != 0;
    const auto deadlock_mode = snapshot.deadlock_ui_mode == DeadlockUiMode::deadlock_ui;
    const auto now = GetTickCount64();

    SmvmActionType action_type = SmvmActionType::none;
    auto action_index = -1;
    auto action_tick = std::int64_t{-1};
    std::uint64_t generation = 0;
    auto request_physical_restore = false;
    auto physical_restore_already_armed = false;

    AcquireSRWLockExclusive(&state.recording_profile_recovery_lock);
    auto kind = static_cast<RecordingProfileRecoveryKind>(
        state.recording_profile_recovery_kind.load(std::memory_order_relaxed));
    if (kind == RecordingProfileRecoveryKind::none) {
        const auto restore_debt =
            state.recording_profile_restore_debt.load(std::memory_order_acquire);
        const auto terminal_restore_required = !active_replay &&
            ShouldRestoreRecordingVisualProfileOnHostLoss(
                state.recording_profile_may_be_active.load(std::memory_order_acquire),
                restore_debt != 0,
                (restore_debt & kRecordingProfileHardRestoreDebt) != 0,
                false);
        if (!terminal_restore_required) {
            ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
            return;
        }

        generation = NextRecordingProfileRecoveryGeneration();
        state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
        state.recording_profile_recovery_target_mode.store(
            static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui),
            std::memory_order_relaxed);
        state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
        state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
        state.recording_profile_recovery_kind.store(
            static_cast<std::uint32_t>(RecordingProfileRecoveryKind::replay_end_restore),
            std::memory_order_release);
        kind = RecordingProfileRecoveryKind::replay_end_restore;
        SetLocalPresentationMode(DeadlockUiMode::deadlock_ui);
        ArmEmergencyDeadlockUiRestore(true);
        request_physical_restore = true;
        physical_restore_already_armed = true;
    }

    generation = state.recording_profile_recovery_generation.load(std::memory_order_relaxed);
    if (generation == 0) {
        generation = NextRecordingProfileRecoveryGeneration();
        state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
        state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
        state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
    }

    if (kind == RecordingProfileRecoveryKind::explicit_restore) {
        const auto acknowledged = IsExplicitRecordingVisualRecoveryAcknowledged(
            generation,
            snapshot.recording_profile_ack_generation,
            deadlock_mode,
            managed_restore_pending,
            managed_transaction_in_progress);
        if (acknowledged) {
            state.recording_profile_recovery_kind.store(
                static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none),
                std::memory_order_release);
            state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
            state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
            ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
            state.recording_profile_restore_debt.fetch_and(
                ~kRecordingProfileAllRestoreDebt, std::memory_order_acq_rel);
            state.recording_profile_may_be_active.store(false, std::memory_order_release);
            state.recording_profile_native_restore_satisfied.store(true, std::memory_order_release);
            return;
        }

        if (managed_transaction_in_progress || !deadlock_mode) {
            const auto satisfied = state.recording_profile_native_restore_satisfied.exchange(
                false, std::memory_order_acq_rel);
            request_physical_restore = satisfied ||
                state.recording_profile_restore_debt.load(std::memory_order_acquire) == 0;
        }
        action_type = SmvmActionType::restore_deadlock_ui;
        action_tick = static_cast<std::int64_t>(generation);
    } else if (kind == RecordingProfileRecoveryKind::owner_transition) {
        const auto profile_error = snapshot.deadlock_ui_error != DeadlockUiError::none;
        const auto target_mode = static_cast<DeadlockUiMode>(
            state.recording_profile_recovery_target_mode.load(std::memory_order_relaxed));
        const auto target_matches = snapshot.deadlock_ui_mode == target_mode;
        const auto target_is_deadlock = target_mode == DeadlockUiMode::deadlock_ui;
        if (!active_replay && !target_is_deadlock) {
            // Replay end wins over even an acknowledged forward owner target.
            // Convert the canceled forward target into terminal replay cleanup.
            // This is deliberately distinct from F9: it restores physically
            // now, but never delivers an old replay's owner-exit action into a
            // newly started replay.
            generation = NextRecordingProfileRecoveryGeneration();
            state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
            state.recording_profile_recovery_target_mode.store(
                static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui),
                std::memory_order_relaxed);
            state.recording_profile_recovery_kind.store(
                static_cast<std::uint32_t>(RecordingProfileRecoveryKind::replay_end_restore),
                std::memory_order_release);
            state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
            state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
            SetLocalPresentationMode(DeadlockUiMode::deadlock_ui);
            request_physical_restore = true;
        } else {
            if (IsOwnerPresentationTransitionComplete(
                    generation,
                    snapshot.recording_profile_ack_generation,
                    target_matches,
                    target_is_deadlock,
                    managed_restore_pending,
                    managed_transaction_in_progress,
                    profile_error)) {
                state.recording_profile_recovery_kind.store(
                    static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none),
                    std::memory_order_release);
                state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
                state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
                ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
                state.recording_profile_restore_debt.fetch_and(
                    ~kRecordingProfileAllRestoreDebt, std::memory_order_acq_rel);
                state.recording_profile_may_be_active.store(!target_is_deadlock, std::memory_order_release);
                state.recording_profile_native_restore_satisfied.store(
                    target_is_deadlock, std::memory_order_release);
                return;
            }

            if (target_mode == DeadlockUiMode::deadlock_ui &&
                (managed_transaction_in_progress || managed_restore_pending || !deadlock_mode)) {
                const auto satisfied = state.recording_profile_native_restore_satisfied.exchange(
                    false, std::memory_order_acq_rel);
                request_physical_restore = satisfied ||
                    state.recording_profile_restore_debt.load(std::memory_order_acquire) == 0;
            }
            action_type = SmvmActionType::set_deadlock_ui_mode;
            action_index = static_cast<std::int32_t>(target_mode);
            // A negative generation identifies owner intent while its magnitude is
            // acknowledged by managed code. Retrying the same generation both
            // invalidates older forward work and prevents a stale matching snapshot
            // from retiring a rapid A -> B selection before B is actually applied.
            action_tick = -static_cast<std::int64_t>(generation);
        }
    } else if (kind == RecordingProfileRecoveryKind::replay_end_restore) {
        const auto hard_restore_pending =
            (state.recording_profile_restore_debt.load(std::memory_order_acquire) &
             kRecordingProfileHardRestoreDebt) != 0;
        const auto disposition = ResolveReplayEndRecordingVisualDisposition(
            active_replay,
            hard_restore_pending,
            deadlock_mode,
            managed_restore_pending,
            managed_transaction_in_progress);
        if (disposition == ReplayEndRecordingVisualDisposition::retire_deadlock) {
            // Terminal replay cleanup may retire from the two independent
            // proofs: the native inverse debt completed and managed now reports
            // a stable Deadlock profile. F9 remains exact-generation-only.
            state.recording_profile_recovery_kind.store(
                static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none),
                std::memory_order_release);
            state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
            state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
            ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
            state.recording_profile_restore_debt.fetch_and(
                ~kRecordingProfileAllRestoreDebt, std::memory_order_acq_rel);
            state.recording_profile_may_be_active.store(false, std::memory_order_release);
            state.recording_profile_native_restore_satisfied.store(true, std::memory_order_release);
            return;
        }
        if (disposition == ReplayEndRecordingVisualDisposition::reassert_active) {
            // A new replay already established its own SMVM/Clean mode while
            // terminal cleanup was finishing. Reassert that fresh managed mode
            // after the inverse using a non-owner recovery generation.
            generation = NextRecordingProfileRecoveryGeneration();
            state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
            state.recording_profile_recovery_target_mode.store(
                static_cast<std::uint32_t>(snapshot.deadlock_ui_mode),
                std::memory_order_relaxed);
            state.recording_profile_recovery_kind.store(
                static_cast<std::uint32_t>(RecordingProfileRecoveryKind::reconnect_reassert),
                std::memory_order_release);
            state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
            state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
            action_type = SmvmActionType::set_deadlock_ui_mode;
            action_index = static_cast<std::int32_t>(snapshot.deadlock_ui_mode);
            action_tick = static_cast<std::int64_t>(generation);
        }
    } else {
        const auto profile_error = snapshot.deadlock_ui_error != DeadlockUiError::none;
        const auto managed_restore_proven = ManagedSnapshotProvesRecordingVisualRestore(
            deadlock_mode,
            managed_restore_pending,
            managed_transaction_in_progress,
            profile_error);
        if (ShouldCancelRecordingVisualReassert(
                active_replay,
                managed_restore_proven)) {
            // The managed owner deliberately selected Deadlock UI while the pipe
            // was away, or the replay ended. Keep the already-restored profile
            // instead of reviving a stale SMVM/Clean intent. A failed forward
            // apply carries an error and deliberately does not enter this path.
            state.recording_profile_recovery_kind.store(
                static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none),
                std::memory_order_release);
            state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
            state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
            ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
            state.recording_profile_restore_debt.fetch_and(
                managed_restore_proven
                    ? ~kRecordingProfileAllRestoreDebt
                    : ~kRecordingProfileSoftRestoreDebt,
                std::memory_order_acq_rel);
            return;
        }

        if (!deadlock_mode) {
            const auto target_mode = static_cast<std::uint32_t>(snapshot.deadlock_ui_mode);
            const auto previous_target = state.recording_profile_recovery_target_mode.exchange(
                target_mode, std::memory_order_relaxed);
            if (previous_target != target_mode) {
                generation = NextRecordingProfileRecoveryGeneration();
                state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
                state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
                state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
            }
            const auto acknowledged = IsRecordingVisualReassertAcknowledged(
                generation,
                snapshot.recording_profile_ack_generation,
                deadlock_mode,
                managed_transaction_in_progress);
            if (acknowledged) {
                state.recording_profile_recovery_kind.store(
                    static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none),
                    std::memory_order_release);
                state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
                state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
                ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);
                state.recording_profile_restore_debt.fetch_and(
                    ~kRecordingProfileAllRestoreDebt, std::memory_order_acq_rel);
                state.recording_profile_may_be_active.store(true, std::memory_order_release);
                state.recording_profile_native_restore_satisfied.store(false, std::memory_order_release);
                return;
            }
        }
        // When a forward transaction fails, managed rollback publishes stable
        // Deadlock UI plus an error and leaves the acknowledgement unchanged.
        // Retain the original target and retry the same generation.
        action_type = SmvmActionType::set_deadlock_ui_mode;
        action_index = static_cast<std::int32_t>(
            state.recording_profile_recovery_target_mode.load(std::memory_order_relaxed));
        action_tick = static_cast<std::int64_t>(generation);
    }

    const auto last_attempt = state.recording_profile_recovery_action_last_attempt_ms.load(
        std::memory_order_relaxed);
    const auto hard_restore_pending =
        (state.recording_profile_restore_debt.load(std::memory_order_acquire) &
         kRecordingProfileHardRestoreDebt) != 0;
    const auto forward_recovery =
        action_type == SmvmActionType::set_deadlock_ui_mode &&
        action_index != static_cast<std::int32_t>(DeadlockUiMode::deadlock_ui);
    const auto should_queue = action_type != SmvmActionType::none &&
        ShouldQueueRecordingVisualRecoveryAction(
            forward_recovery,
            hard_restore_pending,
            active_replay,
            managed_transaction_in_progress,
            now,
            last_attempt);
    if (should_queue) {
        state.recording_profile_recovery_action_last_attempt_ms.store(now, std::memory_order_relaxed);
        const auto queued = QueueActionForSnapshot(
            snapshot,
            action_type,
            action_index,
            action_tick);
        state.recording_profile_recovery_action_queued.store(queued, std::memory_order_relaxed);
    }
    if (request_physical_restore && !physical_restore_already_armed)
        ArmEmergencyDeadlockUiRestore(true);
    ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);

    if (request_physical_restore)
        PumpEmergencyDeadlockUiRestore();
}

void QueueCaptureDiagnostic(
    const SmvmSnapshotPayload& snapshot,
    const SmvmCaptureStage stage,
    const SmvmCaptureRejection rejection = SmvmCaptureRejection::none) noexcept {
    if ((snapshot.flags & smvm_snapshot_capture_diagnostics) == 0 &&
        stage != SmvmCaptureStage::capture_rejected)
        return;
    static_cast<void>(QueueActionForSnapshot(
        snapshot,
        SmvmActionType::capture_diagnostic,
        static_cast<std::int32_t>(stage),
        static_cast<std::int64_t>(rejection)));
}

[[nodiscard]] SmvmCaptureRejection CaptureRejectionFromSnapshot(
    const SmvmSnapshotPayload& snapshot) noexcept {
    if ((snapshot.flags & smvm_snapshot_internal_enabled) == 0)
        return SmvmCaptureRejection::native_backend_unavailable;
    if ((snapshot.flags & smvm_snapshot_replay_active) == 0)
        return SmvmCaptureRejection::replay_unavailable;
    if (snapshot.observer_mode != 4)
        return SmvmCaptureRejection::not_in_free_roam;
    if (!HasSmvmManualCameraOwnership(
            (snapshot.flags & smvm_snapshot_manual_camera_requested) != 0,
            (snapshot.flags & smvm_snapshot_manual_camera_active) != 0))
        return SmvmCaptureRejection::not_in_free_roam;
    if ((snapshot.flags & smvm_snapshot_camera_readable) == 0)
        return SmvmCaptureRejection::camera_unreadable;
    if ((snapshot.flags & smvm_snapshot_campath_playing) != 0)
        return SmvmCaptureRejection::campath_owns_camera;
    return SmvmCaptureRejection::none;
}

enum class BindingFeedback : std::uint32_t {
    none = 0,
    saved = 1,
    rejected = 2,
    keyboard_only = 3,
};

[[nodiscard]] constexpr bool IsBindingActionIndex(const std::int32_t action) noexcept {
    return action >= kFirstManualBindingAction && action <= kLastBindingAction;
}

[[nodiscard]] std::int32_t FindSnapshotBindingConflict(
    const std::int32_t action,
    const std::uint32_t value) noexcept {
    if (value == 0)
        return -1;
    SmvmSnapshotPayload snapshot{};
    if (!ReadSnapshot(snapshot))
        return -1;
    const std::array<std::uint32_t, 29> values{
        snapshot.forward_key, snapshot.backward_key, snapshot.left_key, snapshot.right_key,
        snapshot.up_key, snapshot.down_key, snapshot.fast_key, snapshot.precision_key,
        snapshot.roll_left_key, snapshot.roll_right_key, snapshot.roll_reset_key,
        snapshot.menu_key, snapshot.add_key, snapshot.delete_key, snapshot.clean_view_key,
        snapshot.play_start_key, snapshot.play_current_key, snapshot.stop_key,
        snapshot.undo_key, snapshot.redo_key, snapshot.show_path_key, snapshot.show_cameras_key,
        snapshot.restore_ui_key,
        snapshot.cycle_ui_key, snapshot.toggle_free_camera_key, snapshot.replay_pause_key,
        snapshot.show_labels_key, snapshot.step_back_key, snapshot.step_forward_key,
    };
    for (std::size_t index = 0; index < values.size(); ++index) {
        const auto candidate = kFirstManualBindingAction + static_cast<std::int32_t>(index);
        if (candidate != action && values[index] == value)
            return candidate;
    }
    return -1;
}

void ResetBindingCaptureState() noexcept {
    auto& state = g_overlay;
    state.binding_capture_action.store(-1, std::memory_order_release);
    state.binding_capture_original.store(0, std::memory_order_release);
    state.binding_pending_action.store(-1, std::memory_order_release);
    state.binding_pending_value.store(0, std::memory_order_release);
    state.binding_pending_original.store(0, std::memory_order_release);
    state.binding_pending_since_ms.store(0, std::memory_order_release);
    state.binding_feedback_action.store(-1, std::memory_order_release);
    state.binding_feedback_kind.store(
        static_cast<std::uint32_t>(BindingFeedback::none), std::memory_order_release);
    state.binding_feedback_until_ms.store(0, std::memory_order_release);
    state.binding_conflict_action.store(-1, std::memory_order_release);
    state.binding_modifier_chord_used.store(0, std::memory_order_release);
}

void BeginBindingCapture(const std::int32_t action, const std::uint32_t original) noexcept {
    if (!IsBindingActionIndex(action))
        return;
    auto& state = g_overlay;
    state.binding_pending_action.store(-1, std::memory_order_release);
    state.binding_feedback_action.store(-1, std::memory_order_release);
    state.binding_feedback_kind.store(
        static_cast<std::uint32_t>(BindingFeedback::none), std::memory_order_release);
    state.binding_feedback_until_ms.store(0, std::memory_order_release);
    state.binding_conflict_action.store(-1, std::memory_order_release);
    state.binding_modifier_chord_used.store(0, std::memory_order_release);
    state.binding_capture_original.store(original, std::memory_order_release);
    state.binding_capture_action.store(action, std::memory_order_release);
}

void CancelBindingCapture() noexcept {
    g_overlay.binding_capture_action.store(-1, std::memory_order_release);
    g_overlay.binding_capture_original.store(0, std::memory_order_release);
}

void RejectBindingCapture(const std::int32_t action) noexcept {
    auto& state = g_overlay;
    CancelBindingCapture();
    state.binding_pending_action.store(-1, std::memory_order_release);
    state.binding_conflict_action.store(-1, std::memory_order_release);
    state.binding_feedback_action.store(action, std::memory_order_release);
    state.binding_feedback_kind.store(
        static_cast<std::uint32_t>(BindingFeedback::keyboard_only), std::memory_order_release);
    state.binding_feedback_until_ms.store(
        GetTickCount64() + kBindingFeedbackDurationMs, std::memory_order_release);
}

[[nodiscard]] bool SubmitBindingValue(
    const std::int32_t action,
    const std::uint32_t value,
    const std::uint32_t original) noexcept {
    if (!IsBindingActionIndex(action))
        return false;
    auto& state = g_overlay;
    CancelBindingCapture();
    state.binding_feedback_action.store(-1, std::memory_order_release);
    state.binding_feedback_kind.store(
        static_cast<std::uint32_t>(BindingFeedback::none), std::memory_order_release);
    state.binding_feedback_until_ms.store(0, std::memory_order_release);
    state.binding_conflict_action.store(
        FindSnapshotBindingConflict(action, value), std::memory_order_release);
    if (!QueueAction(SmvmActionType::set_binding, action, -1, static_cast<double>(value))) {
        state.binding_pending_action.store(-1, std::memory_order_release);
        state.binding_feedback_action.store(action, std::memory_order_release);
        state.binding_feedback_kind.store(
            static_cast<std::uint32_t>(BindingFeedback::rejected), std::memory_order_release);
        state.binding_feedback_until_ms.store(
            GetTickCount64() + kBindingFeedbackDurationMs, std::memory_order_release);
        return false;
    }
    state.binding_pending_value.store(value, std::memory_order_release);
    state.binding_pending_original.store(original, std::memory_order_release);
    state.binding_pending_since_ms.store(GetTickCount64(), std::memory_order_release);
    state.binding_pending_action.store(action, std::memory_order_release);
    return true;
}

[[nodiscard]] constexpr bool IsModifierKey(const std::uint32_t key) noexcept {
    return key == VK_CONTROL || key == VK_LCONTROL || key == VK_RCONTROL ||
           key == VK_MENU || key == VK_LMENU || key == VK_RMENU ||
           key == VK_SHIFT || key == VK_LSHIFT || key == VK_RSHIFT ||
           key == VK_LWIN || key == VK_RWIN;
}

[[nodiscard]] constexpr std::uint32_t EncodeBinding(
    const std::uint32_t base,
    const std::uint32_t modifiers) noexcept {
    return (base & kSmvmInputBaseMask) | ((modifiers & 0xFu) << 16);
}

static_assert(EncodeBinding('W', 1u | 4u) == ((5u << 16) | 'W'));
static_assert(EncodeBinding(static_cast<std::uint32_t>(SmvmInputCode::mouse_middle), 2u) ==
              ((2u << 16) | static_cast<std::uint32_t>(SmvmInputCode::mouse_middle)));

void AddTriangle(
    const float x0, const float y0,
    const float x1, const float y1,
    const float x2, const float y2,
    const std::uint32_t color) noexcept {
    auto& state = g_overlay;
    if (state.vertex_count + 3 > state.vertices.size())
        return;
    state.vertices[state.vertex_count++] = Vertex{x0, y0, 0.0F, 0.0F, color};
    state.vertices[state.vertex_count++] = Vertex{x1, y1, 0.0F, 0.0F, color};
    state.vertices[state.vertex_count++] = Vertex{x2, y2, 0.0F, 0.0F, color};
}

void AddRect(const float x, const float y, const float width, const float height, const std::uint32_t color) noexcept {
    if (width <= 0.0F || height <= 0.0F)
        return;
    AddTriangle(x, y, x + width, y, x + width, y + height, color);
    AddTriangle(x, y, x + width, y + height, x, y + height, color);
}

void AddLine(
    const float x0, const float y0, const float x1, const float y1,
    const std::uint32_t color, const float thickness = 1.5F) noexcept {
    const auto dx = x1 - x0;
    const auto dy = y1 - y0;
    const auto length = std::sqrt((dx * dx) + (dy * dy));
    if (length < 0.01F)
        return;
    const auto nx = (-dy / length) * (thickness * 0.5F);
    const auto ny = (dx / length) * (thickness * 0.5F);
    AddTriangle(x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, color);
    AddTriangle(x0 + nx, y0 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny, color);
}

[[nodiscard]] std::uint32_t OverlayFlags() noexcept {
    std::uint32_t flags = 0;
    if (g_overlay.hooks_installed.load(std::memory_order_acquire)) flags |= smvm_overlay_hook_installed;
    if (g_overlay.present_observed.load(std::memory_order_acquire)) flags |= smvm_overlay_present_observed;
    if (g_overlay.ready.load(std::memory_order_acquire)) flags |= smvm_overlay_ready;
    if (g_overlay.menu_open.load(std::memory_order_acquire)) flags |= smvm_overlay_menu_open;
    if (g_overlay.clean_view.load(std::memory_order_acquire)) flags |= smvm_overlay_clean_view;
    if (g_overlay.manual_pointer_active.load(std::memory_order_acquire))
        flags |= smvm_overlay_manual_pointer_active;
    if (g_overlay.manual_mouse_observed.load(std::memory_order_acquire))
        flags |= smvm_overlay_manual_mouse_observed;
    flags |= ManualInputOverlayFlags();
    return flags;
}

struct Vec3 final {
    double x{};
    double y{};
    double z{};
};

struct WorldLine final {
    Vec3 from{};
    Vec3 to{};
};

struct CameraMarkerGeometry final {
    Vec3 origin{};
    std::array<WorldLine, kCameraMarkerLineCount> regular{};
    std::array<WorldLine, kCameraMarkerLineCount> selected{};
};

struct CampathGeometryCache final {
    std::uint64_t key{};
    bool valid{};
    std::uint32_t keyframe_count{};
    std::size_t path_line_count{};
    std::array<WorldLine, kMaxCachedPathLines> path_lines{};
    std::array<CameraMarkerGeometry, kMaxCampathKeyframes> markers{};
};

CampathGeometryCache g_campath_geometry_cache{};

static_assert(kMaxCachedPathLines == 3048);
static_assert(kCameraMarkerLineCount == 9);

struct CameraBasis final {
    Vec3 forward{};
    Vec3 right{};
    Vec3 up{};
};

struct CameraPoint final {
    float right{};
    float up{};
    float depth{};
};

[[nodiscard]] Vec3 Add(const Vec3 a, const Vec3 b) noexcept {
    return {a.x + b.x, a.y + b.y, a.z + b.z};
}

[[nodiscard]] Vec3 Scale(const Vec3 value, const double amount) noexcept {
    return {value.x * amount, value.y * amount, value.z * amount};
}

[[nodiscard]] double Dot(const Vec3 a, const Vec3 b) noexcept {
    return (a.x * b.x) + (a.y * b.y) + (a.z * b.z);
}

[[nodiscard]] CameraBasis BasisFromAngles(const CameraSample& camera) noexcept {
    constexpr auto radians = 3.14159265358979323846 / 180.0;
    const auto pitch = camera.pitch * radians;
    const auto yaw = camera.yaw * radians;
    const auto roll = camera.roll * radians;
    const auto sp = std::sin(pitch);
    const auto cp = std::cos(pitch);
    const auto sy = std::sin(yaw);
    const auto cy = std::cos(yaw);
    const auto sr = std::sin(roll);
    const auto cr = std::cos(roll);
    return {
        {cp * cy, cp * sy, -sp},
        {(-sr * sp * cy) + (cr * sy), (-sr * sp * sy) - (cr * cy), -sr * cp},
        {(cr * sp * cy) + (sr * sy), (cr * sp * sy) - (sr * cy), cr * cp},
    };
}

[[nodiscard]] CameraPoint ToCameraSpace(
    const Vec3 world,
    const CameraSample& view,
    const CameraBasis& basis) noexcept {
    const Vec3 delta{world.x - view.x, world.y - view.y, world.z - view.z};
    return {
        static_cast<float>(Dot(delta, basis.right)),
        static_cast<float>(Dot(delta, basis.up)),
        static_cast<float>(Dot(delta, basis.forward)),
    };
}

[[nodiscard]] bool ProjectCameraPoint(
    const CameraPoint point,
    const CameraSample& view,
    float& x,
    float& y) noexcept {
    if (point.depth < kNearPlane || g_overlay.viewport_width < 1.0F || g_overlay.viewport_height < 1.0F)
        return false;
    constexpr auto radians = 3.14159265358979323846 / 180.0;
    const auto horizontal_tangent = std::tan(std::clamp(view.fov, kMinFov, kMaxFov) * 0.5 * radians);
    if (!std::isfinite(horizontal_tangent) || horizontal_tangent <= 0.0)
        return false;
    const auto aspect = static_cast<double>(g_overlay.viewport_width / g_overlay.viewport_height);
    const auto vertical_tangent = horizontal_tangent / aspect;
    x = (g_overlay.viewport_width * 0.5F) +
        static_cast<float>((static_cast<double>(point.right) / (point.depth * horizontal_tangent)) *
                           (g_overlay.viewport_width * 0.5F));
    y = (g_overlay.viewport_height * 0.5F) -
        static_cast<float>((static_cast<double>(point.up) / (point.depth * vertical_tangent)) *
                           (g_overlay.viewport_height * 0.5F));
    return std::isfinite(x) && std::isfinite(y);
}

[[nodiscard]] bool ClipScreenLine(float& x0, float& y0, float& x1, float& y1) noexcept {
    const auto width = g_overlay.viewport_width;
    const auto height = g_overlay.viewport_height;
    const auto dx = x1 - x0;
    const auto dy = y1 - y0;
    auto t0 = 0.0F;
    auto t1 = 1.0F;
    const auto clip = [&t0, &t1](const float p, const float q) noexcept {
        if (std::abs(p) < 0.00001F)
            return q >= 0.0F;
        const auto ratio = q / p;
        if (p < 0.0F) {
            if (ratio > t1) return false;
            if (ratio > t0) t0 = ratio;
        } else {
            if (ratio < t0) return false;
            if (ratio < t1) t1 = ratio;
        }
        return true;
    };
    if (!clip(-dx, x0) || !clip(dx, width - x0) || !clip(-dy, y0) || !clip(dy, height - y0))
        return false;
    const auto original_x = x0;
    const auto original_y = y0;
    x0 = original_x + (t0 * dx);
    y0 = original_y + (t0 * dy);
    x1 = original_x + (t1 * dx);
    y1 = original_y + (t1 * dy);
    return true;
}

void DrawWorldLine(
    Vec3 from,
    Vec3 to,
    const CameraSample& view,
    const CameraBasis& basis,
    const std::uint32_t color,
    const float thickness) noexcept {
    auto a = ToCameraSpace(from, view, basis);
    auto b = ToCameraSpace(to, view, basis);
    if (a.depth < kNearPlane && b.depth < kNearPlane)
        return;
    if (a.depth < kNearPlane || b.depth < kNearPlane) {
        const auto amount = (kNearPlane - a.depth) / (b.depth - a.depth);
        const CameraPoint clipped{
            a.right + (amount * (b.right - a.right)),
            a.up + (amount * (b.up - a.up)),
            kNearPlane,
        };
        if (a.depth < kNearPlane) a = clipped;
        else b = clipped;
    }
    float x0 = 0.0F;
    float y0 = 0.0F;
    float x1 = 0.0F;
    float y1 = 0.0F;
    if (ProjectCameraPoint(a, view, x0, y0) && ProjectCameraPoint(b, view, x1, y1) &&
        ClipScreenLine(x0, y0, x1, y1))
        AddLine(x0, y0, x1, y1, color, thickness);
}

[[nodiscard]] CameraSample EvaluatePathSample(
    const CampathKeyframe* keys,
    const std::uint32_t count,
    const CampathInterpolation interpolation,
    const CampathEasing easing,
    const std::uint32_t segment,
    const double amount) noexcept {
    return EvaluateCampathCamera(keys, count, segment, amount, interpolation, easing);
}

void HashGeometryBytes(
    std::uint64_t& hash,
    const void* value,
    const std::size_t size) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(value);
    for (std::size_t index = 0; index < size; ++index) {
        hash ^= bytes[index];
        hash *= 1099511628211ULL;
    }
}

template <typename T>
void HashGeometryValue(std::uint64_t& hash, const T& value) noexcept {
    HashGeometryBytes(hash, &value, sizeof(value));
}

[[nodiscard]] std::uint64_t CampathGeometryKey(
    const CampathPayloadHeader& header,
    const CampathKeyframe* keys,
    const double aspect) noexcept {
    auto hash = 14695981039346656037ULL;
    HashGeometryValue(hash, header.keyframe_count);
    HashGeometryValue(hash, header.interpolation);
    HashGeometryValue(hash, header.easing);
    HashGeometryValue(hash, header.end_behavior);
    HashGeometryValue(hash, aspect);
    for (std::uint32_t index = 0; index < header.keyframe_count; ++index) {
        const auto& key = keys[index];
        HashGeometryValue(hash, key.demo_tick);
        HashGeometryValue(hash, key.camera.x);
        HashGeometryValue(hash, key.camera.y);
        HashGeometryValue(hash, key.camera.z);
        HashGeometryValue(hash, key.camera.pitch);
        HashGeometryValue(hash, key.camera.yaw);
        HashGeometryValue(hash, key.camera.roll);
        HashGeometryValue(hash, key.camera.fov);
    }
    return hash;
}

void BuildCameraMarker(
    const CameraSample& camera,
    const double aspect,
    const double axis_length,
    const double frustum_length,
    std::array<WorldLine, kCameraMarkerLineCount>& lines) noexcept {
    const Vec3 origin{camera.x, camera.y, camera.z};
    const auto basis = BasisFromAngles(camera);
    lines[0] = WorldLine{origin, Add(origin, Scale(basis.forward, axis_length))};

    constexpr auto radians = 3.14159265358979323846 / 180.0;
    const auto half_width = std::tan(
        std::clamp(camera.fov, kMinFov, kMaxFov) * 0.5 * radians) * frustum_length;
    const auto half_height = half_width / std::max(aspect, 0.01);
    const auto center = Add(origin, Scale(basis.forward, frustum_length));
    const std::array<Vec3, 4> corners{
        Add(Add(center, Scale(basis.right, half_width)), Scale(basis.up, half_height)),
        Add(Add(center, Scale(basis.right, -half_width)), Scale(basis.up, half_height)),
        Add(Add(center, Scale(basis.right, -half_width)), Scale(basis.up, -half_height)),
        Add(Add(center, Scale(basis.right, half_width)), Scale(basis.up, -half_height)),
    };
    for (std::size_t corner = 0; corner < corners.size(); ++corner) {
        lines[1 + corner] = WorldLine{origin, corners[corner]};
        lines[5 + corner] = WorldLine{corners[corner], corners[(corner + 1) % corners.size()]};
    }
}

[[nodiscard]] bool EnsureCampathGeometryCache(
    const CampathPayloadHeader& header,
    const CampathKeyframe* keys) noexcept {
    if (header.keyframe_count == 0 || header.keyframe_count > kMaxCampathKeyframes || keys == nullptr)
        return false;
    const auto aspect = static_cast<double>(std::max(g_overlay.viewport_width, 1.0F) /
                                            std::max(g_overlay.viewport_height, 1.0F));
    const auto key = CampathGeometryKey(header, keys, aspect);
    auto& cache = g_campath_geometry_cache;
    if (cache.valid && cache.key == key && cache.keyframe_count == header.keyframe_count)
        return true;

    cache.valid = false;
    cache.key = key;
    cache.keyframe_count = header.keyframe_count;
    cache.path_line_count = 0;
    if (header.keyframe_count >= 2) {
        for (std::uint32_t segment = 0; segment + 1 < header.keyframe_count; ++segment) {
            const auto samples = header.interpolation == CampathInterpolation::smooth
                ? kSmoothVisualizationSamplesPerSegment
                : 1u;
            auto prior = keys[segment].camera;
            for (std::uint32_t sample_index = 1; sample_index <= samples; ++sample_index) {
                if (cache.path_line_count >= cache.path_lines.size())
                    return false;
                const auto amount = static_cast<double>(sample_index) / static_cast<double>(samples);
                const auto current = EvaluatePathSample(
                    keys, header.keyframe_count, header.interpolation, header.easing, segment, amount);
                cache.path_lines[cache.path_line_count++] = WorldLine{
                    {prior.x, prior.y, prior.z},
                    {current.x, current.y, current.z},
                };
                prior = current;
            }
        }
    }

    for (std::uint32_t index = 0; index < header.keyframe_count; ++index) {
        auto& marker = cache.markers[index];
        marker.origin = {keys[index].camera.x, keys[index].camera.y, keys[index].camera.z};
        BuildCameraMarker(keys[index].camera, aspect, 24.0, 28.0, marker.regular);
        BuildCameraMarker(keys[index].camera, aspect, 34.0, 38.0, marker.selected);
    }
    cache.valid = true;
    return true;
}

void DrawCampathVisualization(
    const SmvmSnapshotPayload& snapshot,
    const CampathPayloadHeader& header,
    const CampathKeyframe* keys,
    const CameraSample& view) noexcept {
    if (header.keyframe_count == 0 ||
        (snapshot.flags & (smvm_snapshot_show_path | smvm_snapshot_show_cameras)) == 0)
        return;
    if (!EnsureCampathGeometryCache(header, keys))
        return;
    const auto& cache = g_campath_geometry_cache;
    const auto view_basis = BasisFromAngles(view);
    if ((snapshot.flags & smvm_snapshot_show_path) != 0 && header.keyframe_count >= 2) {
        for (std::size_t index = 0; index < cache.path_line_count; ++index)
            DrawWorldLine(
                cache.path_lines[index].from,
                cache.path_lines[index].to,
                view,
                view_basis,
                kPathLine,
                2.0F);
    }

    for (std::uint32_t index = 0; index < header.keyframe_count; ++index) {
        const auto& key = keys[index];
        const auto& marker = cache.markers[index];
        const auto selected = static_cast<std::int32_t>(index) == snapshot.selected_keyframe;
        const auto color = selected ? kSelectedMarker : kMarker;
        const auto& marker_lines = selected ? marker.selected : marker.regular;
        DrawWorldLine(marker_lines[0].from, marker_lines[0].to, view, view_basis, color,
                      selected ? 2.5F : 1.25F);

        if ((snapshot.flags & smvm_snapshot_show_cameras) != 0) {
            for (std::size_t line = 1; line < marker_lines.size(); ++line)
                DrawWorldLine(marker_lines[line].from, marker_lines[line].to,
                              view, view_basis, color, selected ? 1.8F : 1.0F);
        }

        float screen_x = 0.0F;
        float screen_y = 0.0F;
        if (ProjectCameraPoint(ToCameraSpace(marker.origin, view, view_basis),
                               view, screen_x, screen_y)) {
            const auto radius = selected ? 6.0F : 4.0F;
            AddRect(screen_x - radius, screen_y - radius, radius * 2.0F, radius * 2.0F, color);
            // Labels are screen-space text; they are collected here and drawn
            // with the ImGui mono font after NewFrame (see DrawWorldLabels).
            if ((snapshot.flags & smvm_snapshot_show_labels) != 0 &&
                g_overlay.world_label_count < g_overlay.world_labels.size()) {
                auto& label = g_overlay.world_labels[g_overlay.world_label_count++];
                static_cast<void>(std::snprintf(label.text.data(), label.text.size(), "K%u  T%lld",
                                                index + 1, static_cast<long long>(key.demo_tick)));
                label.scale = static_cast<float>(std::clamp(snapshot.path_label_scale, 0.5, 2.0));
                label.x = screen_x + 9.0F;
                label.y = screen_y - (9.0F * label.scale);
                label.selected = selected;
            }
        }
    }
}


LRESULT CALLBACK SmvmWindowProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) noexcept;

void ReleaseSavedState(SavedD3D11State& saved) noexcept {
    for (auto*& render_target : saved.render_targets)
        SafeRelease(render_target);
    SafeRelease(saved.depth_stencil_view);
    SafeRelease(saved.blend_state);
    SafeRelease(saved.depth_stencil_state);
    SafeRelease(saved.rasterizer_state);
    SafeRelease(saved.input_layout);
    SafeRelease(saved.vertex_buffer);
    SafeRelease(saved.vertex_shader);
    SafeRelease(saved.pixel_shader);
    SafeRelease(saved.vertex_constant_buffer);
    SafeRelease(saved.pixel_resource);
    SafeRelease(saved.pixel_sampler);
}

void SavePipelineState(ID3D11DeviceContext* context, SavedD3D11State& saved) noexcept {
    context->OMGetRenderTargets(
        static_cast<UINT>(saved.render_targets.size()),
        saved.render_targets.data(),
        &saved.depth_stencil_view);
    context->OMGetBlendState(&saved.blend_state, saved.blend_factor, &saved.sample_mask);
    context->OMGetDepthStencilState(&saved.depth_stencil_state, &saved.stencil_reference);
    context->RSGetState(&saved.rasterizer_state);
    context->RSGetViewports(&saved.viewport_count, saved.viewports.data());
    context->IAGetInputLayout(&saved.input_layout);
    context->IAGetVertexBuffers(0, 1, &saved.vertex_buffer, &saved.vertex_stride, &saved.vertex_offset);
    context->IAGetPrimitiveTopology(&saved.topology);
    context->VSGetShader(&saved.vertex_shader, nullptr, nullptr);
    context->VSGetConstantBuffers(0, 1, &saved.vertex_constant_buffer);
    context->PSGetShader(&saved.pixel_shader, nullptr, nullptr);
    context->PSGetShaderResources(0, 1, &saved.pixel_resource);
    context->PSGetSamplers(0, 1, &saved.pixel_sampler);
}

void RestorePipelineState(ID3D11DeviceContext* context, SavedD3D11State& saved) noexcept {
    context->OMSetRenderTargets(
        static_cast<UINT>(saved.render_targets.size()),
        saved.render_targets.data(),
        saved.depth_stencil_view);
    context->OMSetBlendState(saved.blend_state, saved.blend_factor, saved.sample_mask);
    context->OMSetDepthStencilState(saved.depth_stencil_state, saved.stencil_reference);
    context->RSSetState(saved.rasterizer_state);
    context->RSSetViewports(
        saved.viewport_count,
        saved.viewport_count > 0 ? saved.viewports.data() : nullptr);
    context->IASetInputLayout(saved.input_layout);
    context->IASetVertexBuffers(0, 1, &saved.vertex_buffer, &saved.vertex_stride, &saved.vertex_offset);
    context->IASetPrimitiveTopology(saved.topology);
    context->VSSetShader(saved.vertex_shader, nullptr, 0);
    context->VSSetConstantBuffers(0, 1, &saved.vertex_constant_buffer);
    context->PSSetShader(saved.pixel_shader, nullptr, 0);
    context->PSSetShaderResources(0, 1, &saved.pixel_resource);
    context->PSSetSamplers(0, 1, &saved.pixel_sampler);
    ReleaseSavedState(saved);
}

void ReleaseRenderTarget() noexcept {
    SafeRelease(g_overlay.render_target);
}

struct SdlMouseApi final {
    SdlGetWindowsFunction get_windows{};
    SdlGetWindowPropertiesFunction get_window_properties{};
    SdlGetPointerPropertyFunction get_pointer_property{};
    SdlFreeFunction free_memory{};
    SdlGetWindowRelativeMouseModeFunction get_relative_mode{};
    SdlSetWindowRelativeMouseModeFunction set_relative_mode{};
};

[[nodiscard]] bool ResolveSdlMouseApi(SdlMouseApi& api) noexcept {
    const auto module = GetModuleHandleW(L"SDL3.dll");
    if (module == nullptr)
        return false;
    api.get_windows = reinterpret_cast<SdlGetWindowsFunction>(
        GetProcAddress(module, "SDL_GetWindows"));
    api.get_window_properties = reinterpret_cast<SdlGetWindowPropertiesFunction>(
        GetProcAddress(module, "SDL_GetWindowProperties"));
    api.get_pointer_property = reinterpret_cast<SdlGetPointerPropertyFunction>(
        GetProcAddress(module, "SDL_GetPointerProperty"));
    api.free_memory = reinterpret_cast<SdlFreeFunction>(GetProcAddress(module, "SDL_free"));
    api.get_relative_mode = reinterpret_cast<SdlGetWindowRelativeMouseModeFunction>(
        GetProcAddress(module, "SDL_GetWindowRelativeMouseMode"));
    api.set_relative_mode = reinterpret_cast<SdlSetWindowRelativeMouseModeFunction>(
        GetProcAddress(module, "SDL_SetWindowRelativeMouseMode"));
    return api.get_windows != nullptr && api.get_window_properties != nullptr &&
           api.get_pointer_property != nullptr && api.free_memory != nullptr &&
           api.get_relative_mode != nullptr && api.set_relative_mode != nullptr;
}

[[nodiscard]] SdlWindow* FindSdlWindowForHwnd(
    const SdlMouseApi& api,
    const HWND target_window) noexcept {
    int count = 0;
    auto** windows = api.get_windows(&count);
    if (windows == nullptr || count <= 0 || count > 1024) {
        if (windows != nullptr)
            api.free_memory(windows);
        return nullptr;
    }
    SdlWindow* match = nullptr;
    for (int index = 0; index < count; ++index) {
        auto* candidate = windows[index];
        if (candidate == nullptr)
            continue;
        const auto properties = api.get_window_properties(candidate);
        if (properties == 0)
            continue;
        const auto hwnd = static_cast<HWND>(api.get_pointer_property(
            properties, "SDL.window.win32.hwnd", nullptr));
        if (hwnd == target_window) {
            match = candidate;
            break;
        }
    }
    api.free_memory(windows);
    return match;
}

[[nodiscard]] bool SuspendSdlRelativeMouseMode(const HWND window) noexcept {
    auto& state = g_overlay;
    SdlMouseApi api{};
    if (!ResolveSdlMouseApi(api))
        return false;
    auto* sdl_window = FindSdlWindowForHwnd(api, window);
    if (sdl_window == nullptr || !api.get_relative_mode(sdl_window))
        return sdl_window != nullptr;
    if (!api.set_relative_mode(sdl_window, false))
        return false;
    // Deadlock can re-enable relative mode after a spectator-mode transition
    // while the SMVM menu is still open. Preserve the original restore intent,
    // but always re-check and withdraw any newly re-enabled mode.
    state.sdl_relative_mouse_restore_pending = true;
    return true;
}

[[nodiscard]] bool RestoreSdlRelativeMouseMode(const HWND window) noexcept {
    auto& state = g_overlay;
    if (!state.sdl_relative_mouse_restore_pending)
        return true;
    SdlMouseApi api{};
    if (!ResolveSdlMouseApi(api))
        return false;
    auto* sdl_window = FindSdlWindowForHwnd(api, window);
    if (sdl_window == nullptr || !api.set_relative_mode(sdl_window, true))
        return false;
    state.sdl_relative_mouse_restore_pending = false;
    return true;
}

[[nodiscard]] constexpr bool IsRawMouseRegistration(
    const RAWINPUTDEVICE& registration) noexcept {
    return registration.usUsagePage == kGenericDesktopUsagePage &&
           registration.usUsage == kMouseUsage;
}

[[nodiscard]] bool ReadRegisteredRawInputDevices(
    std::array<RAWINPUTDEVICE, kMaxRegisteredRawInputDevices>& registrations,
    UINT& count) noexcept {
    count = 0;
    if (GetRegisteredRawInputDevices(nullptr, &count, sizeof(RAWINPUTDEVICE)) ==
        static_cast<UINT>(-1))
        return false;
    if (count == 0)
        return true;
    if (count > registrations.size())
        return false;

    auto capacity = static_cast<UINT>(registrations.size());
    const auto copied = GetRegisteredRawInputDevices(
        registrations.data(), &capacity, sizeof(RAWINPUTDEVICE));
    if (copied == static_cast<UINT>(-1) || copied > registrations.size())
        return false;
    count = copied;
    return true;
}

[[nodiscard]] bool SnapshotRawMouseRegistrationsOnWindowThread() noexcept {
    auto& state = g_overlay;
    if (state.raw_mouse_restore_pending.load(std::memory_order_acquire))
        return true;

    std::array<RAWINPUTDEVICE, kMaxRegisteredRawInputDevices> registrations{};
    UINT count = 0;
    if (!ReadRegisteredRawInputDevices(registrations, count))
        return false;

    UINT mouse_count = 0;
    for (UINT index = 0; index < count; ++index) {
        if (!IsRawMouseRegistration(registrations[index]))
            continue;
        if (mouse_count >= state.suspended_raw_mouse_registrations.size())
            return false;
        state.suspended_raw_mouse_registrations[mouse_count++] = registrations[index];
    }
    state.suspended_raw_mouse_registration_count = mouse_count;
    if (mouse_count != 0)
        state.raw_mouse_restore_pending.store(true, std::memory_order_release);
    return true;
}

[[nodiscard]] bool RemoveCurrentRawMouseRegistrationsOnWindowThread() noexcept {
    std::array<RAWINPUTDEVICE, kMaxRegisteredRawInputDevices> registrations{};
    UINT count = 0;
    if (!ReadRegisteredRawInputDevices(registrations, count))
        return false;

    std::array<RAWINPUTDEVICE, kMaxSuspendedRawMouseRegistrations> removals{};
    UINT removal_count = 0;
    for (UINT index = 0; index < count; ++index) {
        if (!IsRawMouseRegistration(registrations[index]))
            continue;
        if (removal_count >= removals.size())
            return false;
        removals[removal_count++] = RAWINPUTDEVICE{
            registrations[index].usUsagePage,
            registrations[index].usUsage,
            RIDEV_REMOVE,
            nullptr};
    }
    return removal_count == 0 ||
           RegisterRawInputDevices(removals.data(), removal_count, sizeof(RAWINPUTDEVICE)) != FALSE;
}

[[nodiscard]] bool RestoreRawMouseRegistrationsOnWindowThread() noexcept {
    auto& state = g_overlay;
    if (!state.raw_mouse_restore_pending.load(std::memory_order_acquire))
        return true;

    // SDL can rebuild the process' current mouse registration when relative
    // mode is restored, especially after a spectator-mode transition while
    // the editor was open. That registration reflects the new Deadlock mode
    // and is more authoritative than the pre-menu snapshot. Preserve it; only
    // replay the snapshot when SDL left no process mouse registration behind.
    std::array<RAWINPUTDEVICE, kMaxRegisteredRawInputDevices> current{};
    UINT current_count = 0;
    if (!ReadRegisteredRawInputDevices(current, current_count))
        return false;
    for (UINT index = 0; index < current_count; ++index) {
        if (!IsRawMouseRegistration(current[index]))
            continue;
        state.suspended_raw_mouse_registration_count = 0;
        state.raw_mouse_restore_pending.store(false, std::memory_order_release);
        return true;
    }

    const auto count = state.suspended_raw_mouse_registration_count;
    if (count == 0 || count > state.suspended_raw_mouse_registrations.size())
        return false;
    if (RegisterRawInputDevices(
            state.suspended_raw_mouse_registrations.data(),
            count,
            sizeof(RAWINPUTDEVICE)) == FALSE)
        return false;
    state.suspended_raw_mouse_registration_count = 0;
    state.raw_mouse_restore_pending.store(false, std::memory_order_release);
    return true;
}

[[nodiscard]] RawMouseRegistrationDisposition RawMouseRegistrationForWindow(
    const HWND window) noexcept {
    std::array<RAWINPUTDEVICE, kMaxRegisteredRawInputDevices> registrations{};
    UINT count = 0;
    if (!ReadRegisteredRawInputDevices(registrations, count))
        return RawMouseRegistrationDisposition::query_failed;
    auto disposition = RawMouseRegistrationDisposition::none;
    for (UINT index = 0; index < count; ++index) {
        const auto& registration = registrations[index];
        if (!IsRawMouseRegistration(registration))
            continue;
        if (registration.hwndTarget == nullptr)
            return RawMouseRegistrationDisposition::null_target;
        if (registration.hwndTarget == window)
            return RawMouseRegistrationDisposition::exact_window;
        disposition = RawMouseRegistrationDisposition::other_window;
    }
    return disposition;
}

[[nodiscard]] bool AcquireManualRawMouseRegistrationOnWindowThread(
    const HWND window) noexcept {
    auto& state = g_overlay;
    if (state.manual_raw_mouse_restore_pending.load(std::memory_order_acquire)) {
        if (RawMouseRegistrationForWindow(window) ==
            RawMouseRegistrationDisposition::exact_window)
            return true;

        // Deadlock replaced SMVM's live registration (normally while replay
        // transport was changing). Its new registration is authoritative and
        // becomes the fresh restore baseline; replaying the pre-resume snapshot
        // here would overwrite the engine's new input state.
        state.manual_raw_mouse_registration_count = 0;
        state.manual_raw_mouse_restore_pending.store(false, std::memory_order_release);
    }

    std::array<RAWINPUTDEVICE, kMaxRegisteredRawInputDevices> registrations{};
    UINT count = 0;
    if (!ReadRegisteredRawInputDevices(registrations, count))
        return false;

    UINT mouse_count = 0;
    for (UINT index = 0; index < count; ++index) {
        if (!IsRawMouseRegistration(registrations[index]))
            continue;
        if (mouse_count >= state.manual_raw_mouse_registrations.size())
            return false;
        state.manual_raw_mouse_registrations[mouse_count++] = registrations[index];
    }
    state.manual_raw_mouse_registration_count = mouse_count;

    // SDL3 may register the mouse against a private helper HWND. The DXGI
    // output-window subclass cannot observe those WM_INPUT packets even though
    // registration and relative-mode checks both report success. While SMVM
    // owns Free Camera, retarget only the generic-desktop mouse usage to the
    // output window and suppress duplicate legacy motion. The exact original
    // registration is restored when the menu opens, focus is lost, or camera
    // ownership ends.
    const RAWINPUTDEVICE manual_registration{
        kGenericDesktopUsagePage,
        kMouseUsage,
        RIDEV_NOLEGACY,
        window};
    if (RegisterRawInputDevices(
            &manual_registration, 1, sizeof(RAWINPUTDEVICE)) == FALSE) {
        state.manual_raw_mouse_registration_count = 0;
        return false;
    }
    state.manual_raw_mouse_restore_pending.store(true, std::memory_order_release);
    return RawMouseRegistrationForWindow(window) ==
           RawMouseRegistrationDisposition::exact_window;
}

[[nodiscard]] bool RestoreManualRawMouseRegistrationOnWindowThread() noexcept {
    auto& state = g_overlay;
    if (!state.manual_raw_mouse_restore_pending.load(std::memory_order_acquire))
        return true;

    const RAWINPUTDEVICE removal{
        kGenericDesktopUsagePage,
        kMouseUsage,
        RIDEV_REMOVE,
        nullptr};
    if (RegisterRawInputDevices(&removal, 1, sizeof(RAWINPUTDEVICE)) == FALSE)
        return false;

    const auto count = state.manual_raw_mouse_registration_count;
    if (count > state.manual_raw_mouse_registrations.size())
        return false;
    if (count != 0 && RegisterRawInputDevices(
            state.manual_raw_mouse_registrations.data(),
            count,
            sizeof(RAWINPUTDEVICE)) == FALSE)
        return false;

    state.manual_raw_mouse_registration_count = 0;
    state.manual_raw_mouse_restore_pending.store(false, std::memory_order_release);
    return true;
}

void ClearManualMouseReadiness() noexcept {
    auto& state = g_overlay;
    state.manual_pointer_active.store(false, std::memory_order_release);
    state.manual_relative_mouse_ready.store(false, std::memory_order_release);
    state.manual_raw_input_ready.store(false, std::memory_order_release);
    state.manual_cursor_ready.store(false, std::memory_order_release);
}

void ClearManualKeyboardReadiness() noexcept {
    auto& state = g_overlay;
    state.manual_keyboard_ready.store(false, std::memory_order_release);
    state.manual_foreground_ready.store(false, std::memory_order_release);
    state.manual_window_procedure_ready.store(false, std::memory_order_release);
    state.manual_engine_input_ready.store(false, std::memory_order_release);
}

void ClearManualInputReadiness() noexcept {
    ClearManualMouseReadiness();
    ClearManualKeyboardReadiness();
}

void ResetManualMouseAcquisitionState(const HWND window) noexcept {
    auto& state = g_overlay;
    constexpr auto reset = ResetMouseAcquisition();
    state.manual_look_right_delta.store(reset.accumulated_look_right, std::memory_order_release);
    state.manual_look_up_delta.store(reset.accumulated_look_up, std::memory_order_release);
    state.manual_wheel_delta.store(0, std::memory_order_release);
    state.manual_mouse_observed.store(false, std::memory_order_release);
    state.manual_raw_mouse_observed_ms.store(reset.raw_timestamp_ms, std::memory_order_release);
    state.manual_fallback_mouse_observed_ms.store(
        reset.fallback_timestamp_ms, std::memory_order_release);
    state.manual_fallback_mouse_observed.store(false, std::memory_order_release);

    POINT point{};
    const auto baseline_ready = window != nullptr && GetCursorPos(&point) != FALSE &&
        ScreenToClient(window, &point) != FALSE;
    if (baseline_ready) {
        state.manual_legacy_mouse_x.store(point.x, std::memory_order_release);
        state.manual_legacy_mouse_y.store(point.y, std::memory_order_release);
    }
    state.manual_legacy_mouse_seeded.store(
        baseline_ready || reset.fallback_seeded, std::memory_order_release);
    // SDL may queue a baseline, recenter, and delayed recenter echo. Raw Input
    // remains authoritative while the short legacy-only settle window refreshes
    // the absolute fallback baseline. If the queue is empty, the original
    // one-accepted-sample guard still protects the first later fallback packet.
    state.manual_discard_next_legacy_sample.store(
        reset.discard_next_fallback_sample, std::memory_order_release);
    state.manual_fallback_settle_until_ms.store(
        window != nullptr ? GetTickCount64() + kFallbackMouseReacquisitionSettleMs : 0,
        std::memory_order_release);
}

[[nodiscard]] FreeCameraInputReadiness CurrentFreeCameraInputReadiness(
    const HWND window) noexcept {
    const auto& state = g_overlay;
    return {
        state.manual_keyboard_ready.load(std::memory_order_acquire),
        state.manual_relative_mouse_ready.load(std::memory_order_acquire),
        state.manual_raw_input_ready.load(std::memory_order_acquire),
        state.manual_cursor_ready.load(std::memory_order_acquire),
        state.manual_foreground_ready.load(std::memory_order_acquire) &&
            GetForegroundWindow() == window,
        state.manual_window_procedure_ready.load(std::memory_order_acquire) &&
            reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC)) ==
                &SmvmWindowProcedure,
        state.manual_engine_input_ready.load(std::memory_order_acquire) &&
            !state.input_restore_pending.load(std::memory_order_acquire),
    };
}

[[nodiscard]] bool RefreshManualKeyboardReadiness(const HWND window) noexcept {
    auto& state = g_overlay;
    const auto foreground_ready = window != nullptr && IsWindow(window) &&
        GetForegroundWindow() == window;
    const auto window_procedure_ready = foreground_ready &&
        reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC)) ==
            &SmvmWindowProcedure;
    const auto engine_input_ready = window_procedure_ready &&
        !state.input_restore_pending.load(std::memory_order_acquire);
    const auto keyboard_ready = engine_input_ready &&
        !state.menu_open.load(std::memory_order_acquire);
    state.manual_foreground_ready.store(foreground_ready, std::memory_order_release);
    state.manual_window_procedure_ready.store(window_procedure_ready, std::memory_order_release);
    state.manual_engine_input_ready.store(engine_input_ready, std::memory_order_release);
    state.manual_keyboard_ready.store(keyboard_ready, std::memory_order_release);
    return keyboard_ready;
}

[[nodiscard]] bool ApplyManualPointerStateOnWindowThread(
    const HWND window,
    const bool enabled) noexcept {
    auto& state = g_overlay;
    if (enabled) {
        ResetSmvmManualInput();
        ClearManualInputReadiness();
        state.manual_input_error.store(false, std::memory_order_release);
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::none), std::memory_order_release);
    }
    if (window == nullptr || !IsWindow(window) ||
        GetWindowThreadProcessId(window, nullptr) != GetCurrentThreadId()) {
        ClearManualInputReadiness();
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::invalid_window_thread),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        return false;
    }

    if (!enabled) {
        auto restored = true;
        if (state.manual_relative_mouse_restore_pending) {
            SdlMouseApi api{};
            auto* sdl_window = ResolveSdlMouseApi(api) ? FindSdlWindowForHwnd(api, window) : nullptr;
            restored = sdl_window != nullptr && api.set_relative_mode(sdl_window, false);
            if (restored) {
                state.manual_relative_mouse_restore_pending = false;
                // If the editor is open, its pending restore came from the
                // SMVM-owned relative mode. Do not resurrect that mode after
                // Free Camera has been released.
                if (state.menu_open.load(std::memory_order_acquire))
                    state.sdl_relative_mouse_restore_pending = false;
            }
        }
        restored = RestoreManualRawMouseRegistrationOnWindowThread() && restored;
        ClearManualInputReadiness();
        ResetManualMouseAcquisitionState(nullptr);
        state.manual_input_error.store(false, std::memory_order_release);
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::none), std::memory_order_release);
        return restored;
    }

    if (state.menu_open.load(std::memory_order_acquire) || GetForegroundWindow() != window) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::menu_or_foreground),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        ClearManualInputReadiness();
        return false;
    }

    ClearManualInputReadiness();
    ResetManualMouseAcquisitionState(window);
    const auto keyboard_ready = RefreshManualKeyboardReadiness(window);
    if (!keyboard_ready) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::readiness_incomplete),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        return false;
    }

    SdlMouseApi api{};
    if (!ResolveSdlMouseApi(api)) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::sdl_api_unavailable),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        return true;
    }
    auto* sdl_window = FindSdlWindowForHwnd(api, window);
    if (sdl_window == nullptr) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::sdl_window_unavailable),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        return true;
    }

    const auto was_relative = api.get_relative_mode(sdl_window);
    if (!was_relative)
        state.manual_relative_mouse_restore_pending = true;
    if (!was_relative && !api.set_relative_mode(sdl_window, true)) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::sdl_enable_failed),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        return keyboard_ready;
    }
    if (!api.get_relative_mode(sdl_window)) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::sdl_verify_failed),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        return keyboard_ready;
    }
    state.manual_relative_mouse_ready.store(true, std::memory_order_release);

    // SDL relative mode is the supported owner of Deadlock's process-wide Raw
    // Input registration. Verify that the transport was actually rebuilt; a
    // visible cursor with no mouse registration is the paused-camera failure
    // this transition exists to prevent.
    auto raw_registration = RawMouseRegistrationForWindow(window);
    if (raw_registration != RawMouseRegistrationDisposition::exact_window) {
        if (raw_registration == RawMouseRegistrationDisposition::query_failed ||
            !AcquireManualRawMouseRegistrationOnWindowThread(window)) {
            state.manual_raw_registration_disposition.store(
                static_cast<std::uint32_t>(raw_registration), std::memory_order_release);
            state.manual_input_failure.store(
                static_cast<std::uint32_t>(ManualInputFailure::raw_registration_missing),
                std::memory_order_release);
            state.manual_input_error.store(true, std::memory_order_release);
            if (state.manual_relative_mouse_restore_pending) {
                static_cast<void>(api.set_relative_mode(sdl_window, false));
                state.manual_relative_mouse_restore_pending = false;
            }
            static_cast<void>(RestoreManualRawMouseRegistrationOnWindowThread());
            ClearManualMouseReadiness();
            return keyboard_ready;
        }
        raw_registration = RawMouseRegistrationForWindow(window);
    }
    state.manual_raw_registration_disposition.store(
        static_cast<std::uint32_t>(raw_registration), std::memory_order_release);
    if (raw_registration != RawMouseRegistrationDisposition::exact_window) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::raw_registration_missing),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        if (state.manual_relative_mouse_restore_pending) {
            static_cast<void>(api.set_relative_mode(sdl_window, false));
            state.manual_relative_mouse_restore_pending = false;
        }
        static_cast<void>(RestoreManualRawMouseRegistrationOnWindowThread());
        ClearManualMouseReadiness();
        return keyboard_ready;
    }

    state.manual_raw_input_ready.store(true, std::memory_order_release);
    SetCursor(nullptr);
    state.manual_cursor_ready.store(!state.cursor_shown, std::memory_order_release);
    static_cast<void>(RefreshManualKeyboardReadiness(window));
    const auto mouse_ready = CurrentFreeCameraInputReadiness(window).MouseReady();
    state.manual_pointer_active.store(mouse_ready, std::memory_order_release);
    if (!mouse_ready)
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::readiness_incomplete),
            std::memory_order_release);
    if (!mouse_ready) {
        state.manual_input_error.store(true, std::memory_order_release);
        ClearManualMouseReadiness();
    } else {
        state.manual_input_error.store(false, std::memory_order_release);
    }
    return CurrentFreeCameraInputReadiness(window).AnyReady();
}

void SuspendManualPointerForFocusLoss(const HWND window) noexcept {
    auto& state = g_overlay;
    const auto pointer_was_active =
        state.manual_pointer_active.load(std::memory_order_acquire);
    ClearManualInputReadiness();
    ResetManualMouseAcquisitionState(nullptr);
    if (!pointer_was_active)
        return;
    static_cast<void>(RestoreManualRawMouseRegistrationOnWindowThread());
    SdlMouseApi api{};
    if (!ResolveSdlMouseApi(api))
        return;
    if (auto* sdl_window = FindSdlWindowForHwnd(api, window); sdl_window != nullptr)
        static_cast<void>(api.set_relative_mode(sdl_window, false));
}

[[nodiscard]] bool SuspendInputSystemOnWindowThread() noexcept;
[[nodiscard]] bool RestoreInputSystemOnWindowThread() noexcept;
[[nodiscard]] bool SetModalInputMaintenanceOnWindowThread(HWND window, bool enabled) noexcept;

[[nodiscard]] bool MenuPointerStateRestorePending() noexcept {
    const auto& state = g_overlay;
    return state.previous_clip_valid || state.sdl_relative_mouse_restore_pending ||
           state.raw_mouse_restore_pending.load(std::memory_order_acquire) ||
           state.input_restore_pending.load(std::memory_order_acquire);
}

[[nodiscard]] bool PointerStateRestorePending() noexcept {
    return MenuPointerStateRestorePending() || g_overlay.manual_relative_mouse_restore_pending ||
           g_overlay.manual_raw_mouse_restore_pending.load(std::memory_order_acquire);
}

[[nodiscard]] bool ApplyCursorStateOnWindowThread(const bool menu_open) noexcept {
    auto& state = g_overlay;
    if (menu_open) {
        // Deadlock uses SDL relative mode for its spectator mouse. Suspend that
        // public per-window mode while the modal editor owns the pointer so the
        // same physical click cannot also advance the spectator target. This
        // runs on the game window thread, as required by SDL, and restores only
        // a mode that was observed enabled when the menu opened.
        // SDL's public relative-mode transition does not necessarily withdraw
        // the process-wide Raw Input mouse registration before the next click.
        // Snapshot it first, then remove only the generic-desktop mouse usage.
        // Keyboard and every other HID registration remain untouched.
        const auto manual_raw_ok = RestoreManualRawMouseRegistrationOnWindowThread();
        const auto input_ok = manual_raw_ok && SuspendInputSystemOnWindowThread();
        const auto snapshot_ok = input_ok && SnapshotRawMouseRegistrationsOnWindowThread();
        const auto sdl_ok = snapshot_ok && SuspendSdlRelativeMouseMode(state.output_window);
        const auto raw_ok = sdl_ok && RemoveCurrentRawMouseRegistrationsOnWindowThread();
        if (!input_ok || !snapshot_ok || !sdl_ok || !raw_ok) {
            static_cast<void>(RestoreSdlRelativeMouseMode(state.output_window));
            static_cast<void>(RestoreRawMouseRegistrationsOnWindowThread());
            static_cast<void>(RestoreInputSystemOnWindowThread());
            return false;
        }
        if (!state.previous_clip_valid)
            state.previous_clip_valid = GetClipCursor(&state.previous_clip) != FALSE;
        static_cast<void>(ClipCursor(nullptr));
        if (!state.cursor_shown) {
            state.cursor_show_adjustments = 0;
            for (int attempt = 0; attempt < 32; ++attempt) {
                ++state.cursor_show_adjustments;
                if (ShowCursor(TRUE) >= 0)
                    break;
            }
            state.cursor_shown = true;
        }
        SetCursor(LoadCursorW(nullptr, IDC_ARROW));
        return true;
    }

    if (state.cursor_shown) {
        for (int index = 0; index < state.cursor_show_adjustments; ++index)
            static_cast<void>(ShowCursor(FALSE));
        state.cursor_show_adjustments = 0;
        state.cursor_shown = false;
    }
    auto clip_restored = true;
    if (state.previous_clip_valid) {
        if (ClipCursor(&state.previous_clip) != FALSE)
            state.previous_clip_valid = false;
        else
            clip_restored = false;
    }
    // Let SDL rebuild any relative-mode bookkeeping first, then restore the
    // exact process registration that was observed before the editor opened.
    // RegisterRawInputDevices replaces the same usage, so the final flags and
    // target window match Deadlock's original registration.
    const auto sdl_restored = RestoreSdlRelativeMouseMode(state.output_window);
    const auto raw_restored = RestoreRawMouseRegistrationsOnWindowThread();
    // Re-enable the engine input system last so no restored mouse transport can
    // deliver an editor-owned press while teardown is still in progress.
    const auto input_restored = RestoreInputSystemOnWindowThread();
    return clip_restored && sdl_restored && raw_restored && input_restored &&
           !MenuPointerStateRestorePending();
}

[[nodiscard]] bool ResumeFreeCameraInputAfterMenu(const HWND window) noexcept {
    auto& state = g_overlay;
    SmvmSnapshotPayload snapshot{};
    const auto camera_active = ReadSnapshot(snapshot) && CanUseManualCamera(snapshot);
    if (!ShouldReacquireFreeCameraInputAfterMenu(
            true,
            state.menu_open.load(std::memory_order_acquire),
            state.manual_pointer_requested.load(std::memory_order_acquire),
            camera_active)) {
        ClearManualInputReadiness();
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::camera_snapshot_not_ready),
            std::memory_order_release);
        return false;
    }

    if (!ApplyManualPointerStateOnWindowThread(window, true))
        return false;
    const auto ready = CurrentFreeCameraInputReadiness(window).AnyReady();
    if (!ready)
        ClearManualInputReadiness();
    return ready;
}

[[nodiscard]] bool ApplyMenuTransitionOnWindowThread(
    const HWND window,
    const bool should_open) noexcept {
    auto& state = g_overlay;
    auto transition_ok = ApplyCursorStateOnWindowThread(should_open);
    if (transition_ok)
        transition_ok = SetModalInputMaintenanceOnWindowThread(window, should_open);
    if (transition_ok && should_open) {
        ClearManualInputReadiness();
        ResetManualMouseAcquisitionState(nullptr);
    }
    if (transition_ok && !should_open &&
        state.manual_pointer_requested.load(std::memory_order_acquire))
        transition_ok = ResumeFreeCameraInputAfterMenu(window);

    if (transition_ok) {
        return true;
    }

    if (!should_open) {
        // A requested Free Camera without either a validated keyboard route or
        // mouse route is not usable. Reopen the editor with a typed error and
        // let Retry run the same deliberate transition again; camera
        // composition remains owned and untouched throughout.
        state.menu_open.store(true, std::memory_order_release);
        ClearManualInputReadiness();
        ResetManualMouseAcquisitionState(nullptr);
        static_cast<void>(ApplyCursorStateOnWindowThread(true));
        static_cast<void>(SetModalInputMaintenanceOnWindowThread(window, true));
        if (state.manual_input_failure.load(std::memory_order_acquire) ==
            static_cast<std::uint32_t>(ManualInputFailure::none)) {
            state.manual_input_failure.store(
                static_cast<std::uint32_t>(ManualInputFailure::menu_restore_failed),
                std::memory_order_release);
        }
        state.manual_input_error.store(true, std::memory_order_release);
    }
    return false;
}

[[nodiscard]] bool SetModalInputMaintenanceOnWindowThread(
    const HWND window,
    const bool enabled) noexcept {
    if (window == nullptr || !IsWindow(window) ||
        GetWindowThreadProcessId(window, nullptr) != GetCurrentThreadId())
        return false;
    if (!enabled) {
        static_cast<void>(KillTimer(window, kSmvmModalInputTimerId));
        return true;
    }
    return SetTimer(
        window,
        kSmvmModalInputTimerId,
        kSmvmModalInputTimerIntervalMs,
        nullptr) != 0;
}

[[nodiscard]] bool WaitForCallbacksToDrain(
    const std::atomic<std::uint32_t>& counter,
    const std::chrono::milliseconds timeout = kCallbackDrainTimeout) noexcept {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (counter.load(std::memory_order_acquire) != 0) {
        if (std::chrono::steady_clock::now() >= deadline)
            return false;
        Sleep(1);
    }
    return true;
}

[[nodiscard]] bool RestorePublishedWindowProcedure() noexcept;

void ClearWindowProcedurePublication() noexcept {
    auto& state = g_overlay;
    state.original_window_proc = nullptr;
    state.output_window = nullptr;
}

[[nodiscard]] bool InitializeImGui() noexcept {
    auto& state = g_overlay;
    if (state.imgui != nullptr)
        return true;
    state.imgui = ImGui::CreateContext();
    if (state.imgui == nullptr)
        return false;
    ImGui::SetCurrentContext(state.imgui);
    auto& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.LogFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    if (!ImGui_ImplDX11_Init(state.device, state.context)) {
        ImGui::DestroyContext(state.imgui);
        state.imgui = nullptr;
        return false;
    }
    // Zero forces the first rendered frame to rasterize the atlas and apply
    // the style at the current DPI * UI scale.
    state.font_scale = 0.0F;
    state.imgui_button_state = {};
    state.imgui_mouse_outside = true;
    state.ui_ready.store(true, std::memory_order_release);
    return true;
}

void ShutdownImGui() noexcept {
    auto& state = g_overlay;
    state.ui_ready.store(false, std::memory_order_release);
    if (state.imgui == nullptr)
        return;
    ImGui::SetCurrentContext(state.imgui);
    ImGui_ImplDX11_Shutdown();
    ImGui::DestroyContext(state.imgui);
    state.imgui = nullptr;
    state.fonts = {};
    state.font_scale = 0.0F;
    state.imgui_button_state = {};
    state.imgui_mouse_outside = true;
}

// Rasters the atlas at size * dpiScale * uiScale whenever either factor
// changed, then re-uploads renderer device objects and rescales the style.
void EnsureUiScale(const SmvmSnapshotPayload& snapshot) noexcept {
    auto& state = g_overlay;
    auto dpi = static_cast<float>(USER_DEFAULT_SCREEN_DPI);
    if (state.output_window != nullptr && IsWindow(state.output_window))
        dpi = static_cast<float>(GetDpiForWindow(state.output_window));
    const auto effective = std::clamp(dpi / 96.0F, 1.0F, 4.0F) *
        static_cast<float>(std::clamp(snapshot.ui_scale, 0.75, 1.5));
    if (state.font_scale > 0.0F && std::fabs(state.font_scale - effective) < 0.001F)
        return;
    static_cast<void>(smvm_theme::RebuildFontAtlas(state.fonts, effective));
    ImGui_ImplDX11_InvalidateDeviceObjects();
    static_cast<void>(ImGui_ImplDX11_CreateDeviceObjects());
    smvm_theme::ApplySmvmStyle(effective);
    state.font_scale = effective;
}

[[nodiscard]] ImGuiKey TranslateVirtualKey(const std::int32_t key) noexcept {
    if (key >= 'A' && key <= 'Z')
        return static_cast<ImGuiKey>(ImGuiKey_A + (key - 'A'));
    if (key >= '0' && key <= '9')
        return static_cast<ImGuiKey>(ImGuiKey_0 + (key - '0'));
    if (key >= VK_F1 && key <= VK_F24)
        return static_cast<ImGuiKey>(ImGuiKey_F1 + (key - VK_F1));
    if (key >= VK_NUMPAD0 && key <= VK_NUMPAD9)
        return static_cast<ImGuiKey>(ImGuiKey_Keypad0 + (key - VK_NUMPAD0));
    switch (key) {
        case VK_TAB: return ImGuiKey_Tab;
        case VK_LEFT: return ImGuiKey_LeftArrow;
        case VK_RIGHT: return ImGuiKey_RightArrow;
        case VK_UP: return ImGuiKey_UpArrow;
        case VK_DOWN: return ImGuiKey_DownArrow;
        case VK_PRIOR: return ImGuiKey_PageUp;
        case VK_NEXT: return ImGuiKey_PageDown;
        case VK_HOME: return ImGuiKey_Home;
        case VK_END: return ImGuiKey_End;
        case VK_INSERT: return ImGuiKey_Insert;
        case VK_DELETE: return ImGuiKey_Delete;
        case VK_BACK: return ImGuiKey_Backspace;
        case VK_SPACE: return ImGuiKey_Space;
        case VK_RETURN: return ImGuiKey_Enter;
        case VK_ESCAPE: return ImGuiKey_Escape;
        case VK_LCONTROL: return ImGuiKey_LeftCtrl;
        case VK_LSHIFT: return ImGuiKey_LeftShift;
        case VK_LMENU: return ImGuiKey_LeftAlt;
        case VK_LWIN: return ImGuiKey_LeftSuper;
        case VK_RCONTROL: return ImGuiKey_RightCtrl;
        case VK_RSHIFT: return ImGuiKey_RightShift;
        case VK_RMENU: return ImGuiKey_RightAlt;
        case VK_RWIN: return ImGuiKey_RightSuper;
        case VK_OEM_4: return ImGuiKey_LeftBracket;
        case VK_OEM_6: return ImGuiKey_RightBracket;
        case VK_OEM_1: return ImGuiKey_Semicolon;
        case VK_OEM_7: return ImGuiKey_Apostrophe;
        case VK_OEM_3: return ImGuiKey_GraveAccent;
        case VK_OEM_5: return ImGuiKey_Backslash;
        case VK_OEM_COMMA: return ImGuiKey_Comma;
        case VK_OEM_PERIOD: return ImGuiKey_Period;
        case VK_OEM_2: return ImGuiKey_Slash;
        case VK_OEM_MINUS: return ImGuiKey_Minus;
        case VK_OEM_PLUS: return ImGuiKey_Equal;
        default: return ImGuiKey_None;
    }
}

void PushUiEvent(const std::uint32_t kind, const std::int32_t a, const std::int32_t b = 0) noexcept {
    auto& state = g_overlay;
    if (!state.ui_ready.load(std::memory_order_acquire))
        return;
    const auto write = state.ui_events_write.load(std::memory_order_relaxed);
    const auto next = (write + 1) % state.ui_events.size();
    if (next == state.ui_events_read.load(std::memory_order_acquire))
        return;
    state.ui_events[write] = SmvmUiInputEvent{kind, a, b};
    state.ui_events_write.store(static_cast<std::uint32_t>(next), std::memory_order_release);
}

void DiscardUiEvents() noexcept {
    auto& state = g_overlay;
    state.ui_events_read.store(state.ui_events_write.load(std::memory_order_acquire),
                               std::memory_order_release);
}

void DrainUiEvents(const bool feed) noexcept {
    auto& state = g_overlay;
    auto& io = ImGui::GetIO();
    auto read = state.ui_events_read.load(std::memory_order_relaxed);
    const auto write = state.ui_events_write.load(std::memory_order_acquire);
    while (read != write) {
        const auto& event = state.ui_events[read];
        if (feed) {
            switch (event.kind) {
                case smvm_ui_event_key: {
                    const auto key = TranslateVirtualKey(event.a);
                    if (key != ImGuiKey_None)
                        io.AddKeyEvent(key, event.b != 0);
                    break;
                }
                case smvm_ui_event_char:
                    io.AddInputCharacter(static_cast<unsigned int>(event.a));
                    break;
                case smvm_ui_event_wheel:
                    io.AddMouseWheelEvent(0.0F, static_cast<float>(event.a) / WHEEL_DELTA);
                    break;
                case smvm_ui_event_click:
                    if (event.a >= 0 && event.a < 5) {
                        io.AddMouseButtonEvent(event.a, true);
                        io.AddMouseButtonEvent(event.a, false);
                    }
                    break;
                default:
                    break;
            }
        }
        read = (read + 1) % state.ui_events.size();
    }
    state.ui_events_read.store(static_cast<std::uint32_t>(read), std::memory_order_release);
}

[[nodiscard]] bool UiFeedingAllowed(const bool menu_open) noexcept {
    const auto& state = g_overlay;
    return (menu_open || state.replay_tick_input_active.load(std::memory_order_acquire)) &&
           state.ui_ready.load(std::memory_order_acquire) &&
           !IsBindingActionIndex(state.binding_capture_action.load(std::memory_order_acquire));
}

// Mouse position (client coords scaled to back-buffer pixels for DPI) and
// button state (derived from the consumed-route tracking, so both the raw and
// the legacy channels agree). Called on the render thread before NewFrame.
void FeedImguiMouse(const bool menu_open) noexcept {
    auto& state = g_overlay;
    auto& io = ImGui::GetIO();
    if (!menu_open) {
        if (!state.imgui_mouse_outside) {
            io.AddMousePosEvent(-3.402823466e+38F, -3.402823466e+38F);
            state.imgui_mouse_outside = true;
        }
        for (auto button = 0; button < 5; ++button) {
            if (state.imgui_button_state[static_cast<std::size_t>(button)]) {
                io.AddMouseButtonEvent(button, false);
                state.imgui_button_state[static_cast<std::size_t>(button)] = false;
            }
        }
        return;
    }
    state.imgui_mouse_outside = false;
    auto scale_x = 1.0F;
    auto scale_y = 1.0F;
    if (state.output_window != nullptr) {
        RECT client{};
        if (GetClientRect(state.output_window, &client) && client.right > 0 && client.bottom > 0) {
            scale_x = state.viewport_width / static_cast<float>(client.right);
            scale_y = state.viewport_height / static_cast<float>(client.bottom);
        }
    }
    io.AddMousePosEvent(
        static_cast<float>(state.mouse_x.load(std::memory_order_relaxed)) * scale_x,
        static_cast<float>(state.mouse_y.load(std::memory_order_relaxed)) * scale_y);
    for (auto button = 0; button < 5; ++button) {
        const auto down =
            state.menu_mouse_routes[static_cast<std::size_t>(button)].HasAny();
        if (down != state.imgui_button_state[static_cast<std::size_t>(button)]) {
            io.AddMouseButtonEvent(button, down);
            state.imgui_button_state[static_cast<std::size_t>(button)] = down;
        }
    }
}

void DrawWorldLabels() noexcept {
    const auto& state = g_overlay;
    if (state.world_label_count == 0)
        return;
    const auto& fonts = smvm_theme::GetFonts();
    if (fonts.mono == nullptr)
        return;
    auto* draw_list = ImGui::GetBackgroundDrawList();
    for (std::size_t index = 0; index < state.world_label_count; ++index) {
        const auto& label = state.world_labels[index];
        draw_list->AddText(
            fonts.mono,
            fonts.mono->FontSize * label.scale,
            ImVec2(label.x, label.y),
            label.selected ? smvm_theme::colors::kAccent : smvm_theme::colors::kMuted,
            label.text.data());
    }
}

void ReleaseGraphicsResources() noexcept {
    auto& state = g_overlay;
    ShutdownImGui();
    ReleaseRenderTarget();
    SafeRelease(state.vertex_buffer);
    SafeRelease(state.constant_buffer);
    SafeRelease(state.vertex_shader);
    SafeRelease(state.pixel_shader);
    SafeRelease(state.input_layout);
    SafeRelease(state.atlas_view);
    SafeRelease(state.sampler);
    SafeRelease(state.blend_state);
    SafeRelease(state.rasterizer_state);
    SafeRelease(state.depth_state);
    SafeRelease(state.context);
    SafeRelease(state.device);
    state.target_swapchain = nullptr;
    state.viewport_width = 0.0F;
    state.viewport_height = 0.0F;
    state.ready.store(false, std::memory_order_release);
}

[[nodiscard]] bool ReleaseDeviceResources() noexcept {
    if (!RestorePublishedWindowProcedure() ||
        !WaitForCallbacksToDrain(g_overlay.active_window_procedures))
        return false;
    ClearWindowProcedurePublication();
    ReleaseGraphicsResources();
    return true;
}

// The world-geometry pipeline only needs a solid white texel; all UI text is
// rasterized by the ImGui font atlas (see smvm_theme).
[[nodiscard]] bool CreateSolidTexture(ID3D11Device* device) noexcept {
    constexpr std::uint32_t pixel = 0xFFFFFFFFu;
    const D3D11_TEXTURE2D_DESC texture_description{
        1, 1, 1, 1, DXGI_FORMAT_R8G8B8A8_UNORM,
        {1, 0}, D3D11_USAGE_IMMUTABLE, D3D11_BIND_SHADER_RESOURCE, 0, 0,
    };
    const D3D11_SUBRESOURCE_DATA texture_data{&pixel, sizeof(pixel), 0};
    ID3D11Texture2D* texture = nullptr;
    if (FAILED(device->CreateTexture2D(&texture_description, &texture_data, &texture)))
        return false;
    const auto result = device->CreateShaderResourceView(texture, nullptr, &g_overlay.atlas_view);
    texture->Release();
    return SUCCEEDED(result);
}


[[nodiscard]] bool CreatePipeline(ID3D11Device* device) noexcept {
    constexpr std::string_view shader_source = R"(
cbuffer ViewportBuffer : register(b0) { float2 Viewport; float2 Padding; };
struct VSInput { float2 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
struct PSInput { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
PSInput VSMain(VSInput input) {
    PSInput output;
    output.position = float4((input.position.x / Viewport.x) * 2.0 - 1.0,
                             1.0 - (input.position.y / Viewport.y) * 2.0, 0.0, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}
Texture2D Atlas : register(t0);
SamplerState AtlasSampler : register(s0);
float4 PSMain(PSInput input) : SV_TARGET { return input.color * Atlas.Sample(AtlasSampler, input.uv); }
)";
    ID3DBlob* vertex_blob = nullptr;
    ID3DBlob* pixel_blob = nullptr;
    ID3DBlob* errors = nullptr;
    auto result = D3DCompile(shader_source.data(), shader_source.size(), "SMVM", nullptr, nullptr,
                             "VSMain", "vs_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                             &vertex_blob, &errors);
    SafeRelease(errors);
    if (FAILED(result)) {
        SafeRelease(vertex_blob);
        return false;
    }
    result = D3DCompile(shader_source.data(), shader_source.size(), "SMVM", nullptr, nullptr,
                        "PSMain", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                        &pixel_blob, &errors);
    SafeRelease(errors);
    if (FAILED(result)) {
        SafeRelease(vertex_blob);
        SafeRelease(pixel_blob);
        return false;
    }
    result = device->CreateVertexShader(vertex_blob->GetBufferPointer(), vertex_blob->GetBufferSize(),
                                        nullptr, &g_overlay.vertex_shader);
    if (SUCCEEDED(result))
        result = device->CreatePixelShader(pixel_blob->GetBufferPointer(), pixel_blob->GetBufferSize(),
                                           nullptr, &g_overlay.pixel_shader);
    constexpr std::array<D3D11_INPUT_ELEMENT_DESC, 3> input_elements{{
        {"POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"COLOR", 0, DXGI_FORMAT_R8G8B8A8_UNORM, 0, 16, D3D11_INPUT_PER_VERTEX_DATA, 0},
    }};
    if (SUCCEEDED(result))
        result = device->CreateInputLayout(input_elements.data(), static_cast<UINT>(input_elements.size()),
                                           vertex_blob->GetBufferPointer(), vertex_blob->GetBufferSize(),
                                           &g_overlay.input_layout);
    SafeRelease(vertex_blob);
    SafeRelease(pixel_blob);
    if (FAILED(result))
        return false;

    D3D11_BUFFER_DESC vertex_description{};
    vertex_description.ByteWidth = static_cast<UINT>(sizeof(Vertex) * kMaxVertices);
    vertex_description.Usage = D3D11_USAGE_DYNAMIC;
    vertex_description.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    vertex_description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateBuffer(&vertex_description, nullptr, &g_overlay.vertex_buffer)))
        return false;
    D3D11_BUFFER_DESC constant_description{};
    constant_description.ByteWidth = 16;
    constant_description.Usage = D3D11_USAGE_DYNAMIC;
    constant_description.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    constant_description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateBuffer(&constant_description, nullptr, &g_overlay.constant_buffer)))
        return false;

    D3D11_BLEND_DESC blend_description{};
    blend_description.RenderTarget[0].BlendEnable = TRUE;
    blend_description.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
    blend_description.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    blend_description.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    blend_description.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    blend_description.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    blend_description.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    blend_description.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    if (FAILED(device->CreateBlendState(&blend_description, &g_overlay.blend_state)))
        return false;

    D3D11_RASTERIZER_DESC rasterizer_description{};
    rasterizer_description.FillMode = D3D11_FILL_SOLID;
    rasterizer_description.CullMode = D3D11_CULL_NONE;
    rasterizer_description.ScissorEnable = FALSE;
    rasterizer_description.DepthClipEnable = TRUE;
    if (FAILED(device->CreateRasterizerState(&rasterizer_description, &g_overlay.rasterizer_state)))
        return false;

    D3D11_DEPTH_STENCIL_DESC depth_description{};
    depth_description.DepthEnable = FALSE;
    depth_description.StencilEnable = FALSE;
    if (FAILED(device->CreateDepthStencilState(&depth_description, &g_overlay.depth_state)))
        return false;

    D3D11_SAMPLER_DESC sampler_description{};
    sampler_description.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler_description.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_description.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_description.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_description.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sampler_description, &g_overlay.sampler)))
        return false;
    return CreateSolidTexture(device);
}

void SetMenuOpen(const bool open) noexcept {
    auto& state = g_overlay;
    // Text editing is subordinate to the modal boundary. Clearing it on both
    // open and close prevents stale tick-field ownership from suppressing the
    // Free Camera keyboard/pointer reacquisition path.
    state.replay_tick_input_active.store(false, std::memory_order_release);
    if (open) {
        if (state.menu_open.load(std::memory_order_acquire))
            return;
        // Preserve channel-specific ownership for keys that Deadlock already
        // received. In particular, a configured menu key may have had its raw
        // down delivered even though SMVM consumed its window-message down.
        for (std::size_t key = 1; key < state.key_down.size(); ++key) {
            if (state.key_down[key].load(std::memory_order_acquire) == 0) {
                // ResetSmvmManualInput intentionally clears the live state at
                // each modal boundary. Retain an earlier pre-held route until
                // its real key-up arrives; otherwise reopening the menu while
                // the key is still physically held could steal its release.
                continue;
            }
            const auto consumed = state.menu_key_routes[key].load(std::memory_order_acquire);
            auto preheld = 0u;
            if ((consumed & kMenuRawKeyRoute) == 0)
                preheld |= kMenuRawKeyRoute;
            if ((consumed & kMenuWindowKeyRoute) == 0)
                preheld |= kMenuWindowKeyRoute;
            state.menu_preheld_keys[key].store(
                static_cast<std::uint8_t>(preheld), std::memory_order_release);
        }
        auto expected = false;
        if (!state.menu_open.compare_exchange_strong(
                expected, true, std::memory_order_acq_rel, std::memory_order_acquire))
            return;
    } else if (!state.menu_open.exchange(false, std::memory_order_acq_rel)) {
        return;
    }
    // Neither entering nor leaving the modal editor may carry a held gameplay
    // input or accumulated mouse/wheel impulse across the ownership boundary.
    ClearManualKeyboardReadiness();
    ResetSmvmManualInput();
    if (!open) {
        ResetBindingCaptureState();
        DiscardUiEvents();
    }

    // Cursor visibility and clipping are thread-affine game-window state. The
    // menu can be toggled by Present or IPC-driven rendering, so marshal the
    // transition to the output window rather than touching it here.
    const auto window = state.output_window;
    auto transition_ok = !open;
    if (window != nullptr && IsWindow(window)) {
        const auto window_thread = GetWindowThreadProcessId(window, nullptr);
        if (window_thread == GetCurrentThreadId()) {
            transition_ok = ApplyMenuTransitionOnWindowThread(window, open);
        } else {
            transition_ok = PostMessageW(
                window, kSmvmCursorTransitionMessage, open ? TRUE : FALSE, 0) != FALSE;
        }
    }
    if (!transition_ok && open) {
        // A modal menu without exclusive pointer ownership is worse than no
        // menu: fail closed and leave Deadlock's input registrations restored.
        state.menu_open.store(false, std::memory_order_release);
        ResetSmvmManualInput();
    }
    PublishStatus(
        SmvmRendererBackend::d3d11,
        transition_ok ? SmvmRendererError::none : SmvmRendererError::window_hook_failed);
    if (transition_ok && state.presentation_mode.load(std::memory_order_acquire) ==
            static_cast<std::uint32_t>(DeadlockUiMode::smvm_replay_ui)) {
        // Tab/menu transitions can make Deadlock rebuild Panorama. Reassert the
        // movie UI contract through the managed command owner without changing
        // the user's selected presentation mode.
        static_cast<void>(QueueAction(
            SmvmActionType::set_deadlock_ui_mode,
            static_cast<std::int32_t>(DeadlockUiMode::smvm_replay_ui),
            0));
    }
}

void RequestManualPointerState(const bool enabled) noexcept {
    auto& state = g_overlay;
    const auto previous = state.manual_pointer_requested.exchange(enabled, std::memory_order_acq_rel);
    if (previous == enabled) {
        if (!enabled || state.menu_open.load(std::memory_order_acquire))
            return;

        const auto now = GetTickCount64();
        auto next_check = state.manual_pointer_next_health_check_ms.load(std::memory_order_acquire);
        if (now < next_check || !state.manual_pointer_next_health_check_ms.compare_exchange_strong(
                next_check,
                now + kManualPointerHealthCheckIntervalMs,
                std::memory_order_acq_rel,
                std::memory_order_acquire))
            return;

        const auto window = state.output_window;
        const auto raw_exact = window != nullptr && IsWindow(window) &&
            RawMouseRegistrationForWindow(window) == RawMouseRegistrationDisposition::exact_window;
        if (!ShouldRetryManualPointerAcquisition(
                true,
                false,
                state.manual_pointer_active.load(std::memory_order_acquire),
                raw_exact,
                now,
                next_check))
            return;

        // Readiness is stale once the process registration drifts. Keep the
        // keyboard route alive and rebuild only the mouse side on the window
        // thread; this bounds the post-resume failure window to one health tick.
        ClearManualMouseReadiness();
        if (window == nullptr || !IsWindow(window) ||
            PostMessageW(window, kSmvmManualPointerTransitionMessage, TRUE, 0) == FALSE) {
            state.manual_input_failure.store(
                static_cast<std::uint32_t>(ManualInputFailure::invalid_window_thread),
                std::memory_order_release);
            state.manual_input_error.store(true, std::memory_order_release);
            PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::window_hook_failed);
        }
        return;
    }
    state.manual_pointer_next_health_check_ms.store(
        enabled ? GetTickCount64() + kManualPointerHealthCheckIntervalMs : 0,
        std::memory_order_release);
    ClearManualKeyboardReadiness();
    if (!enabled)
        ResetSmvmManualInput();
    if (!enabled) {
        state.manual_input_error.store(false, std::memory_order_release);
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::none), std::memory_order_release);
    }
    if (enabled && state.menu_open.load(std::memory_order_acquire)) {
        // Camera ownership can become active while the activating UI click is
        // still being released. The OPEN -> CLOSED transition owns acquisition
        // and validates the full route after ImGui has finished that click.
        return;
    }
    const auto window = state.output_window;
    if (window == nullptr || !IsWindow(window) ||
        PostMessageW(window, kSmvmManualPointerTransitionMessage, enabled ? TRUE : FALSE, 0) == FALSE) {
        ClearManualInputReadiness();
        if (enabled) {
            state.manual_input_failure.store(
                static_cast<std::uint32_t>(ManualInputFailure::invalid_window_thread),
                std::memory_order_release);
            state.manual_input_error.store(true, std::memory_order_release);
            SetMenuOpen(true);
        }
        PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::window_hook_failed);
    }
}

[[nodiscard]] std::uint32_t CurrentModifiers(const std::uint32_t key = 0) noexcept {
    auto modifiers =
        ((GetKeyState(VK_CONTROL) & 0x8000) != 0 ? 1u : 0u) |
        ((GetKeyState(VK_MENU) & 0x8000) != 0 ? 2u : 0u) |
        ((GetKeyState(VK_SHIFT) & 0x8000) != 0 ? 4u : 0u) |
        (((GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0) ? 8u : 0u);
    if (key == VK_CONTROL || key == VK_LCONTROL || key == VK_RCONTROL) modifiers &= ~1u;
    if (key == VK_MENU || key == VK_LMENU || key == VK_RMENU) modifiers &= ~2u;
    if (key == VK_SHIFT || key == VK_LSHIFT || key == VK_RSHIFT) modifiers &= ~4u;
    if (key == VK_LWIN || key == VK_RWIN) modifiers &= ~8u;
    return modifiers;
}

[[nodiscard]] std::uint32_t NormalizeWindowVirtualKey(
    const WPARAM key,
    const LPARAM key_data) noexcept {
    const auto virtual_key = static_cast<std::uint32_t>(key);
    if (virtual_key == VK_SHIFT) {
        const auto scan_code = static_cast<UINT>((key_data >> 16) & 0xFF);
        const auto mapped = MapVirtualKeyW(scan_code, MAPVK_VSC_TO_VK_EX);
        return mapped == VK_LSHIFT || mapped == VK_RSHIFT ? mapped : virtual_key;
    }
    if (virtual_key == VK_CONTROL)
        return (key_data & (1LL << 24)) != 0 ? VK_RCONTROL : VK_LCONTROL;
    if (virtual_key == VK_MENU)
        return (key_data & (1LL << 24)) != 0 ? VK_RMENU : VK_LMENU;
    return virtual_key;
}

[[nodiscard]] std::uint32_t NormalizeRawVirtualKey(const RAWKEYBOARD& keyboard) noexcept {
    const auto virtual_key = static_cast<std::uint32_t>(keyboard.VKey);
    if (virtual_key == VK_SHIFT) {
        const auto mapped = MapVirtualKeyW(keyboard.MakeCode, MAPVK_VSC_TO_VK_EX);
        return mapped == VK_LSHIFT || mapped == VK_RSHIFT ? mapped : virtual_key;
    }
    if (virtual_key == VK_CONTROL)
        return (keyboard.Flags & RI_KEY_E0) != 0 ? VK_RCONTROL : VK_LCONTROL;
    if (virtual_key == VK_MENU)
        return (keyboard.Flags & RI_KEY_E0) != 0 ? VK_RMENU : VK_LMENU;
    return virtual_key;
}

[[nodiscard]] bool CaptureKeyboardBinding(
    const std::uint32_t key,
    const bool repeated) noexcept {
    auto& state = g_overlay;
    const auto action = state.binding_capture_action.load(std::memory_order_acquire);
    if (!IsBindingActionIndex(action))
        return false;
    const auto modifiers = CurrentModifiers(key);
    // Alt+F4 always belongs to Deadlock / Windows. Do not bind or cancel the
    // capture when the system close chord passes through this procedure.
    if (!IsModifierKey(key) && modifiers != 0)
        state.binding_modifier_chord_used.fetch_or(modifiers, std::memory_order_acq_rel);
    if (key == VK_F4 && (modifiers & 2u) != 0)
        return false;
    if (repeated)
        return true;
    if (key == VK_ESCAPE) {
        CancelBindingCapture();
        return true;
    }
    // Modifier-only presses build the chord; the next non-modifier key is the
    // base input persisted in the binding.
    if (IsModifierKey(key))
        return true;
    if (key == 0 || key > 0xFFu)
        return true;
    const auto original = state.binding_capture_original.load(std::memory_order_acquire);
    static_cast<void>(SubmitBindingValue(action, EncodeBinding(key, modifiers), original));
    return true;
}

[[nodiscard]] bool WasKeyDownConsumed(
    std::uint32_t key,
    std::uint8_t route) noexcept;
[[nodiscard]] bool ConsumeTrackedKeyUp(
    std::uint32_t key,
    std::uint8_t route) noexcept;

[[nodiscard]] bool CaptureStandaloneModifierBinding(const std::uint32_t key) noexcept {
    auto& state = g_overlay;
    const auto action = state.binding_capture_action.load(std::memory_order_acquire);
    if (!IsBindingActionIndex(action) || !IsModifierKey(key))
        return false;
    if (!WasKeyDownConsumed(key, kMenuWindowKeyRoute))
        return false;
    static_cast<void>(ConsumeTrackedKeyUp(key, kMenuWindowKeyRoute));
    auto modifier = 0u;
    if (key == VK_CONTROL || key == VK_LCONTROL || key == VK_RCONTROL) modifier = 1u;
    if (key == VK_MENU || key == VK_LMENU || key == VK_RMENU) modifier = 2u;
    if (key == VK_SHIFT || key == VK_LSHIFT || key == VK_RSHIFT) modifier = 4u;
    if (key == VK_LWIN || key == VK_RWIN) modifier = 8u;
    const auto used = state.binding_modifier_chord_used.fetch_and(~modifier, std::memory_order_acq_rel);
    if ((used & modifier) != 0)
        return true;
    // A modifier release with no other modifier held represents a standalone
    // binding (for example the default Shift boost). Chords are submitted on
    // their non-modifier key-down before this path is reached.
    if (CurrentModifiers(key) != 0)
        return true;
    const auto original = state.binding_capture_original.load(std::memory_order_acquire);
    static_cast<void>(SubmitBindingValue(action, EncodeBinding(key, 0), original));
    return true;
}

[[nodiscard]] bool CaptureMouseBinding(
    const UINT message,
    const WPARAM wparam) noexcept {
    auto& state = g_overlay;
    const auto action = state.binding_capture_action.load(std::memory_order_acquire);
    if (!IsBindingActionIndex(action))
        return false;
    auto base = 0u;
    if (message == WM_MBUTTONDOWN) {
        base = static_cast<std::uint32_t>(SmvmInputCode::mouse_middle);
    } else if (message == WM_XBUTTONDOWN && GET_XBUTTON_WPARAM(wparam) == XBUTTON1) {
        base = static_cast<std::uint32_t>(SmvmInputCode::mouse_x1);
    } else if (message == WM_XBUTTONDOWN && GET_XBUTTON_WPARAM(wparam) == XBUTTON2) {
        base = static_cast<std::uint32_t>(SmvmInputCode::mouse_x2);
    } else if (message == WM_MOUSEWHEEL && GET_WHEEL_DELTA_WPARAM(wparam) > 0) {
        base = static_cast<std::uint32_t>(SmvmInputCode::wheel_up);
    } else if (message == WM_MOUSEWHEEL && GET_WHEEL_DELTA_WPARAM(wparam) < 0) {
        base = static_cast<std::uint32_t>(SmvmInputCode::wheel_down);
    }
    if (base == 0)
        return false;
    // Movement/boost/roll bindings are held-state controls backed by the
    // keyboard state table. Mouse buttons and wheel are intentionally rejected
    // here instead of presenting a binding the manual controller cannot use.
    if (action < kFirstEditorBindingAction || action > kFirstEditorBindingAction + 10) {
        RejectBindingCapture(action);
        return true;
    }
    const auto original = state.binding_capture_original.load(std::memory_order_acquire);
    static_cast<void>(SubmitBindingValue(
        action, EncodeBinding(base, CurrentModifiers()), original));
    return true;
}

[[nodiscard]] bool InputMatchesKeyboard(const std::uint32_t binding, const WPARAM key) noexcept {
    const auto base = binding & kSmvmInputBaseMask;
    const auto modifiers = CurrentModifiers(base);
    const auto actual = static_cast<std::uint32_t>(key);
    const auto base_matches = base == actual ||
        (base == VK_SHIFT && (actual == VK_LSHIFT || actual == VK_RSHIFT)) ||
        (base == VK_CONTROL && (actual == VK_LCONTROL || actual == VK_RCONTROL)) ||
        (base == VK_MENU && (actual == VK_LMENU || actual == VK_RMENU));
    return (binding & kSmvmInputBaseMask) != 0 &&
           base_matches &&
           ((binding & kSmvmInputModifierMask) >> 16) == modifiers;
}

[[nodiscard]] bool InputMatchesMouse(const std::uint32_t binding, const UINT message, const WPARAM wparam) noexcept {
    const auto modifiers = CurrentModifiers();
    if (((binding & kSmvmInputModifierMask) >> 16) != modifiers)
        return false;
    const auto base = binding & kSmvmInputBaseMask;
    if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_middle))
        return message == WM_MBUTTONDOWN;
    if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x1))
        return message == WM_XBUTTONDOWN && GET_XBUTTON_WPARAM(wparam) == XBUTTON1;
    if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x2))
        return message == WM_XBUTTONDOWN && GET_XBUTTON_WPARAM(wparam) == XBUTTON2;
    if (base == static_cast<std::uint32_t>(SmvmInputCode::wheel_up))
        return message == WM_MOUSEWHEEL && GET_WHEEL_DELTA_WPARAM(wparam) > 0;
    if (base == static_cast<std::uint32_t>(SmvmInputCode::wheel_down))
        return message == WM_MOUSEWHEEL && GET_WHEEL_DELTA_WPARAM(wparam) < 0;
    return false;
}

[[nodiscard]] bool CanUseManualCamera(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
           (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
           HasSmvmManualCameraOwnership(
               (snapshot.flags & smvm_snapshot_manual_camera_requested) != 0,
               (snapshot.flags & smvm_snapshot_manual_camera_active) != 0) &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0 && snapshot.observer_mode == 4;
}

[[nodiscard]] bool CanConsumeManualCameraInput(const SmvmSnapshotPayload& snapshot) noexcept {
    const auto& state = g_overlay;
    const auto window = state.output_window;
    const auto readiness = CurrentFreeCameraInputReadiness(window);
    return CanConsumeFreeCameraInput(
        state.menu_open.load(std::memory_order_acquire),
        state.manual_pointer_requested.load(std::memory_order_acquire),
        CanUseManualCamera(snapshot),
        readiness,
        (snapshot.flags & smvm_snapshot_replay_seek_in_progress) != 0);
}

[[nodiscard]] bool CanConsumeManualCameraKeyboardInput(
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto& state = g_overlay;
    if (state.replay_tick_input_active.load(std::memory_order_acquire) ||
        state.cinematic_start_ready.load(std::memory_order_acquire) ||
        state.cinematic_space_consumed.load(std::memory_order_acquire))
        return false;
    return CanConsumeFreeCameraKeyboardInput(
        state.menu_open.load(std::memory_order_acquire),
        state.manual_pointer_requested.load(std::memory_order_acquire),
        CanUseManualCamera(snapshot),
        CurrentFreeCameraInputReadiness(state.output_window),
        (snapshot.flags & smvm_snapshot_replay_seek_in_progress) != 0);
}

[[nodiscard]] bool CanConsumeManualCameraMouseInput(
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto& state = g_overlay;
    return !state.cinematic_start_ready.load(std::memory_order_acquire) &&
           state.manual_pointer_active.load(std::memory_order_acquire) &&
           CanConsumeFreeCameraMouseInput(
               state.menu_open.load(std::memory_order_acquire),
               state.manual_pointer_requested.load(std::memory_order_acquire),
               CanUseManualCamera(snapshot),
               CurrentFreeCameraInputReadiness(state.output_window),
               (snapshot.flags & smvm_snapshot_replay_seek_in_progress) != 0);
}

[[nodiscard]] bool ManualBindingOwnsKey(
    const SmvmSnapshotPayload& snapshot,
    const std::uint32_t key) noexcept {
    const auto modifiers = CurrentModifiers(key);
    return BindingOwnsKeyboardKey(snapshot.forward_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.backward_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.left_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.right_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.up_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.down_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.fast_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.precision_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.roll_left_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.roll_right_key, key, modifiers) ||
           BindingOwnsKeyboardKey(snapshot.roll_reset_key, key, modifiers);
}

void UpdateKeyState(const std::uint32_t key, const bool down) noexcept {
    if (key > 0 && key < g_overlay.key_down.size())
        g_overlay.key_down[key].store(down ? 1u : 0u, std::memory_order_release);
}

[[nodiscard]] bool EditorBindingOwnsKeyboardKey(
    const SmvmSnapshotPayload& snapshot,
    const std::uint32_t key) noexcept {
    const auto matches = [key](const std::uint32_t binding) noexcept {
        const auto base = binding & kSmvmInputBaseMask;
        return base > 0 && base <= 0xFFu && base == key &&
               RequiredInputModifiers(binding) == CurrentModifiers(base);
    };
    return matches(snapshot.menu_key) || matches(snapshot.add_key) ||
           matches(snapshot.delete_key) || matches(snapshot.clean_view_key) ||
           matches(snapshot.play_start_key) || matches(snapshot.play_current_key) ||
           matches(snapshot.stop_key) || matches(snapshot.undo_key) ||
           matches(snapshot.redo_key) || matches(snapshot.show_path_key) ||
           matches(snapshot.show_cameras_key) || matches(snapshot.show_labels_key) ||
           matches(snapshot.restore_ui_key) || matches(snapshot.cycle_ui_key) ||
           matches(snapshot.toggle_free_camera_key) || matches(snapshot.replay_pause_key) ||
           matches(snapshot.step_back_key) || matches(snapshot.step_forward_key);
}

void TrackConsumedKeyDown(const std::uint32_t key, const std::uint8_t route) noexcept {
    if (key > 0 && key < g_overlay.menu_key_routes.size())
        g_overlay.menu_key_routes[key].fetch_or(route, std::memory_order_acq_rel);
}

[[nodiscard]] bool ConsumeTrackedKeyUp(
    const std::uint32_t key,
    const std::uint8_t route) noexcept {
    if (key == 0 || key >= g_overlay.menu_key_routes.size())
        return false;
    const auto previous = g_overlay.menu_key_routes[key].fetch_and(
        static_cast<std::uint8_t>(~route), std::memory_order_acq_rel);
    return (previous & route) != 0;
}

[[nodiscard]] bool WasKeyDownConsumed(
    const std::uint32_t key,
    const std::uint8_t route) noexcept {
    return key > 0 && key < g_overlay.menu_key_routes.size() &&
           (g_overlay.menu_key_routes[key].load(std::memory_order_acquire) & route) != 0;
}

void ResetCinematicStartGate(const bool reset_consumed_space = false) noexcept {
    auto& state = g_overlay;
    state.cinematic_start_armed.store(false, std::memory_order_release);
    state.cinematic_start_ready.store(false, std::memory_order_release);
    state.cinematic_space_released.store(false, std::memory_order_release);
    state.cinematic_start_tick.store(-1, std::memory_order_release);
    state.cinematic_replay_session_generation.store(0, std::memory_order_release);
    if (reset_consumed_space)
        state.cinematic_space_consumed.store(false, std::memory_order_release);
}

void UpdateCinematicStartGate(
    const SmvmSnapshotPayload& snapshot,
    const bool has_path,
    const CampathPayloadHeader& header,
    const CampathKeyframe* keyframes) noexcept {
    auto& state = g_overlay;
    const auto armed = state.cinematic_start_armed.load(std::memory_order_acquire);
    if (!armed)
        return;
    if (snapshot.replay_session_generation == 0 ||
        snapshot.replay_session_generation !=
            state.cinematic_replay_session_generation.load(std::memory_order_acquire)) {
        ResetCinematicStartGate();
        return;
    }

    const auto count = has_path
        ? std::min<std::uint32_t>(header.keyframe_count, kMaxCampathKeyframes)
        : 0u;
    const auto target = state.cinematic_start_tick.load(std::memory_order_acquire);
    const auto playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
    if (!has_path || keyframes == nullptr || count < 3 ||
        keyframes[0].demo_tick != target || playing) {
        ResetCinematicStartGate();
        return;
    }

    const auto free_camera_ready =
        (snapshot.flags & smvm_snapshot_camera_readable) != 0 &&
        (snapshot.flags & smvm_snapshot_manual_camera_requested) != 0 &&
        (snapshot.flags & smvm_snapshot_manual_camera_active) != 0 &&
        snapshot.observer_mode == 4;
    const auto ready = IsCinematicStartPromptReady(
        true,
        has_path,
        count,
        snapshot.current_tick,
        keyframes[0].demo_tick,
        target,
        free_camera_ready,
        playing);
    const auto was_ready = state.cinematic_start_ready.exchange(
        ready, std::memory_order_acq_rel);
    if (ready && !was_ready) {
        const auto space_is_up =
            state.key_down[VK_SPACE].load(std::memory_order_acquire) == 0;
        state.cinematic_space_released.store(space_is_up, std::memory_order_release);
    } else if (!ready) {
        state.cinematic_space_released.store(false, std::memory_order_release);
    }
}

[[nodiscard]] bool HandleCinematicStartSpace(
    const std::uint32_t key,
    const bool down,
    const bool repeated,
    const std::uint8_t route) noexcept {
    auto& state = g_overlay;
    if (key != VK_SPACE)
        return false;

    const auto ready = state.cinematic_start_ready.load(std::memory_order_acquire);
    const auto consumed = state.cinematic_space_consumed.load(std::memory_order_acquire);
    if (!ready && !consumed)
        return false;

    if (!down) {
        state.cinematic_space_released.store(true, std::memory_order_release);
        if (state.cinematic_space_consumed.exchange(false, std::memory_order_acq_rel)) {
            static_cast<void>(ConsumeTrackedKeyUp(key, route));
            return true;
        }
        return ready;
    }

    TrackConsumedKeyDown(key, route);
    const auto released = state.cinematic_space_released.load(std::memory_order_acquire);
    if (!ShouldStartCinematicForSpaceEvent(
            ready,
            released,
            down,
            repeated,
            consumed)) {
        return true;
    }

    state.cinematic_space_released.store(false, std::memory_order_release);
    state.cinematic_space_consumed.store(true, std::memory_order_release);
    SmvmSnapshotPayload snapshot{};
    const auto replay_session_generation =
        state.cinematic_replay_session_generation.load(std::memory_order_acquire);
    if (ReadSnapshot(snapshot) &&
        snapshot.replay_session_generation == replay_session_generation &&
        QueueActionForSnapshot(snapshot, SmvmActionType::play_from_start))
        ResetCinematicStartGate();
    else
        ResetCinematicStartGate();
    return true;
}

[[nodiscard]] bool WasKeyDeliveredBeforeMenu(
    const std::uint32_t key,
    const std::uint8_t route) noexcept {
    return key > 0 && key < g_overlay.menu_preheld_keys.size() &&
           (g_overlay.menu_preheld_keys[key].load(std::memory_order_acquire) & route) != 0;
}

void ClearPreMenuKeyRoute(const std::uint32_t key, const std::uint8_t route) noexcept {
    if (key > 0 && key < g_overlay.menu_preheld_keys.size())
        g_overlay.menu_preheld_keys[key].fetch_and(
            static_cast<std::uint8_t>(~route), std::memory_order_acq_rel);
}

[[nodiscard]] int WindowMouseButtonIndex(const UINT message, const WPARAM wparam) noexcept {
    if (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP) return 0;
    if (message == WM_RBUTTONDOWN || message == WM_RBUTTONUP) return 1;
    if (message == WM_MBUTTONDOWN || message == WM_MBUTTONUP) return 2;
    if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP)
        return GET_XBUTTON_WPARAM(wparam) == XBUTTON1 ? 3 : 4;
    return -1;
}

[[nodiscard]] bool TrackConsumedMouseDown(
    const UINT message,
    const WPARAM wparam,
    const std::uint8_t route) noexcept {
    const auto index = WindowMouseButtonIndex(message, wparam);
    return index >= 0 &&
        g_overlay.menu_mouse_routes[static_cast<std::size_t>(index)].Claim(route);
}

[[nodiscard]] bool ConsumeTrackedMouseUp(
    const UINT message,
    const WPARAM wparam,
    const std::uint8_t route) noexcept {
    const auto index = WindowMouseButtonIndex(message, wparam);
    if (index < 0)
        return false;
    return g_overlay.menu_mouse_routes[static_cast<std::size_t>(index)].Release(route);
}

[[nodiscard]] bool TrackConsumedMouseIndex(
    const std::size_t index,
    const std::uint8_t route) noexcept {
    return index < g_overlay.menu_mouse_routes.size() &&
        g_overlay.menu_mouse_routes[index].Claim(route);
}

[[nodiscard]] bool ConsumeTrackedMouseIndex(
    const std::size_t index,
    const std::uint8_t route) noexcept {
    if (index >= g_overlay.menu_mouse_routes.size())
        return false;
    return g_overlay.menu_mouse_routes[index].Release(route);
}

[[nodiscard]] bool HasTrackedMouseDown(const UINT message, const WPARAM wparam) noexcept {
    const auto index = WindowMouseButtonIndex(message, wparam);
    return index >= 0 &&
        g_overlay.menu_mouse_routes[static_cast<std::size_t>(index)].HasAny();
}

[[nodiscard]] bool EditorBindingOwnsMouseCode(
    const SmvmSnapshotPayload& snapshot,
    const SmvmInputCode code) noexcept {
    const auto matches = [code](const std::uint32_t binding) noexcept {
        return (binding & kSmvmInputBaseMask) == static_cast<std::uint32_t>(code) &&
               RequiredInputModifiers(binding) == CurrentModifiers();
    };
    return matches(snapshot.menu_key) || matches(snapshot.add_key) ||
           matches(snapshot.delete_key) || matches(snapshot.clean_view_key) ||
           matches(snapshot.play_start_key) || matches(snapshot.play_current_key) ||
           matches(snapshot.stop_key) || matches(snapshot.undo_key) ||
           matches(snapshot.redo_key) || matches(snapshot.show_path_key) ||
           matches(snapshot.show_cameras_key);
}

void ResetConsumedReleaseRoutes() noexcept {
    for (auto& routes : g_overlay.menu_key_routes)
        routes.store(0, std::memory_order_release);
    for (auto& routes : g_overlay.menu_mouse_routes)
        routes.Reset();
    for (auto& routes : g_overlay.menu_preheld_keys)
        routes.store(0, std::memory_order_release);
    for (auto& queued : g_overlay.manual_shortcut_queued)
        queued.store(false, std::memory_order_release);
}

[[nodiscard]] bool RouteManualKeyboardEvent(
    const SmvmSnapshotPayload& snapshot,
    const std::uint32_t key,
    const bool down,
    const std::uint8_t route,
    const bool takeover) noexcept {
    if (key == 0 || key >= g_overlay.manual_key_routes.size())
        return false;
    auto& routes = g_overlay.manual_key_routes[key];
    if (!down) {
        const auto previous = routes.fetch_and(static_cast<std::uint8_t>(~route), std::memory_order_acq_rel);
        return takeover && (previous & route) != 0;
    }
    if (!takeover || !ManualBindingOwnsKey(snapshot, key)) {
        routes.fetch_and(static_cast<std::uint8_t>(~route), std::memory_order_acq_rel);
        return false;
    }
    routes.fetch_or(route, std::memory_order_acq_rel);
    return true;
}

[[nodiscard]] bool HandleManualCameraShortcut(
    const SmvmSnapshotPayload& snapshot,
    const std::uint32_t key,
    const bool down,
    const std::uint8_t route,
    const bool menu_open) noexcept {
    if (key == 0 || key >= g_overlay.manual_shortcut_queued.size())
        return false;
    auto& shortcut_queued = g_overlay.manual_shortcut_queued[key];
    if (!down) {
        shortcut_queued.store(false, std::memory_order_release);
        return false;
    }
    const auto action = ResolveManualCameraShortcut(
        InputMatchesKeyboard(snapshot.toggle_free_camera_key, key),
        key == VK_ESCAPE,
        menu_open,
        (snapshot.flags & smvm_snapshot_replay_active) != 0,
        (snapshot.flags & smvm_snapshot_campath_playing) != 0,
        (snapshot.flags & smvm_snapshot_camera_owned) != 0,
        snapshot.camera_ownership == CameraOwnership::smvm_restore ||
            snapshot.camera_ownership == CameraOwnership::smvm_campath);
    if (action == ManualCameraShortcutAction::none)
        return false;

    TrackConsumedKeyDown(key, route);
    if (shortcut_queued.load(std::memory_order_acquire))
        return true;

    if (action == ManualCameraShortcutAction::exit) {
        ResetCinematicStartGate();
        if (QueueActionForSnapshot(snapshot, SmvmActionType::toggle_manual_camera)) {
            shortcut_queued.store(true, std::memory_order_release);
            ResetSmvmManualInput();
            ClearManualInputReadiness();
        }
    } else if (action == ManualCameraShortcutAction::enter_or_reacquire) {
        if (QueueActionForSnapshot(snapshot, SmvmActionType::reacquire_camera))
            shortcut_queued.store(true, std::memory_order_release);
    } else if (action == ManualCameraShortcutAction::consume) {
        // Keep a consumed F2 latched until physical key-up. Otherwise an
        // auto-repeat can cross the path/restore-to-manual handoff and enqueue
        // a stale reacquire after full ownership has already been released.
        shortcut_queued.store(true, std::memory_order_release);
    }
    return true;
}

[[nodiscard]] bool HandleSmvmMouseBinding(
    UINT message,
    WPARAM wparam,
    const SmvmSnapshotPayload& snapshot) noexcept;

void RefreshMenuCursorPosition(const HWND window) noexcept {
    POINT cursor{};
    if (window == nullptr || !GetCursorPos(&cursor) || !ScreenToClient(window, &cursor))
        return;
    g_overlay.mouse_x.store(cursor.x, std::memory_order_relaxed);
    g_overlay.mouse_y.store(cursor.y, std::memory_order_relaxed);
}

[[nodiscard]] bool IsReplayTimelineVisible(const SmvmSnapshotPayload& snapshot) noexcept {
    return ShouldShowReplayTimeline(
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0,
        (snapshot.flags & smvm_snapshot_replay_active) != 0,
        snapshot.deadlock_ui_mode == DeadlockUiMode::smvm_replay_ui);
}

[[nodiscard]] bool HandleRawInput(
    const HWND window,
    const LPARAM lparam,
    const SmvmSnapshotPayload& snapshot,
    const bool has_snapshot,
    const bool menu_open) noexcept {
    std::array<std::byte, 1024> storage{};
    UINT size = static_cast<UINT>(storage.size());
    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lparam), RID_INPUT, storage.data(), &size,
                        sizeof(RAWINPUTHEADER)) == static_cast<UINT>(-1) || size > storage.size())
        return false;
    const auto* input = reinterpret_cast<const RAWINPUT*>(storage.data());
    const auto camera_available = has_snapshot && !menu_open && CanUseManualCamera(snapshot);
    const auto manual_mouse = camera_available && CanConsumeManualCameraMouseInput(snapshot);
    const auto manual_keyboard =
        camera_available && CanConsumeManualCameraKeyboardInput(snapshot);
    const auto input_takeover = has_snapshot && !menu_open && HasSmvmCameraInputTakeover(
        (snapshot.flags & smvm_snapshot_camera_owned) != 0,
        (snapshot.flags & smvm_snapshot_input_takeover) != 0);
    const auto timeline_visible = has_snapshot && IsReplayTimelineVisible(snapshot);
    if (input->header.dwType == RIM_TYPEMOUSE) {
        // Free Cam registers raw mouse input without the corresponding legacy
        // WM_MOUSEMOVE stream. Sample the real client cursor for menu hit tests;
        // otherwise a raw click is evaluated at the last pre-Free-Cam position
        // and can activate the wrong control (or fall through to Deadlock).
        if (menu_open)
            RefreshMenuCursorPosition(window);
        const auto button_flags = input->data.mouse.usButtonFlags;
        constexpr std::array<USHORT, 5> down_flags{
            RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_DOWN,
            RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_5_DOWN};
        constexpr std::array<USHORT, 5> up_flags{
            RI_MOUSE_LEFT_BUTTON_UP, RI_MOUSE_RIGHT_BUTTON_UP,
            RI_MOUSE_MIDDLE_BUTTON_UP, RI_MOUSE_BUTTON_4_UP, RI_MOUSE_BUTTON_5_UP};
        constexpr std::array<SmvmInputCode, 5> input_codes{
            SmvmInputCode::none, SmvmInputCode::none, SmvmInputCode::mouse_middle,
            SmvmInputCode::mouse_x1, SmvmInputCode::mouse_x2};

        auto contains_untracked_release = false;
        auto owns_editor_button = false;
        auto contains_unowned_button = false;
        for (std::size_t index = 0; index < down_flags.size(); ++index) {
            if ((button_flags & down_flags[index]) != 0) {
                if (menu_open) {
                    // Button state reaches ImGui through the consumed-route
                    // tracking (FeedImguiMouse); only binding capture needs
                    // the first delivery here.
                    const auto first_delivery = TrackConsumedMouseIndex(index, kMenuRawKeyRoute);
                    if (first_delivery && has_snapshot && input_codes[index] != SmvmInputCode::none) {
                        const auto message = input_codes[index] == SmvmInputCode::mouse_middle
                            ? WM_MBUTTONDOWN
                            : WM_XBUTTONDOWN;
                        const auto wparam = input_codes[index] == SmvmInputCode::mouse_x2
                            ? MAKEWPARAM(0, XBUTTON2)
                            : MAKEWPARAM(0, XBUTTON1);
                        static_cast<void>(CaptureMouseBinding(message, wparam));
                    }
                    owns_editor_button = true;
                } else {
                    auto handled = false;
                    if (has_snapshot && input_codes[index] != SmvmInputCode::none &&
                        !g_overlay.menu_mouse_routes[index].HasAny()) {
                        const auto message = input_codes[index] == SmvmInputCode::mouse_middle
                            ? WM_MBUTTONDOWN
                            : WM_XBUTTONDOWN;
                        const auto wparam = input_codes[index] == SmvmInputCode::mouse_x2
                            ? MAKEWPARAM(0, XBUTTON2)
                            : MAKEWPARAM(0, XBUTTON1);
                        handled = HandleSmvmMouseBinding(message, wparam, snapshot);
                    }
                    if (handled || (input_takeover && input_codes[index] != SmvmInputCode::none &&
                                    EditorBindingOwnsMouseCode(snapshot, input_codes[index]))) {
                        static_cast<void>(TrackConsumedMouseIndex(index, kMenuRawKeyRoute));
                        owns_editor_button = true;
                    } else {
                        contains_unowned_button = true;
                    }
                }
            }
            if ((button_flags & up_flags[index]) != 0) {
                const auto tracked = ConsumeTrackedMouseIndex(index, kMenuRawKeyRoute);
                owns_editor_button = owns_editor_button || tracked;
                contains_untracked_release = contains_untracked_release || !tracked;
                contains_unowned_button = contains_unowned_button || !tracked;
            }
        }
        const auto wheel_delta = (button_flags & RI_MOUSE_WHEEL) != 0
            ? static_cast<SHORT>(input->data.mouse.usButtonData)
            : 0;
        if (menu_open) {
            if (wheel_delta != 0) {
                g_overlay.raw_wheel_consumed_ms.store(GetTickCount64(), std::memory_order_release);
                const auto wheel_wparam = MAKEWPARAM(0, static_cast<WORD>(wheel_delta));
                if (!has_snapshot || !CaptureMouseBinding(WM_MOUSEWHEEL, wheel_wparam))
                    PushUiEvent(smvm_ui_event_wheel, wheel_delta);
            }
            return !contains_untracked_release;
        }

        const auto relative_motion =
            (input->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0 &&
            (input->data.mouse.lLastX != 0 || input->data.mouse.lLastY != 0);
        if (manual_mouse && relative_motion) {
            const auto delta = NormalizeRawMouseDelta(
                input->data.mouse.lLastX, input->data.mouse.lLastY);
            if (delta.look_right != 0 || delta.look_up != 0) {
                g_overlay.manual_look_right_delta.fetch_add(
                    delta.look_right, std::memory_order_release);
                g_overlay.manual_look_up_delta.fetch_add(delta.look_up, std::memory_order_release);
                g_overlay.manual_mouse_observed.store(true, std::memory_order_release);
                g_overlay.manual_raw_mouse_observed_ms.store(
                    GetTickCount64(), std::memory_order_release);
            }
        }
        if (input_takeover && wheel_delta != 0) {
            g_overlay.raw_wheel_consumed_ms.store(GetTickCount64(), std::memory_order_release);
            if (manual_mouse)
                g_overlay.manual_wheel_delta.fetch_add(wheel_delta, std::memory_order_release);
        }
        const auto contains_horizontal_wheel = (button_flags & RI_MOUSE_HWHEEL) != 0;
        contains_unowned_button = contains_unowned_button || contains_horizontal_wheel;
        // A RAWINPUT packet cannot be split. While SMVM owns the camera it also
        // owns every normal mouse button, wheel, and motion packet so Deadlock
        // gameplay and observer controls cannot run alongside Free Camera.
        if (input_takeover)
            return true;
        return !contains_unowned_button && owns_editor_button;
    }
    if (input->header.dwType == RIM_TYPEKEYBOARD) {
        const auto key = NormalizeRawVirtualKey(input->data.keyboard);
        const auto down = (input->data.keyboard.Flags & RI_KEY_BREAK) == 0;
        UpdateKeyState(key, down);
        if (HandleCinematicStartSpace(key, down, false, kMenuRawKeyRoute))
            return true;
        const auto reserved_menu_binding = has_snapshot && ShouldReserveEditorMenuBinding(
            true,
            (snapshot.flags & smvm_snapshot_replay_active) != 0,
            InputMatchesKeyboard(snapshot.menu_key, key));
        if (reserved_menu_binding) {
            if (down) {
                TrackConsumedKeyDown(key, kMenuRawKeyRoute);
            } else {
                ClearPreMenuKeyRoute(key, kMenuRawKeyRoute);
                static_cast<void>(ConsumeTrackedKeyUp(key, kMenuRawKeyRoute));
            }
            return true;
        }
        if (down && WasKeyDeliveredBeforeMenu(key, kMenuRawKeyRoute))
            return input_takeover;
        if (down && menu_open) {
            TrackConsumedKeyDown(key, kMenuRawKeyRoute);
            return true;
        }
        if (ReplayTimelineEditorOwnsKeyboard(
                menu_open,
                g_overlay.replay_tick_input_active.load(std::memory_order_acquire),
                timeline_visible)) {
            if (down)
                TrackConsumedKeyDown(key, kMenuRawKeyRoute);
            else
                static_cast<void>(ConsumeTrackedKeyUp(key, kMenuRawKeyRoute));
            return true;
        }
        const auto preheld_release = !down &&
            WasKeyDeliveredBeforeMenu(key, kMenuRawKeyRoute);
        if (!down) {
            static_cast<void>(HandleManualCameraShortcut(
                snapshot, key, false, kMenuRawKeyRoute, menu_open));
            ClearPreMenuKeyRoute(key, kMenuRawKeyRoute);
        }
        const auto tracked_release = !down && ConsumeTrackedKeyUp(key, kMenuRawKeyRoute);
        if (preheld_release)
            return false;
        if (down && HandleManualCameraShortcut(
                snapshot, key, down, kMenuRawKeyRoute, menu_open))
            return true;
        if (down && input_takeover && EditorBindingOwnsKeyboardKey(snapshot, key)) {
            TrackConsumedKeyDown(key, kMenuRawKeyRoute);
            return true;
        }
        const auto consumed = RouteManualKeyboardEvent(
            snapshot, key, down, kManualRawKeyRoute, manual_keyboard && input_takeover);
        const auto swallow_while_acquiring = !manual_keyboard && input_takeover &&
            ManualBindingOwnsKey(snapshot, key);
        const auto handled_by_smvm = tracked_release || consumed || swallow_while_acquiring;
        const auto suppress_gameplay = ShouldSuppressGameplayInput(
            input_takeover, handled_by_smvm, false);
        if (down && suppress_gameplay)
            TrackConsumedKeyDown(key, kMenuRawKeyRoute);
        return handled_by_smvm || suppress_gameplay;
    }
    return false;
}

[[nodiscard]] bool CanEditCampath(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
           (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
           (snapshot.flags & smvm_snapshot_camera_readable) != 0 &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0 &&
           HasSmvmManualCameraOwnership(
               (snapshot.flags & smvm_snapshot_manual_camera_requested) != 0,
               (snapshot.flags & smvm_snapshot_manual_camera_active) != 0) &&
           snapshot.observer_mode == 4;
}

[[nodiscard]] bool CanTriggerCampathPlayback(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
           (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
           (snapshot.flags & smvm_snapshot_camera_readable) != 0 &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0 &&
           snapshot.keyframe_count >= 3;
}

[[nodiscard]] bool CanTriggerCampathEditAction(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
           (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0 &&
           HasSmvmManualCameraOwnership(
               (snapshot.flags & smvm_snapshot_manual_camera_requested) != 0,
               (snapshot.flags & smvm_snapshot_manual_camera_active) != 0);
}

[[nodiscard]] bool HandleCampathMouseBinding(
    const UINT message,
    const WPARAM wparam,
    const SmvmSnapshotPayload& snapshot) noexcept {
    if (InputMatchesMouse(snapshot.play_start_key, message, wparam) &&
        CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(SmvmArmCinematicStart(snapshot.replay_session_generation));
        return true;
    }
    if (InputMatchesMouse(snapshot.play_current_key, message, wparam) &&
        CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::play_from_current));
        return true;
    }
    if (InputMatchesMouse(snapshot.stop_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_campath_playing) != 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::stop_campath));
        return true;
    }
    if (InputMatchesMouse(snapshot.undo_key, message, wparam) &&
        CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::undo_edit));
        return true;
    }
    if (InputMatchesMouse(snapshot.redo_key, message, wparam) &&
        CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::redo_edit));
        return true;
    }
    if (InputMatchesMouse(snapshot.show_path_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::toggle_show_path));
        return true;
    }
    if (InputMatchesMouse(snapshot.show_cameras_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::toggle_show_cameras));
        return true;
    }
    return false;
}

[[nodiscard]] bool HandleCampathKeyboardBinding(
    const WPARAM key,
    const SmvmSnapshotPayload& snapshot) noexcept {
    if (InputMatchesKeyboard(snapshot.cycle_ui_key, key) &&
        (snapshot.flags & smvm_snapshot_replay_active) != 0) {
        const auto current = static_cast<DeadlockUiMode>(
            g_overlay.presentation_mode.load(std::memory_order_acquire));
        const auto visible = current == DeadlockUiMode::clean_footage
            ? PreviousVisibleMode()
            : current;
        const auto target = visible == DeadlockUiMode::deadlock_ui
            ? DeadlockUiMode::smvm_replay_ui
            : DeadlockUiMode::deadlock_ui;
        static_cast<void>(RequestOwnerPresentationMode(target, snapshot));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.step_back_key, key) && snapshot.current_tick > 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::step_back));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.step_forward_key, key) && snapshot.current_tick >= 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::step_forward));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.play_start_key, key) && CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(SmvmArmCinematicStart(snapshot.replay_session_generation));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.play_current_key, key) && CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::play_from_current));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.stop_key, key) &&
        (snapshot.flags & smvm_snapshot_campath_playing) != 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::stop_campath));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.undo_key, key) && CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::undo_edit));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.redo_key, key) && CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::redo_edit));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.show_path_key, key) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::toggle_show_path));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.show_cameras_key, key) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::toggle_show_cameras));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.show_labels_key, key) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::toggle_show_labels));
        return true;
    }
    return false;
}

[[nodiscard]] bool HandleMovieMakerKeyboardShortcut(
    const std::uint32_t key,
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto fixed_pause_key = key == 'N' && CurrentModifiers(key) == 0;
    const auto decrease = key == VK_OEM_MINUS || key == VK_SUBTRACT;
    const auto increase = key == VK_OEM_PLUS || key == VK_ADD;
    const auto action = ResolveMovieMakerShortcut(
        fixed_pause_key,
        InputMatchesKeyboard(snapshot.replay_pause_key, key),
        decrease,
        increase,
        (snapshot.flags & smvm_snapshot_replay_active) != 0,
        CanUseManualCamera(snapshot),
        (CurrentModifiers(key) & ~4u) == 0);
    if (action == MovieMakerShortcutAction::toggle_replay_pause) {
        static_cast<void>(QueueActionForSnapshot(snapshot, SmvmActionType::toggle_replay_pause));
        return true;
    }
    if (action == MovieMakerShortcutAction::none)
        return false;
    return QueueActionForSnapshot(
        snapshot,
        SmvmActionType::set_movement_speed,
        -1,
        -1,
        AdjustManualCameraSpeed(
            snapshot.movement_speed,
            action == MovieMakerShortcutAction::increase_camera_speed));
}

[[nodiscard]] bool HandleSmvmMouseBinding(
    const UINT message,
    const WPARAM wparam,
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto menu_open = g_overlay.menu_open.load(std::memory_order_acquire);
    if (InputMatchesMouse(snapshot.menu_key, message, wparam) &&
        ((snapshot.flags & smvm_snapshot_replay_active) != 0 || menu_open)) {
        if (g_overlay.clean_view.load(std::memory_order_acquire))
            static_cast<void>(ToggleCleanFootage(true, snapshot));
        else
            SetMenuOpen(!menu_open);
        return true;
    }
    if (menu_open)
        return false;
    if (InputMatchesMouse(snapshot.add_key, message, wparam)) {
        QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::input_observed);
        QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::binding_matched);
        const auto rejection = CaptureRejectionFromSnapshot(snapshot);
        if (rejection != SmvmCaptureRejection::none) {
            QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::capture_rejected, rejection);
            return true;
        }
        QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::capture_requested);
        if (g_overlay.callbacks.request_camera_capture != nullptr) {
            g_overlay.callbacks.request_camera_capture(
                g_overlay.callbacks.context,
                snapshot.replay_session_generation);
        } else {
            QueueCaptureDiagnostic(
                snapshot,
                SmvmCaptureStage::capture_rejected,
                SmvmCaptureRejection::native_backend_unavailable);
        }
        return true;
    }
    if (InputMatchesMouse(snapshot.delete_key, message, wparam) && CanEditCampath(snapshot)) {
        return QueueActionForSnapshot(
            snapshot,
            SmvmActionType::delete_keyframe,
            snapshot.selected_keyframe);
    }
    if (HandleCampathMouseBinding(message, wparam, snapshot))
        return true;
    if (InputMatchesMouse(snapshot.clean_view_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
        (snapshot.flags & smvm_snapshot_replay_active) != 0) {
        static_cast<void>(ToggleCleanFootage(false, snapshot));
        return true;
    }
    return false;
}

LRESULT CALLBACK SmvmWindowProcedure(
    const HWND window,
    const UINT message,
    const WPARAM wparam,
    const LPARAM lparam) noexcept {
    auto& state = g_overlay;
    ActiveCallbackGuard callback_guard(state.active_window_procedures);
    const auto original = state.original_window_proc;
    if (original == nullptr)
        return DefWindowProcW(window, message, wparam, lparam);

    if (message == kSmvmCursorTransitionMessage) {
        const auto should_open =
            !state.stop_requested.load(std::memory_order_acquire) && wparam != FALSE;
        auto transition_ok = ApplyMenuTransitionOnWindowThread(window, should_open);
        if (!transition_ok && should_open) {
            state.menu_open.store(false, std::memory_order_release);
            ResetSmvmManualInput();
            static_cast<void>(SetModalInputMaintenanceOnWindowThread(window, false));
            static_cast<void>(ApplyCursorStateOnWindowThread(false));
        }
        PublishStatus(
            SmvmRendererBackend::d3d11,
            transition_ok ? SmvmRendererError::none : SmvmRendererError::window_hook_failed);
        return transition_ok ? TRUE : FALSE;
    }
    if (message == kSmvmManualPointerTransitionMessage) {
        const auto enabled = !state.stop_requested.load(std::memory_order_acquire) && wparam != FALSE;
        const auto transition_ok = ApplyManualPointerStateOnWindowThread(window, enabled);
        if (enabled && !transition_ok) {
            state.manual_input_error.store(true, std::memory_order_release);
            SetMenuOpen(true);
        }
        PublishStatus(
            SmvmRendererBackend::d3d11,
            transition_ok ? SmvmRendererError::none : SmvmRendererError::window_hook_failed);
        return transition_ok ? TRUE : FALSE;
    }
    if (message == WM_TIMER && wparam == kSmvmModalInputTimerId) {
        if (!state.menu_open.load(std::memory_order_acquire)) {
            static_cast<void>(SetModalInputMaintenanceOnWindowThread(window, false));
            return 0;
        }

        // Spectator-mode transitions can re-enable SDL relative mode, Raw
        // Input, and IInputSystem while the modal editor remains open. Keep
        // the already-snapshotted state withdrawn before the next physical
        // event instead of racing that event from the external low-level hook.
        if (!ApplyCursorStateOnWindowThread(true)) {
            state.menu_open.store(false, std::memory_order_release);
            ResetSmvmManualInput();
            static_cast<void>(SetModalInputMaintenanceOnWindowThread(window, false));
            static_cast<void>(ApplyCursorStateOnWindowThread(false));
            PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::window_hook_failed);
        }
        return 0;
    }
    if (message == kSmvmPointerActionMessage) {
        if (state.stop_requested.load(std::memory_order_acquire) ||
            !state.menu_open.load(std::memory_order_acquire))
            return FALSE;

        POINT client_point{GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
        RECT client_rect{};
        if (!ScreenToClient(window, &client_point) || !GetClientRect(window, &client_rect) ||
            client_point.x < client_rect.left || client_point.x >= client_rect.right ||
            client_point.y < client_rect.top || client_point.y >= client_rect.bottom)
            return FALSE;

        state.mouse_x.store(client_point.x, std::memory_order_relaxed);
        state.mouse_y.store(client_point.y, std::memory_order_relaxed);
        const auto action = static_cast<SmvmPointerAction>(LOWORD(wparam));
        if (action == SmvmPointerAction::validate)
            return TRUE;

        SmvmSnapshotPayload pointer_snapshot{};
        const auto has_pointer_snapshot = ReadSnapshot(pointer_snapshot) &&
            (pointer_snapshot.flags & smvm_snapshot_internal_enabled) != 0;
        // The managed pointer channel delivers full clicks; replay them into
        // ImGui as a down+up pair through the UI event ring.
        if (action == SmvmPointerAction::left_click) {
            PushUiEvent(smvm_ui_event_click, 0);
            return TRUE;
        }
        if (action == SmvmPointerAction::right_click) {
            PushUiEvent(smvm_ui_event_click, 1);
            return TRUE;
        }

        UINT synthetic_message = 0;
        WPARAM synthetic_wparam = 0;
        if (action == SmvmPointerAction::middle_click) {
            synthetic_message = WM_MBUTTONDOWN;
        } else if (action == SmvmPointerAction::x1_click) {
            synthetic_message = WM_XBUTTONDOWN;
            synthetic_wparam = MAKEWPARAM(0, XBUTTON1);
        } else if (action == SmvmPointerAction::x2_click) {
            synthetic_message = WM_XBUTTONDOWN;
            synthetic_wparam = MAKEWPARAM(0, XBUTTON2);
        } else if (action == SmvmPointerAction::wheel) {
            const auto delta = static_cast<SHORT>(HIWORD(wparam));
            if (delta == 0)
                return FALSE;
            synthetic_message = WM_MOUSEWHEEL;
            synthetic_wparam = MAKEWPARAM(0, static_cast<WORD>(delta));
        } else {
            return FALSE;
        }

        if (has_pointer_snapshot && CaptureMouseBinding(synthetic_message, synthetic_wparam))
            return TRUE;
        if (has_pointer_snapshot &&
            HandleSmvmMouseBinding(synthetic_message, synthetic_wparam, pointer_snapshot))
            return TRUE;
        if (action == SmvmPointerAction::wheel) {
            PushUiEvent(smvm_ui_event_wheel, static_cast<SHORT>(HIWORD(wparam)));
        }
        return TRUE;
    }
    if (message == kSmvmRestoreWindowProcedureMessage) {
        if (!ApplyManualPointerStateOnWindowThread(window, false) ||
            !ApplyCursorStateOnWindowThread(false) || PointerStateRestorePending())
            return FALSE;
        if (window != state.output_window)
            return FALSE;
        const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC));
        if (current == original)
            return TRUE;
        // A later subclass may have installed above SMVM and retained this
        // procedure as its predecessor. Bypassing that chain would leave its
        // saved predecessor dangling, so fail closed and keep the DLL resident.
        if (current != &SmvmWindowProcedure)
            return FALSE;
        SetLastError(ERROR_SUCCESS);
        const auto previous = SetWindowLongPtrW(
            window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(original));
        if (previous == 0 && GetLastError() != ERROR_SUCCESS)
            return FALSE;
        return reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC)) == original
            ? TRUE
            : FALSE;
    }

    if (message == WM_NCDESTROY) {
        state.menu_open.store(false, std::memory_order_release);
        static_cast<void>(SetModalInputMaintenanceOnWindowThread(window, false));
        static_cast<void>(ApplyCursorStateOnWindowThread(false));
    }
    if (state.stop_requested.load(std::memory_order_acquire))
        return CallWindowProcW(original, window, message, wparam, lparam);
    if (!state.menu_open.load(std::memory_order_acquire) && MenuPointerStateRestorePending()) {
        const auto restored = ApplyCursorStateOnWindowThread(false);
        if (!restored)
            PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::window_hook_failed);
    }

    if (message == WM_KILLFOCUS ||
        (message == WM_ACTIVATE && LOWORD(wparam) == WA_INACTIVE) ||
        (message == WM_ACTIVATEAPP && wparam == FALSE)) {
        ResetSmvmManualInput();
        ResetConsumedReleaseRoutes();
        SuspendManualPointerForFocusLoss(window);
    }
    if ((message == WM_SETFOCUS ||
         (message == WM_ACTIVATEAPP && wparam != FALSE)) &&
        state.manual_pointer_requested.load(std::memory_order_acquire) &&
        !state.menu_open.load(std::memory_order_acquire))
        static_cast<void>(ApplyManualPointerStateOnWindowThread(window, true));

    SmvmSnapshotPayload snapshot{};
    const auto has_snapshot = ReadSnapshot(snapshot) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0;
    auto menu_open = state.menu_open.load(std::memory_order_acquire);
    const auto camera_input_takeover = has_snapshot && !menu_open &&
        HasSmvmCameraInputTakeover(
            (snapshot.flags & smvm_snapshot_camera_owned) != 0,
            (snapshot.flags & smvm_snapshot_input_takeover) != 0);
    const auto timeline_visible = has_snapshot && IsReplayTimelineVisible(snapshot);
    const auto replay_tick_editor_owns_keyboard = ReplayTimelineEditorOwnsKeyboard(
        menu_open,
        state.replay_tick_input_active.load(std::memory_order_acquire),
        timeline_visible);
    const auto key_down_message = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    const auto normalized_key =
        (key_down_message || message == WM_KEYUP || message == WM_SYSKEYUP)
        ? NormalizeWindowVirtualKey(wparam, lparam)
        : static_cast<std::uint32_t>(wparam);
    const auto repeated_key = key_down_message && (lparam & (1LL << 30)) != 0;
    const auto restore_pressed = key_down_message && !repeated_key &&
        (normalized_key == VK_F9 ||
         (has_snapshot && InputMatchesKeyboard(snapshot.restore_ui_key, normalized_key)));
    if (restore_pressed) {
        TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
        ResetCinematicStartGate(true);
        SetMenuOpen(false);
        RequestManualPointerState(false);
        SetLocalPresentationMode(DeadlockUiMode::deadlock_ui);
        const auto recovery_generation = BeginRecordingProfileRecovery(
            RecordingProfileRecoveryKind::explicit_restore,
            DeadlockUiMode::deadlock_ui);
        if (recovery_generation != 0 && QueueAction(
                SmvmActionType::restore_deadlock_ui,
                -1,
                static_cast<std::int64_t>(recovery_generation))) {
            MarkRecordingProfileRecoveryActionQueued(recovery_generation);
        }
        // F9 is the hard emergency route: restore the complete Deadlock
        // presentation profile locally even when managed action delivery
        // succeeds, because that action may be serialized behind a seek or
        // self-test.
        RequestEmergencyDeadlockUiRestore(true);
        PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::none);
        return 0;
    }
    if (!has_snapshot) {
        if (menu_open) {
            SetMenuOpen(false);
            menu_open = false;
        }
        ResetSmvmManualInput();
    }
    if (!has_snapshot &&
        IsBindingActionIndex(state.binding_capture_action.load(std::memory_order_acquire)))
        ResetBindingCaptureState();

    // Never turn the editor into an input trap. System close remains owned by
    // Deadlock/Windows even while the SMVM menu is open.
    if ((message == WM_SYSKEYDOWN || message == WM_KEYDOWN) && wparam == VK_F4 &&
        (GetKeyState(VK_MENU) & 0x8000) != 0 &&
        IsBindingActionIndex(state.binding_capture_action.load(std::memory_order_acquire)))
        state.binding_modifier_chord_used.fetch_or(2u, std::memory_order_acq_rel);
    if ((message == WM_SYSKEYDOWN || message == WM_SYSKEYUP) && wparam == VK_F4)
        return CallWindowProcW(original, window, message, wparam, lparam);
    if ((message == WM_KEYDOWN || message == WM_KEYUP) && wparam == VK_F4 &&
        (GetKeyState(VK_MENU) & 0x8000) != 0)
        return CallWindowProcW(original, window, message, wparam, lparam);

    if (message == WM_INPUT && HandleRawInput(window, lparam, snapshot, has_snapshot, menu_open)) {
        // Consumed foreground raw input must still reach DefWindowProc so
        // Windows can release its per-message raw-input resources. Do not call
        // Deadlock's predecessor: takeover intentionally owns this packet.
        if (GET_RAWINPUT_CODE_WPARAM(wparam) == RIM_INPUT)
            static_cast<void>(DefWindowProcW(window, message, wparam, lparam));
        return 0;
    }
    if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
        UpdateKeyState(normalized_key, true);
    if (message == WM_KEYUP || message == WM_SYSKEYUP)
        UpdateKeyState(normalized_key, false);

    if ((key_down_message || message == WM_KEYUP || message == WM_SYSKEYUP) &&
        HandleCinematicStartSpace(
            normalized_key,
            key_down_message,
            repeated_key,
            kMenuWindowKeyRoute)) {
        return 0;
    }

    const auto window_key_down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    const auto window_key_up = message == WM_KEYUP || message == WM_SYSKEYUP;
    const auto preheld_window_key_down = window_key_down &&
        WasKeyDeliveredBeforeMenu(normalized_key, kMenuWindowKeyRoute);
    if (window_key_up)
        ClearPreMenuKeyRoute(normalized_key, kMenuWindowKeyRoute);
    const auto window_mouse_down = message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN ||
        message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN;
    const auto window_mouse_up = message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
        message == WM_MBUTTONUP || message == WM_XBUTTONUP;
    if (window_key_down && MenuMayClaimKeyDown(menu_open, preheld_window_key_down))
        TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
    const auto mouse_down_already_tracked = window_mouse_down && HasTrackedMouseDown(message, wparam);
    const auto first_window_mouse_down = window_mouse_down && menu_open &&
        TrackConsumedMouseDown(message, wparam, kMenuWindowKeyRoute);
    const auto legacy_wheel_duplicate = message == WM_MOUSEWHEEL &&
        GetTickCount64() - state.raw_wheel_consumed_ms.load(std::memory_order_acquire) <= 8;
    if (preheld_window_key_down) {
        if (camera_input_takeover)
            return 0;
        return CallWindowProcW(original, window, message, wparam, lparam);
    }

    const auto menu_binding_matches = has_snapshot &&
        InputMatchesKeyboard(snapshot.menu_key, normalized_key);
    if (ReplayTimelineTextInputConsumesKey(
            replay_tick_editor_owns_keyboard,
            menu_binding_matches) &&
        window_key_down) {
        TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
        if (UiFeedingAllowed(menu_open))
            PushUiEvent(smvm_ui_event_key, static_cast<std::int32_t>(normalized_key), 1);
        return 0;
    }

    if (message == WM_MOUSEMOVE) {
        const auto mouse_x = static_cast<std::int32_t>(GET_X_LPARAM(lparam));
        const auto mouse_y = static_cast<std::int32_t>(GET_Y_LPARAM(lparam));
        state.mouse_x.store(mouse_x, std::memory_order_relaxed);
        state.mouse_y.store(mouse_y, std::memory_order_relaxed);
        if (menu_open)
            return 0;
        const auto manual_camera = has_snapshot && CanUseManualCamera(snapshot);
        const auto manual_input_ready =
            manual_camera && CanConsumeManualCameraMouseInput(snapshot);
        if (manual_input_ready) {
            const auto was_seeded = state.manual_legacy_mouse_seeded.exchange(
                true, std::memory_order_acq_rel);
            const auto previous_x = state.manual_legacy_mouse_x.exchange(
                mouse_x, std::memory_order_acq_rel);
            const auto previous_y = state.manual_legacy_mouse_y.exchange(
                mouse_y, std::memory_order_acq_rel);
            const auto raw_at = state.manual_raw_mouse_observed_ms.load(std::memory_order_acquire);
            const auto now = GetTickCount64();
            const auto delta = ResolveLegacyMouseDelta(
                was_seeded, previous_x, previous_y, mouse_x, mouse_y,
                raw_at != 0 && now - raw_at <= 50);
            if (ShouldApplyFallbackMouseSample(
                    delta.accepted,
                    now,
                    state.manual_fallback_settle_until_ms.load(std::memory_order_acquire),
                    state.manual_discard_next_legacy_sample)) {
                const auto canonical = NormalizeFallbackMouseDelta(delta.x, delta.y);
                state.manual_look_right_delta.fetch_add(
                    canonical.look_right, std::memory_order_release);
                state.manual_look_up_delta.fetch_add(canonical.look_up, std::memory_order_release);
                state.manual_mouse_observed.store(true, std::memory_order_release);
                state.manual_fallback_mouse_observed.store(true, std::memory_order_release);
                state.manual_fallback_mouse_observed_ms.store(now, std::memory_order_release);
            }
            if (camera_input_takeover)
                return 0;
        } else {
            state.manual_legacy_mouse_seeded.store(false, std::memory_order_release);
        }
        if (camera_input_takeover)
            return 0;
    }
    if ((window_mouse_down || window_mouse_up) && menu_open) {
        state.mouse_x.store(GET_X_LPARAM(lparam), std::memory_order_relaxed);
        state.mouse_y.store(GET_Y_LPARAM(lparam), std::memory_order_relaxed);
    }

    if (has_snapshot && menu_open &&
        (message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN || message == WM_MOUSEWHEEL) &&
        !legacy_wheel_duplicate && (!window_mouse_down || first_window_mouse_down) &&
        CaptureMouseBinding(message, wparam))
        return message == WM_XBUTTONDOWN ? TRUE : 0;

    if (has_snapshot &&
        (message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN || message == WM_MOUSEWHEEL) &&
        !legacy_wheel_duplicate &&
        (!window_mouse_down || (menu_open ? first_window_mouse_down : !mouse_down_already_tracked)) &&
        HandleSmvmMouseBinding(message, wparam, snapshot)) {
        if (window_mouse_down)
            static_cast<void>(TrackConsumedMouseDown(message, wparam, kMenuWindowKeyRoute));
        return message == WM_XBUTTONDOWN ? TRUE : 0;
    }

    if (message == WM_LBUTTONDOWN && menu_open)
        return 0;
    if (message == WM_RBUTTONDOWN && menu_open)
        return 0;
    if ((message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN) && menu_open)
        return message == WM_XBUTTONDOWN ? TRUE : 0;
    if (ShouldSuppressGameplayInput(camera_input_takeover, false, false) &&
        (message == WM_LBUTTONDBLCLK || message == WM_RBUTTONDBLCLK ||
         message == WM_MBUTTONDBLCLK || message == WM_XBUTTONDBLCLK))
        return message == WM_XBUTTONDBLCLK ? TRUE : 0;
    if ((window_mouse_down || window_mouse_up) &&
        ShouldSuppressGameplayInput(camera_input_takeover, false, false)) {
        if (window_mouse_down)
            static_cast<void>(TrackConsumedMouseDown(message, wparam, kMenuWindowKeyRoute));
        else
            static_cast<void>(ConsumeTrackedMouseUp(message, wparam, kMenuWindowKeyRoute));
        return message == WM_XBUTTONDOWN || message == WM_XBUTTONUP ? TRUE : 0;
    }
    if (window_mouse_up && ConsumeTrackedMouseUp(message, wparam, kMenuWindowKeyRoute))
        return 0;

    if (message == WM_MOUSEWHEEL && has_snapshot) {
        if (legacy_wheel_duplicate && (menu_open || camera_input_takeover))
            return 0;
        const auto delta = GET_WHEEL_DELTA_WPARAM(wparam);
        if (menu_open) {
            PushUiEvent(smvm_ui_event_wheel, delta);
            return 0;
        }
        if (camera_input_takeover) {
            if (CanConsumeManualCameraMouseInput(snapshot))
                state.manual_wheel_delta.fetch_add(delta, std::memory_order_release);
            return 0;
        }
        if ((snapshot.flags & smvm_snapshot_replay_active) != 0 &&
            (snapshot.flags & smvm_snapshot_fov_writable) != 0 &&
            (snapshot.flags & smvm_snapshot_campath_playing) == 0 && snapshot.observer_mode == 4) {
            const auto now = GetTickCount64();
            const auto previous = state.last_wheel_action_ms.load(std::memory_order_acquire);
            if (now - previous >= 16) {
                state.last_wheel_action_ms.store(now, std::memory_order_release);
                auto direction = delta > 0 ? -1.0 : 1.0;
                if ((snapshot.flags & smvm_snapshot_fov_inverted) != 0)
                    direction = -direction;
                const auto target = std::clamp(snapshot.camera.fov + (direction * snapshot.fov_step),
                                               kMinFov, kMaxFov);
                static_cast<void>(QueueActionForSnapshot(
                    snapshot,
                    SmvmActionType::set_fov,
                    -1,
                    -1,
                    target));
            }
            return 0;
        }
    }
    if (message == WM_MOUSEHWHEEL && camera_input_takeover)
        return 0;

    if (has_snapshot && menu_open &&
        IsBindingActionIndex(state.binding_capture_action.load(std::memory_order_acquire))) {
        if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) {
            const auto repeated = (lparam & (1LL << 30)) != 0;
            if (CaptureKeyboardBinding(normalized_key, repeated))
                return 0;
        }
        if ((message == WM_KEYUP || message == WM_SYSKEYUP) &&
            CaptureStandaloneModifierBinding(normalized_key))
            return 0;
        if (message == WM_CHAR)
            return 0;
    }

    if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && has_snapshot) {
        const auto repeated = (lparam & (1LL << 30)) != 0;
        if (!repeated && InputMatchesKeyboard(snapshot.clean_view_key, normalized_key) &&
            (snapshot.flags & smvm_snapshot_replay_active) != 0) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            static_cast<void>(ToggleCleanFootage(false, snapshot));
            return 0;
        }
        if (!repeated && InputMatchesKeyboard(snapshot.menu_key, normalized_key) &&
            (timeline_visible || menu_open)) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            if (state.clean_view.load(std::memory_order_acquire))
                static_cast<void>(ToggleCleanFootage(true, snapshot));
            else
                SetMenuOpen(!menu_open);
            return 0;
        }
        if (!repeated && HandleMovieMakerKeyboardShortcut(normalized_key, snapshot)) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            return 0;
        }
        if (menu_open) {
            // The menu consumes every remaining key; mirror it into ImGui
            // unless a binding capture owns the keyboard right now.
            if (UiFeedingAllowed(menu_open))
                PushUiEvent(smvm_ui_event_key, static_cast<std::int32_t>(normalized_key), 1);
            return 0;
        }
        if (HandleManualCameraShortcut(
                snapshot,
                normalized_key,
                true,
                kMenuWindowKeyRoute,
                menu_open))
            return 0;
        if (!repeated && InputMatchesKeyboard(snapshot.add_key, normalized_key)) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::input_observed);
            QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::binding_matched);
            const auto rejection = CaptureRejectionFromSnapshot(snapshot);
            if (rejection != SmvmCaptureRejection::none) {
                QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::capture_rejected, rejection);
                return 0;
            }
            QueueCaptureDiagnostic(snapshot, SmvmCaptureStage::capture_requested);
            if (state.callbacks.request_camera_capture != nullptr) {
                state.callbacks.request_camera_capture(
                    state.callbacks.context,
                    snapshot.replay_session_generation);
            } else {
                QueueCaptureDiagnostic(
                    snapshot,
                    SmvmCaptureStage::capture_rejected,
                    SmvmCaptureRejection::native_backend_unavailable);
            }
            return 0;
        }
        if (!repeated && InputMatchesKeyboard(snapshot.delete_key, normalized_key) && CanEditCampath(snapshot)) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            static_cast<void>(QueueActionForSnapshot(
                snapshot,
                SmvmActionType::delete_keyframe,
                snapshot.selected_keyframe));
            return 0;
        }
        if (!repeated && HandleCampathKeyboardBinding(normalized_key, snapshot)) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            return 0;
        }
        if (camera_input_takeover && EditorBindingOwnsKeyboardKey(snapshot, normalized_key)) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            return 0;
        }
        const auto manual_takeover =
            camera_input_takeover && CanConsumeManualCameraKeyboardInput(snapshot);
        if (RouteManualKeyboardEvent(
                snapshot,
                normalized_key,
                true,
                kManualWindowKeyRoute,
                manual_takeover))
            return 0;
        if (camera_input_takeover && !manual_takeover &&
            ManualBindingOwnsKey(snapshot, normalized_key))
            return 0;
        if (ShouldSuppressGameplayInput(camera_input_takeover, false, false)) {
            TrackConsumedKeyDown(normalized_key, kMenuWindowKeyRoute);
            return 0;
        }
    }
    if (replay_tick_editor_owns_keyboard && window_key_up) {
        if (UiFeedingAllowed(menu_open))
            PushUiEvent(smvm_ui_event_key, static_cast<std::int32_t>(normalized_key), 0);
        static_cast<void>(ConsumeTrackedKeyUp(normalized_key, kMenuWindowKeyRoute));
        return 0;
    }
    if (message == WM_KEYUP || message == WM_SYSKEYUP) {
        static_cast<void>(HandleManualCameraShortcut(
            snapshot,
            normalized_key,
            false,
            kMenuWindowKeyRoute,
            menu_open));
        if (menu_open && UiFeedingAllowed(menu_open))
            PushUiEvent(smvm_ui_event_key, static_cast<std::int32_t>(normalized_key), 0);
        const auto manual_takeover =
            camera_input_takeover && CanConsumeManualCameraKeyboardInput(snapshot);
        if (RouteManualKeyboardEvent(
                snapshot,
                normalized_key,
                false,
                kManualWindowKeyRoute,
                manual_takeover))
            return 0;
        if (camera_input_takeover && !manual_takeover &&
            ManualBindingOwnsKey(snapshot, normalized_key))
            return 0;
        if (ConsumeTrackedKeyUp(normalized_key, kMenuWindowKeyRoute))
            return 0;
        if (ShouldSuppressGameplayInput(camera_input_takeover, false, false))
            return 0;
    }
    if (message == WM_CHAR &&
        (menu_open || replay_tick_editor_owns_keyboard || camera_input_takeover)) {
        if (UiFeedingAllowed(menu_open))
            PushUiEvent(smvm_ui_event_char, static_cast<std::int32_t>(wparam));
        return 0;
    }
    if (message == WM_SETCURSOR) {
        if (menu_open) {
            SetCursor(LoadCursorW(nullptr, IDC_ARROW));
            return TRUE;
        }
        if (state.manual_pointer_active.load(std::memory_order_acquire)) {
            SetCursor(nullptr);
            return TRUE;
        }
    }

    return CallWindowProcW(original, window, message, wparam, lparam);
}

[[nodiscard]] bool RestorePublishedWindowProcedure() noexcept {
    auto& state = g_overlay;
    const auto window = state.output_window;
    const auto original = state.original_window_proc;
    if (window == nullptr && original == nullptr)
        return true;
    if (window == nullptr || original == nullptr)
        return false;
    if (!IsWindow(window)) {
        // Destruction makes the procedure unreachable. Cursor state is restored
        // from WM_NCDESTROY; if that callback was bypassed, residency is safer.
        return !state.cursor_shown && !PointerStateRestorePending();
    }

    if (GetWindowThreadProcessId(window, nullptr) == GetCurrentThreadId()) {
        if (!ApplyManualPointerStateOnWindowThread(window, false) ||
            !ApplyCursorStateOnWindowThread(false) || PointerStateRestorePending())
            return false;
        const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC));
        if (current == original)
            return true;
        if (current != &SmvmWindowProcedure)
            return false;
        SetLastError(ERROR_SUCCESS);
        const auto previous = SetWindowLongPtrW(
            window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(original));
        return !(previous == 0 && GetLastError() != ERROR_SUCCESS) &&
               reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC)) == original;
    }

    DWORD_PTR response = FALSE;
    return SendMessageTimeoutW(
               window,
               kSmvmRestoreWindowProcedureMessage,
               0,
               0,
               SMTO_ABORTIFHUNG | SMTO_BLOCK,
               static_cast<UINT>(kCallbackDrainTimeout.count()),
               &response) != 0 &&
           response == TRUE;
}

[[nodiscard]] bool SubclassOutputWindow(const HWND window) noexcept {
    auto& state = g_overlay;
    if (window == nullptr || !IsWindow(window))
        return false;
    if (state.output_window == window && state.original_window_proc != nullptr) {
        const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC));
        if (current == &SmvmWindowProcedure)
            return true;
        // Deadlock can restore its own predecessor when spectator input modes
        // change. Re-publish only when the exact predecessor we recorded is
        // current; an unknown later subclass is not safe to bypass or wrap.
        if (current != state.original_window_proc)
            return false;
        SetLastError(ERROR_SUCCESS);
        const auto previous = SetWindowLongPtrW(
            window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&SmvmWindowProcedure));
        return !(previous == 0 && GetLastError() != ERROR_SUCCESS) &&
               reinterpret_cast<WNDPROC>(previous) == state.original_window_proc &&
               reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC)) ==
                   &SmvmWindowProcedure;
    }
    if (!RestorePublishedWindowProcedure() ||
        !WaitForCallbacksToDrain(state.active_window_procedures))
        return false;
    ClearWindowProcedurePublication();

    // Publish the predecessor before this procedure becomes reachable. Verify
    // the observed predecessor did not change in the small SetWindowLongPtr
    // window; a concurrent subclass is handled as an installation failure.
    SetLastError(ERROR_SUCCESS);
    const auto observed = GetWindowLongPtrW(window, GWLP_WNDPROC);
    if (observed == 0 && GetLastError() != ERROR_SUCCESS)
        return false;
    state.output_window = window;
    state.original_window_proc = reinterpret_cast<WNDPROC>(observed);
    SetLastError(ERROR_SUCCESS);
    const auto previous = SetWindowLongPtrW(window, GWLP_WNDPROC,
                                            reinterpret_cast<LONG_PTR>(&SmvmWindowProcedure));
    if (previous == 0 && GetLastError() != ERROR_SUCCESS) {
        if (reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC)) !=
            &SmvmWindowProcedure)
            ClearWindowProcedurePublication();
        return false;
    }
    if (previous != observed) {
        const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC));
        auto restored = false;
        if (current == &SmvmWindowProcedure) {
            SetLastError(ERROR_SUCCESS);
            const auto replaced = SetWindowLongPtrW(window, GWLP_WNDPROC, previous);
            restored = !(replaced == 0 && GetLastError() != ERROR_SUCCESS) &&
                GetWindowLongPtrW(window, GWLP_WNDPROC) == previous;
        } else {
            // A still-reachable SMVM procedure must call the predecessor that
            // SetWindowLongPtr actually returned, not the stale pre-read.
            state.original_window_proc = reinterpret_cast<WNDPROC>(previous);
        }
        if (restored && WaitForCallbacksToDrain(state.active_window_procedures))
            ClearWindowProcedurePublication();
        return false;
    }
    return state.original_window_proc != nullptr;
}

[[nodiscard]] bool CreateRenderTarget(IDXGISwapChain* swapchain) noexcept {
    ID3D11Texture2D* back_buffer = nullptr;
    if (FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&back_buffer))))
        return false;
    D3D11_TEXTURE2D_DESC description{};
    back_buffer->GetDesc(&description);
    const auto result = g_overlay.device->CreateRenderTargetView(back_buffer, nullptr, &g_overlay.render_target);
    back_buffer->Release();
    if (FAILED(result))
        return false;
    g_overlay.viewport_width = static_cast<float>(description.Width);
    g_overlay.viewport_height = static_cast<float>(description.Height);
    return description.Width > 0 && description.Height > 0;
}

[[nodiscard]] bool InitializeSwapchain(IDXGISwapChain* swapchain) noexcept {
    DXGI_SWAP_CHAIN_DESC description{};
    if (FAILED(swapchain->GetDesc(&description)) || description.OutputWindow == nullptr)
        return false;
    DWORD process_id = 0;
    GetWindowThreadProcessId(description.OutputWindow, &process_id);
    if (process_id != GetCurrentProcessId())
        return false;

    ID3D11Device* device = nullptr;
    if (FAILED(swapchain->GetDevice(IID_PPV_ARGS(&device))) || device == nullptr)
        return false;
    if (g_overlay.device != device) {
        if (!ReleaseDeviceResources()) {
            SafeRelease(device);
            return false;
        }
        g_overlay.device = device;
        device = nullptr;
        g_overlay.device->GetImmediateContext(&g_overlay.context);
        if (g_overlay.context == nullptr || !CreatePipeline(g_overlay.device) ||
            !InitializeImGui()) {
            SafeRelease(device);
            static_cast<void>(ReleaseDeviceResources());
            return false;
        }
    }
    SafeRelease(device);
    ReleaseRenderTarget();
    g_overlay.target_swapchain = swapchain;
    if (!CreateRenderTarget(swapchain) || !SubclassOutputWindow(description.OutputWindow)) {
        static_cast<void>(ReleaseDeviceResources());
        return false;
    }
    g_overlay.ready.store(true, std::memory_order_release);
    return true;
}

[[nodiscard]] bool DrawVertices() noexcept {
    auto& state = g_overlay;
    if (state.vertex_count == 0 || state.context == nullptr || state.render_target == nullptr)
        return true;
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(state.context->Map(state.vertex_buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return false;
    std::memcpy(mapped.pData, state.vertices.data(), state.vertex_count * sizeof(Vertex));
    state.context->Unmap(state.vertex_buffer, 0);
    if (FAILED(state.context->Map(state.constant_buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return false;
    const std::array<float, 4> constants{state.viewport_width, state.viewport_height, 0.0F, 0.0F};
    std::memcpy(mapped.pData, constants.data(), sizeof(constants));
    state.context->Unmap(state.constant_buffer, 0);

    SavedD3D11State saved{};
    SavePipelineState(state.context, saved);
    const D3D11_VIEWPORT viewport{0.0F, 0.0F, state.viewport_width, state.viewport_height, 0.0F, 1.0F};
    state.context->RSSetViewports(1, &viewport);
    state.context->OMSetRenderTargets(1, &state.render_target, nullptr);
    constexpr std::array<FLOAT, 4> blend_factor{};
    state.context->OMSetBlendState(state.blend_state, blend_factor.data(), 0xFFFFFFFFu);
    state.context->OMSetDepthStencilState(state.depth_state, 0);
    state.context->RSSetState(state.rasterizer_state);
    const UINT stride = sizeof(Vertex);
    constexpr UINT offset = 0;
    state.context->IASetInputLayout(state.input_layout);
    state.context->IASetVertexBuffers(0, 1, &state.vertex_buffer, &stride, &offset);
    state.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    state.context->VSSetShader(state.vertex_shader, nullptr, 0);
    state.context->VSSetConstantBuffers(0, 1, &state.constant_buffer);
    state.context->PSSetShader(state.pixel_shader, nullptr, 0);
    state.context->PSSetShaderResources(0, 1, &state.atlas_view);
    state.context->PSSetSamplers(0, 1, &state.sampler);
    state.context->Draw(static_cast<UINT>(state.vertex_count), 0);
    RestorePipelineState(state.context, saved);
    return true;
}

[[nodiscard]] bool RenderFrame(IDXGISwapChain* swapchain) noexcept {
    auto& state = g_overlay;
    if (state.render_lock.test_and_set(std::memory_order_acquire))
        return true;
    if (state.target_swapchain != swapchain || state.render_target == nullptr) {
        if (!InitializeSwapchain(swapchain)) {
            state.render_lock.clear(std::memory_order_release);
            return false;
        }
    }
    SmvmSnapshotPayload snapshot{};
    const auto snapshot_read = ReadSnapshot(snapshot);
    smvm_ui::ObserveReplaySession(snapshot_read ? &snapshot : nullptr, state.ui);
    if (!snapshot_read)
        NotifySmvmHostDisconnected();
    if (state.recording_profile_restore_debt.load(std::memory_order_acquire) != 0)
        PumpEmergencyDeadlockUiRestore();
    if (!snapshot_read || (snapshot.flags & smvm_snapshot_internal_enabled) == 0 ||
        (snapshot.flags & smvm_snapshot_replay_active) == 0) {
        ResetCinematicStartGate(true);
        RequestManualPointerState(false);
        if (snapshot_read) {
            const auto restore_debt =
                state.recording_profile_restore_debt.load(std::memory_order_acquire);
            if ((state.recording_profile_may_be_active.load(std::memory_order_acquire) ||
                 restore_debt != 0) &&
                (restore_debt & kRecordingProfileHardRestoreDebt) == 0) {
                RequestEmergencyDeadlockUiRestore(true);
            }
        }
        SetLocalPresentationMode(DeadlockUiMode::deadlock_ui);
        SetMenuOpen(false);
        state.vertex_count = 0;
        state.world_label_count = 0;
        DiscardUiEvents();
        state.render_lock.clear(std::memory_order_release);
        return true;
    }

    const auto manual_camera_usable = CanUseManualCamera(snapshot);
    // Readiness becomes true only on the game-window thread after that
    // channel's foreground/window/input checks succeed. Do not publish
    // keyboard-ready from Present while a posted transition is still pending.
    if (!manual_camera_usable || state.menu_open.load(std::memory_order_acquire))
        state.manual_keyboard_ready.store(false, std::memory_order_release);
    RequestManualPointerState(
        manual_camera_usable &&
        !state.replay_tick_input_active.load(std::memory_order_acquire));

    CameraSample rendered_camera{};
    const auto has_rendered_camera = state.callbacks.read_rendered_camera != nullptr &&
        state.callbacks.read_rendered_camera(state.callbacks.context, rendered_camera);
    const auto view_camera = SelectRenderCamera(
        snapshot.camera, rendered_camera, has_rendered_camera);

    CampathPayloadHeader path_header{};
    std::array<CampathKeyframe, kMaxCampathKeyframes> keys{};
    const auto has_path = ReadPath(path_header, keys.data(), keys.size());
    UpdateCinematicStartGate(snapshot, has_path, path_header, keys.data());
    const auto menu_open = state.menu_open.load(std::memory_order_acquire);
    const auto clean_view = state.clean_view.load(std::memory_order_acquire);
    state.vertex_count = 0;
    state.world_label_count = 0;

    // Camera markers and their connecting path are part of the simplified
    // placement workflow. Hide them during playback so Play Cinematic shows
    // the composed shot rather than editor guides.
    if (ShouldDrawCampathPlacementGuides(
            snapshot.deadlock_ui_mode == DeadlockUiMode::smvm_replay_ui,
            clean_view,
            has_path,
            (snapshot.flags & smvm_snapshot_campath_playing) != 0,
            (snapshot.flags & smvm_snapshot_camera_readable) != 0)) {
        DrawCampathVisualization(snapshot, path_header, keys.data(), view_camera);
    }

    const auto timeline_visible = !clean_view && IsReplayTimelineVisible(snapshot);
    const auto want_ui = timeline_visible;
    auto imgui_rendered = false;
    if (!clean_view && want_ui && state.imgui != nullptr &&
        state.ui_ready.load(std::memory_order_acquire)) {
        ImGui::SetCurrentContext(state.imgui);
        EnsureUiScale(snapshot);
        auto& io = ImGui::GetIO();
        io.DisplaySize = ImVec2(state.viewport_width, state.viewport_height);
        const auto now = std::chrono::steady_clock::now();
        if (state.last_frame_time.time_since_epoch().count() == 0) {
            io.DeltaTime = 1.0F / 60.0F;
        } else {
            const auto delta = std::chrono::duration<float>(now - state.last_frame_time).count();
            io.DeltaTime = std::clamp(delta, 1.0F / 240.0F, 0.25F);
        }
        state.last_frame_time = now;
        DrainUiEvents(menu_open);
        FeedImguiMouse(menu_open && timeline_visible);
        ImGui_ImplDX11_NewFrame();
        ImGui::NewFrame();

        SmvmUiFrameParams params{};
        params.snapshot = &snapshot;
        params.rendered_camera = &view_camera;
        params.path_header = has_path ? &path_header : nullptr;
        params.keyframes = keys.data();
        params.has_path = has_path;
        params.documents = nullptr;
        params.menu_open = menu_open;
        params.viewport_width = state.viewport_width;
        params.viewport_height = state.viewport_height;
        params.frame_microseconds = state.frame_microseconds.load(std::memory_order_acquire);
        params.overlay_flags = OverlayFlags();
        params.renderer_error = state.last_renderer_error.load(std::memory_order_acquire);
        params.raw_mouse_timestamp_ms =
            state.manual_raw_mouse_observed_ms.load(std::memory_order_acquire);
        params.fallback_mouse_timestamp_ms =
            state.manual_fallback_mouse_observed_ms.load(std::memory_order_acquire);
        params.free_camera_input_error =
            state.manual_input_error.load(std::memory_order_acquire);
        params.cinematic_start_ready =
            state.cinematic_start_ready.load(std::memory_order_acquire);
        params.free_camera_input_failure =
            state.manual_input_failure.load(std::memory_order_acquire);
        params.raw_registration_disposition =
            state.manual_raw_registration_disposition.load(std::memory_order_acquire);
        params.queue_action = [context = state.callbacks.context,
                               queue = state.callbacks.queue_action](const SmvmActionPayload& action) {
            return queue != nullptr && queue(context, action);
        };
        params.request_capture = [context = state.callbacks.context,
                                  capture = state.callbacks.request_camera_capture](
                                     const std::uint64_t replay_session_generation) {
            if (capture != nullptr)
                capture(context, replay_session_generation);
        };
        smvm_ui::DrawFrame(params, state.ui);
        ImGui::Render();
        imgui_rendered = true;
    } else {
        DiscardUiEvents();
    }

    const auto drawn = DrawVertices();
    if (imgui_rendered) {
        // The backend keeps its own state backup; the outer wrapper restores
        // the render target binding and everything it does not cover.
        SavedD3D11State saved{};
        SavePipelineState(state.context, saved);
        const D3D11_VIEWPORT viewport{
            0.0F, 0.0F, state.viewport_width, state.viewport_height, 0.0F, 1.0F};
        state.context->RSSetViewports(1, &viewport);
        state.context->OMSetRenderTargets(1, &state.render_target, nullptr);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
        RestorePipelineState(state.context, saved);
    }
    state.render_lock.clear(std::memory_order_release);
    return drawn;
}

HRESULT STDMETHODCALLTYPE PresentHook(IDXGISwapChain* swapchain, UINT sync_interval, UINT flags) noexcept;
HRESULT STDMETHODCALLTYPE ResizeBuffersHook(
    IDXGISwapChain* swapchain,
    UINT buffer_count,
    UINT width,
    UINT height,
    DXGI_FORMAT format,
    UINT flags) noexcept;
HRESULT STDMETHODCALLTYPE FactoryCreateSwapchainHook(
    IDXGIFactory* factory,
    IUnknown* device,
    DXGI_SWAP_CHAIN_DESC* description,
    IDXGISwapChain** swapchain) noexcept;

[[nodiscard]] bool IsReadableRange(const void* address, const std::size_t size) noexcept {
    if (address == nullptr || size == 0)
        return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) != sizeof(memory) ||
        memory.State != MEM_COMMIT || (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
        return false;
    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto region = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    return start >= region && start - region <= memory.RegionSize &&
           size <= memory.RegionSize - (start - region);
}

[[nodiscard]] bool IsExecutableFunction(const void* function) noexcept {
    if (function == nullptr)
        return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(function, &memory, sizeof(memory)) != sizeof(memory) ||
        memory.State != MEM_COMMIT || (memory.Protect & PAGE_GUARD) != 0)
        return false;
    const auto protection = memory.Protect & 0xFF;
    return protection == PAGE_EXECUTE || protection == PAGE_EXECUTE_READ ||
           protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
}

struct ModuleTextView final {
    std::uint8_t* base{};
    std::size_t size{};
    std::uint8_t* text{};
    std::size_t text_size{};

    [[nodiscard]] bool Contains(
        const std::uintptr_t address,
        const std::size_t length = 1) const noexcept {
        if (base == nullptr || length > size)
            return false;
        const auto start = reinterpret_cast<std::uintptr_t>(base);
        return address >= start && address - start <= size - length;
    }
};

[[nodiscard]] bool InspectModuleText(const HMODULE module, ModuleTextView& view) noexcept {
    view = {};
    if (module == nullptr)
        return false;
    auto* base = reinterpret_cast<std::uint8_t*>(module);
    __try {
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
            return false;
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE ||
            nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
            return false;
        view.base = base;
        view.size = nt->OptionalHeader.SizeOfImage;
        const auto* sections = IMAGE_FIRST_SECTION(nt);
        for (std::uint16_t index = 0; index < nt->FileHeader.NumberOfSections; ++index) {
            const auto& section = sections[index];
            if (std::memcmp(section.Name, ".text", 5) != 0)
                continue;
            view.text = base + section.VirtualAddress;
            view.text_size = std::max<std::size_t>(
                section.Misc.VirtualSize, section.SizeOfRawData);
            break;
        }
        return view.text != nullptr &&
               view.Contains(reinterpret_cast<std::uintptr_t>(view.text), view.text_size);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        view = {};
        return false;
    }
}

[[nodiscard]] std::optional<std::uintptr_t> ResolveRenderFactoryGlobal(
    const HMODULE render_module) noexcept {
    ModuleTextView view{};
    if (!InspectModuleText(render_module, view))
        return std::nullopt;
    const auto pattern = ParsePattern(kRenderFactoryPattern);
    if (!pattern)
        return std::nullopt;
    const auto hits = FindPattern(view.text, view.text_size, *pattern, 2);
    if (hits.size() != 1)
        return std::nullopt;
    constexpr std::size_t kFactoryGlobalInstructionOffset = 10;
    const auto instruction = reinterpret_cast<std::uintptr_t>(
        view.text + hits.front() + kFactoryGlobalInstructionOffset);
    const auto target = ResolveRipRelative(instruction, 3, 7);
    if (!target || !view.Contains(*target, sizeof(void*)))
        return std::nullopt;
    return target;
}

[[nodiscard]] IDXGIFactory* ReadRenderFactory(const std::uintptr_t renderer_global) noexcept {
    if (renderer_global == 0)
        return nullptr;
    __try {
        auto* renderer = *reinterpret_cast<std::uint8_t**>(renderer_global);
        if (renderer == nullptr ||
            !IsReadableRange(renderer + kRenderFactoryOffset, sizeof(void*)))
            return nullptr;
        return *reinterpret_cast<IDXGIFactory**>(renderer + kRenderFactoryOffset);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

[[nodiscard]] void** ReadObjectVtable(void* object, const std::size_t entry_count) noexcept {
    if (object == nullptr || !IsReadableRange(object, sizeof(void*)))
        return nullptr;
    __try {
        auto** vtable = *reinterpret_cast<void***>(object);
        return IsReadableRange(vtable, entry_count * sizeof(void*)) ? vtable : nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

[[nodiscard]] bool ResolveInputSystemGate() noexcept {
    auto& state = g_overlay;
    if (state.input_system != nullptr && state.input_system_enable != nullptr &&
        state.input_enabled_state_offset != 0)
        return true;

    const auto module = GetModuleHandleW(L"inputsystem.dll");
    ModuleTextView view{};
    if (module == nullptr || !InspectModuleText(module, view))
        return false;
    const auto create_interface = reinterpret_cast<CreateInterfaceFunction>(
        GetProcAddress(module, "CreateInterface"));
    if (create_interface == nullptr ||
        !view.Contains(reinterpret_cast<std::uintptr_t>(create_interface)))
        return false;

    int result = -1;
    void* input_system = nullptr;
    __try {
        input_system = create_interface(kInputSystemInterfaceName, &result);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    if (input_system == nullptr || result != 0 ||
        !view.Contains(reinterpret_cast<std::uintptr_t>(input_system), sizeof(void*)))
        return false;

    auto** vtable = ReadObjectVtable(input_system, kInputSystemRequiredVtableEntries);
    if (vtable == nullptr ||
        !view.Contains(reinterpret_cast<std::uintptr_t>(vtable),
                       kInputSystemRequiredVtableEntries * sizeof(void*)))
        return false;
    auto* enable_function = vtable[kInputSystemEnableVtableIndex];
    constexpr std::size_t kEnableInputInstructionSize = 4;
    if (!view.Contains(reinterpret_cast<std::uintptr_t>(enable_function),
                       kEnableInputInstructionSize) ||
        !IsExecutableFunction(enable_function))
        return false;

    std::uint8_t state_offset = 0;
    if (!TryDecodeInputEnabledStateOffset(
            static_cast<const std::uint8_t*>(enable_function),
            kEnableInputInstructionSize,
            state_offset) ||
        !IsReadableRange(static_cast<std::uint8_t*>(input_system) + state_offset, 1))
        return false;
    __try {
        if (*(static_cast<std::uint8_t*>(input_system) + state_offset) > 1)
            return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }

    state.input_system = input_system;
    state.input_system_enable = reinterpret_cast<InputSystemEnableFunction>(enable_function);
    state.input_enabled_state_offset = state_offset;
    return true;
}

[[nodiscard]] bool SuspendInputSystemOnWindowThread() noexcept {
    auto& state = g_overlay;
    if (!ResolveInputSystemGate() || state.input_system == nullptr ||
        state.input_system_enable == nullptr || state.input_enabled_state_offset == 0)
        return false;
    auto* enabled = static_cast<std::uint8_t*>(state.input_system) +
        state.input_enabled_state_offset;
    if (!IsReadableRange(enabled, 1) || !IsExecutableFunction(
            reinterpret_cast<void*>(state.input_system_enable)))
        return false;

    __try {
        if (!state.input_restore_pending.load(std::memory_order_acquire)) {
            if (*enabled > 1)
                return false;
            state.input_was_enabled = *enabled != 0;
            state.input_restore_pending.store(true, std::memory_order_release);
        }
        state.input_system_enable(state.input_system, false);
        return *enabled == 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] bool RestoreInputSystemOnWindowThread() noexcept {
    auto& state = g_overlay;
    if (!state.input_restore_pending.load(std::memory_order_acquire))
        return true;
    if (state.input_system == nullptr || state.input_system_enable == nullptr ||
        state.input_enabled_state_offset == 0)
        return false;
    auto* enabled = static_cast<std::uint8_t*>(state.input_system) +
        state.input_enabled_state_offset;
    if (!IsReadableRange(enabled, 1) || !IsExecutableFunction(
            reinterpret_cast<void*>(state.input_system_enable)))
        return false;

    __try {
        state.input_system_enable(state.input_system, state.input_was_enabled);
        if ((*enabled != 0) != state.input_was_enabled)
            return false;
        state.input_restore_pending.store(false, std::memory_order_release);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

[[nodiscard]] void** CloneVtable(
    void** original,
    const std::size_t entry_count,
    const std::size_t replacement_index,
    void* replacement) noexcept {
    if (original == nullptr || replacement == nullptr || replacement_index >= entry_count ||
        !IsReadableRange(original, entry_count * sizeof(void*)))
        return nullptr;
    auto** clone = static_cast<void**>(VirtualAlloc(
        nullptr,
        entry_count * sizeof(void*),
        MEM_COMMIT | MEM_RESERVE,
        PAGE_READWRITE));
    if (clone == nullptr)
        return nullptr;
    __try {
        std::memcpy(clone, original, entry_count * sizeof(void*));
        clone[replacement_index] = replacement;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        VirtualFree(clone, 0, MEM_RELEASE);
        return nullptr;
    }
    DWORD ignored = 0;
    if (!VirtualProtect(clone, entry_count * sizeof(void*), PAGE_READONLY, &ignored)) {
        VirtualFree(clone, 0, MEM_RELEASE);
        return nullptr;
    }
    return clone;
}

[[nodiscard]] bool PublishObjectVtable(
    void* object,
    void** expected,
    void** replacement) noexcept {
    if (object == nullptr || expected == nullptr || replacement == nullptr)
        return false;
    const auto prior = InterlockedCompareExchangePointer(
        reinterpret_cast<void* volatile*>(object), replacement, expected);
    FlushProcessWriteBuffers();
    return prior == expected;
}

[[nodiscard]] bool RestoreObjectVtable(
    void* object,
    void** original,
    void** replacement) noexcept {
    if (object == nullptr || original == nullptr || replacement == nullptr)
        return false;
    const auto prior = InterlockedCompareExchangePointer(
        reinterpret_cast<void* volatile*>(object), original, replacement);
    FlushProcessWriteBuffers();
    return prior == replacement || prior == original;
}

[[nodiscard]] bool AddRefObject(IUnknown* object) noexcept {
    if (object == nullptr)
        return false;
    __try {
        object->AddRef();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void ReleaseObject(IUnknown*& object) noexcept {
    if (object == nullptr)
        return;
    __try {
        object->Release();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // The module stays resident whenever restoration is uncertain. This is
        // cleanup after a proven restore, so a foreign COM failure must not be
        // allowed to escape the native shutdown path.
    }
    object = nullptr;
}

[[nodiscard]] bool InstallSwapchainInstanceHookLocked(IDXGISwapChain* swapchain) noexcept {
    auto& state = g_overlay;
    if (swapchain == nullptr || state.stop_requested.load(std::memory_order_acquire))
        return false;
    if (state.hooked_swapchain != nullptr)
        return state.hooked_swapchain == swapchain &&
               state.present_hook_reachable.load(std::memory_order_acquire) &&
               state.resize_hook_reachable.load(std::memory_order_acquire);

    auto** original_vtable = ReadObjectVtable(swapchain, kSwapchainVtableEntryCount);
    if (original_vtable == nullptr)
        return false;
    const auto original_present = reinterpret_cast<PresentFunction>(
        original_vtable[kPresentVtableIndex]);
    const auto original_resize = reinterpret_cast<ResizeBuffersFunction>(
        original_vtable[kResizeBuffersVtableIndex]);
    if (!IsExecutableFunction(reinterpret_cast<void*>(original_present)) ||
        !IsExecutableFunction(reinterpret_cast<void*>(original_resize)) ||
        original_present == &PresentHook || original_resize == &ResizeBuffersHook)
        return false;

    auto** clone = CloneVtable(
        original_vtable,
        kSwapchainVtableEntryCount,
        kPresentVtableIndex,
        reinterpret_cast<void*>(&PresentHook));
    if (clone == nullptr)
        return false;
    DWORD old_protection = 0;
    if (!VirtualProtect(
            clone,
            kSwapchainVtableEntryCount * sizeof(void*),
            PAGE_READWRITE,
            &old_protection)) {
        VirtualFree(clone, 0, MEM_RELEASE);
        return false;
    }
    clone[kResizeBuffersVtableIndex] = reinterpret_cast<void*>(&ResizeBuffersHook);
    DWORD ignored = 0;
    if (!VirtualProtect(
            clone,
            kSwapchainVtableEntryCount * sizeof(void*),
            PAGE_READONLY,
            &ignored)) {
        VirtualFree(clone, 0, MEM_RELEASE);
        return false;
    }
    if (!AddRefObject(swapchain)) {
        VirtualFree(clone, 0, MEM_RELEASE);
        return false;
    }

    state.hooked_swapchain = swapchain;
    state.swapchain_original_vtable = original_vtable;
    state.swapchain_hook_vtable = clone;
    state.original_present = original_present;
    state.original_resize = original_resize;
    if (!PublishObjectVtable(swapchain, original_vtable, clone)) {
        IUnknown* held = state.hooked_swapchain;
        state.hooked_swapchain = nullptr;
        state.swapchain_original_vtable = nullptr;
        state.swapchain_hook_vtable = nullptr;
        state.original_present = nullptr;
        state.original_resize = nullptr;
        ReleaseObject(held);
        VirtualFree(clone, 0, MEM_RELEASE);
        return false;
    }
    state.present_hook_reachable.store(true, std::memory_order_release);
    state.resize_hook_reachable.store(true, std::memory_order_release);
    state.hooks_installed.store(true, std::memory_order_release);
    return true;
}

[[nodiscard]] bool InstallFactoryCaptureHook(
    const std::uintptr_t renderer_global) noexcept {
    auto& state = g_overlay;
    auto* factory = ReadRenderFactory(renderer_global);
    if (factory == nullptr)
        return false;

    AcquireSRWLockExclusive(&state.hook_lifecycle_lock);
    if (state.stop_requested.load(std::memory_order_acquire)) {
        ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
        return false;
    }
    if (state.factory_hook_reachable.load(std::memory_order_acquire)) {
        const auto same_factory = state.target_factory == factory;
        ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
        return same_factory;
    }

    auto** original_vtable = ReadObjectVtable(factory, kFactoryVtableEntryCount);
    const auto original_create = original_vtable != nullptr
        ? reinterpret_cast<CreateSwapchainFunction>(
              original_vtable[kCreateSwapchainVtableIndex])
        : nullptr;
    if (!IsExecutableFunction(reinterpret_cast<void*>(original_create)) ||
        original_create == &FactoryCreateSwapchainHook) {
        ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
        return false;
    }
    auto** clone = CloneVtable(
        original_vtable,
        kFactoryVtableEntryCount,
        kCreateSwapchainVtableIndex,
        reinterpret_cast<void*>(&FactoryCreateSwapchainHook));
    if (clone == nullptr || !AddRefObject(factory)) {
        if (clone != nullptr)
            VirtualFree(clone, 0, MEM_RELEASE);
        ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
        return false;
    }

    state.target_factory = factory;
    state.factory_original_vtable = original_vtable;
    state.factory_hook_vtable = clone;
    state.original_create_swapchain = original_create;
    if (!PublishObjectVtable(factory, original_vtable, clone)) {
        IUnknown* held = state.target_factory;
        state.target_factory = nullptr;
        state.factory_original_vtable = nullptr;
        state.factory_hook_vtable = nullptr;
        state.original_create_swapchain = nullptr;
        ReleaseObject(held);
        VirtualFree(clone, 0, MEM_RELEASE);
        ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
        return false;
    }
    state.factory_hook_reachable.store(true, std::memory_order_release);
    state.hooks_installed.store(true, std::memory_order_release);
    ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
    return true;
}

DWORD InstallerThreadBody() noexcept {
    const auto started = std::chrono::steady_clock::now();
    auto capture_attempted = false;
    std::optional<std::uintptr_t> renderer_global;
    while (!g_overlay.stop_requested.load(std::memory_order_acquire)) {
        // Presentation cleanup is renderer-independent. Pump it during cold
        // module discovery as well as after hook installation so a dead host
        // cannot strand X-ray, near-fade, or Panorama state with zero Presents.
        PumpEmergencyDeadlockUiRestore();
        if (GetModuleHandleW(L"rendersystemvulkan.dll") != nullptr) {
            PublishStatus(SmvmRendererBackend::unsupported, SmvmRendererError::unsupported_renderer);
            break;
        }
        const auto render_module = GetModuleHandleW(L"rendersystemdx11.dll");
        if (render_module != nullptr) {
            capture_attempted = true;
            if (!renderer_global)
                renderer_global = ResolveRenderFactoryGlobal(render_module);
            if (renderer_global && InstallFactoryCaptureHook(*renderer_global)) {
                PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::present_not_observed);
                break;
            }
        }
        if (std::chrono::steady_clock::now() - started >= kInstallTimeout) {
            PublishStatus(
                capture_attempted ? SmvmRendererBackend::d3d11 : SmvmRendererBackend::none,
                capture_attempted ? SmvmRendererError::swapchain_probe_failed
                                  : SmvmRendererError::renderer_not_loaded);
            break;
        }
        Sleep(static_cast<DWORD>(kInstallRetryInterval.count()));
    }
    while (!g_overlay.stop_requested.load(std::memory_order_acquire)) {
        PumpEmergencyDeadlockUiRestore();
        Sleep(100);
    }
    return 0;
}

DWORD WINAPI InstallerThread(void*) noexcept {
    __try {
        return InstallerThreadBody();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::hook_install_failed);
        return 0;
    }
}

[[nodiscard]] HRESULT CallOriginalCreateSwapchain(
    const CreateSwapchainFunction original,
    IDXGIFactory* factory,
    IUnknown* device,
    DXGI_SWAP_CHAIN_DESC* description,
    IDXGISwapChain** swapchain) noexcept {
    if (original == nullptr)
        return DXGI_ERROR_INVALID_CALL;
    __try {
        return original(factory, device, description, swapchain);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return E_FAIL;
    }
}

[[nodiscard]] IDXGISwapChain* ReadCreatedSwapchain(IDXGISwapChain** slot) noexcept {
    if (slot == nullptr)
        return nullptr;
    __try {
        return *slot;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

HRESULT STDMETHODCALLTYPE FactoryCreateSwapchainHook(
    IDXGIFactory* factory,
    IUnknown* device,
    DXGI_SWAP_CHAIN_DESC* description,
    IDXGISwapChain** swapchain) noexcept {
    auto& state = g_overlay;
    ActiveCallbackGuard callback_guard(state.active_hooks);
    const auto original = state.original_create_swapchain;
    const auto result = CallOriginalCreateSwapchain(
        original, factory, device, description, swapchain);
    auto* created_swapchain = ReadCreatedSwapchain(swapchain);
    if (FAILED(result) || created_swapchain == nullptr ||
        state.stop_requested.load(std::memory_order_acquire))
        return result;

    AcquireSRWLockExclusive(&state.hook_lifecycle_lock);
    const auto installed = InstallSwapchainInstanceHookLocked(created_swapchain);
    ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
    PublishStatus(
        SmvmRendererBackend::d3d11,
        installed ? SmvmRendererError::present_not_observed
                  : SmvmRendererError::hook_install_failed);
    return result;
}

[[nodiscard]] SmvmRendererError RenderFrameProtected(IDXGISwapChain* swapchain) noexcept {
    __try {
        return RenderFrame(swapchain)
            ? SmvmRendererError::none
            : SmvmRendererError::resource_creation_failed;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_overlay.ready.store(false, std::memory_order_release);
        g_overlay.render_lock.clear(std::memory_order_release);
        return SmvmRendererError::device_reset;
    }
}

HRESULT STDMETHODCALLTYPE PresentHook(
    IDXGISwapChain* swapchain,
    const UINT sync_interval,
    const UINT flags) noexcept {
    auto& state = g_overlay;
    ActiveCallbackGuard callback_guard(state.active_hooks);
    const auto started = std::chrono::steady_clock::now();
    auto error = SmvmRendererError::none;
    if (!state.stop_requested.load(std::memory_order_acquire) &&
        state.hooks_installed.load(std::memory_order_acquire)) {
        state.present_observed.store(true, std::memory_order_release);
        const auto window_hook_healthy = state.output_window == nullptr ||
            SubclassOutputWindow(state.output_window);
        error = RenderFrameProtected(swapchain);
        if (error == SmvmRendererError::none && !window_hook_healthy)
            error = SmvmRendererError::window_hook_failed;
    }
    const auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::steady_clock::now() - started).count();
    const auto microseconds = static_cast<std::uint32_t>(std::clamp<std::int64_t>(elapsed, 0, 0xFFFFFFFFLL));
    PublishStatus(SmvmRendererBackend::d3d11, error, microseconds);
    const auto original = state.original_present;
    const auto result = original != nullptr
        ? original(swapchain, sync_interval, flags)
        : DXGI_ERROR_INVALID_CALL;
    return result;
}

HRESULT STDMETHODCALLTYPE ResizeBuffersHook(
    IDXGISwapChain* swapchain,
    const UINT buffer_count,
    const UINT width,
    const UINT height,
    const DXGI_FORMAT format,
    const UINT flags) noexcept {
    auto& state = g_overlay;
    ActiveCallbackGuard callback_guard(state.active_hooks);
    if (!state.stop_requested.load(std::memory_order_acquire) &&
        state.hooks_installed.load(std::memory_order_acquire) &&
        state.target_swapchain == swapchain &&
        !state.render_lock.test_and_set(std::memory_order_acquire)) {
        ReleaseRenderTarget();
        state.ready.store(false, std::memory_order_release);
        state.render_lock.clear(std::memory_order_release);
    }
    const auto original = state.original_resize;
    const auto result = original != nullptr
        ? original(swapchain, buffer_count, width, height, format, flags)
        : DXGI_ERROR_INVALID_CALL;
    if (FAILED(result))
        PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::device_reset);
    return result;
}

} // namespace

void ObserveSmvmRecordingVisualSnapshot(const SmvmSnapshotPayload& snapshot) noexcept {
    g_overlay.recording_profile_lease_lost.store(false, std::memory_order_release);
    g_overlay.recording_profile_disconnect_restore_guard.store(false, std::memory_order_release);
    ObserveRecordingVisualSnapshotState(snapshot);
    ReconcileRecordingProfileRecovery(snapshot);
    if ((g_overlay.recording_profile_restore_debt.load(std::memory_order_acquire) &
         kRecordingProfileHardRestoreDebt) != 0) {
        PumpEmergencyDeadlockUiRestore();
    }
}

void NotifySmvmHostDisconnected() noexcept {
    auto& state = g_overlay;
    // Snapshot expiry and a closed pipe are the same presentation-lease loss.
    // Edge-trigger the inverse/recovery boundary so a stalled render loop does
    // not rotate generations or issue VConsole batches every frame.
    if (state.recording_profile_lease_lost.exchange(true, std::memory_order_acq_rel))
        return;
    auto restore_required = false;
    AcquireSRWLockExclusive(&state.recording_profile_recovery_lock);
    const auto restore_debt =
        state.recording_profile_restore_debt.load(std::memory_order_acquire);
    auto recovery_kind = static_cast<RecordingProfileRecoveryKind>(
        state.recording_profile_recovery_kind.load(std::memory_order_relaxed));
    if (ShouldRestoreRecordingVisualProfileOnHostLoss(
            state.recording_profile_may_be_active.load(std::memory_order_acquire),
            restore_debt != 0,
            (restore_debt & kRecordingProfileHardRestoreDebt) != 0,
            recovery_kind != RecordingProfileRecoveryKind::none)) {
        restore_required = true;
        state.recording_profile_disconnect_restore_guard.store(true, std::memory_order_release);
        // Establish the hard inverse barrier and the durable post-reconnect
        // target under one lock before the blocking VConsole batch. A fresh
        // snapshot can neither retire an owner target nor queue a forward batch
        // in the gap.
        ArmEmergencyDeadlockUiRestore(true);
        if (recovery_kind == RecordingProfileRecoveryKind::none) {
            const auto target_mode = static_cast<DeadlockUiMode>(
                state.recording_profile_last_managed_mode.load(std::memory_order_acquire));
            const auto generation = state.recording_profile_snapshot_observed.load(
                std::memory_order_acquire)
                ? NextRecordingProfileRecoveryGeneration()
                : 0;
            state.recording_profile_recovery_target_mode.store(
                static_cast<std::uint32_t>(target_mode), std::memory_order_relaxed);
            state.recording_profile_recovery_generation.store(generation, std::memory_order_relaxed);
            state.recording_profile_recovery_kind.store(
                static_cast<std::uint32_t>(RecordingProfileRecoveryKind::reconnect_reassert),
                std::memory_order_release);
        } else if (recovery_kind == RecordingProfileRecoveryKind::owner_transition ||
                   recovery_kind == RecordingProfileRecoveryKind::reconnect_reassert) {
            // Any acknowledgement for a forward profile before this boundary
            // is older than the inverse. Preserve the durable target but rotate
            // the generation so only a post-inverse application can retire it.
            state.recording_profile_recovery_generation.store(
                state.recording_profile_snapshot_observed.load(std::memory_order_acquire)
                    ? NextRecordingProfileRecoveryGeneration()
                    : 0,
                std::memory_order_relaxed);
        }
        state.recording_profile_recovery_action_queued.store(false, std::memory_order_relaxed);
        state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_relaxed);
    }
    ReleaseSRWLockExclusive(&state.recording_profile_recovery_lock);

    if (restore_required)
        PumpEmergencyDeadlockUiRestore();
}

void PumpSmvmOverlayResidentRecovery() noexcept {
    if (g_overlay.recording_profile_restore_debt.load(std::memory_order_acquire) != 0)
        PumpEmergencyDeadlockUiRestore();
}

// --- smvm_ui hooks (declared in smvm_ui.hpp) --------------------------------
// The UI renders the binding capture state machine owned here; SmvmPumpBindingRow
// is a faithful port of the old per-row DrawBindingRow pending/feedback logic.

SmvmBindingRowStatus SmvmPumpBindingRow(
    const std::int32_t action,
    const std::uint32_t current_value) noexcept {
    auto& state = g_overlay;
    SmvmBindingRowStatus status{};
    const auto now = GetTickCount64();
    if (state.binding_pending_action.load(std::memory_order_acquire) == action) {
        const auto expected = state.binding_pending_value.load(std::memory_order_acquire);
        const auto since = state.binding_pending_since_ms.load(std::memory_order_acquire);
        if (current_value == expected || (since > 0 && now - since >= kBindingResponseTimeoutMs)) {
            const auto feedback = current_value == expected
                ? BindingFeedback::saved
                : BindingFeedback::rejected;
            state.binding_pending_action.store(-1, std::memory_order_release);
            state.binding_feedback_action.store(action, std::memory_order_release);
            state.binding_feedback_kind.store(
                static_cast<std::uint32_t>(feedback), std::memory_order_release);
            state.binding_feedback_until_ms.store(now + kBindingFeedbackDurationMs,
                                                   std::memory_order_release);
        }
    }
    if (state.binding_feedback_action.load(std::memory_order_acquire) == action) {
        if (now < state.binding_feedback_until_ms.load(std::memory_order_acquire)) {
            status.feedback_kind = state.binding_feedback_kind.load(std::memory_order_acquire);
        } else {
            state.binding_feedback_action.store(-1, std::memory_order_release);
            state.binding_feedback_kind.store(
                static_cast<std::uint32_t>(BindingFeedback::none), std::memory_order_release);
        }
    }
    status.capturing = state.binding_capture_action.load(std::memory_order_acquire) == action;
    status.pending = state.binding_pending_action.load(std::memory_order_acquire) == action;
    if (status.feedback_kind == static_cast<std::uint32_t>(BindingFeedback::rejected))
        status.conflict_action = state.binding_conflict_action.load(std::memory_order_acquire);
    return status;
}

void SmvmBeginBindingCapture(const std::int32_t action, const std::uint32_t original) noexcept {
    BeginBindingCapture(action, original);
}

void SmvmClearBinding(const std::int32_t action, const std::uint32_t original) noexcept {
    static_cast<void>(SubmitBindingValue(action, 0, original));
}

void SmvmCloseMenu() noexcept {
    SetMenuOpen(false);
}

void SmvmOpenMenu() noexcept {
    SetMenuOpen(true);
}

bool SmvmArmCinematicStart(const std::uint64_t replay_session_generation) noexcept {
    auto& state = g_overlay;
    SmvmSnapshotPayload snapshot{};
    if (!ReadSnapshot(snapshot) ||
        snapshot.replay_session_generation != replay_session_generation) {
        ResetCinematicStartGate();
        return false;
    }
    CampathPayloadHeader header{};
    std::array<CampathKeyframe, kMaxCampathKeyframes> keyframes{};
    if (!ReadPath(header, keyframes.data(), keyframes.size()) ||
        header.keyframe_count < 3 ||
        header.keyframe_count > keyframes.size()) {
        ResetCinematicStartGate();
        return false;
    }

    const auto first_tick = keyframes[0].demo_tick;
    state.cinematic_start_tick.store(first_tick, std::memory_order_release);
    state.cinematic_replay_session_generation.store(
        replay_session_generation,
        std::memory_order_release);
    state.cinematic_start_ready.store(false, std::memory_order_release);
    state.cinematic_space_released.store(false, std::memory_order_release);
    state.cinematic_start_armed.store(true, std::memory_order_release);
    if (QueueActionForSnapshot(snapshot, SmvmActionType::go_to_keyframe, 0))
        return true;

    ResetCinematicStartGate();
    return false;
}

void SmvmSetReplayTickInputActive(const bool active) noexcept {
    auto& state = g_overlay;
    const auto previous = state.replay_tick_input_active.exchange(active, std::memory_order_acq_rel);
    if (previous != active)
        ResetSmvmManualInput();
}

void SmvmReacquireFreeCameraInput() noexcept {
    auto& state = g_overlay;
    if (!state.manual_pointer_requested.load(std::memory_order_acquire)) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::camera_snapshot_not_ready),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        return;
    }
    if (state.menu_open.load(std::memory_order_acquire)) {
        SetMenuOpen(false);
        return;
    }
    const auto window = state.output_window;
    if (window == nullptr || !IsWindow(window) ||
        PostMessageW(window, kSmvmManualPointerTransitionMessage, TRUE, 0) == FALSE) {
        state.manual_input_failure.store(
            static_cast<std::uint32_t>(ManualInputFailure::invalid_window_thread),
            std::memory_order_release);
        state.manual_input_error.store(true, std::memory_order_release);
        SetMenuOpen(true);
    }
}

bool ConsumeSmvmManualInput(
    const SmvmSnapshotPayload& snapshot,
    SmvmManualInputFrame& frame) noexcept {
    frame = {};
    auto& state = g_overlay;
    if (!state.started.load(std::memory_order_acquire) ||
        !CanConsumeManualCameraInput(snapshot)) {
        state.manual_look_right_delta.store(0, std::memory_order_release);
        state.manual_look_up_delta.store(0, std::memory_order_release);
        state.manual_wheel_delta.store(0, std::memory_order_release);
        return false;
    }

    const auto keyboard_ready = CanConsumeManualCameraKeyboardInput(snapshot);
    const auto mouse_ready = CanConsumeManualCameraMouseInput(snapshot);
    const auto binding_down =
        [&state, &snapshot, keyboard_ready](const std::uint32_t binding) noexcept {
        if (!keyboard_ready)
            return false;
        const auto base = binding & kSmvmInputBaseMask;
        if (base == 0 || base >= state.key_down.size())
            return false;

        auto& routes = state.manual_key_routes[base];
        auto& polling_armed = state.manual_poll_armed[base];
        const auto was_polling_armed = polling_armed.load(std::memory_order_acquire);
        const auto polled_down = (GetAsyncKeyState(static_cast<int>(base)) & 0x8000) != 0;
        if (!polled_down) {
            polling_armed.store(true, std::memory_order_release);
            routes.fetch_and(
                static_cast<std::uint8_t>(~kManualPolledKeyRoute),
                std::memory_order_acq_rel);
        } else if (was_polling_armed &&
                   (snapshot.flags & smvm_snapshot_input_takeover) != 0) {
            routes.fetch_or(kManualPolledKeyRoute, std::memory_order_acq_rel);
        }

        // Once foreground polling is armed, a physical-up sample also masks a
        // stale event latch if one delivery channel missed the corresponding
        // release. Event input remains immediately usable during the initial
        // release-arming frame.
        const auto event_down =
            state.key_down[base].load(std::memory_order_acquire) != 0 &&
            (!was_polling_armed || polled_down);
        return ResolveHeldManualBinding(
            event_down,
            polled_down,
            was_polling_armed,
            RequiredInputModifiersAreActive(binding, CurrentModifiers(base)),
            (snapshot.flags & smvm_snapshot_input_takeover) != 0,
            routes.load(std::memory_order_acquire) != 0);
    };

    const auto look_right = state.manual_look_right_delta.exchange(0, std::memory_order_acq_rel);
    const auto look_up = state.manual_look_up_delta.exchange(0, std::memory_order_acq_rel);
    const auto wheel = state.manual_wheel_delta.exchange(0, std::memory_order_acq_rel);
    if (mouse_ready) {
        frame.look_right = static_cast<double>(look_right);
        frame.look_up = static_cast<double>(look_up);
        frame.wheel_steps = static_cast<double>(wheel) / WHEEL_DELTA;
    }
    frame.forward = binding_down(snapshot.forward_key);
    frame.backward = binding_down(snapshot.backward_key);
    frame.left = binding_down(snapshot.left_key);
    frame.right = binding_down(snapshot.right_key);
    frame.up = binding_down(snapshot.up_key);
    frame.down = binding_down(snapshot.down_key);
    frame.fast = binding_down(snapshot.fast_key);
    frame.precision = binding_down(snapshot.precision_key);
    frame.roll_left = binding_down(snapshot.roll_left_key);
    frame.roll_right = binding_down(snapshot.roll_right_key);
    frame.reset_roll = binding_down(snapshot.roll_reset_key);
    return true;
}

void ResetSmvmManualInput() noexcept {
    auto& state = g_overlay;
    state.manual_look_right_delta.store(0, std::memory_order_release);
    state.manual_look_up_delta.store(0, std::memory_order_release);
    state.manual_wheel_delta.store(0, std::memory_order_release);
    state.manual_raw_mouse_observed_ms.store(0, std::memory_order_release);
    state.manual_fallback_mouse_observed_ms.store(0, std::memory_order_release);
    state.manual_fallback_mouse_observed.store(false, std::memory_order_release);
    state.manual_legacy_mouse_seeded.store(false, std::memory_order_release);
    state.manual_discard_next_legacy_sample.store(true, std::memory_order_release);
    state.manual_fallback_settle_until_ms.store(0, std::memory_order_release);
    for (auto& key : state.key_down)
        key.store(0, std::memory_order_release);
    for (auto& routes : state.manual_key_routes)
        routes.store(0, std::memory_order_release);
    for (auto& armed : state.manual_poll_armed)
        armed.store(false, std::memory_order_release);
    ResetBindingCaptureState();
}

void InvalidateSmvmReplaySessionState() noexcept {
    // Space-gate state is replay-owned. Clearing the consumed edge as well as
    // the armed generation ensures replay B always requires a fresh prompt and
    // key press even when it replaces replay A on the same pipe.
    ResetCinematicStartGate(true);
}

bool StartSmvmOverlay(const HMODULE self_module, const SmvmOverlayCallbacks& callbacks) noexcept {
    auto& state = g_overlay;
    if (state.started.exchange(true, std::memory_order_acq_rel))
        return false;
    if (self_module == nullptr || callbacks.read_snapshot == nullptr ||
        callbacks.read_editor_path == nullptr || callbacks.queue_action == nullptr ||
        callbacks.request_camera_capture == nullptr || callbacks.publish_status == nullptr) {
        state.started.store(false, std::memory_order_release);
        return false;
    }
    state.self = self_module;
    state.callbacks = callbacks;
    state.stop_requested.store(false, std::memory_order_release);
    state.hooks_installed.store(false, std::memory_order_release);
    state.factory_hook_reachable.store(false, std::memory_order_release);
    state.present_hook_reachable.store(false, std::memory_order_release);
    state.resize_hook_reachable.store(false, std::memory_order_release);
    state.present_observed.store(false, std::memory_order_release);
    state.ready.store(false, std::memory_order_release);
    state.menu_open.store(false, std::memory_order_release);
    state.replay_tick_input_active.store(false, std::memory_order_release);
    ResetCinematicStartGate(true);
    state.clean_view.store(false, std::memory_order_release);
    state.manual_pointer_requested.store(false, std::memory_order_release);
    state.manual_pointer_active.store(false, std::memory_order_release);
    state.manual_mouse_observed.store(false, std::memory_order_release);
    state.manual_raw_mouse_observed_ms.store(0, std::memory_order_release);
    state.manual_fallback_mouse_observed_ms.store(0, std::memory_order_release);
    state.manual_fallback_mouse_observed.store(false, std::memory_order_release);
    state.manual_legacy_mouse_seeded.store(false, std::memory_order_release);
    state.manual_discard_next_legacy_sample.store(true, std::memory_order_release);
    state.manual_fallback_settle_until_ms.store(0, std::memory_order_release);
    state.manual_keyboard_ready.store(false, std::memory_order_release);
    state.manual_relative_mouse_ready.store(false, std::memory_order_release);
    state.manual_raw_input_ready.store(false, std::memory_order_release);
    state.manual_cursor_ready.store(false, std::memory_order_release);
    state.manual_foreground_ready.store(false, std::memory_order_release);
    state.manual_window_procedure_ready.store(false, std::memory_order_release);
    state.manual_engine_input_ready.store(false, std::memory_order_release);
    state.manual_input_error.store(false, std::memory_order_release);
    state.manual_input_failure.store(0, std::memory_order_release);
    state.manual_raw_registration_disposition.store(0, std::memory_order_release);
    state.manual_relative_mouse_restore_pending = false;
    state.presentation_mode.store(
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui), std::memory_order_release);
    state.previous_visible_mode.store(
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui), std::memory_order_release);
    state.last_vconsole_port.store(29000, std::memory_order_release);
    state.recording_profile_may_be_active.store(false, std::memory_order_release);
    state.recording_profile_restore_debt.store(0, std::memory_order_release);
    state.recording_profile_native_restore_satisfied.store(false, std::memory_order_release);
    state.recording_profile_replay_observed.store(false, std::memory_order_release);
    state.recording_profile_restore_request_epoch.store(0, std::memory_order_release);
    state.recording_profile_recovery_counter.store(0, std::memory_order_release);
    state.recording_profile_recovery_generation.store(0, std::memory_order_release);
    state.recording_profile_recovery_kind.store(
        static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none),
        std::memory_order_release);
    state.recording_profile_recovery_target_mode.store(
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui),
        std::memory_order_release);
    state.recording_profile_recovery_action_queued.store(false, std::memory_order_release);
    state.recording_profile_recovery_action_last_attempt_ms.store(0, std::memory_order_release);
    state.recording_profile_last_managed_mode.store(
        static_cast<std::uint32_t>(DeadlockUiMode::deadlock_ui),
        std::memory_order_release);
    state.recording_profile_snapshot_observed.store(false, std::memory_order_release);
    state.recording_profile_disconnect_restore_guard.store(false, std::memory_order_release);
    state.recording_profile_lease_lost.store(false, std::memory_order_release);
    state.recording_profile_last_ack_generation.store(0, std::memory_order_release);
    state.emergency_restore_attempted.store(false, std::memory_order_release);
    state.emergency_restore_last_attempt_ms.store(0, std::memory_order_release);
    state.input_system = nullptr;
    state.input_system_enable = nullptr;
    state.input_enabled_state_offset = 0;
    state.input_was_enabled = false;
    state.input_restore_pending.store(false, std::memory_order_release);
    g_campath_geometry_cache.valid = false;
    ResetSmvmManualInput();
    ResetConsumedReleaseRoutes();
    state.installer_thread = CreateThread(nullptr, 0, InstallerThread, nullptr, 0, nullptr);
    if (state.installer_thread == nullptr) {
        state.started.store(false, std::memory_order_release);
        PublishStatus(SmvmRendererBackend::none, SmvmRendererError::hook_install_failed);
        return false;
    }
    return true;
}

bool StopSmvmOverlay() noexcept {
    auto& state = g_overlay;
    const auto was_started = state.started.load(std::memory_order_acquire);
    const auto initial_restore_debt =
        state.recording_profile_restore_debt.load(std::memory_order_acquire);
    const auto recovery_pending = state.recording_profile_recovery_kind.load(
        std::memory_order_acquire) !=
        static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none);
    const auto needs_recording_restore = ShouldRestoreRecordingVisualProfileOnHostLoss(
        state.recording_profile_may_be_active.load(std::memory_order_acquire),
        initial_restore_debt != 0,
        (initial_restore_debt & kRecordingProfileHardRestoreDebt) != 0,
        recovery_pending);
    if (!was_started && !needs_recording_restore) {
        return true;
    }
    if (needs_recording_restore) {
        // Publish hard debt before stopping any worker or attempting renderer
        // teardown. Every early-failure route below then leaves recoverable
        // work for the retained backend's renderer-independent pump.
        state.recording_profile_restore_request_epoch.fetch_add(1, std::memory_order_acq_rel);
        state.recording_profile_restore_debt.fetch_or(
            kRecordingProfileAllRestoreDebt, std::memory_order_acq_rel);
    }
    state.stop_requested.store(true, std::memory_order_release);
    state.menu_open.store(false, std::memory_order_release);
    ResetCinematicStartGate(true);
    RequestManualPointerState(false);
    if (state.installer_thread != nullptr) {
        if (WaitForSingleObject(state.installer_thread, 31000) != WAIT_OBJECT_0)
            return false;
        CloseHandle(state.installer_thread);
        state.installer_thread = nullptr;
    }
    if (state.recording_profile_restore_debt.load(std::memory_order_acquire) != 0) {
        // Stop new rendering first, then take the render lock so no in-flight
        // frame can race this bounded final restoration batch.
        auto restore_lock_acquired = false;
        const auto restore_lock_deadline =
            std::chrono::steady_clock::now() + kCallbackDrainTimeout;
        while (std::chrono::steady_clock::now() < restore_lock_deadline) {
            if (!state.render_lock.test_and_set(std::memory_order_acquire)) {
                restore_lock_acquired = true;
                break;
            }
            Sleep(1);
        }
        if (!restore_lock_acquired)
            return false;

        std::uint32_t completed_attempts = 0;
        for (;;) {
            const auto action = RecordingVisualShutdownStep(
                state.recording_profile_restore_debt.load(std::memory_order_acquire) != 0,
                completed_attempts);
            if (action == RecordingVisualShutdownAction::complete)
                break;
            if (action == RecordingVisualShutdownAction::keep_module_resident) {
                state.render_lock.clear(std::memory_order_release);
                return false;
            }

            const auto now = GetTickCount64();
            const auto previous =
                state.emergency_restore_last_attempt_ms.load(std::memory_order_acquire);
            if (previous != 0 && now - previous < kRecordingVisualRestoreRetryMilliseconds) {
                Sleep(static_cast<DWORD>(
                    kRecordingVisualRestoreRetryMilliseconds - (now - previous)));
            }
            PumpEmergencyDeadlockUiRestore();
            ++completed_attempts;
        }
        state.render_lock.clear(std::memory_order_release);
    }
    state.recording_profile_recovery_kind.store(
        static_cast<std::uint32_t>(RecordingProfileRecoveryKind::none),
        std::memory_order_release);
    SetLocalPresentationMode(DeadlockUiMode::deadlock_ui);
    ResetSmvmManualInput();
    if (!was_started)
        return true;

    // Unsubclass first so no new input callback can begin while renderer hooks
    // are being withdrawn. A later subclass above SMVM is deliberately treated
    // as non-restorable: its stored predecessor may still be this DLL.
    const auto window_restored = RestorePublishedWindowProcedure();

    AcquireSRWLockExclusive(&state.hook_lifecycle_lock);
    auto factory_restored = !state.factory_hook_reachable.load(std::memory_order_acquire);
    if (!factory_restored) {
        factory_restored = RestoreObjectVtable(
            state.target_factory,
            state.factory_original_vtable,
            state.factory_hook_vtable);
        if (factory_restored)
            state.factory_hook_reachable.store(false, std::memory_order_release);
    }
    const auto swapchain_reachable =
        state.present_hook_reachable.load(std::memory_order_acquire) ||
        state.resize_hook_reachable.load(std::memory_order_acquire);
    auto swapchain_restored = !swapchain_reachable;
    if (!swapchain_restored) {
        swapchain_restored = RestoreObjectVtable(
            state.hooked_swapchain,
            state.swapchain_original_vtable,
            state.swapchain_hook_vtable);
        if (swapchain_restored) {
            state.present_hook_reachable.store(false, std::memory_order_release);
            state.resize_hook_reachable.store(false, std::memory_order_release);
        }
    }
    ReleaseSRWLockExclusive(&state.hook_lifecycle_lock);
    if (factory_restored && swapchain_restored)
        state.hooks_installed.store(false, std::memory_order_release);

    const auto window_drained = window_restored &&
        WaitForCallbacksToDrain(state.active_window_procedures);
    const auto hooks_drained = factory_restored && swapchain_restored &&
        WaitForCallbacksToDrain(state.active_hooks);
    if (!window_drained || !hooks_drained)
        return false;

    auto render_lock_acquired = false;
    const auto render_deadline = std::chrono::steady_clock::now() + kCallbackDrainTimeout;
    while (std::chrono::steady_clock::now() < render_deadline) {
        if (!state.render_lock.test_and_set(std::memory_order_acquire)) {
            render_lock_acquired = true;
            break;
        }
        Sleep(1);
    }
    if (!render_lock_acquired)
        return false;

    ClearWindowProcedurePublication();
    ReleaseGraphicsResources();
    state.render_lock.clear(std::memory_order_release);

    IUnknown* held_swapchain = state.hooked_swapchain;
    IUnknown* held_factory = state.target_factory;
    auto** swapchain_clone = state.swapchain_hook_vtable;
    auto** factory_clone = state.factory_hook_vtable;
    state.hooked_swapchain = nullptr;
    state.target_factory = nullptr;
    state.swapchain_original_vtable = nullptr;
    state.swapchain_hook_vtable = nullptr;
    state.factory_original_vtable = nullptr;
    state.factory_hook_vtable = nullptr;
    state.original_create_swapchain = nullptr;
    state.original_present = nullptr;
    state.original_resize = nullptr;
    state.input_system = nullptr;
    state.input_system_enable = nullptr;
    state.input_enabled_state_offset = 0;
    state.input_was_enabled = false;
    ReleaseObject(held_swapchain);
    ReleaseObject(held_factory);
    if (swapchain_clone != nullptr)
        VirtualFree(swapchain_clone, 0, MEM_RELEASE);
    if (factory_clone != nullptr)
        VirtualFree(factory_clone, 0, MEM_RELEASE);
    state.callbacks = {};
    state.self = nullptr;
    state.started.store(false, std::memory_order_release);
    state.replay_tick_input_active.store(false, std::memory_order_release);
    ResetCinematicStartGate(true);
    ResetSmvmManualInput();
    ResetConsumedReleaseRoutes();
    return true;
}

} // namespace deadlock_mvm
