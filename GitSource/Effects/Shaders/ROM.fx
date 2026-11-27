texture Frame : COLOR;
sampler Image { Texture = Frame; AddressU = CLAMP; AddressV = CLAMP; };
uniform float TimeMs < source = "timer"; >;
uniform uint FrameCount < source = "framecount"; >;
static const float2 Texel = float2(BUFFER_RCP_WIDTH, BUFFER_RCP_HEIGHT);
static const float3 Luma = float3(0.2126, 0.7152, 0.0722);

float3 Read(float2 uv) { return tex2D(Image, uv).rgb; }
float3 At(float2 uv, float2 off) { return Read(uv + off * Texel); }
float Y(float3 c) { return dot(c, Luma); }
float CellSize() { return clamp(floor(min(BUFFER_WIDTH / 320.0, BUFFER_HEIGHT / 180.0)), 1.0, 9.0); }

float Noise(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

float3 Cross(float2 uv, float r)
{
    return (At(uv, float2(r, 0)) + At(uv, float2(-r, 0)) +
        At(uv, float2(0, r)) + At(uv, float2(0, -r))) * 0.25;
}

float3 Ring(float2 uv, float r)
{
    float k = r * 0.707107;
    float3 diagonal = At(uv, float2(k, k)) + At(uv, float2(-k, k)) +
        At(uv, float2(k, -k)) + At(uv, float2(-k, -k));
    return (Cross(uv, r) + diagonal * 0.25) * 0.5;
}

float2 Slope(float2 uv, float r)
{
    return float2(Y(At(uv, float2(r, 0))) - Y(At(uv, float2(-r, 0))),
        Y(At(uv, float2(0, r))) - Y(At(uv, float2(0, -r))));
}

float3 Bright(float3 c, float cut) { return c * smoothstep(cut, 1.0, max(c.r, max(c.g, c.b))); }

float3 LightRing(float2 uv, float r, float cut)
{
    float3 sum = 0;
    for (int i = 0; i < 8; ++i)
    {
        float angle = i * 0.785398;
        sum += Bright(At(uv, float2(cos(angle), sin(angle)) * r), cut);
    }
    return sum * 0.125;
}

float3 PixelEdge(float2 uv, float3 c)
{
    float r = max(1.0, CellSize() * 0.67);
    float2 gradient = Slope(uv, r);
    float2 slope = abs(gradient);
    float corner = min(slope.x, slope.y);
    float edge = max(slope.x, slope.y);
    float2 n = gradient / max(length(gradient), 0.001);
    float3 across = (At(uv, n * r * 0.65) + At(uv, -n * r * 0.65)) * 0.5;
    return lerp(c, across, smoothstep(0.025, 0.22, edge) * lerp(0.38, 0.9, saturate(corner * 4.0)));
}

float3 EdgeSoften(float2 uv, float3 c)
{
    float r = max(1.0, CellSize() * 0.6);
    float3 sum = c;
    float total = 1.0;
    for (int i = 0; i < 8; ++i)
    {
        float angle = i * 0.785398;
        float3 n = At(uv, float2(cos(angle), sin(angle)) * r);
        float w = exp(-10.0 * length(n - c));
        sum += n * w;
        total += w;
    }
    return sum / total;
}

float3 Denoise(float2 uv, float3 c)
{
    float3 a = At(uv, float2(-2, -1)), b = At(uv, float2(2, 1));
    float3 d = At(uv, float2(1, -2)), e = At(uv, float2(-1, 2));
    return lerp(clamp(c, min(a, b), max(a, b)), clamp(c, min(d, e), max(d, e)), 0.5);
}

float3 Deband(float2 uv, float3 c)
{
    float angle = Noise(floor(uv / Texel)) * 6.283185;
    float2 dir = float2(cos(angle), sin(angle)) * 7.0;
    float3 blur = (At(uv, dir) + At(uv, -dir) + At(uv, dir.yx) + At(uv, -dir.yx)) * 0.25;
    return lerp(c, blur, 1.0 - smoothstep(0.014, 0.065, length(c - blur))) +
        (Noise(floor(uv / Texel) + 5.0) - 0.5) / 255.0;
}

float3 AdaptiveSharp(float2 uv, float3 c)
{
    float3 blur = Cross(uv, 1.0);
    float contrast = length(c - blur);
    return c + (c - blur) * (1.0 - smoothstep(0.04, 0.35, contrast)) *
        smoothstep(0.025, 0.14, Y(c)) * 1.35;
}

float3 Unsharp(float2 uv, float3 c) { return c + (c - Ring(uv, 2.0)) * 0.8; }
float3 HighPass(float2 uv, float3 c) { return c + clamp(Y(c) - Y(Ring(uv, 4.0)), -0.12, 0.12) * 0.9; }

float3 LocalContrast(float2 uv, float3 c)
{
    float lum = Y(c);
    float local = Y(Ring(uv, 18.0));
    return c * (1.0 + clamp(lum - local, -0.32, 0.32) * 0.75);
}

float3 HaloReduce(float2 uv, float3 c)
{
    float3 nearBlur = Cross(uv, 1.0);
    float3 farBlur = Cross(uv, 4.0);
    float overshoot = max(abs(Y(c) - Y(nearBlur)) - 1.7 * abs(Y(nearBlur) - Y(farBlur)), 0.0);
    return lerp(c, nearBlur, saturate(overshoot * 4.5));
}

float3 ShadowDetail(float2 uv, float3 c)
{
    float lum = Y(c);
    float micro = Y(c - Cross(uv, 3.0));
    return c + (1.0 - smoothstep(0.04, 0.48, lum)) * (lum * 0.33 + micro * 0.8);
}

float3 SoftBloom(float2 uv, float3 c)
{
    return c + LightRing(uv, 4.0, 0.58) * 0.20 + LightRing(uv, 12.0, 0.66) * 0.18;
}

float3 NeonGlow(float2 uv, float3 c)
{
    float3 glow = LightRing(uv, 7.0, 0.46) * 0.55 + LightRing(uv, 20.0, 0.56) * 0.45;
    float3 chroma = max(glow - Y(glow) * 0.60, 0.0);
    return c + chroma * float3(1.24, 0.9, 1.4) * 0.75;
}

float3 Halation(float2 uv, float3 c)
{
    float bloom = Y(LightRing(uv, 5.5, 0.55));
    return c + float3(0.74, 0.11, 0.045) * bloom * (1.0 - Y(c));
}

float3 Streak(float2 uv, float3 c)
{
    float3 light = 0;
    for (int i = 1; i <= 5; ++i)
        light += (Bright(At(uv, float2(i * 5.0, 0)), 0.72) +
            Bright(At(uv, float2(-i * 5.0, 0)), 0.72)) / (i + 1.0);
    return c + light * float3(0.23, 0.36, 0.55);
}

float3 Starburst(float2 uv, float3 c)
{
    float3 light = 0;
    for (int i = 0; i < 8; ++i)
    {
        float angle = i * 0.785398;
        float2 dir = float2(cos(angle), sin(angle));
        light += Bright(At(uv, dir * 8.0), 0.78) + Bright(At(uv, dir * 24.0), 0.83) * 0.55;
    }
    return c + light * 0.09;
}

float3 LensDirt(float2 uv, float3 c)
{
    float2 grid = uv * float2(27.0, 16.0);
    float2 cell = floor(grid);
    float size = lerp(0.12, 0.37, Noise(cell));
    float spots = 1.0 - smoothstep(size * 0.6, size, length(frac(grid) - 0.5));
    float smudge = (sin(uv.x * 32.0 + uv.y * 17.0) * sin(uv.y * 41.0 + uv.x * 18.0) + 1.0) * 0.28;
    return c + LightRing(uv, 21.0, 0.60) * (spots + smudge) * float3(0.49, 0.36, 0.22);
}

float3 Exposure(float2 uv, float3 c) { return c * 1.7; }
float3 Gamma(float2 uv, float3 c) { return pow(saturate(c), 0.71); }
float3 Contrast(float2 uv, float3 c) { return c * c * (3.0 - 2.0 * c); }

float3 Filmic(float2 uv, float3 c)
{
    float3 x = c * 1.22;
    return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
}

float3 BlackLift(float2 uv, float3 c) { return c + 0.095 * (1.0 - c) * (1.0 - c); }
float3 HighlightRoll(float2 uv, float3 c) { return c - 0.24 * pow(max(c - 0.36, 0.0) / 0.64, 2.0); }
float3 Saturation(float2 uv, float3 c) { return lerp(Y(c), c, 1.70); }

float3 Vibrance(float2 uv, float3 c)
{
    float chroma = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
    return lerp(Y(c), c, 1.0 + 0.85 * (1.0 - chroma));
}

float3 Temperature(float2 uv, float3 c) { return c * float3(1.15, 1.025, 0.80); }
float3 Tint(float2 uv, float3 c) { return c * float3(1.09, 0.85, 1.12); }

float3 SplitTone(float2 uv, float3 c)
{
    float lum = Y(c);
    float3 shadows = float3(-0.024, 0.034, 0.112) * (1.0 - smoothstep(0.08, 0.57, lum));
    float3 lights = float3(0.11, 0.043, -0.028) * smoothstep(0.43, 0.91, lum);
    return c + shadows * (0.30 + lum) + lights * lum;
}

float3 HueShift(float2 uv, float3 c)
{
    float angle = 0.55 * smoothstep(0.0, 0.30, c.b - c.r);
    float3 axis = float3(0.57735, 0.57735, 0.57735);
    return c * cos(angle) + cross(axis, c) * sin(angle) + axis * dot(axis, c) * (1 - cos(angle));
}

float3 Grain(float2 uv, float3 c)
{
    float grain = Noise(floor(uv / Texel) + floor(TimeMs * 0.025)) - 0.5;
    return c + grain * 0.18 * saturate(1.0 - abs(Y(c) - 0.4) * 1.25);
}

float3 Vignette(float2 uv, float3 c)
{
    return c * (1.0 - 0.72 * smoothstep(0.25, 0.75, length((uv - 0.5) * float2(1.1, 0.95))));
}

float3 Monochrome(float2 uv, float3 c) { return dot(c, float3(0.29, 0.60, 0.11)); }

float3 Sepia(float2 uv, float3 c)
{
    return float3(dot(c, float3(0.393, 0.769, 0.189)), dot(c, float3(0.349, 0.686, 0.168)),
        dot(c, float3(0.272, 0.534, 0.131)));
}

float3 Bleach(float2 uv, float3 c)
{
    float lum = Y(c);
    float3 overlay = lum < 0.5 ? 2.0 * lum * c : 1.0 - 2.0 * (1.0 - lum) * (1.0 - c);
    return lerp(overlay, dot(overlay, Luma), 0.52);
}

float3 Fade(float2 uv, float3 c)
{
    return lerp(c, Y(c), 0.25) * float3(0.80, 0.81, 0.76) + float3(0.115, 0.10, 0.085);
}

float3 Chromatic(float2 uv, float3 c)
{
    float2 d = (uv - 0.5) * Texel * 8.0;
    return float3(Read(uv + d).r, c.g, Read(uv - d).b);
}

float3 EdgeSoftness(float2 uv, float3 c)
{
    return lerp(c, Ring(uv, 4.5), smoothstep(0.28, 0.63, length(uv - 0.5)));
}

float3 RadialBlur(float2 uv, float3 c)
{
    float2 d = (uv - 0.5) * 0.024;
    return (c * 2.0 + Read(uv - d) + Read(uv - d * 2.0) + Read(uv + d)) * 0.2;
}

float3 TiltShift(float2 uv, float3 c)
{
    float defocus = smoothstep(0.14, 0.47, abs(uv.y - 0.48));
    return lerp(c, Ring(uv, 3.0 + defocus * 9.0), defocus);
}

float3 Diffusion(float2 uv, float3 c)
{
    float3 spread = Ring(uv, 3.6);
    return lerp(c, 1.0 - (1.0 - c) * (1.0 - spread * 0.35), 0.60) + (spread - c) * 0.35;
}

float3 Bokeh(float2 uv, float3 c)
{
    float3 energy = c * c;
    for (int i = 0; i < 6; ++i)
    {
        float a = i * 1.047198;
        float3 n = At(uv, float2(cos(a), sin(a)) * 7.0);
        energy += n * n;
    }
    return sqrt(energy / 7.0);
}

float3 Scanlines(float2 uv, float3 c)
{
    float line = cos(uv.y / Texel.y * 6.283185 / max(3.0, CellSize()));
    return c * (0.77 + 0.23 * line);
}

float3 Phosphor(float2 uv, float3 c)
{
    uint index = uint(uv.x / Texel.x) % 3;
    float3 mask = float3(index == 0, index == 1, index == 2);
    return c * (0.52 + 0.62 * mask);
}

float3 Dither(float2 uv, float3 c)
{
    int2 p = int2(uv / Texel) & 3;
    int lo = ((p.x ^ p.y) & 1) * 2 + (p.y & 1);
    int hi = (((p.x >> 1) ^ (p.y >> 1)) & 1) * 2 + ((p.y >> 1) & 1);
    float threshold = (lo * 4.0 + hi + 0.5) / 16.0;
    return floor(c * 15.0 + threshold) / 15.0;
}

float3 Posterize(float2 uv, float3 c) { return round(c * 7.0) / 7.0; }

float3 PixelGrid(float2 uv, float3 c)
{
    float2 p = frac(uv / Texel / max(3.0, CellSize()));
    float grid = max(1.0 - smoothstep(0.0, 0.24, p.x), 1.0 - smoothstep(0.0, 0.24, p.y));
    return c * (1.0 - grid * 0.43);
}

float3 Interlace(float2 uv, float3 c)
{
    float row = float((uint(uv.y / Texel.y) + FrameCount) & 1);
    return lerp(c, At(uv, float2(0.7, 1.0)) * 0.79, row);
}

float3 CrossProcess(float2 uv, float3 c)
{
    return float3(pow(abs(c.r), 0.8) * 1.05, c.g * 0.91 + 0.05, pow(abs(c.b), 1.38) + 0.09);
}

float3 Gradient(float2 uv, float3 c)
{
    float t = saturate(0.14 + 0.58 * uv.y + 0.28 * uv.x);
    return c * lerp(float3(0.71, 0.95, 1.26), float3(1.24, 0.88, 0.67), t);
}

float3 Paper(float2 uv, float3 c)
{
    float2 p = uv / Texel;
    float fibers = sin(p.x * 0.89 + sin(p.y * 0.12) * 0.26) * sin(p.y * 0.57);
    float speckle = Noise(floor(p)) - 0.5;
    return c * float3(0.97, 0.91, 0.79) + 0.06 + (fibers * 0.035 + speckle * 0.075) * (1.0 - Y(c) * 0.65);
}

float3 Emboss(float2 uv, float3 c)
{
    float relief = Y(At(uv, float2(1.7, 1.7)) - At(uv, float2(-1.7, -1.7)));
    return lerp(c, Y(c), 0.65) * 0.55 + 0.23 + relief * 0.95;
}

float3 Outline(float2 uv, float3 c)
{
    float strength = smoothstep(0.04, 0.33, length(Slope(uv, max(1.0, CellSize() * 0.65))));
    return lerp(c, float3(0.025, 0.034, 0.066), strength * 0.86);
}

float3 Watercolor(float2 uv, float3 c)
{
    float r = max(2.0, CellSize());
    float3 a = (c + At(uv, float2(r, r)) + At(uv, float2(r, 0)) + At(uv, float2(0, r))) * 0.25;
    float3 b = (c + At(uv, float2(-r, r)) + At(uv, float2(-r, 0)) + At(uv, float2(0, r))) * 0.25;
    float3 d = (c + At(uv, float2(r, -r)) + At(uv, float2(r, 0)) + At(uv, float2(0, -r))) * 0.25;
    float3 e = (c + At(uv, float2(-r, -r)) + At(uv, float2(-r, 0)) + At(uv, float2(0, -r))) * 0.25;
    float3 top = length(a - c) < length(b - c) ? a : b;
    float3 bottom = length(d - c) < length(e - c) ? d : e;
    float3 pigment = length(top - c) < length(bottom - c) ? top : bottom;
    return round(pigment * 19.0) / 19.0;
}

void Fullscreen(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD)
{
    uv = float2((id << 1) & 2, id & 2);
    pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

#define ROM_EFFECT(id, label, category, amount) \
    uniform float ROMStrength_ ## id = amount; \
    float4 PS_ ## id(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target { \
        float4 original = tex2D(Image, uv); \
        return float4(saturate(lerp(original.rgb, id(uv, original.rgb), ROMStrength_ ## id)), original.a); \
    } \
    technique ROM_ ## id { pass { VertexShader = Fullscreen; PixelShader = PS_ ## id; } }

#define ROM_UPSTREAM(id, label, category, amount)
#include "Catalog.inc"
