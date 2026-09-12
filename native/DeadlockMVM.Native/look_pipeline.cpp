#include "look_pipeline.hpp"
#include "look_settings.hpp"
#include <algorithm>
#include <array>
#include <cmath>
#include <d3d11.h>
#include <vector>
#include <wrl/client.h>

#include "look_fresh_cs.h"
#include "look_fresh_ps.h"
#include "look_ps.h"
#include "look_vs.h"

namespace deadlock_mvm {
using Microsoft::WRL::ComPtr;
namespace {
// Copies and clears are also subject to the game's outstanding predicate.
struct PredicateScope {
    ID3D11DeviceContext *context;
    ComPtr<ID3D11Predicate> predicate;
    BOOL value{};
    explicit PredicateScope(ID3D11DeviceContext *c) : context(c) {
        context->GetPredication(&predicate, &value);
        context->SetPredication(nullptr, FALSE);
    }
    ~PredicateScope() {
        context->SetPredication(predicate.Get(), value);
    }
};

struct Constants {
    float grade[4], balance[4], lift[4], gamma[4], gain[4], bloom[4], finish[4], texel[4];
    UINT flags[4];
    float lut[4];
    float dof[4];
};
struct SavedState {
    ID3D11DeviceContext *c;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3D11GeometryShader> gs;
    ComPtr<ID3D11HullShader> hs;
    ComPtr<ID3D11DomainShader> ds;
    std::array<ID3D11ClassInstance *, 256> vc{}, pc{}, gc{}, hc{}, dc{};
    UINT vn = 256, pn = 256, gn = 256, hn = 256, dn = 256;
    std::array<ID3D11RenderTargetView *, 8> rt{};
    ComPtr<ID3D11DepthStencilView> depth;
    std::array<ID3D11UnorderedAccessView *, 8> outputUavs{};
    ComPtr<ID3D11Predicate> predicate;
    BOOL predicateValue = FALSE;
    std::array<ID3D11ShaderResourceView *, 4> srv{};
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11Buffer> cb;
    ComPtr<ID3D11InputLayout> layout;
    D3D11_PRIMITIVE_TOPOLOGY topology{};
    ComPtr<ID3D11BlendState> blend;
    float factor[4]{};
    UINT mask{};
    ComPtr<ID3D11DepthStencilState> depthState;
    UINT stencil{};
    ComPtr<ID3D11RasterizerState> raster;
    UINT count = 16;
    D3D11_VIEWPORT viewport[16]{};
    explicit SavedState(ID3D11DeviceContext *context) : c(context) {
        c->VSGetShader(&vs, vc.data(), &vn);
        c->PSGetShader(&ps, pc.data(), &pn);
        c->GSGetShader(&gs, gc.data(), &gn);
        c->HSGetShader(&hs, hc.data(), &hn);
        c->DSGetShader(&ds, dc.data(), &dn);
        c->OMGetRenderTargets(8, rt.data(), &depth);
        c->PSGetShaderResources(0, 4, srv.data());
        c->PSGetSamplers(0, 1, &sampler);
        c->PSGetConstantBuffers(0, 1, &cb);
        c->OMGetRenderTargetsAndUnorderedAccessViews(0, nullptr, nullptr, 0, 8, outputUavs.data());
        c->GetPredication(&predicate, &predicateValue);
        c->SetPredication(nullptr, FALSE);
        c->IAGetInputLayout(&layout);
        c->IAGetPrimitiveTopology(&topology);
        c->OMGetBlendState(&blend, factor, &mask);
        c->OMGetDepthStencilState(&depthState, &stencil);
        c->RSGetState(&raster);
        c->RSGetViewports(&count, viewport);
    }
    ~SavedState() {
        ID3D11ShaderResourceView *nulls[4]{};
        c->PSSetShaderResources(0, 4, nulls);
        UINT rtCount = 0;
        for (UINT i = 0; i < 8; ++i)
            if (rt[i])
                rtCount = i + 1;
        c->OMSetRenderTargetsAndUnorderedAccessViews(rtCount, rt.data(), depth.Get(), rtCount, 8 - rtCount,
                                                     outputUavs.data() + rtCount, nullptr);
        c->PSSetShaderResources(0, 4, srv.data());
        c->PSSetSamplers(0, 1, sampler.GetAddressOf());
        c->PSSetConstantBuffers(0, 1, cb.GetAddressOf());
        c->VSSetShader(vs.Get(), vc.data(), vn);
        c->PSSetShader(ps.Get(), pc.data(), pn);
        c->GSSetShader(gs.Get(), gc.data(), gn);
        c->HSSetShader(hs.Get(), hc.data(), hn);
        c->DSSetShader(ds.Get(), dc.data(), dn);
        c->IASetInputLayout(layout.Get());
        c->IASetPrimitiveTopology(topology);
        c->OMSetBlendState(blend.Get(), factor, mask);
        c->OMSetDepthStencilState(depthState.Get(), stencil);
        c->RSSetState(raster.Get());
        c->RSSetViewports(count, viewport);
        c->SetPredication(predicate.Get(), predicateValue);
        for (auto *p : rt)
            if (p)
                p->Release();
        for (auto *p : srv)
            if (p)
                p->Release();
        for (auto *p : outputUavs)
            if (p)
                p->Release();
        for (auto *p : vc)
            if (p)
                p->Release();
        for (auto *p : pc)
            if (p)
                p->Release();
        for (auto *p : gc)
            if (p)
                p->Release();
        for (auto *p : hc)
            if (p)
                p->Release();
        for (auto *p : dc)
            if (p)
                p->Release();
    }
};
struct Surface {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11RenderTargetView> rtv;
};
bool MakeSurface(ID3D11Device *d, UINT width, UINT height, DXGI_FORMAT format, Surface &s) {
    UINT support = 0;
    constexpr UINT required = D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT_SHADER_SAMPLE |
                              D3D11_FORMAT_SUPPORT_RENDER_TARGET;
    if (FAILED(d->CheckFormatSupport(format, &support)) || (support & required) != required)
        return false;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    return SUCCEEDED(d->CreateTexture2D(&desc, nullptr, &s.texture)) &&
           SUCCEEDED(d->CreateShaderResourceView(s.texture.Get(), nullptr, &s.srv)) &&
           SUCCEEDED(d->CreateRenderTargetView(s.texture.Get(), nullptr, &s.rtv));
}
} // namespace
struct LookPipeline::Impl {
    struct Timer {
        ComPtr<ID3D11Query> disjoint, start, end;
        bool pending = false;
    };
    std::array<Timer, 4> timers;
    std::array<double, 128> milliseconds{};
    UINT sampleCount = 0, sampleNext = 0;
    ComPtr<ID3D11ComputeShader> compare;
    ComPtr<ID3D11PixelShader> recover;
    ComPtr<ID3D11Buffer> changed;
    ComPtr<ID3D11UnorderedAccessView> changedUav;
    ComPtr<ID3D11ShaderResourceView> changedSrv;
    Surface incoming, presented, raw[2];
    UINT rawIndex{}, freshWidth{}, freshHeight{};
    DXGI_FORMAT freshFormat = DXGI_FORMAT_UNKNOWN;
    bool presentedValid = false;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3D11Buffer> cb;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> raster;
    ComPtr<ID3D11DepthStencilState> depth;
    Surface source, processed, blur[2];
    ComPtr<ID3D11ShaderResourceView> lut;
    UINT lutSize{}, width{}, height{}, bw{}, bh{}, quality{};
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    const char *message = "Reshade initializing";
    Timer *BeginTiming(ID3D11DeviceContext *c) {
        Timer *available = nullptr;
        for (auto &t : timers) {
            if (t.pending) {
                D3D11_QUERY_DATA_TIMESTAMP_DISJOINT d{};
                UINT64 a = 0, b = 0;
                if (c->GetData(t.disjoint.Get(), &d, sizeof(d), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK &&
                    c->GetData(t.start.Get(), &a, sizeof(a), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK &&
                    c->GetData(t.end.Get(), &b, sizeof(b), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK) {
                    t.pending = false;
                    if (!d.Disjoint && d.Frequency && b >= a) {
                        milliseconds[sampleNext] =
                            static_cast<double>(b - a) * 1000 / static_cast<double>(d.Frequency);
                        sampleNext = (sampleNext + 1) % 128;
                        sampleCount = std::min(128u, sampleCount + 1);
                    }
                }
            }
            if (!t.pending && t.disjoint && !available)
                available = &t;
        }
        if (available) {
            c->Begin(available->disjoint.Get());
            c->End(available->start.Get());
        }
        return available;
    }
    void EndTiming(ID3D11DeviceContext *c, Timer *t) {
        if (t) {
            c->End(t->end.Get());
            c->End(t->disjoint.Get());
            t->pending = true;
        }
    }
    bool Sizes(const D3D11_TEXTURE2D_DESC &desc, UINT q) {
        if (width == desc.Width && height == desc.Height && format == desc.Format && quality == q &&
            source.texture)
            return true;
        source = {};
        processed = {};
        blur[0] = {};
        blur[1] = {};
        width = height = 0;
        UINT divisor = q == 0 ? 4 : 2;
        bw = (desc.Width + divisor - 1) / divisor;
        bh = (desc.Height + divisor - 1) / divisor;
        DXGI_FORMAT f = desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB   ? DXGI_FORMAT_R8G8B8A8_UNORM
                        : desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ? DXGI_FORMAT_B8G8R8A8_UNORM
                                                                         : desc.Format;
        if (!MakeSurface(device.Get(), desc.Width, desc.Height, f, source) ||
            !MakeSurface(device.Get(), desc.Width, desc.Height, f, processed) ||
            !MakeSurface(device.Get(), bw, bh, DXGI_FORMAT_R16G16B16A16_FLOAT, blur[0]) ||
            !MakeSurface(device.Get(), bw, bh, DXGI_FORMAT_R16G16B16A16_FLOAT, blur[1]))
            return false;
        width = desc.Width;
        height = desc.Height;
        format = desc.Format;
        quality = q;
        return true;
    }
    bool Draw(ID3D11DeviceContext *c, ID3D11RenderTargetView *target, ID3D11ShaderResourceView *input,
              ID3D11ShaderResourceView *bloomSrv, UINT w, UINT h, const Constants &values,
              ID3D11ShaderResourceView *depthSrv) {
        // One permanent DEFAULT buffer avoids the observer's dynamic Map registry path.
        c->UpdateSubresource(cb.Get(), 0, nullptr, &values, 0, 0);
        ID3D11ShaderResourceView *nulls[4]{};
        c->PSSetShaderResources(0, 4, nulls);
        c->OMSetRenderTargets(1, &target, nullptr);
        // Depth occupies the fourth saved slot at t3 and is left unbound (or a
        // harmless dummy) when the effect is disabled.
        ID3D11ShaderResourceView *views[4]{input, bloomSrv, lut.Get(),
                                           depthSrv != nullptr ? depthSrv : input};
        c->PSSetShaderResources(0, 4, views);
        D3D11_VIEWPORT vp{0, 0, static_cast<float>(w), static_cast<float>(h), 0, 1};
        c->RSSetViewports(1, &vp);
        c->Draw(3, 0);
        return true;
    }
};
LookPipeline::LookPipeline() : impl_(std::make_unique<Impl>()) {
}
LookPipeline::~LookPipeline() = default;
void LookPipeline::ResetSizeResources() noexcept {
    impl_->source = {};
    impl_->processed = {};
    impl_->blur[0] = {};
    impl_->blur[1] = {};
    impl_->width = impl_->height = impl_->bw = impl_->bh = 0;
    impl_->incoming = {};
    impl_->presented = {};
    impl_->raw[0] = {};
    impl_->raw[1] = {};
    impl_->freshWidth = impl_->freshHeight = 0;
    impl_->presentedValid = false;
}
void LookPipeline::Reset() noexcept {
    *impl_ = Impl{};
}
const char *LookPipeline::StatusMessage() const noexcept {
    return impl_->message;
}
LookGpuTimings LookPipeline::Timings() const noexcept {
    LookGpuTimings result{};
    result.samples = impl_->sampleCount;
    auto sorted = impl_->milliseconds;
    std::sort(sorted.begin(), sorted.begin() + result.samples);
    if (result.samples) {
        result.median_ms = sorted[result.samples / 2];
        result.p95_ms = sorted[(result.samples - 1) * 95 / 100];
    }
    result.texture_bytes = static_cast<std::uint64_t>(impl_->width) * impl_->height * 8 +
                           static_cast<std::uint64_t>(impl_->bw) * impl_->bh * 16 +
                           static_cast<std::uint64_t>(impl_->freshWidth) * impl_->freshHeight * 16 +
                           static_cast<std::uint64_t>(impl_->lutSize) * impl_->lutSize * impl_->lutSize * 16;
    return result;
}
ID3D11Texture2D *LookPipeline::SourceTexture() const noexcept {
    return impl_->source.texture.Get();
}
void LookPipeline::RestoreSource(ID3D11DeviceContext *c, ID3D11Texture2D *b) noexcept {
    if (c && b && impl_->source.texture) {
        PredicateScope predicate(c);
        c->CopyResource(b, impl_->source.texture.Get());
    }
}
bool LookPipeline::Initialize(ID3D11Device *d) noexcept {
    if (!d)
        return false;
    if (impl_->device.Get() == d && impl_->ps)
        return true;
    Reset();
    auto &p = *impl_;
    p.device = d;
    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = sizeof(Constants);
    bd.Usage = D3D11_USAGE_DEFAULT;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    D3D11_RASTERIZER_DESC rd{};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    D3D11_DEPTH_STENCIL_DESC dd{};
    dd.DepthEnable = FALSE;
    if (FAILED(d->CreateVertexShader(g_look_vs, sizeof(g_look_vs), nullptr, &p.vs)) ||
        FAILED(d->CreatePixelShader(g_look_ps, sizeof(g_look_ps), nullptr, &p.ps)) ||
        FAILED(d->CreateBuffer(&bd, nullptr, &p.cb)) || FAILED(d->CreateSamplerState(&sd, &p.sampler)) ||
        FAILED(d->CreateRasterizerState(&rd, &p.raster)) ||
        FAILED(d->CreateDepthStencilState(&dd, &p.depth)) ||
        FAILED(d->CreateComputeShader(g_look_fresh_cs, sizeof(g_look_fresh_cs), nullptr, &p.compare)) ||
        FAILED(d->CreatePixelShader(g_look_fresh_ps, sizeof(g_look_fresh_ps), nullptr, &p.recover))) {
        p.ps.Reset();
        p.message = "Reshade GPU initialization failed";
        return false;
    }
    D3D11_BUFFER_DESC flagDesc{};
    flagDesc.ByteWidth = 4;
    flagDesc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud{};
    ud.Format = DXGI_FORMAT_R32_UINT;
    ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    ud.Buffer.NumElements = 1;
    D3D11_SHADER_RESOURCE_VIEW_DESC sv{};
    sv.Format = DXGI_FORMAT_R32_UINT;
    sv.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    sv.Buffer.NumElements = 1;
    if (FAILED(d->CreateBuffer(&flagDesc, nullptr, &p.changed)) ||
        FAILED(d->CreateUnorderedAccessView(p.changed.Get(), &ud, &p.changedUav)) ||
        FAILED(d->CreateShaderResourceView(p.changed.Get(), &sv, &p.changedSrv))) {
        p.message = "Reshade freshness initialization failed";
        p.ps.Reset();
        return false;
    }
    for (auto &t : p.timers) {
        D3D11_QUERY_DESC q{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
        if (FAILED(d->CreateQuery(&q, &t.disjoint)))
            continue;
        q.Query = D3D11_QUERY_TIMESTAMP;
        if (FAILED(d->CreateQuery(&q, &t.start)) || FAILED(d->CreateQuery(&q, &t.end)))
            t.disjoint.Reset();
    }
    p.message = "Reshade ready (post-tonemap SDR)";
    return true;
}
bool LookPipeline::RecoverRepeatedSource(ID3D11DeviceContext *c, ID3D11Texture2D *b, bool repeated) noexcept {
    auto &p = *impl_;
    if (!c || !b || !p.compare)
        return false;
    D3D11_TEXTURE2D_DESC d{};
    b->GetDesc(&d);
    if (!d.Width || !d.Height || d.SampleDesc.Count != 1 || d.ArraySize != 1 || d.MipLevels != 1)
        return false;
    DXGI_FORMAT f = d.Format;
    if (f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)
        f = DXGI_FORMAT_R8G8B8A8_UNORM;
    if (f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
        f = DXGI_FORMAT_B8G8R8A8_UNORM;
    if (f != DXGI_FORMAT_R8G8B8A8_UNORM && f != DXGI_FORMAT_B8G8R8A8_UNORM)
        return false;
    if (p.freshWidth != d.Width || p.freshHeight != d.Height || p.freshFormat != f || !p.raw[0].texture) {
        p.incoming = {};
        p.presented = {};
        p.raw[0] = {};
        p.raw[1] = {};
        p.presentedValid = false;
        p.freshWidth = p.freshHeight = 0;
        if (!MakeSurface(p.device.Get(), d.Width, d.Height, f, p.incoming) ||
            !MakeSurface(p.device.Get(), d.Width, d.Height, f, p.presented) ||
            !MakeSurface(p.device.Get(), d.Width, d.Height, f, p.raw[0]) ||
            !MakeSurface(p.device.Get(), d.Width, d.Height, f, p.raw[1]))
            return false;
        p.freshWidth = d.Width;
        p.freshHeight = d.Height;
        p.freshFormat = f;
        p.rawIndex = 0;
    }
    if (!repeated || !p.presentedValid) {
        PredicateScope predicate(c);
        c->CopyResource(p.raw[p.rawIndex].texture.Get(), b);
        return true;
    }
    SavedState graphics(c);
    ComPtr<ID3D11ComputeShader> previousCs;
    std::array<ID3D11ClassInstance *, 256> instances{};
    UINT n = 256;
    c->CSGetShader(&previousCs, instances.data(), &n);
    std::array<ID3D11ShaderResourceView *, 2> views{};
    c->CSGetShaderResources(0, 2, views.data());
    ComPtr<ID3D11UnorderedAccessView> uav;
    c->CSGetUnorderedAccessViews(0, 1, &uav);
    c->OMSetRenderTargets(0, nullptr, nullptr);
    c->CopyResource(p.incoming.texture.Get(), b);
    UINT zeros[4]{};
    c->ClearUnorderedAccessViewUint(p.changedUav.Get(), zeros);
    ID3D11ShaderResourceView *compareViews[]{p.incoming.srv.Get(), p.presented.srv.Get()};
    c->CSSetShaderResources(0, 2, compareViews);
    c->CSSetUnorderedAccessViews(0, 1, p.changedUav.GetAddressOf(), nullptr);
    c->CSSetShader(p.compare.Get(), nullptr, 0);
    c->Dispatch((d.Width + 15) / 16, (d.Height + 15) / 16, 1);
    ID3D11UnorderedAccessView *noUav = nullptr;
    c->CSSetUnorderedAccessViews(0, 1, &noUav, nullptr);
    ID3D11ShaderResourceView *noViews[2]{};
    c->CSSetShaderResources(0, 2, noViews);
    c->VSSetShader(p.vs.Get(), nullptr, 0);
    c->PSSetShader(p.recover.Get(), nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    c->HSSetShader(nullptr, nullptr, 0);
    c->DSSetShader(nullptr, nullptr, 0);
    c->IASetInputLayout(nullptr);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->OMSetBlendState(nullptr, nullptr, 0xffffffff);
    c->OMSetDepthStencilState(p.depth.Get(), 0);
    c->RSSetState(p.raster.Get());
    UINT next = 1 - p.rawIndex;
    c->OMSetRenderTargets(1, p.raw[next].rtv.GetAddressOf(), nullptr);
    ID3D11ShaderResourceView *recoverViews[]{p.incoming.srv.Get(), p.presented.srv.Get(),
                                             p.raw[p.rawIndex].srv.Get(), p.changedSrv.Get()};
    c->PSSetShaderResources(0, 4, recoverViews);
    D3D11_VIEWPORT vp{0, 0, static_cast<float>(d.Width), static_cast<float>(d.Height), 0, 1};
    c->RSSetViewports(1, &vp);
    c->Draw(3, 0);
    ID3D11ShaderResourceView *nulls[4]{};
    c->PSSetShaderResources(0, 4, nulls);
    c->OMSetRenderTargets(0, nullptr, nullptr);
    c->CopyResource(b, p.raw[next].texture.Get());
    p.rawIndex = next;
    c->CSSetShader(previousCs.Get(), instances.data(), n);
    c->CSSetShaderResources(0, 2, views.data());
    c->CSSetUnorderedAccessViews(0, 1, uav.GetAddressOf(), nullptr);
    for (auto *instance : instances)
        if (instance)
            instance->Release();
    for (auto *view : views)
        if (view)
            view->Release();
    return true;
}
void LookPipeline::SavePresentedImage(ID3D11DeviceContext *c, ID3D11Texture2D *b) noexcept {
    if (!c || !b || !impl_->presented.texture)
        return;
    D3D11_TEXTURE2D_DESC d{};
    b->GetDesc(&d);
    if (d.Width != impl_->freshWidth || d.Height != impl_->freshHeight)
        return;
    PredicateScope predicate(c);
    c->CopyResource(impl_->presented.texture.Get(), b);
    impl_->presentedValid = true;
}
bool LookPipeline::PrepareLut(ID3D11Device *d, const float *rgb, std::uint32_t size) noexcept {
    if (!d || impl_->device.Get() != d || !rgb || size < 2 || size > 64)
        return false;
    try {
        std::vector<float> rgba(static_cast<size_t>(size) * size * size * 4);
        for (size_t i = 0; i < rgba.size() / 4; ++i) {
            for (size_t ch = 0; ch < 3; ++ch) {
                float x = rgb[i * 3 + ch];
                if (!std::isfinite(x) || x < 0 || x > 1)
                    return false;
                rgba[i * 4 + ch] = x;
            }
            rgba[i * 4 + 3] = 1;
        }
        D3D11_TEXTURE3D_DESC td{};
        td.Width = td.Height = td.Depth = size;
        td.MipLevels = 1;
        td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
        td.Usage = D3D11_USAGE_IMMUTABLE;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{rgba.data(), size * 16, size * size * 16};
        ComPtr<ID3D11Texture3D> tex;
        ComPtr<ID3D11ShaderResourceView> view;
        if (FAILED(d->CreateTexture3D(&td, &data, &tex)) ||
            FAILED(d->CreateShaderResourceView(tex.Get(), nullptr, &view)))
            return false;
        impl_->lut = std::move(view);
        impl_->lutSize = size;
        return true;
    } catch (...) {
        return false;
    }
}
LookProcessStatus LookPipeline::Process(ID3D11DeviceContext *c, ID3D11Texture2D *b, const LookSettings &s,
                                        std::uint64_t key, const LookDofParameters *dof,
                                        ID3D11ShaderResourceView *depth) noexcept {
    auto &p = *impl_;
    const auto dof_active = dof != nullptr && dof->enabled && dof->max_radius > 0.0f;
    if ((!s.enabled || IsNeutralLook(s)) && !dof_active) {
        p.message = "Reshade bypassed";
        return LookProcessStatus::Bypassed;
    }
    if (!c || !b || !p.ps || !ValidateLookSettings(s)) {
        p.message = "Reshade invalid settings or uninitialized device";
        return LookProcessStatus::Failed;
    }
    D3D11_TEXTURE2D_DESC desc{};
    b->GetDesc(&desc);
    if (desc.Width == 0 || desc.Height == 0 || desc.SampleDesc.Count != 1 || desc.ArraySize != 1 ||
        desc.MipLevels != 1 ||
        (desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM && desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM_SRGB &&
         desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)) {
        p.message = "Reshade requires single-sample RGBA/BGRA 8-bit SDR";
        return LookProcessStatus::Unsupported;
    }
    if (s.lut_intensity > 0 && (!p.lut || p.lutSize != s.lut_size)) {
        p.message = "Selected LUT not uploaded; preserving source";
        return LookProcessStatus::Failed;
    }
    if (!p.Sizes(desc, s.bloom_quality)) {
        p.message = "Reshade size resource allocation failed";
        return LookProcessStatus::Failed;
    }
    SavedState saved(c);
    auto *timer = p.BeginTiming(c);
    c->OMSetRenderTargets(0, nullptr, nullptr);
    c->CopyResource(p.source.texture.Get(), b);
    c->VSSetShader(p.vs.Get(), nullptr, 0);
    c->PSSetShader(p.ps.Get(), nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    c->HSSetShader(nullptr, nullptr, 0);
    c->DSSetShader(nullptr, nullptr, 0);
    c->IASetInputLayout(nullptr);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->OMSetBlendState(nullptr, nullptr, 0xffffffff);
    c->OMSetDepthStencilState(p.depth.Get(), 0);
    c->RSSetState(p.raster.Get());
    c->PSSetSamplers(0, 1, p.sampler.GetAddressOf());
    c->PSSetConstantBuffers(0, 1, p.cb.GetAddressOf());
    Constants v{{s.exposure, s.contrast, s.saturation, s.vibrance},
                {s.temperature, s.tint, s.shadows, s.highlights},
                {s.lift_r, s.lift_g, s.lift_b, 0},
                {s.gamma_r, s.gamma_g, s.gamma_b, 0},
                {s.gain_r, s.gain_g, s.gain_b, 0},
                {s.bloom_threshold, s.bloom_knee, s.bloom_intensity, s.bloom_radius},
                {s.sharpen, s.vignette, s.grain_strength, s.strength},
                {1.0f / p.width, 1.0f / p.height, 0, 0},
                {s.grain_seed, static_cast<UINT>(key ^ (key >> 32)), 0, 0},
                {s.lut_intensity, static_cast<float>(s.lut_size), 0, 0},
                {dof_active ? dof->focus : 0.0f, dof_active ? dof->strength : 0.0f,
                 dof_active ? dof->max_radius : 0.0f, dof_active ? 1.0f : 0.0f}};
    bool ok = true;
    if (s.bloom_intensity > 0) {
        v.flags[2] = 1;
        ok = p.Draw(c, p.blur[0].rtv.Get(), p.source.srv.Get(), nullptr, p.bw, p.bh, v, nullptr);
        v.flags[2] = 2;
        UINT iterations = s.bloom_quality == 2 ? 2 : 1;
        for (UINT i = 0; i < iterations && ok; ++i) {
            v.texel[2] = s.bloom_radius / p.bw;
            v.texel[3] = 0;
            ok = p.Draw(c, p.blur[1].rtv.Get(), p.blur[0].srv.Get(), nullptr, p.bw, p.bh, v, nullptr);
            v.texel[2] = 0;
            v.texel[3] = s.bloom_radius / p.bh;
            if (ok)
                ok = p.Draw(c, p.blur[0].rtv.Get(), p.blur[1].srv.Get(), nullptr, p.bw, p.bh, v, nullptr);
        }
    }
    v.flags[2] = 0;
    v.texel[2] = v.texel[3] = 0;
    if (ok)
        ok = p.Draw(c, p.processed.rtv.Get(), p.source.srv.Get(),
                    s.bloom_intensity > 0 ? p.blur[0].srv.Get() : nullptr, p.width, p.height, v,
                    dof_active ? depth : nullptr);
    if (ok) {
        c->OMSetRenderTargets(0, nullptr, nullptr);
        c->CopyResource(b, p.processed.texture.Get());
    }
    p.EndTiming(c, timer);
    if (FAILED(p.device->GetDeviceRemovedReason())) {
        p.message = "Reshade device removed; awaiting renderer recovery";
        return LookProcessStatus::Failed;
    }
    p.message = ok ? "Reshade active (post-tonemap SDR)" : "Reshade GPU processing failed";
    return ok ? LookProcessStatus::Applied : LookProcessStatus::Failed;
}
} // namespace deadlock_mvm
