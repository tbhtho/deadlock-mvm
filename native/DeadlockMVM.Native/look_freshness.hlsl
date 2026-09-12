// GPU-only detection of a repeated presented image (including editor pixels).
Texture2D<float4> incoming : register(t0);
Texture2D<float4> lastPresented : register(t1);
Texture2D<float4> lastRaw : register(t2);
RWBuffer<uint> changed : register(u0);
Buffer<uint> changedRead : register(t3);
[numthreads(16,16,1)]
void CompareCS(uint3 id : SV_DispatchThreadID) {
 uint w,h;incoming.GetDimensions(w,h);if(id.x>=w||id.y>=h)return;
 if(any(incoming.Load(int3(id.xy,0))!=lastPresented.Load(int3(id.xy,0))))InterlockedOr(changed[0],1);
}
float4 RecoverPS(float4 position : SV_POSITION) : SV_TARGET {
 int3 p=int3(position.xy,0);return changedRead[0]==0?lastRaw.Load(p):incoming.Load(p);
}
