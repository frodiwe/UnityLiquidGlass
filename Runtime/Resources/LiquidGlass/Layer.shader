// LiquidGlassPass's layer: the uGUI glass shapes (glass and content) are drawn into a layer at the end
// of the frame, and the layer goes onto the screen only where the shapes' markers survived (alpha 0
// in the frame copy): a popup drawn over a button hides its glass AND its icon and text.
Shader "Hidden/LiquidGlass/Layer"
{
    Properties
    {
        _MainTex ("Content", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend One OneMinusSrcAlpha
        CGINCLUDE
        #include "UnityCG.cginc"
        struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float2 px : TEXCOORD1; };
        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv; o.color = v.color;
            o.px = v.vertex.xy;   // GL.LoadPixelMatrix: vertices are screen pixels
            return o;
        }
        ENDCG

        // 0: content (a uGUI mesh, a UI Toolkit copy) onto the target, premultiplied.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _MainTex;
            float _Premultiplied;   // the texture holds premultiplied colour
            float _ToLinear;        // vertex colours are gamma (uGUI's) in a linear project
            float _Fade;            // the alpha the content inherits (CanvasGroup, a fading button)
            half4 frag(v2f i) : SV_Target
            {
                half4 c = tex2D(_MainTex, i.uv);
                if (_Premultiplied < .5) c.rgb *= c.a;
                half4 tint = i.color;
                if (_ToLinear > .5) tint.rgb = GammaToLinearSpace(tint.rgb);
                return c * tint.a * _Fade * half4(tint.rgb, 1);
            }
            ENDCG
        }

        // 1: the layer onto the screen, wherever the frame copy kept the markers' alpha 0.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _Layer, _Frame;
            float4 _LayerScreen;   // width, height (px), flip the frame copy's y
            half4 frag(v2f i) : SV_Target
            {
                float2 uv = i.px / _LayerScreen.xy;
                float2 fuv = uv; if (_LayerScreen.z > .5) fuv.y = 1 - fuv.y;
                return tex2D(_Layer, uv) * (1 - tex2D(_Frame, fuv).a);
            }
            ENDCG
        }
    }
}
