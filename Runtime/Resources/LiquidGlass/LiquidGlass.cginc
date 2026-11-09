#ifndef LIQUID_GLASS_INCLUDED
#define LIQUID_GLASS_INCLUDED

// Liquid glass built the way Apple's is (iOS 26 .glassEffect: Core Animation's glassBackground filter and
// an SDF key/fill highlight), with the numbers read from it and checked on probes in the iOS simulator.
// The layers, in Apple's order:
//   1. The backdrop is blurred (Gaussian, _FrostRadius points), lighter towards the outline.
//   2. The blurred backdrop is refracted by a bezel of fixed width along the outline. The glass is a flat
//      slab whose edge is a quarter circle: at depth e from the outline it shows the backdrop
//        A * (1 - sqrt(1 - (1 - e/H)^2))
//      points further in, with A = min(S, _BezelDepth) and H = min(_BezelWidth, _BezelFraction * S),
//      S the shape's shorter side. The middle is not magnified; a small button gets a narrower bezel.
//   3. One colour transform over what the glass shows: lift + gain * c (Apple's face colour matrix).
//   4. A hairline highlight lit by two opposite lights (key and fill), in a colour derived from what is
//      under it (Apple's vibrant colour matrix), so it vanishes over bright greens and glows over darks.
// Apple does all of this on sRGB-encoded values. A shader that reads a frame copy encoded that way in a
// Linear project (LiquidGlassPass encodes it before its mips are built) defines LIQUID_GLASS_SRGB_FRAME,
// and LiquidFinish decodes its result.
// Lengths are the UI's units (canvas or panel units, like iOS points); shapes are rounded boxes, radius 0
// meaning the shorter half side (a pill or a circle), or any outline from a sprite (radius < 0).

sampler2D _SharpTex;
float4 _SharpTex_TexelSize;
float  _Liquid, _LensStrength, _BezelDepth, _BezelWidth, _BezelFraction, _Dispersion;
float  _FrostRadius, _FrostEdge, _Lift, _Gain, _Saturation, _Tint, _Specular, _Highlight;
float4 _LightDir;

// A custom shape (radius < 0): its outline as a signed distance field (ShapeField), sampled over the
// shape's rect. r: distance in texels (negative inside), gb: the outward normal.
sampler2D _ShapeTex;
float4 _ShapeUV;       // shape uv → texture uv: scale xy, offset zw
float4 _ShapeTexels;   // the rect in texels (w, h), the deepest inside distance in texels

float LiquidDistance(float2 p, float2 halfSize, float radius, out float2 n)
{
    if (radius < 0)
    {
        float4 f = tex2Dlod(_ShapeTex, float4((p / (2 * halfSize) + .5) * _ShapeUV.xy + _ShapeUV.zw, 0, 0));
        n = dot(f.gb, f.gb) > 1e-6 ? normalize(f.gb) : float2(0, 1);
        return f.r * (halfSize.x / _ShapeTexels.x + halfSize.y / _ShapeTexels.y);   // texels → points
    }
    float r = radius > 0 ? min(radius, min(halfSize.x, halfSize.y)) : min(halfSize.x, halfSize.y);
    float2 q = abs(p) - halfSize + r;
    float2 g = q.x > 0 && q.y > 0 ? normalize(q) : (q.x > q.y ? float2(1, 0) : float2(0, 1));
    n = g * ((p >= 0) * 2 - 1);
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}

// The shape's inscribed radius: how far its middle is from the outline.
float LiquidInscribed(float2 halfSize, float radius)
{
    return radius < 0 ? _ShapeTexels.z * (halfSize.x / _ShapeTexels.x + halfSize.y / _ShapeTexels.y)
                      : min(halfSize.x, halfSize.y);
}

// Where the glass at p (points from the shape's centre) takes the backdrop from, as an offset in
// points. e: depth inside the outline (points), n: the outward normal.
float2 LiquidShift(float2 p, float2 halfSize, float radius, out float e, out float2 n)
{
    float d = LiquidDistance(p, halfSize, radius, n);
    e = max(-d, 0);
    float S = 2 * LiquidInscribed(halfSize, radius);
    float A = min(S, _BezelDepth);
    float H = max(min(_BezelWidth, _BezelFraction * S), 1e-3);
    float t = saturate(e / H);
    return -n * A * (1 - sqrt(t * (2 - t))) * _LensStrength;
}

