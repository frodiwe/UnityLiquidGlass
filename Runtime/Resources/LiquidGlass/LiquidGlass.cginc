#ifndef LIQUID_GLASS_INCLUDED
#define LIQUID_GLASS_INCLUDED

// Liquid glass, matched against Apple's own (iOS 26 SwiftUI .glassEffect, rendered in the iOS simulator
// over a test grid). Seen through the glass, the backdrop is remapped across the shape, from
// its medial axis (the middle line of a capsule, the centre of a circle) to its outline:
//   U = R * g(u / R),  g(x) = x / magnify + b * x^rimPower,  b = 1 + rimReach - 1 / magnify
// u is the distance from the axis, R the shape's inscribed radius. The middle is magnified
// (g' = 1 / magnify); towards the rim the image folds back, and at the outline the glass shows
// what lies on the far side of the middle (rimReach < 0), so straight lines bow into arcs. Measured
// on Apple's capsule: magnify 1.3, rimReach -1.72, rimPower 6.5. Apple's glass also brightens
// what it shows by about 9% (brightness), with no colour split. Light gathers where it is squeezed (a brighter
// band), a hairline catches it on the outline, and it all scatters a little (frost). Lengths are
// the UI's units (canvas or panel units, like iOS points); shapes are rounded boxes, radius 0 meaning the shorter half side
// (a pill or a circle).

sampler2D _SharpTex;
float4 _SharpTex_TexelSize;
float  _Highlight, _SourceBlur, _Saturation, _Contrast, _RimWhite, _EdgeReach, _EdgePower;
float  _Liquid, _Magnify, _RimReach, _RimPower, _LensStrength, _Dispersion, _Frost, _FrostRadius, _Specular, _Tint, _Brightness;
float4 _LightDir;

float LiquidDistance(float2 p, float2 halfSize, float radius, out float2 n)
{
    float r = radius > 0 ? min(radius, min(halfSize.x, halfSize.y)) : min(halfSize.x, halfSize.y);
    float2 q = abs(p) - halfSize + r;
    float2 g = q.x > 0 && q.y > 0 ? normalize(q) : (q.x > q.y ? float2(1, 0) : float2(0, 1));
    n = g * ((p >= 0) * 2 - 1);
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}

// Where the glass at p (points from the shape's centre) takes the backdrop from, as an offset
// in points; x is 0 on the medial axis and 1 on the outline.
float2 LiquidShift(float2 p, float2 halfSize, float radius, out float x, out float d, out float2 n)
{
    d = LiquidDistance(p, halfSize, radius, n);
    float R = min(halfSize.x, halfSize.y);
    x = saturate((R + d) / R);
    float a = 1 / (abs(_Magnify) < .01 ? .01 : _Magnify);   // negative: the middle shows the backdrop upside down
    float b = 1 + _RimReach - a;
    // Right at the outline the fold steepens again and reaches across to the far side of the
    // button, so the rim shows the colours opposite it, as Apple's does (a yellow card on the left
    // turns its right edge yellow): the rim is the refraction itself, not a line.
    float edge = _EdgeReach * pow(x, max(_EdgePower, 1));
    return n * (a * x + b * pow(x, _RimPower) + edge - x) * R * _LensStrength;
}

