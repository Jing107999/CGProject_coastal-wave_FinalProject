// Rock.shader: draws the cliff (and later rocks) in URP using per-vertex colours.
//
// URP's standard Lit shader ignores vertex colours, so this small shader does:
//   colour = vertex RGB x (sun light x shadow + sky light x AO), then fog,
// where AO (baked ambient occlusion) comes from the vertex colour's ALPHA channel.
// Debug views show the rock colour, sun light, sky light or AO on their own.
// Three passes:
//   1. "Rock"         the visible surface
//   2. "ShadowCaster" so the cliff casts shadows (onto the water and itself)
//   3. "DepthOnly"    so the depth texture contains the cliff (soft spray particles fade where they touch it)

Shader "Coastal/Rock"
{
    Properties
    {
        _Tint ("Tint (multiplies the vertex colour)", Color) = (1, 1, 1, 1)
        _AmbientAmount ("Sky light amount", Range(0, 2)) = 1
        _AODirect ("AO on sunlight too (deep caves the shadow map can't resolve)", Range(0, 1)) = 0.5

        [Header(Detail normal (micro ridges per pixel))]
        _DetailStrength ("Detail strength", Range(0, 2)) = 1
        _DetailScale ("Detail frequency (per metre)", Float) = 2.3
        _DetailHeight ("Detail bump height (metres)", Float) = 0.05
        _DetailFade ("Fade-out distance (metres)", Float) = 60

        [Header(Wet rock (S4.6 ported from Unity Shader Graph samples Wet and Rain_Drips))]
        _Permeability ("Permeability (how much the stone darkens when wet)", Range(0, 1)) = 0.6
        [NoScaleOffset] _DripTex ("Drips (B = channel mask, A = time offset)", 2D) = "black" {}
        [NoScaleOffset] _DripMaskTex ("Drip mask (scrolled down)", 2D) = "black" {}
        _DripTiling ("Drip tiling (repeats per metre)", Float) = 0.35
        _WetGloss ("Wet highlight strength", Range(0, 2)) = 0.6

        [Header(Debug)]
        [Enum(Shaded, 0, Albedo, 1, Sun only, 2, Sky only, 3, Ambient occlusion, 4, Wetness, 5)] _DebugView ("Debug view", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)   // SRP Batcher compatible
            half4 _Tint;
            float _AmbientAmount, _AODirect, _DebugView;
            float _DetailStrength, _DetailScale, _DetailHeight, _DetailFade;
            float _Permeability, _DripTiling, _WetGloss;
        CBUFFER_END

        // S4.4: white water on the face, published by RunupSystem (u = z, v = y). All 0 = none yet.
        // S4.6: R = water film (only shown in the debug view; the drips were removed), G = wetness (darkens the stone).
        TEXTURE2D(_RunupTex); SAMPLER(sampler_RunupTex);
        float4 _RunupRect;   // (zMin, yMin, 1 / z size, 1 / y size)
        TEXTURE2D(_DripTex); SAMPLER(sampler_DripTex);
        TEXTURE2D(_DripMaskTex); SAMPLER(sampler_DripMaskTex);
        float _SimTime;      // the simulation clock (drips pause with it)
        ENDHLSL

        // ---------------------------------------------------------------
        // Pass 1: the visible rock
        // ---------------------------------------------------------------
        Pass
        {
            Name "Rock"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;      // the vertex colour set by CliffGenerator
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                half4  color      : COLOR;
                float  fogFactor  : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                output.color      = input.color;
                output.fogFactor  = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // 3D value noise with its analytic gradient (Quilez, "Value noise derivatives";
            // our own implementation). Returns (value, d/dx, d/dy, d/dz).
            float Hash3(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float4 ValueNoiseD(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                float3 u = f * f * (3.0 - 2.0 * f);          // smooth blend weights
                float3 du = 6.0 * f * (1.0 - f);             // their derivatives
                float a = Hash3(i),               b = Hash3(i + float3(1, 0, 0));
                float c = Hash3(i + float3(0, 1, 0)), d = Hash3(i + float3(1, 1, 0));
                float e = Hash3(i + float3(0, 0, 1)), f1 = Hash3(i + float3(1, 0, 1));
                float g = Hash3(i + float3(0, 1, 1)), h = Hash3(i + float3(1, 1, 1));
                float k1 = b - a, k2 = c - a, k3 = e - a, k4 = a - b - c + d;
                float k5 = a - c - e + g, k6 = a - b - e + f1, k7 = -a + b + c - d + e - f1 - g + h;
                float value = a + k1 * u.x + k2 * u.y + k3 * u.z + k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x + k7 * u.x * u.y * u.z;
                float3 grad = du * float3(k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z,
                                          k2 + k5 * u.z + k4 * u.x + k7 * u.z * u.x,
                                          k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y);
                return float4(value, grad);
            }

            // DETAIL NORMAL: treat two octaves of noise as tiny bumps (a few cm) on the rock and
            // tilt the normal by their slope (the gradient's component along the surface). The
            // mesh stays the same; only the lighting sees the micro-ridges. Fades with distance.
            float3 DetailNormal(float3 N, float3 positionWS)
            {
                float dist = distance(positionWS, _WorldSpaceCameraPos);
                float fade = saturate(1.0 - dist / _DetailFade) * _DetailStrength;
                if (fade <= 0.0) return N;
                float3 p = positionWS * _DetailScale;
                float3 grad = ValueNoiseD(p).yzw * (_DetailHeight * _DetailScale)
                            + ValueNoiseD(p * 2.7 + 17.0).yzw * (0.4 * _DetailHeight * _DetailScale * 2.7);
                float3 slope = grad - dot(grad, N) * N;       // keep only the part along the surface
                return normalize(N - fade * slope);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 N = DetailNormal(normalize(input.normalWS), input.positionWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS));

                // Baked ambient occlusion from CliffGenerator, stored in the vertex colour's alpha:
                // 1 = open to the sky, small = deep in a crack, cleft or cave.
                half ao = input.color.a;

                // Diffuse (Lambert) light from the sun, darkened where something casts a shadow.
                // AO also dims it partly: the shadow map is too coarse to darken deep caves fully.
                half3 direct = sun.color * saturate(dot(N, sun.direction)) * sun.shadowAttenuation
                             * lerp(1.0, ao, _AODirect);
                // Soft light from the whole sky (Unity's ambient probe), blocked in recesses by AO.
                half3 ambient = SampleSH(N) * _AmbientAmount * ao;

                half3 albedo = input.color.rgb * _Tint.rgb;

                // S4.6: WET ROCK where the waves ran up (replaces S4.4's broad white glaze).
                float wetness = 0.0, film = 0.0;
                if (_RunupRect.z > 0.0)
                {
                    float2 uv = float2((input.positionWS.z - _RunupRect.x) * _RunupRect.z,
                                       (input.positionWS.y - _RunupRect.y) * _RunupRect.w);
                    if (all(uv > 0.0) && all(uv < 1.0))
                    {
                        float2 rg = SAMPLE_TEXTURE2D(_RunupTex, sampler_RunupTex, uv).rg;
                        film = saturate(rg.r);
                        wetness = saturate(rg.g);
                    }
                }
                if (wetness > 0.0)
                {
                    // 1. Darker and more saturated where wet (Unity's Wet subgraph): water fills the
                    //    pores, light bounces inside the film and is absorbed (Jensen et al. 1999).
                    float luma = dot(albedo, float3(0.2126729, 0.7151522, 0.0721750));
                    half3 wetColor = saturate(luma + 1.5 * (albedo - luma)) * 0.2;
                    albedo = lerp(albedo, wetColor, saturate(_Permeability * wetness));

                    // 2. S5.3: the running-water drips (Unity's Rain_Drips textures) were REMOVED. Their
                    //    2-3 cm channels are thinner than a pixel at viewing distance, so each drip head
                    //    showed as a short white tick, in rows every 1/_DripTiling = 2.9 m: scratches,
                    //    not water. Darkening (1) and gloss (3) alone read as wet stone.
                    //    (_DripTex, _DripMaskTex, _DripTiling are left declared but unused.)
                }

                // Debug views: each ingredient on its own, to see the effects in isolation.
                if (_DebugView > 4.5) return half4(wetness, film, 0, 1);         // wetness (red), water film (green)
                if (_DebugView > 3.5) return half4(ao.xxx, 1);                  // ambient occlusion (grey)
                if (_DebugView > 2.5) return half4(ambient, 1);                 // sky light only
                if (_DebugView > 1.5) return half4(direct, 1);                  // sunlight only (incl. shadows)
                if (_DebugView > 0.5) return half4(albedo, 1);                  // rock colour only, no lighting

                half3 color = albedo * (direct + ambient);

                // 3. Wet stone is glossier (Unity's Wet subgraph raises smoothness towards 0.9): a
                //    tight sun highlight (Blinn-Phong, ~4 % reflectance like water), only where wet.
                if (wetness > 0.0)
                {
                    float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                    float3 H = normalize(V + sun.direction);
                    float smooth = lerp(0.3, 0.9, wetness);
                    float power = exp2(10.0 * smooth + 1.0);
                    float spec = pow(saturate(dot(N, H)), power) * (power + 8.0) / 25.13 * 0.04;
                    color += sun.color * spec * sun.shadowAttenuation * wetness * _WetGloss;
                }

                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // Pass 2: shadow caster. Renders the cliff from the sun's point of view into
        // the shadow map. Positions are nudged along the normal ("shadow bias") to avoid
        // "shadow acne" (surfaces shadowing themselves in stripes).
        // Follows the pattern of URP's own ShadowCasterPass.hlsl (ApplyShadowBias with
        // _LightDirection, clamp to the near plane); see the URP 17 documentation and
        // package source.
        // ---------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;   // set by URP while rendering the shadow map

            struct ShadowAttributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };

            float4 shadowVert(ShadowAttributes input) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                // Keep shadow casters behind the camera's near plane from being clipped away.
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 shadowFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // Pass 3: depth only (for the camera depth texture)
        // ---------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag

            float4 depthVert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half depthFrag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
