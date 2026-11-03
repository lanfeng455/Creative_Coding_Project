#ifndef FOCUS_WATER_SURFACE_NOISE
#define FOCUS_WATER_SURFACE_NOISE
// Simplex noise adapted from Ian McEwan / Ashima Arts, MIT.
// https://github.com/ashima/webgl-noise
// Water height composition adapted from Stockholms Strom,
// Peder Norrby / Trapcode, 2016, CC BY-NC-SA 3.0.
float3 WaterMod289(float3 x) { return x - floor(x / 289.0) * 289.0; }
float4 WaterMod289(float4 x) { return x - floor(x / 289.0) * 289.0; }
float4 WaterPermute(float4 x) { return WaterMod289(((x * 34.0) + 1.0) * x); }
float4 WaterInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float WaterNoise(float3 v)
{
    const float2 C = float2(1.0/6.0, 1.0/3.0);
    const float4 D = float4(0.0,0.5,1.0,2.0);
    float3 i = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);
    float3 g = step(x0.yzx, x0.xyz);
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + C.xxx;
    float3 x2 = x0 - i2 + C.yyy;
    float3 x3 = x0 - D.yyy;
    i = WaterMod289(i);
    float4 p = WaterPermute(WaterPermute(WaterPermute(
        i.z + float4(0.0,i1.z,i2.z,1.0))
        + i.y + float4(0.0,i1.y,i2.y,1.0))
        + i.x + float4(0.0,i1.x,i2.x,1.0));
    float n = 0.142857142857;
    float3 ns = n * D.wyz - D.xzx;
    float4 j = p - 49.0 * floor(p * ns.z * ns.z);
    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = x_ * ns.x + ns.yyyy;
    float4 y = y_ * ns.x + ns.yyyy;
    float4 h = 1.0 - abs(x) - abs(y);
    float4 b0 = float4(x.xy,y.xy);
    float4 b1 = float4(x.zw,y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, float4(0,0,0,0));
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    float3 p0 = float3(a0.xy,h.x);
    float3 p1 = float3(a0.zw,h.y);
    float3 p2 = float3(a1.xy,h.z);
    float3 p3 = float3(a1.zw,h.w);
    float4 norm = WaterInvSqrt(float4(dot(p0,p0),dot(p1,p1),dot(p2,p2),dot(p3,p3)));
    p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
    float4 m = max(0.6 - float4(dot(x0,x0),dot(x1,x1),dot(x2,x2),dot(x3,x3)),0.0);
    m *= m;
    return 42.0 * dot(m*m, float4(dot(p0,x0),dot(p1,x1),dot(p2,x2),dot(p3,x3)));
}

float3 WaterRotate(float3 p, float angle)
{
    float s = sin(angle), c = cos(angle);
    return float3(c*p.x + s*p.y, -s*p.x + c*p.y, p.z);
}

float WaterFNoise(float3 p, float time)
{
    float3 r = WaterRotate(p, 0.5 * time);
    float add = WaterNoise(r);
    float msc = saturate(add + 0.7);
    float sum = 0.6 * add;
    p *= 2.0;
    add = WaterNoise(WaterRotate(p, 0.5 * time)) * msc;
    sum += 0.5 * add;
    msc = saturate(msc * (add + 0.7));
    p.xy *= 2.0;
    p = WaterRotate(p, 0.3 * time);
    add = WaterNoise(p) * msc;
    sum += 0.25 * abs(add);
    msc = saturate(msc * (add + 0.7));
    p *= 2.0;
    add = WaterNoise(p) * msc;
    sum += 0.125 * abs(add);
    msc = saturate(msc * (add + 0.2));
    p *= 2.0;
    add = WaterNoise(p) * msc;
    sum += 0.0625 * abs(add);
    return sum * 0.516129;
}

float WaterHeight(float3 p, float time)
{
    return 0.3 - 0.5 * WaterFNoise(float3(0.5*p.x,0.5*p.z,0.4*time), time);
}
#endif
