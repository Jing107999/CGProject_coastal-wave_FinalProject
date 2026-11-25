// Water.shader: draws the ocean in URP.
//
// Vertex stage:   moves each flat grid vertex to its Gerstner position (the wave SHAPE).
// Fragment stage: re-evaluates Gerstner per pixel for an exact normal, then lights it
//                 like water: body colour + sky reflection (Fresnel) + sun glint + fog.
//
// S1.4 is the "core" version (decision D11): opaque, no depth colour.
// S3.4 adds foam: the GPU foam map (FoamSystem) whitens the water where waves hit the cliff.
// S4.1 adds contact foam: a white band where the water touches the rock (CliffFaceMap).
// S4.2 adds crest foam (whitecaps) where the surface is squeezed (low Jacobian J). For a Gerstner
// wave J = 1 - kA at the crest, so the theoretical breaking limit H/L = 0.142 is J = 0.55. Real
// crests break before that limit; J < 0.71 makes ~0.7 % of our sea foam, the whitecap coverage
// measured at sea for the ~9 m/s wind that our longest (70 m) waves imply.

Shader "Coastal/Water"
{
    // Properties = the settings shown on the material in the Inspector.
    Properties
    {
        [Header(Water body)]
        _DeepColor     ("Deep colour", Color) = (0.02, 0.10, 0.16, 1)
        _AmbientAmount ("Sky light on the body", Range(0, 1)) = 0.4

        [Header(Sky reflection)]
        _SkyZenith  ("Sky colour overhead", Color) = (0.22, 0.42, 0.72, 1)
        _SkyHorizon ("Sky colour at horizon", Color) = (0.72, 0.80, 0.86, 1)
        _FresnelF0  ("Reflectance looking straight down (F0)", Range(0, 0.2)) = 0.02

        [Header(Sun glint)]
        _SunSpecPower    ("Glint sharpness", Float) = 900
        _SunSpecStrength ("Glint brightness", Float) = 6

        [Header(Foam)]
        _FoamColor ("Foam colour", Color) = (0.95, 0.97, 1, 1)
        _FoamGain  ("Foam gain (map value -> coverage)", Float) = 6    // S5.5: was 12 (S4.10d); with tau 10 s foam piles up, so less gain keeps the lace
        _FoamFloor ("Foam map: values below this draw nothing (hides the faint haze)", Range(0, 0.1)) = 0.02
        _FoamEdgeFade ("Foam map: fade-out width at its border (m)", Float) = 15
        _FoamPatternScale ("Foam patches: pattern frequency (per metre)", Float) = 1.6
        _FoamPatternSoftness ("Foam patches: edge softness", Range(0.01, 0.5)) = 0.12
        [Toggle] _FoamLaceAll ("Foam lace: also on the contact band and crest foam (S5.4)", Float) = 1
        _ContactSolidWidth ("Contact foam: solid line at the rock (m)", Float) = 0.15
        _ContactFoamWidth    ("Contact foam width (m)", Float) = 0.9
        _ContactFoamStrength ("Contact foam strength", Range(0, 1)) = 0.85
        _CrestFoamBias     ("Crest foam: Jacobian below this foams", Float) = 0.71
        _CrestFoamStrength ("Crest foam: coverage per unit below", Float) = 2

        [Header(Debug)]
        [Enum(Shaded, 0, Normals, 1, Jacobian, 2, Foam map, 3)] _DebugView ("Debug view", Float) = 0
    }

    SubShader
    {
        // "UniversalPipeline": this shader belongs to URP. Opaque, drawn with the other solid objects.
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        // ---------------------------------------------------------------
        // Code shared by both passes below
        // ---------------------------------------------------------------
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Gerstner.hlsl"

        // Material settings. Wrapping them in UnityPerMaterial makes the shader
        // compatible with URP's SRP Batcher (faster drawing).
        CBUFFER_START(UnityPerMaterial)
            half4 _DeepColor, _SkyZenith, _SkyHorizon, _FoamColor;
            float _AmbientAmount, _FresnelF0, _SunSpecPower, _SunSpecStrength, _FoamGain, _DebugView;
            float _ContactFoamWidth, _ContactFoamStrength, _CrestFoamBias, _CrestFoamStrength;
            float _FoamFloor, _FoamEdgeFade, _FoamPatternScale, _FoamPatternSoftness;
            float _FoamLaceAll, _ContactSolidWidth;
        CBUFFER_END

        // The foam map, published as globals by FoamSystem (not material settings).
        TEXTURE2D(_FoamTex); SAMPLER(sampler_FoamTex);
        float4 _FoamRect;   // xy = world (x, z) of the map's corner, zw = 1 / map size
        float4 _FoamDrift;  // xy = the foam map's drift (m/s), published by FoamSystem (S4.10f)

        // Where the cliff face is, published by CliffFaceMap: R = x of the rock face at (z, y).
        TEXTURE2D(_CliffFaceTex); SAMPLER(sampler_CliffFaceTex);
        float4 _CliffFaceRect;  // (zMin, yMin, 1 / z range, 1 / y range); all 0 = no map

        // What the vertex stage receives from the mesh.
        struct Attributes
        {
            float4 positionOS : POSITION;   // flat grid position, object space
        };

        // Shared vertex step: rest position -> displaced world position.
        float3 DisplacedWorldPosition(float3 positionOS, out float2 restXZ)
        {
            float3 rest = TransformObjectToWorld(positionOS);   // where this vertex lies on the calm sea
            restXZ = rest.xz;
            return EvaluateGerstner(rest.xz, _SimTime).position;
        }
        ENDHLSL

        // ---------------------------------------------------------------
        // Pass 1: the visible water
        // ---------------------------------------------------------------
        Pass
        {
            Name "Water"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off      // draw both sides; a steep wave can show its underside near the camera
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            // Variants so the water receives the sun's shadows and Unity's fog.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // What the vertex stage hands to the fragment stage (interpolated across each triangle).
            struct Varyings
            {
                float4 positionCS : SV_POSITION;  // position on screen (clip space)
                float3 positionWS : TEXCOORD0;    // displaced world position
                float2 restXZ     : TEXCOORD1;    // rest position, to re-evaluate the waves per pixel
                float  fogFactor  : TEXCOORD2;
            };

            // VERTEX STAGE: runs once per grid vertex.
            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = DisplacedWorldPosition(input.positionOS.xyz, output.restXZ);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.fogFactor  = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // Sky colour in direction dir: a gradient from horizon to overhead.
            half3 SkyColor(float3 dir)
            {
                return lerp(_SkyHorizon.rgb, _SkyZenith.rgb, pow(saturate(dir.y), 0.45));
            }

            // Simple blue -> yellow -> red ramp for the Jacobian debug view.
            half3 Heat(float x)
            {
                x = saturate(x);
                return x < 0.5 ? lerp(half3(0.05, 0.1, 0.45), half3(1.0, 0.85, 0.1), x * 2.0)
                               : lerp(half3(1.0, 0.85, 0.1), half3(0.9, 0.1, 0.05), x * 2.0 - 1.0);
            }

            // How much foam lies at this position (0 outside the foam map). Callers pass the REST
            // position (S5.2); the parameter name is from the first version.
            float SampleFoam(float2 worldXZ)
            {
                float2 uv = (worldXZ - _FoamRect.xy) * _FoamRect.zw;
                float inside = all(uv > 0.0) && all(uv < 1.0) ? 1.0 : 0.0;
                return SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, uv).r * inside;
            }

            // S4.10f: FOAM PATCHES. Real foam is a lace of bubbles: cells and streaks with holes,
            // not a uniform white sheet. A cheap stand-in: value noise (smoothly interpolated random
            // values on a grid), three octaves, folded with 1 - |2n - 1| so it forms ridges (the lace).
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }
            float ValueNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);                    // smooth blend between grid points
                return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), u.x),
                            lerp(Hash21(i + float2(0, 1)), Hash21(i + float2(1, 1)), u.x), u.y);
            }
            // restXZ: the foam map is indexed by rest position (S5.2), and the map drifts by _FoamDrift,
            // so the pattern is evaluated at (rest - drift * t): the lace moves WITH the foam. The small
            // time offsets inside let the lace slowly churn as well.
            float FoamPattern(float2 restXZ, float t)
            {
                float2 p = (restXZ - _FoamDrift.xy * t) * _FoamPatternScale;
                float n = 0.55 * ValueNoise(p + float2(t * 0.15, t * 0.07))
                        + 0.30 * ValueNoise(p * 2.7 - float2(t * 0.11, -t * 0.13))
                        + 0.15 * ValueNoise(p * 7.1 + float2(t * 0.05, t * 0.2));
                return saturate(1.15 * (1.0 - abs(n * 2.0 - 1.0)) - 0.1);
            }
            // Coverage of the MAP foam with the pattern: dense foam (d near 1) passes almost every
            // threshold, so it stays mostly white with a few holes (capped at 0.92); as the foam thins,
            // fewer cells pass and it breaks into islands and streaks before it disappears.
            // The ridged pattern clusters near 1, so its square spreads the thresholds over 0..1.
            float PatchedCoverage(float d, float pattern)
            {
                float threshold = pattern * pattern;
                float cells = smoothstep(threshold - _FoamPatternSoftness, threshold + _FoamPatternSoftness, min(d, 0.92));
                return cells * saturate(d * 3.0);   // very thin foam fades out as a whole, too
            }

            // S4.10c: the foam map as it is DRAWN, so its rectangle never shows on the water.
            //  1. Floor: whitecap trails spread and fade into a thin, even layer of tiny values over
            //     the whole map. Times the gain, that tinted the water grey up to a straight border.
            //     Values below _FoamFloor now draw nothing; real foam is shifted down by the floor.
            //  2. Edge fade: the map's contribution goes smoothly to 0 over the last _FoamEdgeFade
            //     metres before its border, so foam drifting to the edge doesn't end in a line.
            // The map itself is unchanged (it still decays exactly as e^(-t/tau)); only the drawing.
            float DrawnFoam(float2 worldXZ)
            {
                if (_FoamRect.z <= 0.0) return 0.0;                  // "no foam map" (Edit mode)
                float2 uv = (worldXZ - _FoamRect.xy) * _FoamRect.zw;
                float2 toEdge = min(uv, 1.0 - uv) / _FoamRect.zw;      // metres to the nearest border, per axis
                float edge = smoothstep(0.0, _FoamEdgeFade, min(toEdge.x, toEdge.y));   // 0 outside the map
                return max(SampleFoam(worldXZ) - _FoamFloor, 0.0) * edge;
            }

            // Horizontal distance (m) from world point p to the rock face at p's own height.
            // The face is x = W(z) at this height, so its normal is ~(1, 0, -dW/dz) and the
            // perpendicular distance is (W - x) / sqrt(1 + (dW/dz)^2). Large if there's no map.
            float DistanceToRock(float3 p)
            {
                if (_CliffFaceRect.z == 0.0) return 1e4;
                float2 uv = float2((p.z - _CliffFaceRect.x) * _CliffFaceRect.z, (p.y - _CliffFaceRect.y) * _CliffFaceRect.w);
                if (any(uv < 0.0) || any(uv > 1.0)) return 1e4;
                const float dz = 0.5;   // metres, for the slope dW/dz
                float w  = SAMPLE_TEXTURE2D(_CliffFaceTex, sampler_CliffFaceTex, uv).r;
                float w1 = SAMPLE_TEXTURE2D(_CliffFaceTex, sampler_CliffFaceTex, uv + float2(dz * _CliffFaceRect.z, 0)).r;
                float w0 = SAMPLE_TEXTURE2D(_CliffFaceTex, sampler_CliffFaceTex, uv - float2(dz * _CliffFaceRect.z, 0)).r;
                float slope = (w1 - w0) / (2.0 * dz);
                return (w - p.x) / sqrt(1.0 + slope * slope);
            }

            // FRAGMENT STAGE: runs once per screen pixel covered by water.
            // FRONT_FACE_TYPE / FRONT_FACE_SEMANTIC are URP macros that tell us whether we see the
            // top or the underside of a triangle, written portably for Metal, DirectX and WebGL.
            half4 frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                // Exact normal at THIS pixel (finer than interpolating between vertices).
                GerstnerSample g = EvaluateGerstner(input.restXZ, _SimTime);
                bool isFrontFace = IS_FRONT_VFACE(face, true, false);
                float3 N = isFrontFace ? g.normal : -g.normal;

                // S5.2: the foam map is indexed by REST position, so look it up at this pixel's rest
                // position. The foam then sits on the same water particles and moves with them: in
                // with the crests, back out with the reflected waves. (Before S5.2: positionWS.xz,
                // foam fixed in the world while the surface slid under it.)
                float foam = SampleFoam(input.restXZ);        // the raw map (for the debug view)
                float foamDrawn = DrawnFoam(input.restXZ);    // as drawn: floor + edge fade (S4.10c)

                // Debug views (set on the material, or cycled with F in Play mode).
                if (_DebugView > 2.5) return half4(Heat(saturate(foam * 4.0)) * saturate(foam * 40.0), 1); // foam map: black = none
                if (_DebugView > 1.5) return half4(Heat((1.0 - g.jacobian) * 1.5), 1); // squeezed crests = hot
                if (_DebugView > 0.5) return half4(N * 0.5 + 0.5, 1);                   // normal as colour

                float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);  // towards the camera
                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 L = sun.direction;                                        // towards the sun
                half shadow = sun.shadowAttenuation;                             // 0 = in shadow, 1 = lit

                // 1. BODY: the water's own colour, lit by the sun and by the sky (ambient).
                half3 ambient = SampleSH(N);   // sky light from Unity's ambient probe
                half3 body = _DeepColor.rgb *
                             (sun.color * (0.35 + 0.65 * saturate(L.y)) * lerp(0.6, 1.0, shadow) + ambient * _AmbientAmount);

                // 2. SKY REFLECTION: mirror the view direction about the normal, look up the sky there.
                float3 R = reflect(-V, N);
                R.y = abs(R.y);                // never reflect "below the horizon" into the sea
                half3 reflection = SkyColor(R);

                // 3. FRESNEL (Schlick 1994): 2% reflection looking down, ~100% at grazing angles.
                float NdotV = saturate(dot(N, V));
                float fresnel = _FresnelF0 + (1.0 - _FresnelF0) * pow(1.0 - NdotV, 5.0);

                // 4. SUN GLINT: bright where the reflected ray points almost exactly at the sun.
                half3 glint = sun.color * pow(saturate(dot(R, L)), _SunSpecPower) * _SunSpecStrength * shadow;

                half3 color = lerp(body, reflection, fresnel) + glint;

                // 5. FOAM: white, rough and matte, so it is lit by the sun (softened, never fully dark
                //    in shadow) and the sky, and has no reflection or glint. It covers the water in
                //    proportion to the foam map, so on screen it fades like the map: e^(-t/tau).
                // Contact foam: water touching the rock is churned white, fading out over
                // _ContactFoamWidth metres. (Squared, so the band has a soft outer edge.)
                float contact = 1.0 - saturate(DistanceToRock(input.positionWS) / _ContactFoamWidth);
                contact *= contact;
                // Crest foam: where a crest gets too steep, the surface is squeezed and J drops below
                // the bias (0.55 = theoretical limit; default 0.71 = calibrated to observed coverage).
                float crest = saturate((_CrestFoamBias - g.jacobian) * _CrestFoamStrength);

                // With _FoamLaceAll off: only the map foam (impacts, trails) goes through the patch
                // pattern; the contact band and the crest foam are drawn plain. The default (on) is below.
                float pattern = FoamPattern(input.restXZ, _SimTime);
                float mapFoam = PatchedCoverage(saturate(foamDrawn * _FoamGain), pattern);
                float coverage = saturate(max(max(mapFoam, contact * _ContactFoamStrength), crest));

                // S5.4: the contact band and the crest foam go through the SAME pattern, so the band
                // at the rock frays into the threads of the foam drifting away (no painted strip) and
                // whitecaps out at sea are lace, not soft grey blobs. The amounts are combined first
                // (max), so there is one pattern, never two side by side. Only a thin line where the
                // water touches the rock (_ContactSolidWidth) stays solid white.
                if (_FoamLaceAll > 0.5)
                {
                    float amount = max(max(saturate(foamDrawn * _FoamGain), contact * _ContactFoamStrength), crest);
                    float touchLine = 1.0 - saturate(DistanceToRock(input.positionWS) / max(_ContactSolidWidth, 1e-3));
                    coverage = saturate(max(PatchedCoverage(amount, pattern), touchLine * _ContactFoamStrength));
                }
                half3 foamLit = _FoamColor.rgb * lerp(0.75, 1.0, pattern) *       // lace ridges a bit brighter
                                (sun.color * (0.45 + 0.55 * saturate(dot(N, L))) * lerp(0.55, 1.0, shadow) + ambient * 0.5);
                color = lerp(color, foamLit, coverage);

                // 6. FOG: blend towards the fog colour with distance.
                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // Pass 2: depth only. URP uses it to fill the depth texture, so effects that read it
        // (the soft spray particles) see the WAVES, not the flat grid.
        // ---------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex depthVert
            #pragma fragment depthFrag

            float4 depthVert(Attributes input) : SV_POSITION
            {
                float2 restXZ;
                return TransformWorldToHClip(DisplacedWorldPosition(input.positionOS.xyz, restXZ));
            }

            half depthFrag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
