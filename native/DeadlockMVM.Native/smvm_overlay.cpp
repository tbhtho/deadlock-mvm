#include "smvm_overlay.hpp"
#include "smvm_input_route.hpp"

#include "campath_math.hpp"
#include "pattern_scan.hpp"
#include "smvm_input_gate.hpp"

#include <Windows.h>
#include <windowsx.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <optional>
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
constexpr std::uint32_t kAtlasWidth = 256;
constexpr std::uint32_t kAtlasHeight = 144;
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
constexpr std::uint8_t kMenuRawKeyRoute = 1u << 0;
constexpr std::uint8_t kMenuWindowKeyRoute = 1u << 1;
constexpr std::int32_t kFirstManualBindingAction = 100;
constexpr std::int32_t kFirstEditorBindingAction = 111;
constexpr std::int32_t kLastBindingAction = 121;
constexpr auto kBindingResponseTimeoutMs = 1500ULL;
constexpr auto kBindingFeedbackDurationMs = 2200ULL;
constexpr UINT kSmvmCursorTransitionMessage = WM_APP + 0x4D0;
constexpr UINT kSmvmRestoreWindowProcedureMessage = WM_APP + 0x4D1;
constexpr UINT kSmvmPointerActionMessage = WM_APP + 0x4D2;
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

