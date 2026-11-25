// FoamUpdate.shader: ONE simulation step of the foam density map.
//
// The foam map is a float texture over the coast: each texel holds "how much foam is here".
// FoamSystem runs this shader with Graphics.Blit, reading last step's map (_MainTex) and
// writing a new one (ping-pong: a shader can't read and write the same texture at once).
//
//   F' = decay * diffuse(F)  +  sum over splats  s * exp(-|x - c|^2 / r^2)
//
// decay = exp(-dt / tau), computed on the CPU. Multiplying by exp(-dt/tau) every step gives
// exactly exp(-t/tau) after ANY sequence of steps, so the fade does not depend on frame rate.
//
// diffuse(F) = F + alpha * (mean of the 4 neighbours - F): one explicit step of the heat
// equation dF/dt = D * laplacian(F), with alpha = 4 D dt / h^2 computed on the CPU (h = texel
// size in metres). alpha <= 1 keeps the result a weighted average, so foam never goes negative;
// FoamSystem splits a long frame into substeps to guarantee that.
//
// S3.1 decay + splats, S3.3 diffusion.
// S4.8 WHITECAP TRAILS + DRIFT (the foam that travels with the waves):
//   - whitecaps: at every texel, find the water particle that is there now (fixed-point inversion,
//     as the CPU detector does) and read the surface Jacobian J. Where J < bias the crest is steep
//     enough to break, and foam is ADDED at rate * dt * (bias - J). The crest moves on, so it lays
//     a trail that then fades and spreads like all other foam (Dupuy & Bruneton 2012 accumulate
//     whitecap foam the same way).
//   - drift: foam is carried slowly with the swell (Stokes drift): sample from upstream.
// S5.2 FOAM RIDES THE WATER: texels are REST positions of water particles (the water shader looks
//   the map up at each pixel's rest position). Foam therefore moves with the particles' orbits,
//   including the reflected waves near the cliff, without any advection step here. The drift is
//   now a drift of rest positions: the particles' mean (Stokes) drift.

Shader "Hidden/Coastal/FoamUpdate"
{
    Properties
    {
        _MainTex ("Previous foam map", 2D) = "black" {}
    }

    SubShader
    {
        // A full-screen pass over a texture: no depth, no culling, no blending.
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Gerstner.hlsl"   // the same wave formula as the water and the CPU (incl. reflection)

            #define MAX_SPLATS 64

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;    // set by Unity: x = 1/width, y = 1/height (one texel in uv)

            float4 _Splats[MAX_SPLATS];   // xy = centre (uv), z = radius (metres), w = strength
            float  _SplatCount;           // how many entries of _Splats are used this step
            float  _Decay;                // exp(-dt / tau)
            float  _Diffusion;            // alpha = 4 D dt / h^2, at most 1
            float2 _MapSize;              // world size of the map in metres (x, z)
            float2 _MapMin;               // world (x, z) of uv (0, 0)
            float  _WhitecapAmount;       // rate * dt (0 = no whitecaps; only in the last substep)
            float  _WhitecapBias;         // J below this foams (0.71, calibrated in S4.2)
            float2 _DriftUV;              // how far foam is carried this pass, in uv

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            // Graphics.Blit draws one quad covering the target; just pass its uv through.
            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float frag(Varyings i) : SV_Target
            {
                // 1. Old foam spreads: move towards the mean of the 4 neighbours.
                //    (Clamp sampling at the border = no foam flows in or out there.)
                //    S4.8: everything is read from slightly upstream, so the foam drifts with the swell.
                float2 t = _MainTex_TexelSize.xy;
                float2 src = i.uv - _DriftUV;
                float centre = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, src).r;
                float mean4 = 0.25 * (SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, src + float2(t.x, 0)).r
                                    + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, src - float2(t.x, 0)).r
                                    + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, src + float2(0, t.y)).r
                                    + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, src - float2(0, t.y)).r);
                float foam = lerp(centre, mean4, _Diffusion);

                // 2. ... and fades.
                foam *= _Decay;

                // 3. New foam: Gaussian splats. Distances are measured in metres
                //    (uv difference * map size), so a splat is round even though the map isn't square.
                int count = (int)_SplatCount;
                [loop] for (int k = 0; k < MAX_SPLATS; k++)
                {
                    if (k >= count) break;
                    float4 s = _Splats[k];
                    float2 d = (i.uv - s.xy) * _MapSize / s.z;
                    foam += s.w * exp(-dot(d, d));
                }

                // 4. S4.8: whitecap foam on steep crests (J < bias).
                //    S5.2: the map is indexed by REST position, so this texel IS a water particle:
                //    read its Jacobian directly. (Before S5.2 the texel was a fixed world point, and a
                //    4-iteration inversion had to find the particle there; it is no longer needed.)
                if (_WhitecapAmount > 0.0)
                {
                    float2 rest = _MapMin + i.uv * _MapSize;
                    float J = EvaluateGerstner(rest, _SimTime).jacobian;
                    foam += _WhitecapAmount * saturate(_WhitecapBias - J);
                }
                return foam;
            }
            ENDHLSL
        }
    }
}
