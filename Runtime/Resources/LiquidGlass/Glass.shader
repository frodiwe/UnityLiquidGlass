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
    }
}