struct Glyph final {
    float u0{};
    float v0{};
    float u1{};
    float v1{};
    float width{};
    float height{};
    float advance{};
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

enum class SmvmPage : std::uint32_t {
    replay,
    camera,
    campath,
    visuals,
    capture,
    settings,
};

enum class SmvmSettingsSection : std::uint32_t {
    bindings,
    camera,
    interface_settings,
    advanced,
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
    std::atomic<bool> clean_view{false};
    std::atomic<std::uint32_t> active_hooks{0};
    std::atomic<std::uint32_t> active_window_procedures{0};
    std::atomic<bool> factory_hook_reachable{false};
    std::atomic<bool> present_hook_reachable{false};
    std::atomic<bool> resize_hook_reachable{false};
    std::atomic<std::uint32_t> mouse_buttons{0};
    std::atomic<std::int32_t> mouse_x{0};
    std::atomic<std::int32_t> mouse_y{0};
    std::atomic<std::int32_t> wheel_delta{0};
    std::atomic<std::int64_t> manual_mouse_delta_x{0};
    std::atomic<std::int64_t> manual_mouse_delta_y{0};
    std::atomic<std::int32_t> manual_wheel_delta{0};
    std::array<std::atomic<std::uint8_t>, 256> key_down{};
    std::array<std::atomic<std::uint8_t>, 256> manual_key_routes{};
    std::array<std::atomic<std::uint8_t>, 256> menu_key_routes{};
    std::array<std::atomic<std::uint8_t>, 256> menu_preheld_keys{};
    std::array<SmvmInputRoute, 5> menu_mouse_routes{};
    std::atomic<std::uint64_t> last_wheel_action_ms{0};
    std::atomic<std::uint64_t> raw_wheel_consumed_ms{0};
    std::atomic<std::uint32_t> frame_microseconds{0};
    std::atomic<std::uint32_t> text_edit_mode{0};
    std::atomic<std::uint32_t> text_edit_length{0};
    std::array<std::atomic<std::uint8_t>, 64> text_edit{};
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
    void* input_system{};
    InputSystemEnableFunction input_system_enable{};
    std::uint8_t input_enabled_state_offset{};
    bool input_was_enabled{};
    std::atomic<bool> input_restore_pending{false};
    std::array<RAWINPUTDEVICE, kMaxSuspendedRawMouseRegistrations>
        suspended_raw_mouse_registrations{};
    UINT suspended_raw_mouse_registration_count{};
    std::atomic<bool> raw_mouse_restore_pending{false};

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
    std::array<Glyph, 95> glyphs{};
    std::array<Vertex, kMaxVertices> vertices{};
    std::size_t vertex_count{};
    SmvmPage page{SmvmPage::campath};
    SmvmSettingsSection settings_section{SmvmSettingsSection::bindings};
    int list_scroll{};
    bool frame_left_click{};
    bool frame_right_click{};
    float ui_scale{1.0F};
    float ui_opacity{1.0F};
    float ui_origin_x{};
    float ui_origin_y{};
    bool ui_transform{};
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

constexpr auto kPanel = Color(18, 17, 16, 248);
constexpr auto kPanelRaised = Color(29, 27, 24, 246);
constexpr auto kPanelSoft = Color(24, 23, 21, 238);
constexpr auto kCard = Color(32, 30, 27, 238);
constexpr auto kCardRaised = Color(39, 36, 31, 244);
constexpr auto kControl = Color(47, 43, 37, 238);
constexpr auto kControlHover = Color(62, 55, 45, 248);
constexpr auto kGold = Color(210, 164, 83, 255);
constexpr auto kGoldSoft = Color(145, 105, 49, 226);
constexpr auto kHairline = Color(82, 75, 64, 218);
constexpr auto kText = Color(239, 235, 226, 255);
constexpr auto kMuted = Color(166, 159, 147, 255);
constexpr auto kDarkMuted = Color(102, 97, 88, 230);
constexpr auto kDanger = Color(211, 92, 74, 255);
constexpr auto kGreen = Color(101, 188, 126, 255);
constexpr auto kWhiteTextureU = 0.5F / static_cast<float>(kAtlasWidth);
constexpr auto kWhiteTextureV = 0.5F / static_cast<float>(kAtlasHeight);

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
    if (frame_microseconds > 0)
        state.frame_microseconds.store(frame_microseconds, std::memory_order_release);
    if (state.callbacks.publish_status != nullptr)
        state.callbacks.publish_status(state.callbacks.context, backend, error, flags, frame_microseconds);
}

[[nodiscard]] bool ReadSnapshot(SmvmSnapshotPayload& snapshot) noexcept {
    const auto& callbacks = g_overlay.callbacks;
    return callbacks.read_snapshot != nullptr && callbacks.read_snapshot(callbacks.context, snapshot);
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
    SmvmActionPayload action{type, index, tick, value, camera, {}};
    const auto text_length = std::min(text.size(), action.text.size() - 1);
    if (text_length > 0)
        std::memcpy(action.text.data(), text.data(), text_length);
    return callbacks.queue_action(callbacks.context, action);
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

[[nodiscard]] const char* BindingActionName(const std::int32_t action) noexcept {
    constexpr std::array<const char*, 22> names{
        "FORWARD", "BACKWARD", "LEFT", "RIGHT", "UP", "DOWN", "FAST", "PRECISION",
        "ROLL LEFT", "ROLL RIGHT", "RESET ROLL", "MENU", "ADD KEY", "DELETE KEY",
        "CLEAN VIEW", "PLAY START", "PLAY CURRENT", "STOP", "UNDO", "REDO",
        "SHOW PATH", "SHOW CAMERAS",
    };
    return IsBindingActionIndex(action)
        ? names[static_cast<std::size_t>(action - kFirstManualBindingAction)]
        : nullptr;
}

[[nodiscard]] std::int32_t FindSnapshotBindingConflict(
    const std::int32_t action,
    const std::uint32_t value) noexcept {
    if (value == 0)
        return -1;
    SmvmSnapshotPayload snapshot{};
    if (!ReadSnapshot(snapshot))
        return -1;
    const std::array<std::uint32_t, 22> values{
        snapshot.forward_key, snapshot.backward_key, snapshot.left_key, snapshot.right_key,
        snapshot.up_key, snapshot.down_key, snapshot.fast_key, snapshot.precision_key,
        snapshot.roll_left_key, snapshot.roll_right_key, snapshot.roll_reset_key,
        snapshot.menu_key, snapshot.add_key, snapshot.delete_key, snapshot.clean_view_key,
        snapshot.play_start_key, snapshot.play_current_key, snapshot.stop_key,
        snapshot.undo_key, snapshot.redo_key, snapshot.show_path_key, snapshot.show_cameras_key,
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

[[nodiscard]] std::uint32_t ApplyUiOpacity(const std::uint32_t color) noexcept {
    if (!g_overlay.ui_transform)
        return color;
    const auto alpha = static_cast<std::uint8_t>((color >> 24) & 0xFFu);
    const auto adjusted = static_cast<std::uint8_t>(std::clamp(
        static_cast<float>(alpha) * g_overlay.ui_opacity, 0.0F, 255.0F));
    return (color & 0x00FFFFFFu) | (static_cast<std::uint32_t>(adjusted) << 24);
}

void TransformUiPoint(float& x, float& y) noexcept {
    if (!g_overlay.ui_transform)
        return;
    x = g_overlay.ui_origin_x + (x * g_overlay.ui_scale);
    y = g_overlay.ui_origin_y + (y * g_overlay.ui_scale);
}

void AddTriangle(
    const float x0, const float y0,
    const float x1, const float y1,
    const float x2, const float y2,
    const std::uint32_t color,
    const float u0 = kWhiteTextureU, const float v0 = kWhiteTextureV,
    const float u1 = kWhiteTextureU, const float v1 = kWhiteTextureV,
    const float u2 = kWhiteTextureU, const float v2 = kWhiteTextureV) noexcept {
    auto& state = g_overlay;
    if (state.vertex_count + 3 > state.vertices.size())
        return;
    auto tx0 = x0;
    auto ty0 = y0;
    auto tx1 = x1;
    auto ty1 = y1;
    auto tx2 = x2;
    auto ty2 = y2;
    TransformUiPoint(tx0, ty0);
    TransformUiPoint(tx1, ty1);
    TransformUiPoint(tx2, ty2);
    const auto adjusted_color = ApplyUiOpacity(color);
    state.vertices[state.vertex_count++] = Vertex{tx0, ty0, u0, v0, adjusted_color};
    state.vertices[state.vertex_count++] = Vertex{tx1, ty1, u1, v1, adjusted_color};
    state.vertices[state.vertex_count++] = Vertex{tx2, ty2, u2, v2, adjusted_color};
}

void AddRect(const float x, const float y, const float width, const float height, const std::uint32_t color) noexcept {
    if (width <= 0.0F || height <= 0.0F)
        return;
    AddTriangle(x, y, x + width, y, x + width, y + height, color);
    AddTriangle(x, y, x + width, y + height, x, y + height, color);
}

void AddOutline(
    const float x, const float y, const float width, const float height,
    const std::uint32_t color, const float thickness = 1.0F) noexcept {
    AddRect(x, y, width, thickness, color);
    AddRect(x, y + height - thickness, width, thickness, color);
    AddRect(x, y, thickness, height, color);
    AddRect(x + width - thickness, y, thickness, height, color);
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

void AddText(
    const float x,
    const float y,
    const std::string_view text,
    const std::uint32_t color,
    const float text_scale = 1.0F) noexcept {
    auto cursor = x;
    for (const auto value : text) {
        if (value == '\n') {
            cursor = x;
            continue;
        }
        if (value < 32 || value > 126)
            continue;
        const auto& glyph = g_overlay.glyphs[static_cast<std::size_t>(value - 32)];
        if (value != ' ') {
            AddTriangle(
                cursor, y, cursor + (glyph.width * text_scale), y,
                cursor + (glyph.width * text_scale), y + (glyph.height * text_scale),
                color, glyph.u0, glyph.v0, glyph.u1, glyph.v0, glyph.u1, glyph.v1);
            AddTriangle(
                cursor, y, cursor + (glyph.width * text_scale), y + (glyph.height * text_scale),
                cursor, y + (glyph.height * text_scale),
                color, glyph.u0, glyph.v0, glyph.u1, glyph.v1, glyph.u0, glyph.v1);
        }
        cursor += glyph.advance * text_scale;
    }
}

void AddLimitedText(
    const float x,
    const float y,
    const std::string_view text,
    const std::size_t maximum_characters,
    const std::uint32_t color) noexcept {
    AddText(x, y, text.substr(0, std::min(text.size(), maximum_characters)), color);
}

[[nodiscard]] float TextWidth(const std::string_view text, const float text_scale = 1.0F) noexcept {
    auto width = 0.0F;
    for (const auto value : text) {
        if (value >= 32 && value <= 126)
            width += g_overlay.glyphs[static_cast<std::size_t>(value - 32)].advance * text_scale;
    }
    return width;
}

[[nodiscard]] bool MouseInside(
    const float x, const float y, const float width, const float height) noexcept {
    auto mouse_x = static_cast<float>(g_overlay.mouse_x.load(std::memory_order_relaxed));
    auto mouse_y = static_cast<float>(g_overlay.mouse_y.load(std::memory_order_relaxed));
    if (g_overlay.ui_transform && g_overlay.ui_scale > 0.0F) {
        mouse_x = (mouse_x - g_overlay.ui_origin_x) / g_overlay.ui_scale;
        mouse_y = (mouse_y - g_overlay.ui_origin_y) / g_overlay.ui_scale;
    }
    return mouse_x >= x && mouse_x <= x + width && mouse_y >= y && mouse_y <= y + height;
}

[[nodiscard]] bool Button(
    const float x, const float y, const float width, const float height,
    const std::string_view label, const bool enabled = true, const bool selected = false) noexcept {
    const auto hovered = enabled && MouseInside(x, y, width, height);
    AddRect(x, y, width, height,
            !enabled ? Color(34, 32, 29, 210) :
            (selected ? Color(73, 57, 34, 242) : (hovered ? kControlHover : kControl)));
    AddOutline(x, y, width, height, selected ? kGold : kHairline);
    if (selected)
        AddRect(x, y, 3.0F, height, kGold);
    const auto label_width = TextWidth(label);
    AddText(x + std::max(6.0F, (width - label_width) * 0.5F), y + ((height - 18.0F) * 0.5F),
            label, enabled ? kText : kDarkMuted);
    if (hovered && g_overlay.frame_left_click) {
        g_overlay.frame_left_click = false;
        return true;
    }
    return false;
}

[[nodiscard]] bool Toggle(
    const float x,
    const float y,
    const std::string_view label,
    const bool value,
    const bool enabled = true) noexcept {
    const auto clicked = Button(x, y, 22.0F, 22.0F, value ? "X" : "", enabled, value);
    AddText(x + 30.0F, y + 2.0F, label, enabled ? kText : kDarkMuted);
    return clicked;
}

[[nodiscard]] std::uint32_t OverlayFlags() noexcept {
    std::uint32_t flags = 0;
    if (g_overlay.hooks_installed.load(std::memory_order_acquire)) flags |= smvm_overlay_hook_installed;
    if (g_overlay.present_observed.load(std::memory_order_acquire)) flags |= smvm_overlay_present_observed;
    if (g_overlay.ready.load(std::memory_order_acquire)) flags |= smvm_overlay_ready;
    if (g_overlay.menu_open.load(std::memory_order_acquire)) flags |= smvm_overlay_menu_open;
    if (g_overlay.clean_view.load(std::memory_order_acquire)) flags |= smvm_overlay_clean_view;
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
    double amount) noexcept {
    amount = ApplyCampathEasing(std::clamp(amount, 0.0, 1.0), easing);
    const auto& p1 = keys[segment].camera;
    const auto& p2 = keys[segment + 1].camera;
    if (interpolation == CampathInterpolation::linear)
        return EvaluateLinearCamera(p1, p2, amount);
    const auto p0 = segment > 0 ? keys[segment - 1].camera : ReflectCamera(p1, p2);
    const auto p3 = segment + 2 < count ? keys[segment + 2].camera : ReflectCamera(p2, p1);
    return EvaluateSmoothCamera(p0, p1, p2, p3, amount);
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
    const CampathKeyframe* keys) noexcept {
    if (header.keyframe_count == 0 ||
        (snapshot.flags & (smvm_snapshot_show_path | smvm_snapshot_show_cameras)) == 0)
        return;
    if (!EnsureCampathGeometryCache(header, keys))
        return;
    const auto& cache = g_campath_geometry_cache;
    const auto view_basis = BasisFromAngles(snapshot.camera);
    if ((snapshot.flags & smvm_snapshot_show_path) != 0 && header.keyframe_count >= 2) {
        for (std::size_t index = 0; index < cache.path_line_count; ++index)
            DrawWorldLine(
                cache.path_lines[index].from,
                cache.path_lines[index].to,
                snapshot.camera,
                view_basis,
                kGoldSoft,
                2.0F);
    }

    for (std::uint32_t index = 0; index < header.keyframe_count; ++index) {
        const auto& key = keys[index];
        const auto& marker = cache.markers[index];
        const auto selected = static_cast<std::int32_t>(index) == snapshot.selected_keyframe;
        const auto color = selected ? kGold : Color(174, 177, 178, 210);
        const auto& marker_lines = selected ? marker.selected : marker.regular;
        DrawWorldLine(marker_lines[0].from, marker_lines[0].to, snapshot.camera, view_basis, color,
                      selected ? 2.5F : 1.25F);

        if ((snapshot.flags & smvm_snapshot_show_cameras) != 0) {
            for (std::size_t line = 1; line < marker_lines.size(); ++line)
                DrawWorldLine(marker_lines[line].from, marker_lines[line].to,
                              snapshot.camera, view_basis, color, selected ? 1.8F : 1.0F);
        }

        float screen_x = 0.0F;
        float screen_y = 0.0F;
        if (ProjectCameraPoint(ToCameraSpace(marker.origin, snapshot.camera, view_basis),
                               snapshot.camera, screen_x, screen_y)) {
            const auto radius = selected ? 6.0F : 4.0F;
            AddRect(screen_x - radius, screen_y - radius, radius * 2.0F, radius * 2.0F, color);
            if ((snapshot.flags & smvm_snapshot_show_labels) != 0) {
                std::array<char, 48> label{};
                static_cast<void>(std::snprintf(label.data(), label.size(), "K%u  T%lld", index + 1,
                                                static_cast<long long>(key.demo_tick)));
                const auto label_scale = static_cast<float>(
                    std::clamp(snapshot.path_label_scale, 0.5, 2.0));
                AddText(screen_x + 9.0F, screen_y - (9.0F * label_scale), label.data(),
                        selected ? kGold : kMuted, label_scale);
            }
        }
    }
}

[[nodiscard]] const char* ObserverModeName(const std::uint32_t mode) noexcept {
    switch (mode) {
        case 3: return "IN EYE";
        case 4: return "FREE CAMERA";
        case 5: return "FIXED";
        case 6: return "CHASE";
        default: return "SPECTATOR";
    }
}

[[nodiscard]] const char* PlaybackStateName(const std::uint32_t state) noexcept {
    switch (state) {
        case 0: return "STOPPED";
        case 1: return "VALIDATING";
        case 2: return "PREPARING";
        case 3: return "SEEKING";
        case 4: return "WAITING FOR TICK";
        case 5: return "FREE ROAM";
        case 6: return "TRANSFERRING";
        case 7: return "ARMING CAMERA";
        case 8: return "WAITING FOR CAMERA";
        case 9: return "PLAYING";
        case 10: return "COMPLETED";
        case 11: return "STOPPING";
        case 12: return "STOPPED";
        case 13: return "FAILED";
        case 14: return "CANCELLED";
        default: return "UNAVAILABLE";
    }
}

void DrawCard(
    const float x,
    const float y,
    const float width,
    const float height,
    const std::string_view title,
    const std::string_view eyebrow = {}) noexcept {
    AddRect(x, y, width, height, kCard);
    AddOutline(x, y, width, height, kHairline);
    AddRect(x, y, 3.0F, height, Color(107, 78, 38, 220));
    if (!eyebrow.empty())
        AddText(x + 16.0F, y + 12.0F, eyebrow, kDarkMuted);
    AddText(x + 16.0F, y + (eyebrow.empty() ? 13.0F : 31.0F), title, kText);
}

void DrawValueRow(
    const float x,
    const float y,
    const std::string_view label,
    const std::string_view value,
    const bool available = true,
    const std::uint32_t value_color = kText) noexcept {
    AddText(x, y, label, kMuted);
    AddText(x + 126.0F, y, available ? value : "--", available ? value_color : kDarkMuted);
}

void DrawStatusBadge(
    const float x,
    const float y,
    const std::string_view label,
    const std::uint32_t color) noexcept {
    const auto width = TextWidth(label) + 20.0F;
    AddRect(x, y, width, 24.0F, Color(35, 33, 29, 244));
    AddOutline(x, y, width, 24.0F, color);
    AddRect(x + 7.0F, y + 9.0F, 5.0F, 5.0F, color);
    AddText(x + 17.0F, y + 3.0F, label, color);
}

[[nodiscard]] const char* CameraAvailabilityName(const CameraAvailability availability) noexcept {
    switch (availability) {
        case CameraAvailability::initializing: return "INITIALIZING";
        case CameraAvailability::ready: return "READY";
        case CameraAvailability::replay_unavailable: return "REPLAY UNAVAILABLE";
        case CameraAvailability::replay_seeking: return "REPLAY SEEKING";
        case CameraAvailability::not_in_free_roam: return "FREE ROAM REQUIRED";
        case CameraAvailability::observer_target_active: return "TARGET ACTIVE";
        case CameraAvailability::camera_manager_unavailable: return "MANAGER UNAVAILABLE";
        case CameraAvailability::camera_object_unavailable: return "CAMERA UNAVAILABLE";
        case CameraAvailability::camera_readback_unavailable: return "READBACK UNAVAILABLE";
        case CameraAvailability::native_backend_disconnected: return "NATIVE DISCONNECTED";
        case CameraAvailability::signature_unavailable: return "SIGNATURE UNAVAILABLE";
        case CameraAvailability::managed_host_disconnected: return "HOST DISCONNECTED";
        case CameraAvailability::ownership_held_by_campath: return "CAMPATH OWNS CAMERA";
        case CameraAvailability::ownership_rejected: return "OWNERSHIP REJECTED";
        case CameraAvailability::snapshot_stale: return "SNAPSHOT STALE";
        case CameraAvailability::protocol_mismatch: return "PROTOCOL MISMATCH";
        default: return "UNAVAILABLE";
    }
}

[[nodiscard]] const char* CameraOwnershipName(const CameraOwnership ownership) noexcept {
    switch (ownership) {
        case CameraOwnership::deadlock_spectator: return "DEADLOCK";
        case CameraOwnership::smvm_manual_camera: return "SMVM MANUAL";
        case CameraOwnership::smvm_restore: return "SMVM SHOT";
        case CameraOwnership::smvm_campath: return "SMVM CAMPATH";
        default: return "NONE";
    }
}

void DrawMenuHeader(const float width, const SmvmSnapshotPayload& snapshot) noexcept {
    AddText(24.0F, 18.0F, "SMVM", kGold, 1.12F);
    AddText(90.0F, 20.0F, "REPLAY MOVIE MAKER", kText);
    AddText(24.0F, 43.0F, "IN-PROCESS DIRECTOR", kDarkMuted);
    const auto online = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    const auto label = online ? "REPLAY ONLINE" : "REPLAY OFFLINE";
    DrawStatusBadge(width - TextWidth(label) - 44.0F, 20.0F, label, online ? kGreen : kDanger);
    AddRect(18.0F, 68.0F, width - 36.0F, 1.0F, kHairline);
}

void DrawNavigation(const float x, const float y, const float height) noexcept {
    struct NavigationItem final { const char* name; SmvmPage page; };
    constexpr std::array<NavigationItem, 5> items{{
        {"REPLAY", SmvmPage::replay},
        {"CAMERA", SmvmPage::camera},
        {"CAMPATH", SmvmPage::campath},
        {"VISUALS", SmvmPage::visuals},
        {"CAPTURE", SmvmPage::capture},
    }};
    AddText(x + 8.0F, y, "WORKSPACE", kDarkMuted);
    auto item_y = y + 28.0F;
    for (const auto& item : items) {
        if (Button(x, item_y, 144.0F, 40.0F, item.name, true, g_overlay.page == item.page))
            g_overlay.page = item.page;
        item_y += 48.0F;
    }
    AddRect(x, y + height - 96.0F, 144.0F, 1.0F, kHairline);
    if (Button(x, y + height - 82.0F, 144.0F, 40.0F, "SETTINGS", true,
               g_overlay.page == SmvmPage::settings))
        g_overlay.page = SmvmPage::settings;
    AddText(x + 9.0F, y + height - 27.0F, "TAB  CLOSE", kGold);
}

void BeginTickEdit(const std::int64_t current_tick) noexcept;

void DrawReplayPage(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    AddText(x, y, "REPLAY", kGold, 1.08F);
    AddText(x + 84.0F, y + 1.0F,
            snapshot.replay_name[0] != '\0' ? snapshot.replay_name.data() : "UNTITLED REPLAY", kText);

    DrawCard(x, y + 38.0F, width, 104.0F, "SESSION", "LIVE REPLAY CLOCK");
    std::array<char, 48> value{};
    const auto tick_known = snapshot.current_tick >= 0;
    const auto total_known = snapshot.total_ticks > 0;
    if (tick_known)
        static_cast<void>(std::snprintf(value.data(), value.size(), "%lld",
                                        static_cast<long long>(snapshot.current_tick)));
    DrawValueRow(x + 18.0F, y + 88.0F, "CURRENT TICK", value.data(), tick_known, kGold);
    value = {};
    if (total_known)
        static_cast<void>(std::snprintf(value.data(), value.size(), "%lld",
                                        static_cast<long long>(snapshot.total_ticks)));
    DrawValueRow(x + 258.0F, y + 88.0F, "TOTAL TICKS", value.data(), total_known);
    const auto pause_known = (snapshot.flags & smvm_snapshot_pause_known) != 0;
    DrawValueRow(x + 492.0F, y + 88.0F, "STATE",
                 (snapshot.flags & smvm_snapshot_paused) != 0 ? "PAUSED" : "PLAYING",
                 pause_known, pause_known && (snapshot.flags & smvm_snapshot_paused) != 0 ? kGold : kGreen);

    DrawCard(x, y + 154.0F, width, 132.0F, "TRANSPORT", "DISCRETE CONTROLS - NO TIMELINE");
    const auto replay_active = (snapshot.flags & smvm_snapshot_replay_active) != 0;
    auto button_x = x + 18.0F;
    if (Button(button_x, y + 204.0F, 110.0F, 36.0F,
               (snapshot.flags & smvm_snapshot_paused) != 0 ? "PLAY" : "PAUSE",
               replay_active && pause_known))
        static_cast<void>(QueueAction(SmvmActionType::toggle_replay_pause));
    button_x += 120.0F;
    if (Button(button_x, y + 204.0F, 88.0F, 36.0F, "- 1 TICK", replay_active && tick_known))
        static_cast<void>(QueueAction(SmvmActionType::step_back));
    button_x += 98.0F;
    if (Button(button_x, y + 204.0F, 88.0F, 36.0F, "+ 1 TICK", replay_active && tick_known))
        static_cast<void>(QueueAction(SmvmActionType::step_forward));
    button_x += 98.0F;
    if (Button(button_x, y + 204.0F, 112.0F, 36.0F, "GO TO TICK", replay_active && tick_known))
        BeginTickEdit(snapshot.current_tick);
    AddText(x + 18.0F, y + 254.0F,
            pause_known ? "PAUSE STATE IS AUTHORITATIVE" : "PAUSE STATE  --  WAITING FOR REPLAY HOST", kMuted);

    DrawCard(x, y + 298.0F, width, 100.0F, "TIMESCALE", "REPLAY SPEED");
    constexpr std::array<double, 5> speeds{0.25, 0.5, 1.0, 2.0, 4.0};
    button_x = x + 18.0F;
    for (const auto speed : speeds) {
        std::array<char, 16> label{};
        static_cast<void>(std::snprintf(label.data(), label.size(), "%.2gx", speed));
        if (Button(button_x, y + 348.0F, 74.0F, 32.0F, label.data(), replay_active,
                   std::abs(snapshot.timescale - speed) < 0.001))
            static_cast<void>(QueueAction(SmvmActionType::set_timescale, -1, -1, speed));
        button_x += 84.0F;
    }

    DrawCard(x, y + 410.0F, width, 92.0F, "STATUS", "HOST FEEDBACK");
    AddLimitedText(x + 18.0F, y + 460.0F,
                   snapshot.status[0] != '\0' ? snapshot.status.data() : "READY", 80, kMuted);
}

void DrawCameraPage(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    AddText(x, y, "CAMERA", kGold, 1.08F);
    const auto camera_ready = (snapshot.flags & smvm_snapshot_camera_readable) != 0 &&
        snapshot.camera_availability == CameraAvailability::ready;
    const auto availability_color = camera_ready ? kGreen : kDanger;
    DrawStatusBadge(x + width - TextWidth(CameraAvailabilityName(snapshot.camera_availability)) - 40.0F,
                    y - 2.0F, CameraAvailabilityName(snapshot.camera_availability), availability_color);

    const auto column_width = (width - 12.0F) * 0.5F;
    const auto right_x = x + column_width + 12.0F;
    DrawCard(x, y + 38.0F, column_width, 174.0F, "SPECTATOR MODE", "DEADLOCK OBSERVER");
    DrawValueRow(x + 18.0F, y + 89.0F, "MODE", ObserverModeName(snapshot.observer_mode), true, kGold);
    DrawValueRow(x + 18.0F, y + 115.0F, "OWNER", CameraOwnershipName(snapshot.camera_ownership));
    auto button_x = x + 18.0F;
    if (Button(button_x, y + 147.0F, 92.0F, 30.0F, "FREE ROAM", true, snapshot.observer_mode == 4))
        static_cast<void>(QueueAction(SmvmActionType::free_roam));
    button_x += 100.0F;
    if (Button(button_x, y + 147.0F, 64.0F, 30.0F, "IN EYE", true, snapshot.observer_mode == 3))
        static_cast<void>(QueueAction(SmvmActionType::in_eye));
    button_x += 72.0F;
    if (Button(button_x, y + 147.0F, 64.0F, 30.0F, "CHASE", true, snapshot.observer_mode == 6))
        static_cast<void>(QueueAction(SmvmActionType::chase));
    if (Button(x + 18.0F, y + 181.0F, 82.0F, 24.0F, "PREV TARGET"))
        static_cast<void>(QueueAction(SmvmActionType::previous_player));
    if (Button(x + 108.0F, y + 181.0F, 82.0F, 24.0F, "NEXT TARGET"))
        static_cast<void>(QueueAction(SmvmActionType::next_player));

    DrawCard(right_x, y + 38.0F, column_width, 174.0F, "LIVE TRANSFORM", "AUTHORITATIVE READBACK");
    std::array<char, 52> live{};
    if (camera_ready)
        static_cast<void>(std::snprintf(live.data(), live.size(), "%.2f / %.2f / %.2f",
                                        snapshot.camera.x, snapshot.camera.y, snapshot.camera.z));
    DrawValueRow(right_x + 18.0F, y + 89.0F, "POSITION", live.data(), camera_ready);
    live = {};
    if (camera_ready)
        static_cast<void>(std::snprintf(live.data(), live.size(), "%.2f / %.2f",
                                        snapshot.camera.pitch, snapshot.camera.yaw));
    DrawValueRow(right_x + 18.0F, y + 117.0F, "PITCH / YAW", live.data(), camera_ready);
    live = {};
    if (camera_ready)
        static_cast<void>(std::snprintf(live.data(), live.size(), "%.2f", snapshot.camera.roll));
    DrawValueRow(right_x + 18.0F, y + 145.0F, "ROLL", live.data(), camera_ready);
    AddLimitedText(right_x + 18.0F, y + 180.0F,
                   camera_ready ? "LIVE SAMPLE" :
                   (snapshot.camera_status[0] != '\0' ? snapshot.camera_status.data() : "CAMERA DATA  --"),
                   38, camera_ready ? kGreen : kDarkMuted);

    DrawCard(x, y + 224.0F, column_width, 278.0F, "LENS + ROLL", "GAME-THREAD VALUES");
    const auto fov_writable = camera_ready && (snapshot.flags & smvm_snapshot_fov_writable) != 0;
    std::array<char, 32> lens{};
    if (camera_ready)
        static_cast<void>(std::snprintf(lens.data(), lens.size(), "%.1f DEG", snapshot.camera.fov));
    DrawValueRow(x + 18.0F, y + 276.0F, "ACTIVE FOV", lens.data(), camera_ready,
                 fov_writable ? kGold : kText);
    button_x = x + 18.0F;
    if (Button(button_x, y + 306.0F, 34.0F, 30.0F, "-", fov_writable))
        static_cast<void>(QueueAction(SmvmActionType::set_fov, -1, -1,
                                      std::clamp(snapshot.camera.fov - snapshot.fov_step, kMinFov, kMaxFov)));
    button_x += 42.0F;
    constexpr std::array<double, 4> fovs{30.0, 45.0, 60.0, 90.0};
    for (const auto fov : fovs) {
        std::array<char, 12> label{};
        static_cast<void>(std::snprintf(label.data(), label.size(), "%.0f", fov));
        if (Button(button_x, y + 306.0F, 46.0F, 30.0F, label.data(), fov_writable,
                   camera_ready && std::abs(snapshot.camera.fov - fov) < 0.1))
            static_cast<void>(QueueAction(SmvmActionType::set_fov, -1, -1, fov));
        button_x += 52.0F;
    }
    if (Button(button_x, y + 306.0F, 34.0F, 30.0F, "+", fov_writable))
        static_cast<void>(QueueAction(SmvmActionType::set_fov, -1, -1,
                                      std::clamp(snapshot.camera.fov + snapshot.fov_step, kMinFov, kMaxFov)));

    const auto rendered_roll = (snapshot.capabilities & smvm_capability_rendered_roll) != 0;
    const auto roll_writable = camera_ready && rendered_roll &&
        (snapshot.flags & smvm_snapshot_roll_writable) != 0;
    lens = {};
    if (camera_ready && rendered_roll)
        static_cast<void>(std::snprintf(lens.data(), lens.size(), "%.1f DEG", snapshot.camera.roll));
    DrawValueRow(x + 18.0F, y + 356.0F, "ROLL", lens.data(), camera_ready && rendered_roll,
                 roll_writable ? kGold : kText);
    constexpr std::array<double, 3> roll_targets{-15.0, 0.0, 15.0};
    constexpr std::array<const char*, 3> roll_labels{"-15", "RESET 0", "+15"};
    constexpr std::array<float, 3> roll_widths{62.0F, 88.0F, 62.0F};
    button_x = x + 18.0F;
    for (std::size_t index = 0; index < roll_targets.size(); ++index) {
        if (Button(button_x, y + 386.0F, roll_widths[index], 30.0F, roll_labels[index], roll_writable,
                   roll_writable && std::abs(snapshot.camera.roll - roll_targets[index]) < 0.1))
            static_cast<void>(QueueAction(SmvmActionType::set_roll, -1, -1, roll_targets[index]));
        button_x += roll_widths[index] + 8.0F;
    }
    AddText(x + 18.0F, y + 434.0F,
            rendered_roll ? "WHEEL ADJUSTS FOV IN FREE ROAM" : "RENDERED ROLL  --  NOT VERIFIED",
            rendered_roll ? kMuted : kDarkMuted);

    DrawCard(right_x, y + 224.0F, column_width, 278.0F, "SHOT + MANUAL CAMERA", "SMVM OWNERSHIP");
    const auto manual_capable = (snapshot.capabilities & smvm_capability_manual_camera) != 0;
    const auto manual_requested = (snapshot.flags & smvm_snapshot_manual_camera_requested) != 0;
    const auto manual_active = (snapshot.flags & smvm_snapshot_manual_camera_active) != 0;
    const auto manual_available = manual_capable &&
        (manual_requested || (camera_ready && snapshot.observer_mode == 4 &&
         (snapshot.flags & smvm_snapshot_campath_playing) == 0));
    DrawValueRow(right_x + 18.0F, y + 276.0F, "MANUAL CAMERA",
                 manual_active ? "ACTIVE" : (manual_requested ? "REQUESTED" : "OFF"),
                 manual_capable, manual_active ? kGreen : kText);
    DrawValueRow(right_x + 18.0F, y + 304.0F, "INPUT",
                 (snapshot.flags & smvm_snapshot_input_takeover) != 0 ? "TEMPORARY TAKEOVER" : "SHARED",
                 manual_capable);
    if (Button(right_x + 18.0F, y + 340.0F, column_width - 36.0F, 38.0F,
               manual_requested ? "STOP MANUAL CAMERA" : "START MANUAL CAMERA", manual_available,
               manual_requested))
        static_cast<void>(QueueAction(SmvmActionType::toggle_manual_camera));
    if (Button(right_x + 18.0F, y + 390.0F, 118.0F, 32.0F, "SAVE CAMERA", camera_ready))
        static_cast<void>(QueueAction(SmvmActionType::save_camera));
    static_cast<void>(Button(right_x + 146.0F, y + 390.0F, 132.0F, 32.0F, "RESTORE CAMERA", false));
    AddText(right_x + 18.0F, y + 438.0F,
            manual_capable ? "RESTORE  --  SAVED-SHOT STATE NOT PUBLISHED" :
                             "MANUAL CAMERA  --  CAPABILITY UNAVAILABLE",
            kDarkMuted);
    if (Button(right_x + 18.0F, y + 466.0F, 116.0F, 25.0F, "REACQUIRE", manual_capable))
        static_cast<void>(QueueAction(SmvmActionType::reacquire_camera));
    AddText(right_x + 144.0F, y + 470.0F,
            manual_capable ? "REBASE FROM LIVE CAMERA" : "CAPABILITY  --", kDarkMuted);
}

void BeginTextEdit(const std::uint32_t mode, const std::string_view initial_value) noexcept {
    auto& state = g_overlay;
    const auto length = static_cast<std::uint32_t>(std::min(initial_value.size(), state.text_edit.size() - 1));
    for (std::size_t index = 0; index < state.text_edit.size(); ++index)
        state.text_edit[index].store(index < length ? static_cast<std::uint8_t>(initial_value[index]) : 0,
                                     std::memory_order_relaxed);
    state.text_edit_length.store(length, std::memory_order_release);
    state.text_edit_mode.store(mode, std::memory_order_release);
}

void BeginPathNameEdit(const std::string_view current_name) noexcept {
    BeginTextEdit(1, current_name);
}

void BeginTickEdit(const std::int64_t current_tick) noexcept {
    std::array<char, 32> tick{};
    static_cast<void>(std::snprintf(tick.data(), tick.size(), "%lld",
                                    static_cast<long long>(std::max<std::int64_t>(0, current_tick))));
    BeginTextEdit(2, tick.data());
}

[[nodiscard]] std::array<char, 64> ReadEditText() noexcept {
    std::array<char, 64> text{};
    const auto length = std::min<std::uint32_t>(
        g_overlay.text_edit_length.load(std::memory_order_acquire),
        static_cast<std::uint32_t>(text.size() - 1));
    for (std::uint32_t index = 0; index < length; ++index)
        text[index] = static_cast<char>(g_overlay.text_edit[index].load(std::memory_order_relaxed));
    return text;
}

void DrawTextEditor(const float x, const float y) noexcept {
    const auto mode = g_overlay.text_edit_mode.load(std::memory_order_acquire);
    if (mode == 0)
        return;
    const auto text = ReadEditText();
    AddRect(x, y, 440.0F, 116.0F, Color(10, 11, 13, 248));
    AddOutline(x, y, 440.0F, 116.0F, kGold);
    AddText(x + 18.0F, y + 14.0F, mode == 2 ? "GO TO REPLAY TICK" : "NAME CAMPATH", kGold);
    AddRect(x + 18.0F, y + 42.0F, 404.0F, 34.0F, kPanelRaised);
    AddOutline(x + 18.0F, y + 42.0F, 404.0F, 34.0F, Color(103, 94, 75, 230));
    AddText(x + 28.0F, y + 48.0F, text.data(), kText);
    AddRect(x + 28.0F + TextWidth(text.data()), y + 49.0F, 1.0F, 19.0F, kGold);
    AddText(x + 18.0F, y + 88.0F,
            mode == 2 ? "ENTER  SEEK     ESC  CANCEL" : "ENTER  APPLY     ESC  CANCEL", kMuted);
}

void DrawCampathPage(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot,
    const CampathPayloadHeader& path_header,
    const CampathKeyframe* keys,
    const bool has_path) noexcept {
    AddText(x, y, "CAMPATH", kGold, 1.08F);
    AddText(x + 98.0F, y + 1.0F,
            snapshot.path_name[0] != '\0' ? snapshot.path_name.data() : "UNTITLED PATH", kText);
    const auto playing = (snapshot.flags & smvm_snapshot_campath_playing) != 0;
    const auto playback_color = snapshot.playback_state == 13 ? kDanger : (playing ? kGreen : kMuted);
    DrawStatusBadge(x + width - TextWidth(PlaybackStateName(snapshot.playback_state)) - 40.0F,
                    y - 2.0F, PlaybackStateName(snapshot.playback_state), playback_color);
    if (Button(x + width - 86.0F, y + 32.0F, 86.0F, 26.0F, "RENAME", !playing))
        BeginPathNameEdit(snapshot.path_name.data());

    constexpr auto list_width = 344.0F;
    constexpr auto row_height = 27.0F;
    constexpr auto top_y = 68.0F;
    DrawCard(x, y + top_y, list_width, 278.0F, "KEYFRAMES", "CAMERA SAMPLES");
    std::array<char, 24> count_label{};
    static_cast<void>(std::snprintf(count_label.data(), count_label.size(), "%u KEYS", path_header.keyframe_count));
    AddText(x + list_width - TextWidth(count_label.data()) - 16.0F, y + top_y + 31.0F,
            count_label.data(), kGold);
    AddText(x + 14.0F, y + top_y + 59.0F, "#   TICK         FOV     ROLL", kMuted);
    AddRect(x + 12.0F, y + top_y + 80.0F, list_width - 24.0F, 1.0F, kHairline);
    const auto has_keys = has_path && path_header.keyframe_count > 0;
    if (has_keys) {
        const auto max_rows = 7;
        const auto maximum_scroll = std::max(0, static_cast<int>(path_header.keyframe_count) - max_rows);
        g_overlay.list_scroll = std::clamp(g_overlay.list_scroll, 0, maximum_scroll);
        for (auto row = 0; row < max_rows; ++row) {
            const auto index = g_overlay.list_scroll + row;
            if (index >= static_cast<int>(path_header.keyframe_count))
                break;
            const auto row_y = y + top_y + 87.0F + (row * row_height);
            const auto selected = index == snapshot.selected_keyframe;
            const auto hovered = MouseInside(x + 10.0F, row_y, list_width - 20.0F, row_height - 2.0F);
            if (selected || hovered)
                AddRect(x + 10.0F, row_y, list_width - 20.0F, row_height - 2.0F,
                        selected ? Color(78, 59, 34, 235) : kCardRaised);
            std::array<char, 80> row_text{};
            static_cast<void>(std::snprintf(row_text.data(), row_text.size(), "%02d  %-10lld  %5.1f  %+6.1f",
                                            index + 1, static_cast<long long>(keys[index].demo_tick),
                                            keys[index].camera.fov, keys[index].camera.roll));
            AddText(x + 15.0F, row_y + 4.0F, row_text.data(), selected ? kText : kMuted);
            if (hovered && g_overlay.frame_left_click) {
                static_cast<void>(QueueAction(SmvmActionType::select_keyframe, index));
                g_overlay.frame_left_click = false;
            }
        }
        AddText(x + 14.0F, y + top_y + 254.0F, "WHEEL  SCROLL KEYFRAMES", kDarkMuted);
    } else {
        AddText(x + 92.0F, y + top_y + 122.0F, "NO KEYFRAMES YET", kText);
        AddText(x + 48.0F, y + top_y + 153.0F, "ENTER FREE ROAM, FRAME A SHOT,", kMuted);
        AddText(x + 59.0F, y + top_y + 176.0F, "THEN ADD THE CURRENT CAMERA.", kMuted);
    }

    const auto inspector_x = x + list_width + 12.0F;
    const auto inspector_width = width - list_width - 12.0F;
    DrawCard(inspector_x, y + top_y, inspector_width, 278.0F, "INSPECTOR", "SELECTED KEYFRAME");
    const auto editing_enabled = !playing;
    const auto selection_valid = has_keys && snapshot.selected_keyframe >= 0 &&
        static_cast<std::uint32_t>(snapshot.selected_keyframe) < path_header.keyframe_count;
    if (selection_valid) {
        const auto& selected = keys[snapshot.selected_keyframe];
        std::array<char, 64> value{};
        static_cast<void>(std::snprintf(value.data(), value.size(), "KEY %02d", snapshot.selected_keyframe + 1));
        AddText(inspector_x + 16.0F, y + top_y + 58.0F, value.data(), kGold);
        static_cast<void>(std::snprintf(value.data(), value.size(), "%lld",
                                        static_cast<long long>(selected.demo_tick)));
        DrawValueRow(inspector_x + 16.0F, y + top_y + 86.0F, "TICK", value.data());
        static_cast<void>(std::snprintf(value.data(), value.size(), "%.1f / %.1f / %.1f",
                                        selected.camera.x, selected.camera.y, selected.camera.z));
        DrawValueRow(inspector_x + 16.0F, y + top_y + 112.0F, "POSITION", value.data());
        static_cast<void>(std::snprintf(value.data(), value.size(), "%.1f / %.1f",
                                        selected.camera.pitch, selected.camera.yaw));
        DrawValueRow(inspector_x + 16.0F, y + top_y + 138.0F, "PITCH / YAW", value.data());
        static_cast<void>(std::snprintf(value.data(), value.size(), "%.1f / %.1f",
                                        selected.camera.fov, selected.camera.roll));
        DrawValueRow(inspector_x + 16.0F, y + top_y + 164.0F, "FOV / ROLL", value.data(), true, kGold);
    } else {
        AddText(inspector_x + 60.0F, y + top_y + 110.0F, "SELECT A KEYFRAME", kMuted);
        AddText(inspector_x + 42.0F, y + top_y + 138.0F, "TO INSPECT ITS CAMERA", kDarkMuted);
    }

    auto button_x = inspector_x + 16.0F;
    if (Button(button_x, y + top_y + 198.0F, 102.0F, 30.0F, "ADD CURRENT", editing_enabled)) {
        if (g_overlay.callbacks.request_camera_capture != nullptr)
            g_overlay.callbacks.request_camera_capture(g_overlay.callbacks.context);
    }
    button_x += 110.0F;
    if (Button(button_x, y + top_y + 198.0F, 74.0F, 30.0F, "UPDATE", editing_enabled && selection_valid))
        static_cast<void>(QueueAction(SmvmActionType::update_keyframe, snapshot.selected_keyframe));
    button_x += 82.0F;
    if (Button(button_x, y + top_y + 198.0F, 70.0F, 30.0F, "DELETE", editing_enabled && selection_valid))
        static_cast<void>(QueueAction(SmvmActionType::delete_keyframe, snapshot.selected_keyframe));
    button_x = inspector_x + 16.0F;
    if (Button(button_x, y + top_y + 236.0F, 74.0F, 27.0F, "GO TO", selection_valid))
        static_cast<void>(QueueAction(SmvmActionType::go_to_keyframe, snapshot.selected_keyframe));
    button_x += 82.0F;
    if (Button(button_x, y + top_y + 236.0F, 64.0F, 27.0F, "UNDO", editing_enabled))
        static_cast<void>(QueueAction(SmvmActionType::undo_edit));
    button_x += 72.0F;
    if (Button(button_x, y + top_y + 236.0F, 64.0F, 27.0F, "REDO", editing_enabled))
        static_cast<void>(QueueAction(SmvmActionType::redo_edit));

    const auto playback_y = y + 358.0F;
    DrawCard(x, playback_y, width, 150.0F, "PLAYBACK", "PATH EXECUTION + SHAPE");
    const auto can_play = has_keys && path_header.keyframe_count >= 2 && editing_enabled;
    button_x = x + 16.0F;
    if (Button(button_x, playback_y + 48.0F, 122.0F, 32.0F, "PLAY FROM START", can_play))
        static_cast<void>(QueueAction(SmvmActionType::play_from_start));
    button_x += 130.0F;
    if (Button(button_x, playback_y + 48.0F, 142.0F, 32.0F, "PLAY FROM CURRENT", can_play))
        static_cast<void>(QueueAction(SmvmActionType::play_from_current));
    button_x += 150.0F;
    if (Button(button_x, playback_y + 48.0F, 66.0F, 32.0F, "STOP", playing))
        static_cast<void>(QueueAction(SmvmActionType::stop_campath));
    button_x += 74.0F;
    if (Button(button_x, playback_y + 48.0F, 62.0F, 32.0F, "SAVE", has_keys))
        static_cast<void>(QueueAction(SmvmActionType::save_path));
    button_x += 70.0F;
    if (Button(button_x, playback_y + 48.0F, 62.0F, 32.0F, "LOAD", editing_enabled))
        static_cast<void>(QueueAction(SmvmActionType::load_next_path));
    button_x += 70.0F;
    if (Button(button_x, playback_y + 48.0F, 62.0F, 32.0F, "CLEAR", editing_enabled && has_keys))
        static_cast<void>(QueueAction(SmvmActionType::clear_path));

    AddText(x + 16.0F, playback_y + 92.0F, "INTERPOLATION", kDarkMuted);
    if (Button(x + 16.0F, playback_y + 112.0F, 68.0F, 27.0F, "LINEAR", editing_enabled,
               snapshot.interpolation == CampathInterpolation::linear))
        static_cast<void>(QueueAction(SmvmActionType::set_interpolation, 0));
    if (Button(x + 90.0F, playback_y + 112.0F, 72.0F, 27.0F, "SMOOTH", editing_enabled,
               snapshot.interpolation == CampathInterpolation::smooth))
        static_cast<void>(QueueAction(SmvmActionType::set_interpolation, 1));

    AddText(x + 178.0F, playback_y + 92.0F, "EASING", kDarkMuted);
    constexpr std::array<const char*, 4> easing_labels{"LINEAR", "IN", "OUT", "IN/OUT"};
    for (std::int32_t index = 0; index < static_cast<std::int32_t>(easing_labels.size()); ++index) {
        if (Button(x + 178.0F + (index * 53.0F), playback_y + 112.0F, 48.0F, 27.0F,
                   easing_labels[index], editing_enabled,
                   static_cast<std::uint32_t>(index) == static_cast<std::uint32_t>(snapshot.easing)))
            static_cast<void>(QueueAction(SmvmActionType::set_easing, index));
    }

    AddText(x + 410.0F, playback_y + 92.0F, "ON END", kDarkMuted);
    if (Button(x + 410.0F, playback_y + 112.0F, 118.0F, 27.0F, "STOP + RELEASE", editing_enabled,
               snapshot.end_behavior == CampathEndBehavior::stop_and_release))
        static_cast<void>(QueueAction(SmvmActionType::set_end_behavior, 0));
    if (Button(x + 536.0F, playback_y + 112.0F, 98.0F, 27.0F, "HOLD FINAL", editing_enabled,
               snapshot.end_behavior == CampathEndBehavior::hold_final_camera))
        static_cast<void>(QueueAction(SmvmActionType::set_end_behavior, 1));
    if (snapshot.end_behavior == CampathEndBehavior::loop)
        AddText(x + 642.0F, playback_y + 117.0F, "LOOP", kDarkMuted);

    AddText(x, y + 516.0F, "ADD / DELETE / PLAY BINDINGS ARE CONFIGURABLE IN SETTINGS", kDarkMuted);
}

[[nodiscard]] std::array<char, 48> FormatInput(const std::uint32_t input) noexcept {
    std::array<char, 48> result{};
    std::array<char, 20> generated{};
    const auto base = input & kSmvmInputBaseMask;
    const char* base_name = generated.data();
    if (base == 0) {
        base_name = "--";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_middle)) {
        base_name = "MOUSE3";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x1)) {
        base_name = "MOUSE4";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::mouse_x2)) {
        base_name = "MOUSE5";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::wheel_up)) {
        base_name = "WHEEL UP";
    } else if (base == static_cast<std::uint32_t>(SmvmInputCode::wheel_down)) {
        base_name = "WHEEL DOWN";
    } else if (base >= 'A' && base <= 'Z') {
        generated[0] = static_cast<char>(base);
        generated[1] = '\0';
    } else if (base >= '0' && base <= '9') {
        generated[0] = static_cast<char>(base);
        generated[1] = '\0';
    } else if (base >= VK_F1 && base <= VK_F24) {
        static_cast<void>(std::snprintf(generated.data(), generated.size(), "F%u", base - VK_F1 + 1));
    } else {
        switch (base) {
            case VK_TAB: base_name = "TAB"; break;
            case VK_RETURN: base_name = "ENTER"; break;
            case VK_ESCAPE: base_name = "ESC"; break;
            case VK_SPACE: base_name = "SPACE"; break;
            case VK_CONTROL: case VK_LCONTROL: case VK_RCONTROL: base_name = "CTRL"; break;
            case VK_MENU: case VK_LMENU: case VK_RMENU: base_name = "ALT"; break;
            case VK_SHIFT: case VK_LSHIFT: case VK_RSHIFT: base_name = "SHIFT"; break;
            case VK_DELETE: base_name = "DELETE"; break;
            case VK_LEFT: base_name = "LEFT"; break;
            case VK_RIGHT: base_name = "RIGHT"; break;
            case VK_UP: base_name = "UP"; break;
            case VK_DOWN: base_name = "DOWN"; break;
            default:
                static_cast<void>(std::snprintf(generated.data(), generated.size(), "VK%02X", base));
                break;
        }
    }
    const auto modifiers = RequiredInputModifiers(input);
    static_cast<void>(std::snprintf(
        result.data(), result.size(), "%s%s%s%s%s",
        (modifiers & 1u) != 0 ? "CTRL+" : "",
        (modifiers & 2u) != 0 ? "ALT+" : "",
        (modifiers & 4u) != 0 ? "SHIFT+" : "",
        (modifiers & 8u) != 0 ? "WIN+" : "",
        base_name));
    return result;
}

void DrawBindingRow(
    const float x,
    const float y,
    const float width,
    const std::string_view label,
    const std::uint32_t input,
    const std::int32_t action) noexcept {
    auto& state = g_overlay;
    const auto now = GetTickCount64();
    if (state.binding_pending_action.load(std::memory_order_acquire) == action) {
        const auto expected = state.binding_pending_value.load(std::memory_order_acquire);
        const auto since = state.binding_pending_since_ms.load(std::memory_order_acquire);
        if (input == expected || (since > 0 && now - since >= kBindingResponseTimeoutMs)) {
            const auto feedback = input == expected ? BindingFeedback::saved : BindingFeedback::rejected;
            state.binding_pending_action.store(-1, std::memory_order_release);
            state.binding_feedback_action.store(action, std::memory_order_release);
            state.binding_feedback_kind.store(
                static_cast<std::uint32_t>(feedback), std::memory_order_release);
            state.binding_feedback_until_ms.store(now + kBindingFeedbackDurationMs,
                                                   std::memory_order_release);
        }
    }

    auto feedback = BindingFeedback::none;
    if (state.binding_feedback_action.load(std::memory_order_acquire) == action) {
        if (now < state.binding_feedback_until_ms.load(std::memory_order_acquire)) {
            feedback = static_cast<BindingFeedback>(
                state.binding_feedback_kind.load(std::memory_order_acquire));
        } else {
            state.binding_feedback_action.store(-1, std::memory_order_release);
            state.binding_feedback_kind.store(
                static_cast<std::uint32_t>(BindingFeedback::none), std::memory_order_release);
        }
    }

    const auto capturing = state.binding_capture_action.load(std::memory_order_acquire) == action;
    const auto pending = state.binding_pending_action.load(std::memory_order_acquire) == action;
    const auto value = FormatInput(input);
    std::array<char, 48> feedback_text{};
    AddText(x, y, label, kMuted);
    const char* field_text = value.data();
    if (capturing) field_text = "PRESS INPUT...";
    else if (pending) field_text = "SAVING...";
    else if (feedback == BindingFeedback::saved) field_text = "SAVED";
    else if (feedback == BindingFeedback::rejected) {
        const auto* conflict = BindingActionName(
            state.binding_conflict_action.load(std::memory_order_acquire));
        if (conflict != nullptr) {
            static_cast<void>(std::snprintf(
                feedback_text.data(), feedback_text.size(), "USED BY %s", conflict));
            field_text = feedback_text.data();
        } else {
            field_text = "CONFLICT";
        }
    }
    else if (feedback == BindingFeedback::keyboard_only) field_text = "KEYBOARD ONLY";

    constexpr auto clear_width = 22.0F;
    constexpr auto field_width = 142.0F;
    constexpr auto gap = 5.0F;
    const auto field_x = x + width - clear_width - gap - field_width;
    const auto field_enabled = !pending;
    if (Button(field_x, y - 5.0F, field_width, 24.0F, field_text, field_enabled, capturing))
        BeginBindingCapture(action, input);
    if (Button(x + width - clear_width, y - 5.0F, clear_width, 24.0F, "X",
               input != 0 && !pending && !capturing))
        static_cast<void>(SubmitBindingValue(action, 0, input));
}

void DrawVisualsPage(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    AddText(x, y, "VISUALS", kGold, 1.08F);
    AddText(x + 96.0F, y + 1.0F, "CAMERA PATH OVERLAYS", kText);
    const auto path_available = (snapshot.capabilities & smvm_capability_path_visualization) != 0;

    DrawCard(x, y + 38.0F, width, 206.0F, "PATH OVERLAY", "LIVE D3D11 VISUALIZATION");
    if (Toggle(x + 18.0F, y + 91.0F, "SHOW PATH", (snapshot.flags & smvm_snapshot_show_path) != 0,
               path_available))
        static_cast<void>(QueueAction(SmvmActionType::toggle_show_path));
    AddText(x + 300.0F, y + 93.0F, "INTERPOLATED CAMERA TRAJECTORY", kMuted);
    if (Toggle(x + 18.0F, y + 130.0F, "SHOW CAMERAS",
               (snapshot.flags & smvm_snapshot_show_cameras) != 0, path_available))
        static_cast<void>(QueueAction(SmvmActionType::toggle_show_cameras));
    AddText(x + 300.0F, y + 132.0F, "ROLLED FOV FRUSTA AT EACH KEY", kMuted);
    if (Toggle(x + 18.0F, y + 169.0F, "SHOW LABELS",
               (snapshot.flags & smvm_snapshot_show_labels) != 0, path_available))
        static_cast<void>(QueueAction(SmvmActionType::toggle_show_labels));
    AddText(x + 300.0F, y + 171.0F, "KEY NUMBER + AUTHORITATIVE TICK", kMuted);
    AddText(x + 18.0F, y + 213.0F,
            path_available ? "PATH VISUALIZATION CAPABILITY READY" : "PATH VISUALIZATION  --  UNAVAILABLE",
            path_available ? kGreen : kDanger);

    DrawCard(x, y + 256.0F, width, 132.0F, "PLAYBACK VISIBILITY", "PERSISTED VALUES");
    if (Toggle(x + 18.0F, y + 309.0F, "HIDE PATH WHILE PLAYING",
               (snapshot.flags & smvm_snapshot_hide_path_while_playing) != 0, path_available))
        static_cast<void>(QueueAction(SmvmActionType::toggle_hide_path_while_playing));
    AddText(x + 318.0F, y + 311.0F, "HIDES ALL WORLD PATH GEOMETRY", kMuted);
    std::array<char, 32> scale{};
    static_cast<void>(std::snprintf(scale.data(), scale.size(), "%.2fx", snapshot.path_label_scale));
    DrawValueRow(x + 18.0F, y + 348.0F, "LABEL SCALE", scale.data());
    AddText(x + 318.0F, y + 350.0F, "VALUE IS APPLIED TO WORLD LABELS", kMuted);

    DrawCard(x, y + 400.0F, width, 102.0F, "CLEAN FOOTAGE", "LOCAL OVERLAY STATE");
    const auto clean_binding = FormatInput(snapshot.clean_view_key);
    AddText(x + 18.0F, y + 452.0F, "CLEAN VIEW HIDES PATH, MENU, AND THE MINIMAL PILL.", kMuted);
    AddText(x + width - TextWidth(clean_binding.data()) - 18.0F, y + 452.0F,
            clean_binding.data(), kGold);
}

void DrawCapturePage(
    const float x,
    const float y,
    const float width) noexcept {
    AddText(x, y, "CAPTURE", kGold, 1.08F);
    DrawStatusBadge(x + width - TextWidth("NOT CONNECTED") - 40.0F, y - 2.0F,
                    "NOT CONNECTED", kDarkMuted);
    DrawCard(x, y + 38.0F, width, 192.0F, "CAPTURE PIPELINE", "HONEST CAPABILITY BOUNDARY");
    AddText(x + 22.0F, y + 96.0F, "NO NATIVE CAPTURE BACKEND IS AVAILABLE IN THIS BUILD.", kText);
    AddText(x + 22.0F, y + 126.0F, "SMVM WILL NOT CLAIM RECORDING, ENCODING, OR FILE OUTPUT.", kMuted);
    AddText(x + 22.0F, y + 170.0F, "RECORDING STATUS", kMuted);
    AddText(x + 202.0F, y + 170.0F, "--", kDarkMuted);
    static_cast<void>(Button(x + width - 176.0F, y + 158.0F, 154.0F, 34.0F, "START CAPTURE", false));

    const auto column_width = (width - 12.0F) * 0.5F;
    DrawCard(x, y + 242.0F, column_width, 206.0F, "OUTPUT", "UNAVAILABLE");
    DrawValueRow(x + 18.0F, y + 296.0F, "FOLDER", {}, false);
    DrawValueRow(x + 18.0F, y + 326.0F, "CONTAINER", {}, false);
    DrawValueRow(x + 18.0F, y + 356.0F, "RESOLUTION", {}, false);
    DrawValueRow(x + 18.0F, y + 386.0F, "FRAME RATE", {}, false);
    DrawValueRow(x + 18.0F, y + 416.0F, "AUDIO", {}, false);

    const auto right_x = x + column_width + 12.0F;
    DrawCard(right_x, y + 242.0F, column_width, 206.0F, "ENCODER", "UNAVAILABLE");
    DrawValueRow(right_x + 18.0F, y + 296.0F, "VIDEO", {}, false);
    DrawValueRow(right_x + 18.0F, y + 326.0F, "COLOR", {}, false);
    DrawValueRow(right_x + 18.0F, y + 356.0F, "BITRATE", {}, false);
    DrawValueRow(right_x + 18.0F, y + 386.0F, "QUEUE", {}, false);
    static_cast<void>(Button(right_x + 18.0F, y + 408.0F, column_width - 36.0F, 28.0F,
                             "CAPTURE SETTINGS UNAVAILABLE", false));
    AddText(x, y + 468.0F, "USE A VERIFIED EXTERNAL CAPTURE TOOL FOR CURRENT PRODUCTIONS.", kDarkMuted);
}

void DrawSettingsTabs(const float x, const float y) noexcept {
    struct Item final { const char* label; SmvmSettingsSection section; float width; };
    constexpr std::array<Item, 4> items{{
        {"BINDINGS", SmvmSettingsSection::bindings, 106.0F},
        {"CAMERA", SmvmSettingsSection::camera, 96.0F},
        {"INTERFACE", SmvmSettingsSection::interface_settings, 112.0F},
        {"ADVANCED", SmvmSettingsSection::advanced, 108.0F},
    }};
    auto item_x = x;
    for (const auto& item : items) {
        if (Button(item_x, y, item.width, 30.0F, item.label, true,
                   g_overlay.settings_section == item.section))
            g_overlay.settings_section = item.section;
        item_x += item.width + 8.0F;
    }
}

void DrawBindingSettings(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto column_width = (width - 12.0F) * 0.5F;
    DrawCard(x, y, column_width, 394.0F, "MANUAL CAMERA", "MOVEMENT + LENS");
    constexpr std::array<const char*, 11> labels{
        "FORWARD", "BACKWARD", "LEFT", "RIGHT", "UP", "DOWN",
        "FAST", "PRECISION", "ROLL LEFT", "ROLL RIGHT", "RESET ROLL"};
    const std::array<std::uint32_t, 11> values{
        snapshot.forward_key, snapshot.backward_key, snapshot.left_key, snapshot.right_key,
        snapshot.up_key, snapshot.down_key, snapshot.fast_key, snapshot.precision_key,
        snapshot.roll_left_key, snapshot.roll_right_key, snapshot.roll_reset_key};
    for (std::size_t index = 0; index < labels.size(); ++index)
        DrawBindingRow(x + 16.0F, y + 58.0F + (static_cast<float>(index) * 29.0F),
                       column_width - 32.0F, labels[index], values[index],
                       kFirstManualBindingAction + static_cast<std::int32_t>(index));

    const auto right_x = x + column_width + 12.0F;
    DrawCard(right_x, y, column_width, 394.0F, "EDITOR + PLAYBACK", "MENU / CAMPATH");
    constexpr std::array<const char*, 11> editor_labels{
        "MENU", "ADD KEY", "DELETE KEY", "CLEAN VIEW", "PLAY START", "PLAY CURRENT",
        "STOP", "UNDO", "REDO", "SHOW PATH", "SHOW CAMERAS"};
    const std::array<std::uint32_t, 11> editor_values{
        snapshot.menu_key, snapshot.add_key, snapshot.delete_key, snapshot.clean_view_key,
        snapshot.play_start_key, snapshot.play_current_key, snapshot.stop_key,
        snapshot.undo_key, snapshot.redo_key, snapshot.show_path_key, snapshot.show_cameras_key};
    for (std::size_t index = 0; index < editor_labels.size(); ++index)
        DrawBindingRow(right_x + 16.0F, y + 58.0F + (static_cast<float>(index) * 29.0F),
                       column_width - 32.0F, editor_labels[index], editor_values[index],
                       kFirstEditorBindingAction + static_cast<std::int32_t>(index));
    if (Button(x, y + 408.0F, 142.0F, 30.0F, "RESET DEFAULTS")) {
        ResetBindingCaptureState();
        static_cast<void>(QueueAction(SmvmActionType::reset_bindings));
    }
    AddText(x + 158.0F, y + 414.0F,
            "CLICK A FIELD, THEN PRESS A KEY / MOUSE3-5 / WHEEL.  ESC CANCELS.", kDarkMuted);
}

void DrawCameraSettings(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto column_width = (width - 12.0F) * 0.5F;
    DrawCard(x, y, column_width, 300.0F, "MOTION TUNING", "PERSISTED VALUES");
    std::array<char, 40> value{};
    static_cast<void>(std::snprintf(value.data(), value.size(), "%.1f UNITS/S", snapshot.movement_speed));
    DrawValueRow(x + 18.0F, y + 62.0F, "MOVE SPEED", value.data());
    if (Button(x + column_width - 76.0F, y + 54.0F, 28.0F, 27.0F, "-"))
        static_cast<void>(QueueAction(SmvmActionType::set_movement_speed, -1, -1,
                                      std::max(1.0, snapshot.movement_speed - 50.0)));
    if (Button(x + column_width - 42.0F, y + 54.0F, 28.0F, 27.0F, "+"))
        static_cast<void>(QueueAction(SmvmActionType::set_movement_speed, -1, -1,
                                      std::min(10000.0, snapshot.movement_speed + 50.0)));
    static_cast<void>(std::snprintf(value.data(), value.size(), "%.2fx", snapshot.boost_multiplier));
    DrawValueRow(x + 18.0F, y + 94.0F, "FAST MULT", value.data());
    static_cast<void>(std::snprintf(value.data(), value.size(), "%.2fx", snapshot.precision_multiplier));
    DrawValueRow(x + 18.0F, y + 126.0F, "PRECISION", value.data());
    static_cast<void>(std::snprintf(value.data(), value.size(), "%.3f", snapshot.mouse_sensitivity));
    DrawValueRow(x + 18.0F, y + 158.0F, "MOUSE SENSE", value.data());
    if (Button(x + column_width - 76.0F, y + 150.0F, 28.0F, 27.0F, "-"))
        static_cast<void>(QueueAction(SmvmActionType::set_mouse_sensitivity, -1, -1,
                                      std::max(0.001, snapshot.mouse_sensitivity - 0.01)));
    if (Button(x + column_width - 42.0F, y + 150.0F, 28.0F, 27.0F, "+"))
        static_cast<void>(QueueAction(SmvmActionType::set_mouse_sensitivity, -1, -1,
                                      std::min(5.0, snapshot.mouse_sensitivity + 0.01)));
    static_cast<void>(std::snprintf(value.data(), value.size(), "%.2f", snapshot.smoothing));
    DrawValueRow(x + 18.0F, y + 190.0F, "SMOOTHING", value.data());
    if (Button(x + column_width - 76.0F, y + 182.0F, 28.0F, 27.0F, "-"))
        static_cast<void>(QueueAction(SmvmActionType::set_smoothing, -1, -1,
                                      std::max(0.0, snapshot.smoothing - 0.05)));
    if (Button(x + column_width - 42.0F, y + 182.0F, 28.0F, 27.0F, "+"))
        static_cast<void>(QueueAction(SmvmActionType::set_smoothing, -1, -1,
                                      std::min(0.95, snapshot.smoothing + 0.05)));
    if (Toggle(x + 18.0F, y + 226.0F, "INVERT Y",
               (snapshot.flags & smvm_snapshot_invert_y) != 0))
        static_cast<void>(QueueAction(SmvmActionType::toggle_invert_y));
    AddText(x + 18.0F, y + 265.0F, "CHANGES ARE PERSISTED BY THE MANAGED HOST", kDarkMuted);

    const auto right_x = x + column_width + 12.0F;
    DrawCard(right_x, y, column_width, 300.0F, "INPUT OWNERSHIP", "TEMPORARY - NEVER GAME CONFIG");
    if (Toggle(right_x + 18.0F, y + 62.0F, "TAKE OVER CAMERA INPUT",
               (snapshot.flags & smvm_snapshot_input_takeover) != 0))
        static_cast<void>(QueueAction(SmvmActionType::toggle_input_takeover));
    AddText(right_x + 18.0F, y + 100.0F, "WHEN ACTIVE, ONLY SMVM-OWNED", kMuted);
    AddText(right_x + 18.0F, y + 123.0F, "BINDINGS ARE CONSUMED.", kMuted);
    AddText(right_x + 18.0F, y + 154.0F, "FOCUS LOSS, MENU OPEN, AND", kMuted);
    AddText(right_x + 18.0F, y + 177.0F, "DISCONNECT RESET HELD INPUT.", kMuted);
    const auto fast = FormatInput(snapshot.fast_key);
    const auto precision = FormatInput(snapshot.precision_key);
    DrawValueRow(right_x + 18.0F, y + 213.0F, "FAST", fast.data());
    DrawValueRow(right_x + 18.0F, y + 243.0F, "PRECISION", precision.data());

    DrawCard(x, y + 312.0F, width, 112.0F, "FOV WHEEL", "SUPPORTED SETTING ACTION");
    static_cast<void>(std::snprintf(value.data(), value.size(), "%.2f DEG / STEP", snapshot.fov_step));
    DrawValueRow(x + 18.0F, y + 366.0F, "STEP", value.data(), true, kGold);
    if (Button(x + 318.0F, y + 354.0F, 42.0F, 30.0F, "-"))
        static_cast<void>(QueueAction(SmvmActionType::set_binding, 4, -1,
                                      std::max(0.05, snapshot.fov_step - 0.25)));
    if (Button(x + 368.0F, y + 354.0F, 42.0F, 30.0F, "+"))
        static_cast<void>(QueueAction(SmvmActionType::set_binding, 4, -1,
                                      std::min(30.0, snapshot.fov_step + 0.25)));
    AddText(x + 438.0F, y + 362.0F,
            (snapshot.flags & smvm_snapshot_fov_inverted) != 0 ? "DIRECTION  INVERTED" : "DIRECTION  STANDARD",
            kMuted);
}

void DrawInterfaceSettings(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    DrawCard(x, y, width, 218.0F, "APPEARANCE", "PERSISTED UI STATE");
    std::array<char, 36> value{};
    static_cast<void>(std::snprintf(value.data(), value.size(), "%.2fx", snapshot.ui_scale));
    DrawValueRow(x + 18.0F, y + 62.0F, "UI SCALE", value.data(), true, kGold);
    if (Button(x + 270.0F, y + 54.0F, 28.0F, 27.0F, "-"))
        static_cast<void>(QueueAction(SmvmActionType::set_ui_scale, -1, -1,
                                      std::max(0.75, snapshot.ui_scale - 0.05)));
    if (Button(x + 304.0F, y + 54.0F, 28.0F, 27.0F, "+"))
        static_cast<void>(QueueAction(SmvmActionType::set_ui_scale, -1, -1,
                                      std::min(1.5, snapshot.ui_scale + 0.05)));
    static_cast<void>(std::snprintf(value.data(), value.size(), "%d%%",
                                    static_cast<int>(std::round(snapshot.menu_opacity * 100.0))));
    DrawValueRow(x + 18.0F, y + 94.0F, "MENU OPACITY", value.data());
    if (Button(x + 270.0F, y + 86.0F, 28.0F, 27.0F, "-"))
        static_cast<void>(QueueAction(SmvmActionType::set_menu_opacity, -1, -1,
                                      std::max(0.65, snapshot.menu_opacity - 0.05)));
    if (Button(x + 304.0F, y + 86.0F, 28.0F, 27.0F, "+"))
        static_cast<void>(QueueAction(SmvmActionType::set_menu_opacity, -1, -1,
                                      std::min(1.0, snapshot.menu_opacity + 0.05)));
    DrawValueRow(x + 18.0F, y + 126.0F, "MENU ANCHOR",
                 snapshot.menu_anchor == SmvmMenuAnchor::right ? "RIGHT" : "LEFT");
    if (Button(x + 270.0F, y + 118.0F, 62.0F, 27.0F, "LEFT", true,
               snapshot.menu_anchor == SmvmMenuAnchor::left))
        static_cast<void>(QueueAction(SmvmActionType::set_menu_anchor, 0));
    if (Button(x + 338.0F, y + 118.0F, 62.0F, 27.0F, "RIGHT", true,
               snapshot.menu_anchor == SmvmMenuAnchor::right))
        static_cast<void>(QueueAction(SmvmActionType::set_menu_anchor, 1));
    if (Toggle(x + 430.0F, y + 62.0F, "MINIMAL PILL",
               (snapshot.flags & smvm_snapshot_show_minimal_pill) != 0))
        static_cast<void>(QueueAction(SmvmActionType::toggle_minimal_pill));
    if (Toggle(x + 430.0F, y + 102.0F, "NOTIFICATIONS",
               (snapshot.flags & smvm_snapshot_notifications) != 0))
        static_cast<void>(QueueAction(SmvmActionType::toggle_notifications));
    AddText(x + 18.0F, y + 174.0F, "APPEARANCE CHANGES ARE PERSISTED IMMEDIATELY", kDarkMuted);

    DrawCard(x, y + 230.0F, width, 194.0F, "SAFE AREA + CLEAN VIEW", "CAPTURE-SAFE OVERLAY BEHAVIOR");
    AddText(x + 18.0F, y + 286.0F, "THE MENU IS FIT-CLAMPED INSIDE THE ACTIVE VIEWPORT", kMuted);
    AddText(x + 18.0F, y + 313.0F, "AND ANCHORED TO THE SELECTED SAFE EDGE.", kMuted);
    const auto clean = FormatInput(snapshot.clean_view_key);
    DrawValueRow(x + 18.0F, y + 350.0F, "CLEAN VIEW", clean.data(), true, kGold);
    AddText(x + 18.0F, y + 384.0F, "CLEAN VIEW HIDES THE MENU, PATH, LABELS, AND PILL.", kText);
}

void DrawAdvancedSettings(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto column_width = (width - 12.0F) * 0.5F;
    DrawCard(x, y, column_width, 318.0F, "DIAGNOSTICS", "PROTOCOL V7");
    DrawValueRow(x + 18.0F, y + 62.0F, "PROTOCOL", "V7", true, kGold);
    DrawValueRow(x + 18.0F, y + 94.0F, "RENDERER", "D3D11", true, kGreen);
    DrawValueRow(x + 18.0F, y + 126.0F, "MANAGED HOST",
                 (snapshot.flags & smvm_snapshot_internal_enabled) != 0 ? "CONNECTED" : "DISCONNECTED",
                 true, (snapshot.flags & smvm_snapshot_internal_enabled) != 0 ? kGreen : kDanger);
    DrawValueRow(x + 18.0F, y + 158.0F, "REPLAY GATE",
                 (snapshot.flags & smvm_snapshot_replay_active) != 0 ? "OPEN" : "CLOSED",
                 true, (snapshot.flags & smvm_snapshot_replay_active) != 0 ? kGreen : kDanger);
    std::array<char, 48> value{};
    static_cast<void>(std::snprintf(value.data(), value.size(), "%u US",
                                    g_overlay.frame_microseconds.load(std::memory_order_acquire)));
    DrawValueRow(x + 18.0F, y + 190.0F, "PRESENT COST", value.data());
    static_cast<void>(std::snprintf(value.data(), value.size(), "0x%08X", OverlayFlags()));
    DrawValueRow(x + 18.0F, y + 222.0F, "OVERLAY FLAGS", value.data());
    static_cast<void>(std::snprintf(value.data(), value.size(), "0x%08X", snapshot.capabilities));
    DrawValueRow(x + 18.0F, y + 254.0F, "CAPABILITIES", value.data());
    AddText(x + 18.0F, y + 288.0F, "MOVIE-SHAPED IPC ONLY", kDarkMuted);

    const auto right_x = x + column_width + 12.0F;
    DrawCard(right_x, y, column_width, 318.0F, "CAMERA HEALTH", "TYPED AVAILABILITY");
    DrawValueRow(right_x + 18.0F, y + 62.0F, "STATE",
                 CameraAvailabilityName(snapshot.camera_availability), true,
                 snapshot.camera_availability == CameraAvailability::ready ? kGreen : kDanger);
    DrawValueRow(right_x + 18.0F, y + 94.0F, "OWNERSHIP",
                 CameraOwnershipName(snapshot.camera_ownership), true, kGold);
    AddText(right_x + 18.0F, y + 134.0F, "DETAIL", kMuted);
    AddLimitedText(right_x + 18.0F, y + 160.0F,
                   snapshot.camera_status[0] != '\0' ? snapshot.camera_status.data() : "--", 38, kText);
    const auto manual_capable = (snapshot.capabilities & smvm_capability_manual_camera) != 0;
    if (Button(right_x + 18.0F, y + 205.0F, 102.0F, 30.0F, "REACQUIRE", manual_capable))
        static_cast<void>(QueueAction(SmvmActionType::reacquire_camera));
    const auto test_ownership_clear =
        snapshot.camera_ownership != CameraOwnership::smvm_restore &&
        snapshot.camera_ownership != CameraOwnership::smvm_campath &&
        (snapshot.flags & smvm_snapshot_campath_playing) == 0;
    const auto test_prerequisites = test_ownership_clear &&
        (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
        (snapshot.flags & smvm_snapshot_camera_readable) != 0 && snapshot.observer_mode == 4;
    const auto camera_test_ready = test_prerequisites &&
        (snapshot.capabilities & smvm_capability_camera_self_test) != 0;
    const auto path_test_ready = test_prerequisites && snapshot.keyframe_count >= 2 &&
        (snapshot.capabilities & smvm_capability_campath_self_test) != 0;
    if (Button(right_x + 128.0F, y + 205.0F, 96.0F, 30.0F, "CAM TEST", camera_test_ready))
        static_cast<void>(QueueAction(SmvmActionType::camera_self_test));
    if (Button(right_x + 232.0F, y + 205.0F, 98.0F, 30.0F, "PATH TEST", path_test_ready))
        static_cast<void>(QueueAction(SmvmActionType::campath_self_test));
    AddText(right_x + 18.0F, y + 250.0F,
            camera_test_ready || path_test_ready ? "SELF-TEST READY" : "SELF-TEST UNAVAILABLE",
            camera_test_ready || path_test_ready ? kGreen : kDarkMuted);
    AddLimitedText(right_x + 18.0F, y + 274.0F,
                   snapshot.status[0] != '\0' ? snapshot.status.data() : "--", 42, kMuted);

    DrawCard(x, y + 330.0F, width, 94.0F, "LAST STATUS", "HOST / PLAYBACK");
    AddLimitedText(x + 18.0F, y + 382.0F,
                   snapshot.status[0] != '\0' ? snapshot.status.data() : "--", 80,
                   snapshot.start_failure == 0 ? kText : kDanger);
}

void DrawSettingsPage(
    const float x,
    const float y,
    const float width,
    const SmvmSnapshotPayload& snapshot) noexcept {
    AddText(x, y, "SETTINGS", kGold, 1.08F);
    AddText(x + 112.0F, y + 1.0F, "NATIVE OVERLAY", kText);
    DrawSettingsTabs(x, y + 38.0F);
    const auto content_y = y + 80.0F;
    switch (g_overlay.settings_section) {
        case SmvmSettingsSection::bindings:
            DrawBindingSettings(x, content_y, width, snapshot);
            break;
        case SmvmSettingsSection::camera:
            DrawCameraSettings(x, content_y, width, snapshot);
            break;
        case SmvmSettingsSection::interface_settings:
            DrawInterfaceSettings(x, content_y, width, snapshot);
            break;
        case SmvmSettingsSection::advanced:
            DrawAdvancedSettings(x, content_y, width, snapshot);
            break;
    }
}

void DrawMinimalPill(const SmvmSnapshotPayload& snapshot) noexcept {
    if ((snapshot.flags & smvm_snapshot_show_minimal_pill) == 0)
        return;
    auto& state = g_overlay;
    constexpr auto width = 112.0F;
    constexpr auto height = 28.0F;
    const auto safe = std::clamp(std::min(state.viewport_width, state.viewport_height) * 0.024F, 22.0F, 40.0F);
    const auto requested_scale = static_cast<float>(std::clamp(snapshot.ui_scale, 0.75, 1.5));
    const auto fit = std::min((state.viewport_width - (safe * 2.0F)) / width,
                              (state.viewport_height - (safe * 2.0F)) / height);
    const auto scale = std::min(requested_scale, fit);
    if (scale <= 0.0F)
        return;
    state.ui_scale = scale;
    state.ui_opacity = static_cast<float>(std::clamp(snapshot.menu_opacity, 0.65, 1.0));
    state.ui_origin_x = snapshot.menu_anchor == SmvmMenuAnchor::right
        ? state.viewport_width - safe - (width * scale)
        : safe;
    state.ui_origin_y = safe;
    state.ui_transform = true;
    AddRect(0.0F, 0.0F, width, height, Color(17, 16, 15, 232));
    AddOutline(0.0F, 0.0F, width, height, kHairline);
    AddRect(0.0F, 0.0F, 3.0F, height, kGold);
    AddText(12.0F, 5.0F, "SMVM  TAB", kText);
    state.ui_transform = false;
}

void DrawFullMenu(
    const SmvmSnapshotPayload& snapshot,
    const CampathPayloadHeader& path_header,
    const CampathKeyframe* keys,
    const bool has_path) noexcept {
    auto& state = g_overlay;
    constexpr auto width = 920.0F;
    constexpr auto height = 650.0F;
    const auto safe = std::clamp(std::min(state.viewport_width, state.viewport_height) * 0.024F, 22.0F, 40.0F);
    const auto requested_scale = static_cast<float>(std::clamp(snapshot.ui_scale, 0.75, 1.5));
    const auto fit_scale = std::min((state.viewport_width - (safe * 2.0F)) / width,
                                    (state.viewport_height - (safe * 2.0F)) / height);
    const auto scale = std::min(requested_scale, fit_scale);
    if (scale < 0.55F)
        return;

    state.ui_transform = false;
    const auto opacity = static_cast<float>(std::clamp(snapshot.menu_opacity, 0.65, 1.0));
    const auto backdrop_alpha = static_cast<std::uint8_t>(std::clamp(94.0F * opacity, 0.0F, 255.0F));
    AddRect(0.0F, 0.0F, state.viewport_width, state.viewport_height, Color(4, 4, 4, backdrop_alpha));

    state.ui_scale = scale;
    state.ui_opacity = opacity;
    state.ui_origin_x = snapshot.menu_anchor == SmvmMenuAnchor::right
        ? state.viewport_width - safe - (width * scale)
        : safe;
    state.ui_origin_y = std::clamp((state.viewport_height - (height * scale)) * 0.5F,
                                   safe, state.viewport_height - safe - (height * scale));
    state.ui_transform = true;
    AddRect(0.0F, 0.0F, width, height, kPanel);
    AddOutline(0.0F, 0.0F, width, height, Color(94, 83, 66, 242));
    AddRect(1.0F, 1.0F, width - 2.0F, 3.0F, kGoldSoft);
    DrawMenuHeader(width, snapshot);

    AddRect(16.0F, 82.0F, 158.0F, 550.0F, kPanelSoft);
    AddOutline(16.0F, 82.0F, 158.0F, 550.0F, kHairline);
    if (state.text_edit_mode.load(std::memory_order_acquire) != 0)
        state.frame_left_click = false;
    DrawNavigation(23.0F, 96.0F, 522.0F);

    constexpr auto content_x = 190.0F;
    constexpr auto content_y = 88.0F;
    constexpr auto content_width = 712.0F;
    AddRect(content_x - 8.0F, content_y - 6.0F, content_width + 8.0F, 536.0F, Color(21, 20, 18, 224));
    AddOutline(content_x - 8.0F, content_y - 6.0F, content_width + 8.0F, 536.0F, kHairline);
    switch (state.page) {
        case SmvmPage::replay:
            DrawReplayPage(content_x, content_y, content_width, snapshot);
            break;
        case SmvmPage::camera:
            DrawCameraPage(content_x, content_y, content_width, snapshot);
            break;
        case SmvmPage::campath:
            DrawCampathPage(content_x, content_y, content_width, snapshot, path_header, keys, has_path);
            break;
        case SmvmPage::visuals:
            DrawVisualsPage(content_x, content_y, content_width, snapshot);
            break;
        case SmvmPage::capture:
            DrawCapturePage(content_x, content_y, content_width);
            break;
        case SmvmPage::settings:
            DrawSettingsPage(content_x, content_y, content_width, snapshot);
            break;
    }
    DrawTextEditor((width - 440.0F) * 0.5F, 250.0F);
    state.ui_transform = false;
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

[[nodiscard]] bool SuspendInputSystemOnWindowThread() noexcept;
[[nodiscard]] bool RestoreInputSystemOnWindowThread() noexcept;

[[nodiscard]] bool PointerStateRestorePending() noexcept {
    const auto& state = g_overlay;
    return state.previous_clip_valid || state.sdl_relative_mouse_restore_pending ||
           state.raw_mouse_restore_pending.load(std::memory_order_acquire) ||
           state.input_restore_pending.load(std::memory_order_acquire);
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
        const auto input_ok = SuspendInputSystemOnWindowThread();
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
           !PointerStateRestorePending();
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

void ReleaseGraphicsResources() noexcept {
    auto& state = g_overlay;
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

[[nodiscard]] bool CreateFontAtlas(ID3D11Device* device) noexcept {
    std::array<std::uint32_t, kAtlasWidth * kAtlasHeight> pixels{};
    BITMAPINFO bitmap_info{};
    bitmap_info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bitmap_info.bmiHeader.biWidth = static_cast<LONG>(kAtlasWidth);
    bitmap_info.bmiHeader.biHeight = -static_cast<LONG>(kAtlasHeight);
    bitmap_info.bmiHeader.biPlanes = 1;
    bitmap_info.bmiHeader.biBitCount = 32;
    bitmap_info.bmiHeader.biCompression = BI_RGB;

    auto* screen = GetDC(nullptr);
    if (screen == nullptr)
        return false;
    auto* memory = CreateCompatibleDC(screen);
    void* bitmap_bits = nullptr;
    auto* bitmap = CreateDIBSection(screen, &bitmap_info, DIB_RGB_COLORS, &bitmap_bits, nullptr, 0);
    ReleaseDC(nullptr, screen);
    if (memory == nullptr || bitmap == nullptr || bitmap_bits == nullptr) {
        if (bitmap != nullptr) DeleteObject(bitmap);
        if (memory != nullptr) DeleteDC(memory);
        return false;
    }

    const auto old_bitmap = SelectObject(memory, bitmap);
    auto* font = CreateFontW(
        -16, 0, 0, 0, FW_MEDIUM, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    if (font == nullptr) {
        SelectObject(memory, old_bitmap);
        DeleteObject(bitmap);
        DeleteDC(memory);
        return false;
    }
    const auto old_font = SelectObject(memory, font);
    SetBkColor(memory, RGB(0, 0, 0));
    SetTextColor(memory, RGB(255, 255, 255));
    SetBkMode(memory, OPAQUE);
    PatBlt(memory, 0, 0, static_cast<int>(kAtlasWidth), static_cast<int>(kAtlasHeight), BLACKNESS);

    constexpr auto cell_width = 16;
    constexpr auto cell_height = 24;
    for (int character = 32; character <= 126; ++character) {
        const auto glyph_index = character - 32;
        const auto cell_x = (glyph_index % 16) * cell_width;
        const auto cell_y = (glyph_index / 16) * cell_height;
        const wchar_t text[2]{static_cast<wchar_t>(character), L'\0'};
        SIZE extent{};
        static_cast<void>(GetTextExtentPoint32W(memory, text, 1, &extent));
        static_cast<void>(TextOutW(memory, cell_x + 1, cell_y + 2, text, 1));
        auto& glyph = g_overlay.glyphs[static_cast<std::size_t>(glyph_index)];
        glyph.u0 = static_cast<float>(cell_x) / kAtlasWidth;
        glyph.v0 = static_cast<float>(cell_y) / kAtlasHeight;
        glyph.u1 = static_cast<float>(cell_x + cell_width) / kAtlasWidth;
        glyph.v1 = static_cast<float>(cell_y + cell_height) / kAtlasHeight;
        glyph.width = static_cast<float>(cell_width);
        glyph.height = static_cast<float>(cell_height);
        glyph.advance = static_cast<float>(std::clamp<LONG>(extent.cx + 1, 5, 16));
    }

    const auto* dib_pixels = static_cast<const std::uint32_t*>(bitmap_bits);
    for (std::size_t index = 0; index < pixels.size(); ++index) {
        const auto source = dib_pixels[index];
        const auto blue = static_cast<std::uint8_t>(source & 0xFF);
        const auto green = static_cast<std::uint8_t>((source >> 8) & 0xFF);
        const auto red = static_cast<std::uint8_t>((source >> 16) & 0xFF);
        const auto alpha = std::max({red, green, blue});
        pixels[index] = Color(255, 255, 255, alpha);
    }
    pixels[0] = Color(255, 255, 255, 255);

    SelectObject(memory, old_font);
    SelectObject(memory, old_bitmap);
    DeleteObject(font);
    DeleteObject(bitmap);
    DeleteDC(memory);

    const D3D11_TEXTURE2D_DESC texture_description{
        kAtlasWidth, kAtlasHeight, 1, 1, DXGI_FORMAT_R8G8B8A8_UNORM,
        {1, 0}, D3D11_USAGE_IMMUTABLE, D3D11_BIND_SHADER_RESOURCE, 0, 0,
    };
    const D3D11_SUBRESOURCE_DATA texture_data{pixels.data(), kAtlasWidth * sizeof(std::uint32_t), 0};
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
    return CreateFontAtlas(device);
}

void SetMenuOpen(const bool open) noexcept {
    auto& state = g_overlay;
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
    ResetSmvmManualInput();
    if (!open) {
        ResetBindingCaptureState();
        state.text_edit_mode.store(0, std::memory_order_release);
        state.text_edit_length.store(0, std::memory_order_release);
    }

    // Cursor visibility and clipping are thread-affine game-window state. The
    // menu can be toggled by Present or IPC-driven rendering, so marshal the
    // transition to the output window rather than touching it here.
    const auto window = state.output_window;
    auto transition_ok = !open;
    if (window != nullptr && IsWindow(window)) {
        const auto window_thread = GetWindowThreadProcessId(window, nullptr);
        if (window_thread == GetCurrentThreadId()) {
            transition_ok = ApplyCursorStateOnWindowThread(open);
            if (transition_ok)
                transition_ok = SetModalInputMaintenanceOnWindowThread(window, open);
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
    if (action >= kFirstManualBindingAction && action < kFirstEditorBindingAction) {
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
    return (binding & kSmvmInputBaseMask) != 0 &&
           base == static_cast<std::uint32_t>(key) &&
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
           (snapshot.flags & smvm_snapshot_manual_camera_active) != 0 &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0 && snapshot.observer_mode == 4;
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
           matches(snapshot.show_cameras_key);
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
    const auto manual = has_snapshot && !menu_open && CanUseManualCamera(snapshot);
    const auto takeover = manual && (snapshot.flags & smvm_snapshot_input_takeover) != 0;
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
                    const auto first_delivery = TrackConsumedMouseIndex(index, kMenuRawKeyRoute);
                    if (first_delivery && index == 0)
                        g_overlay.mouse_buttons.fetch_or(1u, std::memory_order_release);
                    if (first_delivery && index == 1)
                        g_overlay.mouse_buttons.fetch_or(2u, std::memory_order_release);
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
                    if (handled || (takeover && input_codes[index] != SmvmInputCode::none &&
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
                    g_overlay.wheel_delta.fetch_add(wheel_delta, std::memory_order_release);
            }
            return !contains_untracked_release;
        }

        auto owns_packet = false;
        if (manual && (input->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0) {
            const auto delta_x = input->data.mouse.lLastX;
            const auto delta_y = input->data.mouse.lLastY;
            if (delta_x != 0 || delta_y != 0) {
                g_overlay.manual_mouse_delta_x.fetch_add(delta_x, std::memory_order_release);
                g_overlay.manual_mouse_delta_y.fetch_add(delta_y, std::memory_order_release);
                owns_packet = true;
            }
        }
        if (manual && wheel_delta != 0) {
            g_overlay.raw_wheel_consumed_ms.store(GetTickCount64(), std::memory_order_release);
            g_overlay.manual_wheel_delta.fetch_add(wheel_delta, std::memory_order_release);
            owns_packet = true;
        }
        const auto editor_owns_wheel = takeover && wheel_delta != 0 && EditorBindingOwnsMouseCode(
            snapshot, wheel_delta > 0 ? SmvmInputCode::wheel_up : SmvmInputCode::wheel_down);
        const auto contains_owned_wheel = manual && (button_flags & RI_MOUSE_WHEEL) != 0;
        contains_unowned_button = contains_unowned_button || (button_flags & RI_MOUSE_HWHEEL) != 0;
        // A RAWINPUT packet cannot be split. Preserve any unrelated button in a
        // mixed packet, even though that also lets the coalesced motion through.
        return !contains_unowned_button &&
               (owns_editor_button ||
                (takeover && (owns_packet || contains_owned_wheel || editor_owns_wheel)));
    }
    if (input->header.dwType == RIM_TYPEKEYBOARD) {
        const auto key = static_cast<std::uint32_t>(input->data.keyboard.VKey);
        const auto down = (input->data.keyboard.Flags & RI_KEY_BREAK) == 0;
        UpdateKeyState(key, down);
        if (down && WasKeyDeliveredBeforeMenu(key, kMenuRawKeyRoute))
            return false;
        if (down && menu_open) {
            TrackConsumedKeyDown(key, kMenuRawKeyRoute);
            return true;
        }
        const auto preheld_release = !down &&
            WasKeyDeliveredBeforeMenu(key, kMenuRawKeyRoute);
        if (!down)
            ClearPreMenuKeyRoute(key, kMenuRawKeyRoute);
        const auto tracked_release = !down && ConsumeTrackedKeyUp(key, kMenuRawKeyRoute);
        if (preheld_release)
            return false;
        if (down && takeover && EditorBindingOwnsKeyboardKey(snapshot, key)) {
            TrackConsumedKeyDown(key, kMenuRawKeyRoute);
            return true;
        }
        const auto consumed = RouteManualKeyboardEvent(
            snapshot, key, down, kManualRawKeyRoute, takeover);
        return tracked_release || consumed;
    }
    return false;
}

[[nodiscard]] bool CanEditCampath(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
           (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
           (snapshot.flags & smvm_snapshot_camera_readable) != 0 &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0 &&
           snapshot.observer_mode == 4;
}

[[nodiscard]] bool CanTriggerCampathPlayback(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
           (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
           (snapshot.flags & smvm_snapshot_camera_readable) != 0 &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0 &&
           snapshot.keyframe_count >= 2;
}

[[nodiscard]] bool CanTriggerCampathEditAction(const SmvmSnapshotPayload& snapshot) noexcept {
    return (snapshot.flags & smvm_snapshot_internal_enabled) != 0 &&
           (snapshot.flags & smvm_snapshot_replay_active) != 0 &&
           (snapshot.flags & smvm_snapshot_campath_playing) == 0;
}

[[nodiscard]] bool HandleCampathMouseBinding(
    const UINT message,
    const WPARAM wparam,
    const SmvmSnapshotPayload& snapshot) noexcept {
    if (InputMatchesMouse(snapshot.play_start_key, message, wparam) &&
        CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::play_from_start));
        return true;
    }
    if (InputMatchesMouse(snapshot.play_current_key, message, wparam) &&
        CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::play_from_current));
        return true;
    }
    if (InputMatchesMouse(snapshot.stop_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_campath_playing) != 0) {
        static_cast<void>(QueueAction(SmvmActionType::stop_campath));
        return true;
    }
    if (InputMatchesMouse(snapshot.undo_key, message, wparam) &&
        CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::undo_edit));
        return true;
    }
    if (InputMatchesMouse(snapshot.redo_key, message, wparam) &&
        CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::redo_edit));
        return true;
    }
    if (InputMatchesMouse(snapshot.show_path_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueAction(SmvmActionType::toggle_show_path));
        return true;
    }
    if (InputMatchesMouse(snapshot.show_cameras_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueAction(SmvmActionType::toggle_show_cameras));
        return true;
    }
    return false;
}

[[nodiscard]] bool HandleCampathKeyboardBinding(
    const WPARAM key,
    const SmvmSnapshotPayload& snapshot) noexcept {
    if (InputMatchesKeyboard(snapshot.play_start_key, key) && CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::play_from_start));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.play_current_key, key) && CanTriggerCampathPlayback(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::play_from_current));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.stop_key, key) &&
        (snapshot.flags & smvm_snapshot_campath_playing) != 0) {
        static_cast<void>(QueueAction(SmvmActionType::stop_campath));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.undo_key, key) && CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::undo_edit));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.redo_key, key) && CanTriggerCampathEditAction(snapshot)) {
        static_cast<void>(QueueAction(SmvmActionType::redo_edit));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.show_path_key, key) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueAction(SmvmActionType::toggle_show_path));
        return true;
    }
    if (InputMatchesKeyboard(snapshot.show_cameras_key, key) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        static_cast<void>(QueueAction(SmvmActionType::toggle_show_cameras));
        return true;
    }
    return false;
}