// `blurred` is what the glass shows with the lens off (_Liquid 0). `toUv` turns an offset in points
// into a backdrop-UV offset.
half3 LiquidBackdrop(half3 blurred, float2 bguv, float2 p, float2 halfSize, float radius, float2 toUv, out half3 rimLight)
{
    rimLight = 0;
    if (_Liquid <= 0 || halfSize.x <= 0 || halfSize.y <= 0) return blurred;
    float x, d; float2 n;
    float2 shift = LiquidShift(p, halfSize, radius, x, d, n);
    float split = 1 + _Dispersion * .25;
    half3 sharp = half3(tex2D(_SharpTex, bguv + shift * split * toUv).r,
                        tex2D(_SharpTex, bguv + shift * toUv).g,
                        tex2D(_SharpTex, bguv + shift / split * toUv).b);
    // Light scatters in the glass (frost): the refracted image itself blurred over the glass, as
    // Apple's is (a squeezed rim stays smooth instead of breaking into thin copies). 32 taps on a
    // golden-angle spiral out to _FrostRadius points, each refracted where it lands; all from the
    // same sharp sample, so it never lags the backdrop.
    half3 soft = 0; float wsum = 0;
    // Each tap reads a mip level about as wide as the gap between taps, so a thin line is smeared
    // into one soft band instead of showing up in a few taps as ghost copies.
    float texelsPerPx = max(abs(toUv.x) * _SharpTex_TexelSize.z, 1e-3);
    float lod = log2(max(1, _FrostRadius * (1 + _SourceBlur) * .45 * texelsPerPx));
    [unroll] for (int i = 0; i < 32; i++)
    {
        float an = i * 2.39996;
        float rr = sqrt((i + .5) / 32);
        float2 o = float2(cos(an), sin(an)) * rr * _FrostRadius;
        float tx, td; float2 tn;
        float2 ts = LiquidShift(p + o, halfSize, radius, tx, td, tn);
        // Gaussian weights (sigma a third of the reach) give one soft falloff, not a disc's rim.
        // The same tap also blurs the backdrop itself (sourceBlur), in the same direction, so
        // the two blurs add up instead of leaving a shifted ghost; a line that only grazes the
        // fold still shows there as a soft band, as Apple's does.
        float w = exp(-4.5 * rr * rr);
        soft += w * tex2Dlod(_SharpTex, float4(bguv + (o * (1 + _SourceBlur) + ts) * toUv, 0, lod)).rgb;
        wsum += w;
    }
    soft /= wsum;
    // Local contrast: the blurred lines stand out from the glass around them as much as Apple's do
    // (measured: theirs vary 21% more), without making the blur any sharper.
    half3 wide = tex2Dlod(_SharpTex, float4(bguv + shift * toUv, 0, lod + 2.5)).rgb;
    soft = max(wide + (soft - wide) * (_Contrast > 0 ? _Contrast : 1), 0);
    float R = min(halfSize.x, halfSize.y);
    float a = 1 / (abs(_Magnify) < .01 ? .01 : _Magnify);
    float b = 1 + _RimReach - a;
    // Where the rim squeezes the backdrop, its light gathers: a brighter band along the outline,
    // a little stronger on the side facing the light; a hairline catches it on the outline itself.
    
    float slope = abs(a + _RimPower * b * pow(x, _RimPower - 1));
    float squeeze = saturate((slope - 1) / max(abs(a + _RimPower * b) - 1, 1e-3));
    float facing = dot(n, normalize(_LightDir.xy));
    float hair = 1 - smoothstep(0, 1.2, -d);
    // The hairline is lit from one diagonal: brightest on the side facing the light and on the
    // opposite one, nearly gone across from them (measured on Apple's circle: top-left and
    // bottom-right bright, top-right and bottom-left as clear as the glass).
    // The light on the rim is the colour the glass bends into it, brightened, not a white line
    // drawn over the glass: Apple's edge goes yellow opposite a yellow card.
    half3 rimTint = lerp(lerp(sharp, soft, _Frost), half3(1, 1, 1), _RimWhite);
    rimLight = rimTint * (_Liquid * _Specular * (d < 0) * (.08 * squeeze * squeeze * (.75 + .25 * facing) + .4 * hair * facing * facing));
    // A frosted capsule catches a soft highlight: an oval near its top-left, 1.7 R in from the left end
    // (Apple's .regular capsule: 30 x 11 pt on a 56 pt tall one, about 9% brighter). Not on circles.
    if (halfSize.x > halfSize.y * 1.5)
    {
        float2 hc = float2(-halfSize.x + 1.7 * R, halfSize.y - .375 * R);
        float he = length((p - hc) / float2(.54 * R, .2 * R));
        rimLight += _Liquid * _Highlight * smoothstep(1.3, .7, he);   // a white highlight, as Apple's
    }
    return lerp(blurred, lerp(sharp, soft, _Frost), _Liquid);
}

// The one look every glass shape ends with: the refracted backdrop, vibrancy, one white tint,
// the rim light.
half3 LiquidFinish(half3 refracted, half3 rimLight)
{
    // Frosted glass keeps colour vivid under its blur (Apple's vibrancy): saturation above 1.
    half grey = dot(refracted, half3(.299, .587, .114));
    refracted = max(grey + (refracted - grey) * (_Saturation > 0 ? _Saturation : 1), 0);
    return lerp(refracted * (_Brightness > 0 ? _Brightness : 1), half3(1, 1, 1), _Tint) + rimLight;
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
