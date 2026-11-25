// RunupUpdate.shader: ONE simulation step of the white water on the cliff face (S4.4).
//
// The map lies on the cliff face: u = coast position z, v = height y (both world metres via
// _MapRect). Two channels (S4.6: wet rock):
//   R = water film running down the stone (short-lived, drains) -> keeps the stone wet (G below)
//   G = wetness of the stone (dries slowly, never below R)       -> darkens the rock
// One step for R:
//
//   W' = decay * W(y + drain)  +  sum over bands  s * exp(-(dz / halfWidth)^2) * inBand(y)
//
// - drain: the water slides DOWN the rock, so each texel takes what was a little above it.
// - decay = exp(-dt / tau), as for the foam on the sea (frame-rate independent).
// - bands: the parts of each uprush the jet climbed through during this step (from RunupSystem):
//   a Gaussian across the coast, and between the previous and the current height of the jet.
// and for G:  G' = max(G * wetDecay, R')   (stays where water was, then slowly dries)

Shader "Hidden/Coastal/RunupUpdate"
{
    Properties
    {
        _MainTex ("Previous run-up map", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define MAX_BANDS 64

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float4 _Bands[MAX_BANDS];   // x = z centre (m), y = y low (m), z = y high (m), w = strength
            float  _BandHalfWidth[MAX_BANDS];   // half-width across the coast (m)
            float  _BandCount;
            float  _Decay;              // exp(-dt / tau)          for the water film (R)
            float  _WetDecay;           // exp(-dt / tauWet)       for the wetness (G)
            float  _Drain;              // how far the water slides down this step, in uv
            float4 _MapRect;            // (zMin, yMin, z size, y size), metres

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float2 frag(Varyings i) : SV_Target
            {
                // 1. Old water film drains down (take it from slightly higher up) and fades.
                float w = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv + float2(0, _Drain)).r * _Decay;
                float wet = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).g * _WetDecay;

                // 2. New white water where an uprush climbed during this step.
                float z = _MapRect.x + i.uv.x * _MapRect.z;
                float y = _MapRect.y + i.uv.y * _MapRect.w;
                int count = (int)_BandCount;
                [loop] for (int k = 0; k < MAX_BANDS; k++)
                {
                    if (k >= count) break;
                    float4 b = _Bands[k];
                    float dz = (z - b.x) / _BandHalfWidth[k];
                    // Half-open interval [low, high): consecutive steps tile the climb exactly, so each
                    // texel on the path is counted once, however many frames the climb takes
                    // (soft overlapping edges would deposit more at high frame rates).
                    float inBand = (y >= b.y && y < b.z) ? 1.0 : 0.0;
                    w += b.w * exp(-dz * dz) * inBand;
                }
                w = min(w, 2.0);                      // cap: many overlapping hits can't build up without limit
                return float2(w, max(wet, saturate(w)));   // the stone is at least as wet as the film on it
            }
            ENDHLSL
        }
    }
}