[[nodiscard]] bool HandleSmvmMouseBinding(
    const UINT message,
    const WPARAM wparam,
    const SmvmSnapshotPayload& snapshot) noexcept {
    const auto menu_open = g_overlay.menu_open.load(std::memory_order_acquire);
    if (InputMatchesMouse(snapshot.menu_key, message, wparam) &&
        ((snapshot.flags & smvm_snapshot_replay_active) != 0 || menu_open)) {
        SetMenuOpen(!menu_open);
        return true;
    }
    if (menu_open)
        return false;
    if (InputMatchesMouse(snapshot.add_key, message, wparam) && CanEditCampath(snapshot)) {
        if (g_overlay.callbacks.request_camera_capture != nullptr)
            g_overlay.callbacks.request_camera_capture(g_overlay.callbacks.context);
        return true;
    }
    if (InputMatchesMouse(snapshot.delete_key, message, wparam) && CanEditCampath(snapshot)) {
        return QueueAction(SmvmActionType::delete_keyframe, snapshot.selected_keyframe);
    }
    if (HandleCampathMouseBinding(message, wparam, snapshot))
        return true;
    if (InputMatchesMouse(snapshot.clean_view_key, message, wparam) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0) {
        const auto clean = !g_overlay.clean_view.load(std::memory_order_acquire);
        if (clean)
            SetMenuOpen(false);
        g_overlay.clean_view.store(clean, std::memory_order_release);
        PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::none);
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
        auto transition_ok = ApplyCursorStateOnWindowThread(should_open);
        if (transition_ok)
            transition_ok = SetModalInputMaintenanceOnWindowThread(window, should_open);
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
        if (action == SmvmPointerAction::left_click) {
            state.mouse_buttons.fetch_or(1u, std::memory_order_release);
            return TRUE;
        }
        if (action == SmvmPointerAction::right_click) {
            state.mouse_buttons.fetch_or(2u, std::memory_order_release);
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
            state.wheel_delta.fetch_add(
                static_cast<SHORT>(HIWORD(wparam)), std::memory_order_release);
        }
        return TRUE;
    }
    if (message == kSmvmRestoreWindowProcedureMessage) {
        if (!ApplyCursorStateOnWindowThread(false) || PointerStateRestorePending())
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
    if (!state.menu_open.load(std::memory_order_acquire) && PointerStateRestorePending()) {
        const auto restored = ApplyCursorStateOnWindowThread(false);
        if (!restored)
            PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::window_hook_failed);
    }

    if (message == WM_KILLFOCUS ||
        (message == WM_ACTIVATE && LOWORD(wparam) == WA_INACTIVE) ||
        (message == WM_ACTIVATEAPP && wparam == FALSE)) {
        ResetSmvmManualInput();
        ResetConsumedReleaseRoutes();
    }

    SmvmSnapshotPayload snapshot{};
    const auto has_snapshot = ReadSnapshot(snapshot) &&
        (snapshot.flags & smvm_snapshot_internal_enabled) != 0;
    auto menu_open = state.menu_open.load(std::memory_order_acquire);
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
        UpdateKeyState(static_cast<std::uint32_t>(wparam), true);
    if (message == WM_KEYUP || message == WM_SYSKEYUP)
        UpdateKeyState(static_cast<std::uint32_t>(wparam), false);

    const auto window_key_down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    const auto window_key_up = message == WM_KEYUP || message == WM_SYSKEYUP;
    const auto preheld_window_key_down = window_key_down &&
        WasKeyDeliveredBeforeMenu(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
    if (window_key_up)
        ClearPreMenuKeyRoute(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
    const auto window_mouse_down = message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN ||
        message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN;
    const auto window_mouse_up = message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
        message == WM_MBUTTONUP || message == WM_XBUTTONUP;
    if (window_key_down && MenuMayClaimKeyDown(menu_open, preheld_window_key_down))
        TrackConsumedKeyDown(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
    const auto mouse_down_already_tracked = window_mouse_down && HasTrackedMouseDown(message, wparam);
    const auto first_window_mouse_down = window_mouse_down && menu_open &&
        TrackConsumedMouseDown(message, wparam, kMenuWindowKeyRoute);
    const auto legacy_wheel_duplicate = message == WM_MOUSEWHEEL &&
        GetTickCount64() - state.raw_wheel_consumed_ms.load(std::memory_order_acquire) <= 8;
    if (preheld_window_key_down)
        return CallWindowProcW(original, window, message, wparam, lparam);

    if (message == WM_MOUSEMOVE) {
        state.mouse_x.store(GET_X_LPARAM(lparam), std::memory_order_relaxed);
        state.mouse_y.store(GET_Y_LPARAM(lparam), std::memory_order_relaxed);
        if (menu_open)
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

    if (message == WM_LBUTTONDOWN && menu_open) {
        if (first_window_mouse_down)
            state.mouse_buttons.fetch_or(1u, std::memory_order_release);
        return 0;
    }
    if (message == WM_RBUTTONDOWN && menu_open) {
        if (first_window_mouse_down)
            state.mouse_buttons.fetch_or(2u, std::memory_order_release);
        return 0;
    }
    if ((message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN) && menu_open)
        return message == WM_XBUTTONDOWN ? TRUE : 0;
    if (window_mouse_up && ConsumeTrackedMouseUp(message, wparam, kMenuWindowKeyRoute))
        return 0;

    if (message == WM_MOUSEWHEEL && has_snapshot) {
        if (legacy_wheel_duplicate && (menu_open || CanUseManualCamera(snapshot)))
            return 0;
        const auto delta = GET_WHEEL_DELTA_WPARAM(wparam);
        if (menu_open) {
            state.wheel_delta.fetch_add(delta, std::memory_order_release);
            return 0;
        }
        if (CanUseManualCamera(snapshot)) {
            state.manual_wheel_delta.fetch_add(delta, std::memory_order_release);
            if ((snapshot.flags & smvm_snapshot_input_takeover) != 0)
                return 0;
            return CallWindowProcW(original, window, message, wparam, lparam);
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
                static_cast<void>(QueueAction(SmvmActionType::set_fov, -1, -1, target));
            }
            return 0;
        }
    }

    if (has_snapshot && menu_open &&
        IsBindingActionIndex(state.binding_capture_action.load(std::memory_order_acquire))) {
        if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) {
            const auto repeated = (lparam & (1LL << 30)) != 0;
            if (CaptureKeyboardBinding(static_cast<std::uint32_t>(wparam), repeated))
                return 0;
        }
        if ((message == WM_KEYUP || message == WM_SYSKEYUP) &&
            CaptureStandaloneModifierBinding(static_cast<std::uint32_t>(wparam)))
            return 0;
        if (message == WM_CHAR)
            return 0;
    }

    if (message == WM_CHAR && menu_open && state.text_edit_mode.load(std::memory_order_acquire) != 0) {
        const auto edit_mode = state.text_edit_mode.load(std::memory_order_acquire);
        auto length = std::min<std::uint32_t>(state.text_edit_length.load(std::memory_order_acquire), 63);
        const auto character = static_cast<std::uint32_t>(wparam);
        if (character == VK_RETURN) {
            const auto text = ReadEditText();
            if (edit_mode == 1 && text[0] != '\0') {
                static_cast<void>(QueueAction(
                    SmvmActionType::set_path_name, -1, -1, 0.0, {}, text.data()));
            } else if (edit_mode == 2 && has_snapshot && length > 0) {
                std::int64_t target = 0;
                const auto parsed = std::from_chars(text.data(), text.data() + length, target);
                if (parsed.ec == std::errc{} && parsed.ptr == text.data() + length) {
                    const auto maximum = snapshot.total_ticks > 0
                        ? snapshot.total_ticks
                        : (std::numeric_limits<std::int64_t>::max)();
                    static_cast<void>(QueueAction(
                        SmvmActionType::seek_tick, -1, std::clamp<std::int64_t>(target, 0, maximum)));
                }
            }
            state.text_edit_mode.store(0, std::memory_order_release);
            state.text_edit_length.store(0, std::memory_order_release);
        } else if (character == VK_BACK) {
            if (length > 0) {
                --length;
                state.text_edit[length].store(0, std::memory_order_relaxed);
                state.text_edit_length.store(length, std::memory_order_release);
            }
        } else if (character >= 32 && character <= 126 && length < 63 &&
                   (edit_mode != 2 || (character >= '0' && character <= '9'))) {
            state.text_edit[length].store(static_cast<std::uint8_t>(character), std::memory_order_relaxed);
            state.text_edit_length.store(length + 1, std::memory_order_release);
        }
        return 0;
    }

    if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && has_snapshot) {
        const auto repeated = (lparam & (1LL << 30)) != 0;
        if (!repeated && state.text_edit_mode.load(std::memory_order_acquire) != 0 && wparam == VK_ESCAPE) {
            state.text_edit_mode.store(0, std::memory_order_release);
            state.text_edit_length.store(0, std::memory_order_release);
            return 0;
        }
        if (!repeated && InputMatchesKeyboard(snapshot.clean_view_key, wparam)) {
            TrackConsumedKeyDown(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
            const auto clean = !state.clean_view.load(std::memory_order_acquire);
            if (clean)
                SetMenuOpen(false);
            state.clean_view.store(clean, std::memory_order_release);
            PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::none);
            return 0;
        }
        if (!repeated && InputMatchesKeyboard(snapshot.menu_key, wparam) &&
            ((snapshot.flags & smvm_snapshot_replay_active) != 0 || menu_open)) {
            TrackConsumedKeyDown(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
            SetMenuOpen(!menu_open);
            return 0;
        }
        if (menu_open)
            return 0;
        if (!repeated && InputMatchesKeyboard(snapshot.add_key, wparam) && CanEditCampath(snapshot)) {
            TrackConsumedKeyDown(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
            if (state.callbacks.request_camera_capture != nullptr)
                state.callbacks.request_camera_capture(state.callbacks.context);
            return 0;
        }
        if (!repeated && InputMatchesKeyboard(snapshot.delete_key, wparam) && CanEditCampath(snapshot)) {
            TrackConsumedKeyDown(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
            static_cast<void>(QueueAction(SmvmActionType::delete_keyframe, snapshot.selected_keyframe));
            return 0;
        }
        if (!repeated && HandleCampathKeyboardBinding(wparam, snapshot)) {
            TrackConsumedKeyDown(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute);
            return 0;
        }
        const auto takeover = CanUseManualCamera(snapshot) &&
            (snapshot.flags & smvm_snapshot_input_takeover) != 0;
        if (RouteManualKeyboardEvent(
                snapshot,
                static_cast<std::uint32_t>(wparam),
                true,
                kManualWindowKeyRoute,
                takeover))
            return 0;
    }
    if (message == WM_KEYUP || message == WM_SYSKEYUP) {
        const auto takeover = has_snapshot && CanUseManualCamera(snapshot) &&
            (snapshot.flags & smvm_snapshot_input_takeover) != 0;
        if (RouteManualKeyboardEvent(
                snapshot,
                static_cast<std::uint32_t>(wparam),
                false,
                kManualWindowKeyRoute,
                takeover))
            return 0;
        if (ConsumeTrackedKeyUp(static_cast<std::uint32_t>(wparam), kMenuWindowKeyRoute))
            return 0;
    }
    if (message == WM_CHAR && menu_open)
        return 0;
    if (message == WM_SETCURSOR && menu_open) {
        SetCursor(LoadCursorW(nullptr, IDC_ARROW));
        return TRUE;
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
        if (!ApplyCursorStateOnWindowThread(false) || PointerStateRestorePending())
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
        if (g_overlay.context == nullptr || !CreatePipeline(g_overlay.device)) {
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
    if (!ReadSnapshot(snapshot) || (snapshot.flags & smvm_snapshot_internal_enabled) == 0 ||
        (snapshot.flags & smvm_snapshot_replay_active) == 0) {
        SetMenuOpen(false);
        state.vertex_count = 0;
        state.render_lock.clear(std::memory_order_release);
        return true;
    }

    CampathPayloadHeader path_header{};
    std::array<CampathKeyframe, kMaxCampathKeyframes> keys{};
    const auto has_path = ReadPath(path_header, keys.data(), keys.size());
    const auto clicks = state.mouse_buttons.exchange(0, std::memory_order_acq_rel);
    const auto menu_wheel = state.wheel_delta.exchange(0, std::memory_order_acq_rel);
    if (state.page == SmvmPage::campath && menu_wheel != 0)
        state.list_scroll = std::max(0, state.list_scroll - (menu_wheel / WHEEL_DELTA));
    state.frame_left_click = (clicks & 1u) != 0;
    state.frame_right_click = (clicks & 2u) != 0;
    state.vertex_count = 0;
    state.ui_transform = false;
    if (!state.clean_view.load(std::memory_order_acquire)) {
        const auto hide_visualization =
            (snapshot.flags & smvm_snapshot_hide_path_while_playing) != 0 &&
            (snapshot.flags & smvm_snapshot_campath_playing) != 0;
        if (has_path && !hide_visualization && (snapshot.flags & smvm_snapshot_camera_readable) != 0)
            DrawCampathVisualization(snapshot, path_header, keys.data());
        if (state.menu_open.load(std::memory_order_acquire))
            DrawFullMenu(snapshot, path_header, keys.data(), has_path);
        else
            DrawMinimalPill(snapshot);
    }
    const auto drawn = DrawVertices();
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
        if (GetModuleHandleW(L"rendersystemvulkan.dll") != nullptr) {
            PublishStatus(SmvmRendererBackend::unsupported, SmvmRendererError::unsupported_renderer);
            return 0;
        }
        const auto render_module = GetModuleHandleW(L"rendersystemdx11.dll");
        if (render_module != nullptr) {
            capture_attempted = true;
            if (!renderer_global)
                renderer_global = ResolveRenderFactoryGlobal(render_module);
            if (renderer_global && InstallFactoryCaptureHook(*renderer_global)) {
                PublishStatus(SmvmRendererBackend::d3d11, SmvmRendererError::present_not_observed);
                return 0;
            }
        }
        if (std::chrono::steady_clock::now() - started >= kInstallTimeout) {
            PublishStatus(
                capture_attempted ? SmvmRendererBackend::d3d11 : SmvmRendererBackend::none,
                capture_attempted ? SmvmRendererError::swapchain_probe_failed
                                  : SmvmRendererError::renderer_not_loaded);
            return 0;
        }
        Sleep(static_cast<DWORD>(kInstallRetryInterval.count()));
    }
    if (g_overlay.stop_requested.load(std::memory_order_acquire))
        return 0;
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

bool ConsumeSmvmManualInput(
    const SmvmSnapshotPayload& snapshot,
    SmvmManualInputFrame& frame) noexcept {
    frame = {};
    auto& state = g_overlay;
    if (!state.started.load(std::memory_order_acquire) ||
        state.menu_open.load(std::memory_order_acquire) || !CanUseManualCamera(snapshot)) {
        state.manual_mouse_delta_x.store(0, std::memory_order_release);
        state.manual_mouse_delta_y.store(0, std::memory_order_release);
        state.manual_wheel_delta.store(0, std::memory_order_release);
        return false;
    }

    const auto binding_down = [&state, &snapshot](const std::uint32_t binding) noexcept {
        const auto base = binding & kSmvmInputBaseMask;
        if (base == 0 || base >= state.key_down.size() ||
            state.key_down[base].load(std::memory_order_acquire) == 0)
            return false;
        if (!RequiredInputModifiersAreActive(binding, CurrentModifiers(base)))
            return false;
        // Under takeover, begin motion only from a key-down SMVM actually
        // consumed. This prevents activating a chord after its base key was
        // already delivered to Deadlock.
        return (snapshot.flags & smvm_snapshot_input_takeover) == 0 ||
               state.manual_key_routes[base].load(std::memory_order_acquire) != 0;
    };

    frame.mouse_delta_x = static_cast<double>(
        state.manual_mouse_delta_x.exchange(0, std::memory_order_acq_rel));
    frame.mouse_delta_y = static_cast<double>(
        state.manual_mouse_delta_y.exchange(0, std::memory_order_acq_rel));
    frame.wheel_steps = static_cast<double>(
        state.manual_wheel_delta.exchange(0, std::memory_order_acq_rel)) / WHEEL_DELTA;
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
    state.mouse_buttons.store(0, std::memory_order_release);
    state.wheel_delta.store(0, std::memory_order_release);
    state.manual_mouse_delta_x.store(0, std::memory_order_release);
    state.manual_mouse_delta_y.store(0, std::memory_order_release);
    state.manual_wheel_delta.store(0, std::memory_order_release);
    for (auto& key : state.key_down)
        key.store(0, std::memory_order_release);
    for (auto& routes : state.manual_key_routes)
        routes.store(0, std::memory_order_release);
    ResetBindingCaptureState();
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
    state.clean_view.store(false, std::memory_order_release);
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
    if (!state.started.load(std::memory_order_acquire))
        return true;
    state.stop_requested.store(true, std::memory_order_release);
    state.menu_open.store(false, std::memory_order_release);
    ResetSmvmManualInput();

    if (state.installer_thread != nullptr) {
        if (WaitForSingleObject(state.installer_thread, 31000) != WAIT_OBJECT_0)
            return false;
        CloseHandle(state.installer_thread);
        state.installer_thread = nullptr;
    }

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
    ResetSmvmManualInput();
    ResetConsumedReleaseRoutes();
    return true;
}

} // namespace deadlock_mvm
