// Rain streaks for RainSystem. Every drop is a quad whose vertices carry the drop's random seed; this shader places
// it in a box that follows the camera (world-anchored, so drops don't swim when you turn or walk), moves it with the
// fall + wind velocity, wraps it around the box, hides it under shelter (roofs, trees: _RainShelter holds the
// highest surface under each spot) and stretches it along its velocity, facing the camera.
Shader "ProjectZed/Rain"
{
    Properties
    {
        _Color ("Color", Color) = (0.75, 0.8, 0.85, 0.16)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Rain"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "RainCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float4 seed : TEXCOORD0; float2 corner : TEXCOORD1; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float alpha : TEXCOORD1; float3 light : TEXCOORD2; };

            Varyings vert(Attributes i)
            {
                Varyings o = (Varyings)0;
                float3 box = _RainBoxSize.xyz;
                float t = _RainCenter.w;
                float3 vel = _RainVelocity.xyz * lerp(0.85, 1.15, i.seed.w);
                float3 origin = _RainCenter.xyz - box * 0.5;
                float3 p = i.seed.xyz * box + vel * t;
                p = origin + frac((p - origin) / box) * box;

                float visible = step(frac(i.seed.w * 13.7 + i.seed.x * 3.1), _RainBoxSize.w);
                visible *= step(RainShelterHeight(p.xz), p.y);

                float3 dir = normalize(vel);
                float3 toCam = normalize(_WorldSpaceCameraPos - p);
                float3 side = normalize(cross(dir, toCam)) * _RainParams.x;
                float dcam = distance(p, _WorldSpaceCameraPos);
                // Wider far away so distant rain still reads, thinner up close
                side *= lerp(0.6, 1.8, saturate(dcam / 15.0));
                float3 wp = p + side * i.corner.x - dir * _RainVelocity.w * i.corner.y;

                float fade = saturate((dcam - 0.4) / 1.5) * saturate((box.x * 0.5 - dcam) / 5.0);
                o.positionCS = visible > 0.5 ? TransformWorldToHClip(wp) : float4(0, 0, -1, 1);
                o.uv = i.corner;
                o.alpha = fade;
                Light ml = GetMainLight();
                o.light = ml.color * 0.25 + SampleSH(float3(0, 1, 0)) * 0.9;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float across = 1.0 - abs(i.uv.x);
                float along = smoothstep(0.0, 0.25, i.uv.y) * smoothstep(1.0, 0.6, i.uv.y);
                float a = _Color.a * i.alpha * across * along;
                return half4(_Color.rgb * i.light, a);
            }
            ENDHLSL
        }
    }
}
