// Sky.shader: a procedural skybox with drifting clouds, for URP.
//
// A skybox is drawn behind everything else. For every pixel we only know the DIRECTION the
// camera looks in (the vertex position of Unity's sky sphere, which is centred on the camera).
// From that direction we build the sky in three layers:
//
//   1. GRADIENT: horizon colour blending to the overhead colour as the direction tilts up.
//      Defaults equal the water's _SkyZenith / _SkyHorizon, so the sea still reflects "this" sky.
//   2. SUN: a small bright disc plus a soft glow around the main directional light.
//   3. CLOUDS: the view ray is extended until it hits a flat, imaginary cloud layer high above
//      the camera. The hit point gives 2D coordinates; fractal value noise (fBm) at those
//      coordinates is the cloud density. Thresholding it by "cover" makes separate clouds
//      instead of an even haze. Clouds drift slowly with time and fade out near the horizon,
//      where the flat-layer projection would stretch them into streaks.
//
// S5.9 v1 (start simple): no ray marching, no cloud reflection in the water, no shadows.

Shader "Coastal/Sky"
{
    Properties
    {
        [Header(Sky gradient)]
        _Zenith  ("Sky colour overhead", Color) = (0.22, 0.42, 0.72, 1)
        _Horizon ("Sky colour at horizon", Color) = (0.72, 0.80, 0.86, 1)
        _Below   ("Colour below the horizon", Color) = (0.62, 0.72, 0.80, 1)   // = fog colour

        [Header(Sun)]
        _SunColor ("Sun colour", Color) = (1, 0.95, 0.85, 1)
        _SunSize  ("Sun disc size (cos of radius)", Range(0.999, 0.99999)) = 0.9997
        _SunGlow  ("Sun glow strength", Range(0, 2)) = 0.6

        [Header(Clouds)]
        _CloudScale  ("Cloud size (bigger = smaller clouds)", Float) = 1.2
        _CloudCover  ("Cloud cover (0 clear .. 1 overcast)", Range(0, 1)) = 0.45
        _CloudSoft   ("Cloud edge softness", Range(0.01, 0.5)) = 0.2
        _CloudLit    ("Cloud colour, sun side", Color) = (1, 1, 1, 1)
        _CloudDark   ("Cloud colour, thick part", Color) = (0.62, 0.66, 0.72, 1)
        _CloudSpeed  ("Cloud drift (layer units / s)", Vector) = (0.004, 0.0015, 0, 0)
        _HorizonFade ("Clouds fade out below this height", Range(0.01, 0.5)) = 0.12
    }

    SubShader
    {
        // "Background" queue + "Skybox" preview: Unity knows this material is meant for a sky.
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off   // we see the sky sphere from the inside
        ZWrite Off // the sky never hides anything

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Zenith, _Horizon, _Below, _SunColor, _CloudLit, _CloudDark;
                float _SunSize, _SunGlow, _CloudScale, _CloudCover, _CloudSoft, _HorizonFade;
                float4 _CloudSpeed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;   // sky sphere is centred on the camera: position = direction
                return o;
            }

            // --- Value noise: random values on a grid, smoothly blended between grid points ---
            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);   // smoothstep curve: no visible grid lines
                float a = Hash(i);
                float b = Hash(i + float2(1, 0));
                float c = Hash(i + float2(0, 1));
                float d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // fBm (fractal Brownian motion): add 5 octaves, each twice as fine and half as strong.
            // Big octaves give the cloud shapes, small ones the ragged edges.
            float Fbm(float2 p)
            {
                float sum = 0, amp = 0.5;
                for (int k = 0; k < 5; k++)
                {
                    sum += amp * ValueNoise(p);
                    p = p * 2.03 + 17.1;   // not exactly 2, and shifted: hides repetition
                    amp *= 0.5;
                }
                return sum;   // roughly 0..1, mostly around 0.5
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float3 sunDir = normalize(_MainLightPosition.xyz);   // URP: direction TOWARDS the sun

                // 1. GRADIENT (same curve as the water's SkyColor, so the reflection matches)
                half3 sky = lerp(_Horizon.rgb, _Zenith.rgb, pow(saturate(dir.y), 0.45));

                // 2. SUN: soft glow everywhere near the sun, plus a sharp disc.
                float cosSun = dot(dir, sunDir);
                sky += _SunColor.rgb * _SunGlow * pow(saturate(cosSun), 64);
                sky += _SunColor.rgb * 20 * smoothstep(_SunSize, _SunSize + 0.00005, cosSun);

                // 3. CLOUDS on a flat layer: hit point of the ray = dir.xz / dir.y.
                //    (The 0.05 floor stops the division blowing up at the horizon.)
                float2 uv = dir.xz / max(dir.y, 0.05) * _CloudScale + _CloudSpeed.xy * _Time.y;
                float n = Fbm(uv);
                // Cover 0 needs n > 1 (nothing), cover 1 needs n > 0 (everything).
                float density = smoothstep(1 - _CloudCover, 1 - _CloudCover + _CloudSoft, n);
                // Thicker parts (n far above the threshold) are darker underneath.
                float thickness = saturate((n - (1 - _CloudCover)) * 3);
                half3 cloud = lerp(_CloudLit.rgb, _CloudDark.rgb, thickness);
                cloud += _SunColor.rgb * 0.4 * pow(saturate(cosSun), 8) * (1 - thickness); // thin edges glow towards the sun
                density *= smoothstep(0, _HorizonFade, dir.y);   // fade out at the horizon
                sky = lerp(sky, cloud, density);

                // Below the horizon (seen only past the ocean's edge): flat fog colour.
                sky = lerp(_Below.rgb, sky, smoothstep(-0.02, 0.0, dir.y));

                return half4(sky, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
