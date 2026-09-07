Shader "Hidden/RehabPhotoGame/FocusBlur"
{
    Properties
    {
        _MainTex ("Photo", 2D) = "white" {}
        _BlurPixels ("Blur Pixels", Float) = 12
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _BlurPixels;

            fixed4 frag(v2f_img input) : SV_Target
            {
                float2 offset = _MainTex_TexelSize.xy * _BlurPixels;
                fixed4 color = tex2D(_MainTex, input.uv) * 0.20;
                color += tex2D(_MainTex, input.uv + float2( offset.x, 0)) * 0.10;
                color += tex2D(_MainTex, input.uv + float2(-offset.x, 0)) * 0.10;
                color += tex2D(_MainTex, input.uv + float2(0,  offset.y)) * 0.10;
                color += tex2D(_MainTex, input.uv + float2(0, -offset.y)) * 0.10;
                color += tex2D(_MainTex, input.uv + float2( offset.x,  offset.y)) * 0.10;
                color += tex2D(_MainTex, input.uv + float2(-offset.x,  offset.y)) * 0.10;
                color += tex2D(_MainTex, input.uv + float2( offset.x, -offset.y)) * 0.10;
                color += tex2D(_MainTex, input.uv + float2(-offset.x, -offset.y)) * 0.10;
                return color;
            }
            ENDCG
        }
    }
}
