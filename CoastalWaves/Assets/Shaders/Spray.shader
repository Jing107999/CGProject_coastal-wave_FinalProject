// Spray.shader: draws spray particles (S4.5) as soft, round, see-through puffs.
//
// Each particle is a camera-facing quad from Unity's particle system. The round shape is computed
// from the quad's UV (distance from its centre), so no texture is needed. Colour and fade-out come
// from the particle's vertex colour (set by the particle system's colour-over-lifetime).
// Lit simply: sun + sky, so spray in the cliff's shadow is darker. Alpha-blended, no depth write.

Shader "Coastal/Spray"
{
    Properties
    {
        _Tint ("Spray colour", Color) = (0.92, 0.94, 0.96, 1)
        _Softness ("Edge softness", Range(0.5, 4)) = 1.6
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "Spray"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                float _Softness;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);   // particles are in world space
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.color = v.color;
                o.uv = v.uv;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Round puff: 1 in the middle, 0 at the quad's inscribed circle.
                float r = length(i.uv * 2.0 - 1.0);
                float shape = pow(saturate(1.0 - r), _Softness);

                // Lighting: sun (with the cliff's shadow) + sky. Spray scatters light in all
                // directions, so no N.L term.
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half3 light = sun.color * lerp(0.35, 1.0, sun.shadowAttenuation) + SampleSH(half3(0, 1, 0)) * 0.6;

                half3 color = _Tint.rgb * i.color.rgb * light;
                color = MixFog(color, i.fogFactor);
                return half4(color, shape * i.color.a * _Tint.a);
            }
            ENDHLSL
        }
    }
}