// `flat` is what the glass shows with the lens off (_Liquid 0). `toUv` turns an offset in points into a
// backdrop-UV offset. rimLight.x gets the hairline's strength, rimLight.y the frosted capsule highlight's.
half3 LiquidBackdrop(half3 flat, float2 bguv, float2 p, float2 halfSize, float radius, float2 toUv, out half3 rimLight)
{
    rimLight = 0;
    if (_Liquid <= 0 || halfSize.x <= 0 || halfSize.y <= 0) return flat;
    float e; float2 n;
    float2 shift = LiquidShift(p, halfSize, radius, e, n);
    float R = LiquidInscribed(halfSize, radius);
    // The blur is lighter towards the outline: full in the middle, (1 - _FrostEdge) at the edge (Apple's
    // blur opacities: 1 in the middle, 0.5 a point inside the outline).
    float sigma = _FrostRadius * lerp(1 - _FrostEdge, 1, saturate((e - 1) / max(R - 1, 1e-3)));
    // Gaussian blur of the backdrop around the refracted point: 32 taps on a golden-angle spiral out
    // to 2.5 sigma, each from a mip level about as wide as the gap between taps.
    // Where the bezel squeezes the backdrop, one screen pixel covers many backdrop texels: read a
    // coarser mip there too, or the squeezed rim breaks into thin rings.
    float texelsPerPt = max(abs(toUv.x) * _SharpTex_TexelSize.z, 1e-3);
    float2 centre = (bguv + shift * toUv) * _SharpTex_TexelSize.zw;
    float squeeze = max(length(ddx(centre)), length(ddy(centre)));
    float lod = log2(max(1, max(sigma * .8 * texelsPerPt, squeeze)));
    float split = 1 + _Dispersion * .25;
    half3 c = 0; float wsum = 0;
    [unroll] for (int i = 0; i < 32; i++)
    {
        float an = i * 2.39996;
        float rr = sqrt((i + .5) / 32) * 2.5;
        float2 o = float2(cos(an), sin(an)) * rr * sigma;
        float w = exp(-.5 * rr * rr);
        float2 at = bguv + (shift + o) * toUv;
        half3 s = _Dispersion > 0
            ? half3(tex2Dlod(_SharpTex, float4(bguv + (shift * split + o) * toUv, 0, lod)).r,
                    tex2Dlod(_SharpTex, float4(at, 0, lod)).g,
                    tex2Dlod(_SharpTex, float4(bguv + (shift / split + o) * toUv, 0, lod)).b)
            : tex2Dlod(_SharpTex, float4(at, 0, lod)).rgb;
        c += w * s;
        wsum += w;
    }
    c /= wsum;
    // The hairline: two opposite lights (key and fill, from _LightDir and the far side), brightest
    // on the outline's side facing either, gone across from them; half a point inside the outline.
    float facing = abs(dot(n, normalize(_LightDir.xy)));
    rimLight.x = _Liquid * _Specular * facing * exp(-.5 * pow((e - .5) / .35, 2));
    // A frosted capsule catches a soft highlight: an oval near its top-left, 1.7 R in from the left end
    // (Apple's .regular capsule: 30 x 11 pt on a 56 pt tall one, about 9% brighter). Not on circles.
    if (_Highlight > 0 && radius >= 0 && halfSize.x > halfSize.y * 1.5)
    {
        float2 hc = float2(-halfSize.x + 1.7 * R, halfSize.y - .375 * R);
        float he = length((p - hc) / float2(.54 * R, .2 * R));
        rimLight.y = _Liquid * _Highlight * smoothstep(1.3, .7, he);
    }
    return lerp(flat, c, _Liquid);
}

// The one look every glass shape ends with, on sRGB-encoded colour: Apple's face colour matrix (lift,
// gain, saturation), a white tint for the frosted variant, then the hairline in the vibrant colour of
// what it lies over.
half3 LiquidFinish(half3 c, half3 rimLight)
{
    half grey = dot(c, half3(.299, .587, .114));
    c = grey + (c - grey) * (_Saturation > 0 ? _Saturation : 1);
    c = saturate(_Lift + _Gain * c);
    c = lerp(c, 1, _Tint);
    // Apple's vibrant colour matrix for the highlight: near white over dark and neutral colours, the
    // colour itself over bright ones (over a bright green the line disappears).
    half3 v = saturate(half3(dot(c, half3(1.2024, -1.0014, -.1010)),
                             dot(c, half3(-.2976, .4987, -.1011)),
                             dot(c, half3(-.2977, -1.0012, 1.3989))) + .9);
    c = lerp(c, v, saturate(rimLight.x)) + rimLight.y;
    c = saturate(c);
#if defined(LIQUID_GLASS_SRGB_FRAME) && !defined(UNITY_COLORSPACE_GAMMA)
    c = GammaToLinearSpace(c);   // the frame's values are linear in a Linear project
#endif
    return c;
}

// The button's coverage from its rounded-box outline, antialiased: the same edge for every shape.
half LiquidCoverage(float2 p, float2 halfSize, float radius)
{
    float2 n;
    float d = LiquidDistance(p, halfSize, radius, n);
    float aa = max(fwidth(d), 1e-3);
    return 1 - smoothstep(-aa * .5, aa * .5, d);
}

#endif
