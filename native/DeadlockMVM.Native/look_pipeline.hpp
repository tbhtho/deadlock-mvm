#pragma once
#include <cstdint>
#include <memory>
struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11Texture2D;
struct ID3D11ShaderResourceView;
namespace deadlock_mvm {
struct LookSettings;
enum class LookProcessStatus {
    Bypassed,
    Applied,
    Unsupported,
    Failed
};
struct LookGpuTimings {
    std::uint32_t samples{};
    double median_ms{}, p95_ms{};
    std::uint64_t texture_bytes{};
};
// Depth-of-field controls for the live post-process. Native-owned so the
// feature does not force a wire-format change; zero radius disables it.
struct LookDofParameters {
    bool enabled{};
    float focus{0.5F};      // reversed-Z device depth of the in-focus plane
    float strength{1.0F};   // how fast blur grows away from focus
    float max_radius{0.0F}; // maximum blur radius in source texels
};
// Render-thread owned. Initialize/PrepareLut must be serialized with Process.
// Shader compilation is a build step; no disk access occurs in this class.
class LookPipeline final {
  public:
    LookPipeline();
    ~LookPipeline();
    LookPipeline(const LookPipeline &) = delete;
    LookPipeline &operator=(const LookPipeline &) = delete;
    bool Initialize(ID3D11Device *device) noexcept;
    bool PrepareLut(ID3D11Device *device, const float *rgb, std::uint32_t size) noexcept;
    LookProcessStatus Process(ID3D11DeviceContext *context, ID3D11Texture2D *backbuffer,
                              const LookSettings &settings, std::uint64_t frame_key,
                              const LookDofParameters *dof = nullptr,
                              ID3D11ShaderResourceView *depth = nullptr) noexcept;
    ID3D11Texture2D *SourceTexture() const noexcept;
    void RestoreSource(ID3D11DeviceContext *, ID3D11Texture2D *) noexcept;
    // Call before Fog, then SavePresentedImage after editor drawing. No CPU readback.
    bool RecoverRepeatedSource(ID3D11DeviceContext *, ID3D11Texture2D *,
                               bool camera_sequence_repeated) noexcept;
    void SavePresentedImage(ID3D11DeviceContext *, ID3D11Texture2D *) noexcept;
    void ResetSizeResources() noexcept;
    void Reset() noexcept;
    const char *StatusMessage() const noexcept;
    // Nonblocking rolling GPU effect timing; excludes freshness recovery and capture.
    LookGpuTimings Timings() const noexcept;

  private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace deadlock_mvm
