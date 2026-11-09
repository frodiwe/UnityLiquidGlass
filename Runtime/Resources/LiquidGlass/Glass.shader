// The glass itself, drawn by LiquidGlassPass over a copy of the finished frame (_SharpTex), one quad
// per shape. The lens is LiquidGlass.cginc.
Shader "Hidden/LiquidGlass/Glass"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        // Colour and alpha both composed correctly: the uGUI shapes go into a layer first (Layer.shader).
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #define LIQUID_GLASS_SRGB_FRAME   // LiquidGlassPass encodes the frame copy in Linear projects
            #include "LiquidGlass.cginc"

            float4 _GlassRect;     // centre (screen px, y up), half size (screen px)
            float4 _GlassShape;    // screen px per point, corner radius (points; 0 = pill), alpha, flip y
            float4 _GlassScreen;   // screen width, height (px)

            struct v2f { float4 pos : SV_POSITION; float2 px : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.px = v.vertex.xy;          // GL.LoadPixelMatrix: vertices are screen pixels
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float units = max(_GlassShape.x, 1e-3);
                float2 p = (i.px - _GlassRect.xy) / units;            // points from the centre, y up
                float2 halfSize = _GlassRect.zw / units;
                float2 uv = i.px / _GlassScreen.xy;
                if (_GlassShape.w > .5) uv.y = 1 - uv.y;
                float2 toUv = units / _GlassScreen.xy;
                if (_GlassShape.w > .5) toUv.y = -toUv.y;
                half3 bg = tex2D(_SharpTex, uv).rgb;
                half3 rimLight;
                bg = LiquidBackdrop(bg, uv, p, halfSize, _GlassShape.y, toUv, rimLight);
                half cover = LiquidCoverage(p, halfSize, _GlassShape.y);
                return half4(LiquidFinish(bg, rimLight), cover * _GlassShape.z);
            }
            ENDCG
        }

        // 1: .regular's shadow around the shape, onto the screen before any glass (LiquidShadow).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "LiquidGlass.cginc"

            float4 _GlassRect, _GlassShape, _GlassScreen;
            float4 _GlassShadow;   // x: show only where the shape's marker survived at its centre; y: Linear project

            struct v2f { float4 pos : SV_POSITION; float2 px : TEXCOORD0; };
            v2f vert(appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.px = v.vertex.xy; return o; }

            half4 frag(v2f i) : SV_Target
            {
                float units = max(_GlassShape.x, 1e-3);
                float2 p = (i.px - _GlassRect.xy) / units;
                float2 halfSize = _GlassRect.zw / units;
                float2 uv = i.px / _GlassScreen.xy;
                if (_GlassShape.w > .5) uv.y = 1 - uv.y;
                half luma = dot(tex2Dlod(_SharpTex, float4(uv, 0, 0)).rgb, half3(.2126, .7152, .0722));
                half a = LiquidShadow(p, halfSize, _GlassShape.y, luma) * (1 - LiquidCoverage(p, halfSize, _GlassShape.y)) * _GlassShape.z;
                if (_GlassShadow.x > .5)
                {
                    float2 c = _GlassRect.xy / _GlassScreen.xy;
                    if (_GlassShape.w > .5) c.y = 1 - c.y;
                    a *= 1 - tex2Dlod(_SharpTex, float4(c, 0, 0)).a;
                }
                // Apple multiplies sRGB values by 1 - a; in a Linear project the blend multiplies linear ones,
                // and 1 - linear(1 - a) darkens them by the same ratio.
                if (_GlassShadow.y > .5) a = 1 - GammaToLinearSpaceExact(1 - a);
                return half4(0, 0, 0, a);
            }
            ENDCG
        }
    }
}
