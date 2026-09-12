cbuffer Look : register(b0) {
 float4 grade; // exposure, contrast, saturation, vibrance
 float4 balance; // temperature, tint, shadows, highlights
 float4 liftValue; float4 gammaValue; float4 gainValue;
 float4 bloom; // threshold, knee, intensity, radius
 float4 finish; // sharpen, vignette, grain, strength
 float4 texel; // source width inverse, height inverse, blur x, blur y
 uint4 flags; // seed, frame low, mode, source is sRGB view
 float4 lutInfo; // intensity, size, unused, unused
 float4 dof; // focus device depth, strength, max radius texels, enabled
};
Texture2D source : register(t0);
Texture2D bloomImage : register(t1);
Texture3D lutImage : register(t2);
Texture2D depthImage : register(t3);
SamplerState linearClamp : register(s0);
struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
Vertex VSMain(uint id : SV_VertexID) {
 Vertex o; o.uv=float2((id<<1)&2,id&2);o.position=float4(o.uv*float2(2,-2)+float2(-1,1),0,1);return o;
}
float3 Decode(float3 c) { return lerp(c/12.92,pow(max((c+.055)/1.055,0),2.4),step(.04045,c)); }
float3 Encode(float3 c) { c=max(c,0);return lerp(c*12.92,1.055*pow(c,1/2.4)-.055,step(.0031308,c)); }
float3 SampleLinear(float2 uv) {float3 c=source.SampleLevel(linearClamp,uv,0).rgb;return flags.w?c:Decode(c);}
float3 Bright(float2 uv) {
 float3 c=SampleLinear(uv);float br=max(c.r,max(c.g,c.b));float knee=max(bloom.y*bloom.x,.0001);
 float soft=clamp(br-bloom.x+knee,0,2*knee);soft=soft*soft/(4*knee);
 return c*(max(br-bloom.x,soft)/max(br,.0001));
}
uint Hash(uint x) { x ^= x>>16;x*=0x7feb352d;x^=x>>15;x*=0x846ca68b;x^=x>>16;return x; }
float4 PSMain(Vertex input) : SV_TARGET {
 float2 uv=input.uv;
 if(flags.z==1) { // Four source taps prevent single-sample downsample aliasing.
  float2 d=texel.xy*.5;
  return float4((Bright(uv+d)+Bright(uv-d)+Bright(uv+float2(d.x,-d.y))+Bright(uv+float2(-d.x,d.y)))*.25,1);
 }
 if(flags.z==2) {
  float2 d=texel.zw;
  float3 c=source.SampleLevel(linearClamp,uv,0).rgb*.227027;
  c+=(source.SampleLevel(linearClamp,uv+d*1.384615,0).rgb+source.SampleLevel(linearClamp,uv-d*1.384615,0).rgb)*.316216;
  c+=(source.SampleLevel(linearClamp,uv+d*3.230769,0).rgb+source.SampleLevel(linearClamp,uv-d*3.230769,0).rgb)*.070270;
  return float4(c,1);
 }
 float4 original=source.SampleLevel(linearClamp,uv,0);
 float3 base=flags.w?original.rgb:Decode(original.rgb);
 float3 c=base;
 if(dof.w>0.5 && dof.z>0.0) {
  // Reversed-Z device depth: far ~= 0, near ~= 1. Blur grows with the
  // distance from the in-focus plane and is capped by the max radius.
  float deviceDepth=depthImage.SampleLevel(linearClamp,uv,0).r;
  float coc=saturate(abs(deviceDepth-dof.x)*dof.y);
  if(coc>0.002) {
   float2 r=texel.xy*(dof.z*coc);
   const float2 k[8]={float2(1,0),float2(-1,0),float2(0,1),float2(0,-1),
                      float2(.707,.707),float2(-.707,.707),float2(.707,-.707),float2(-.707,-.707)};
   float3 acc=c; float w=1.0;
   [unroll] for(int i=0;i<8;++i){ acc+=SampleLinear(uv+k[i]*r); w+=1.0; }
   c=acc/w;
  }
 }
 if(finish.x>0) {
  float3 n=(SampleLinear(uv+float2(texel.x,0))+SampleLinear(uv-float2(texel.x,0))+SampleLinear(uv+float2(0,texel.y))+SampleLinear(uv-float2(0,texel.y)))*.25;
  c=max(0,c+clamp(c-n,-.05,.05)*finish.x);
 }
 c+=bloomImage.SampleLevel(linearClamp,uv,0).rgb*bloom.z;
 c*=exp2(grade.x)*max(float3(.1,.1,.1),float3(1+balance.x*.1+balance.y*.05,1-balance.y*.1,1-balance.x*.1+balance.y*.05));
 c=max(0,c+liftValue.rgb);c=pow(c,1/max(gammaValue.rgb,.05))*gainValue.rgb;
 float lum=dot(c,float3(.2126,.7152,.0722));
 c*=exp2(balance.z*(1-smoothstep(0,.4,lum))+balance.w*smoothstep(.4,1,lum));
 c=max(0,(c-.18)*grade.y+.18);
 lum=dot(c,float3(.2126,.7152,.0722));float spread=max(c.r,max(c.g,c.b))-min(c.r,min(c.g,c.b));
 c=lerp(lum.xxx,c,grade.z*(1+grade.w*(1-saturate(spread))));
 c=saturate(Encode(c));
 if(lutInfo.x>0 && lutInfo.y>=2) {
  float3 coord=(c*(lutInfo.y-1)+.5)/lutInfo.y;
  c=lerp(c,lutImage.SampleLevel(linearClamp,coord,0).rgb,lutInfo.x);
 }
 float2 p=(uv-.5)*2;float v=smoothstep(.2,1.5,dot(p,p));c*=1-finish.y*v;
 uint2 pixel=(uint2)input.position.xy;
 float noise=(Hash(pixel.x+pixel.y*65537u+Hash(flags.x^flags.y))&65535u)/65535.0-.5;
 c+=noise*finish.z*.08;
 c=lerp(Encode(base),saturate(c),finish.w);
 // Write through UNORM RTV; explicit transfer conversion also covers SRGB source views.
 return float4(saturate(c),original.a);
}
