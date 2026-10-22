// Splash rings for RainSystem: each slot picks a new random spot (fixed in the world) every cycle, sits on the highest
// surface there (ground, roof, porch) and draws an expanding, fading ring.
Shader "ProjectZed/RainSplash"
{
    Properties
    {
        _Color ("Color", Color) = (0.8, 0.85, 0.9, 0.45)
        _Size ("Ring size (m)", Float) = 0.22
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "RainSplash"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "RainCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Size;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float4 seed : TEXCOORD0; float2 corner : TEXCOORD1; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float phase : TEXCOORD1; float3 light : TEXCOORD2; float fade : TEXCOORD3; };

            Varyings vert(Attributes i)
            {
                Varyings o = (Varyings)0;
                float cycle = _RainCenter.w * _RainParams.y + i.seed.w;
                float n = floor(cycle);
                o.phase = frac(cycle);
                float2 area = _RainBoxSize.xz * 0.6;
                // A spot fixed in the world (tiled every `area` metres), wrapped into the window around the camera,
                // so splashes stay where they land while you walk past.
                float2 world = frac(i.seed.xy + n * float2(0.6180339, 0.4142135)) * area;
                float2 origin = _RainCenter.xz - area * 0.5;
                float2 xz = origin + frac((world - origin) / area) * area;
                float h = RainShelterHeight(xz);
                float visible = step(frac(i.seed.z * 17.3), _RainBoxSize.w) * step(-1e4, h);
                float2 c = float2(i.corner.x, i.corner.y * 2.0 - 1.0);
                float size = _Size * lerp(0.6, 1.2, frac(i.seed.z * 7.1));
                float3 wp = float3(xz.x + c.x * size, h + 0.015, xz.y + c.y * size);
                o.positionCS = visible > 0.5 ? TransformWorldToHClip(wp) : float4(0, 0, -1, 1);
                o.uv = c;
                o.fade = saturate((_RainBoxSize.x * 0.3 - distance(wp, _WorldSpaceCameraPos)) / 4.0);
                Light ml = GetMainLight();
                o.light = ml.color * 0.3 + SampleSH(float3(0, 1, 0));
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float r = length(i.uv);
                float ring = 1.0 - saturate(abs(r - i.phase) / 0.12);
                float a = _Color.a * ring * (1.0 - i.phase) * step(r, 1.0) * i.fade;
                return half4(_Color.rgb * i.light, a);
            }
            ENDHLSL
        }
    }
}
