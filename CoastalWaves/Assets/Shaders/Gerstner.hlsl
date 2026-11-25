// Gerstner.hlsl: the GPU copy of the wave formula.
//
// This MUST match WaveField.Evaluate() in C# exactly. The CPU uses its copy for
// buoys, wave-break detection and foam; the GPU uses this copy to draw the water.
// If the two differ, the buoys float above (or sink into) the visible sea.
//
// For each wave i, with phase  theta_i = k_i (D_i . x0) - w_i t + phi_i :
//     x = x0 + sum D_i.x A_i cos(theta_i)
//     y =      sum       A_i sin(theta_i)      (plus sea level)
//     z = z0 + sum D_i.z A_i cos(theta_i)
//
// The numbers are uploaded by WaveField.Upload() as global shader properties.
//
// S4.3: image (reflected) waves have _GerstnerB.w = 1 and are faded out with distance from the
// wall plane n.x = c by w = exp(-max(c - n.x0, 0) / L). Same formula and derivatives as C#.

#ifndef COASTAL_GERSTNER_INCLUDED   // "include guard": lets several files include this safely
#define COASTAL_GERSTNER_INCLUDED

// Shaders can't have resizable arrays, so we reserve a fixed maximum.
// Must equal WaveField.MaxWaves in C#.
#define MAX_WAVES 24

float4 _GerstnerA[MAX_WAVES];   // per wave: (D.x, D.z, k, A)
float4 _GerstnerB[MAX_WAVES];   // per wave: (omega, phase, steepness s, windowed 0/1)
float  _GerstnerCount;          // how many entries are in use
float  _SeaLevel;               // height of the calm sea (m)
float  _GerstnerMaxHeight;      // sum of all amplitudes (highest possible crest)
float  _SimTime;                // the simulation clock's time (CoastalSimulation)
float4 _WallPlane;              // (n.x, n.z, c, 1 / fade length) for the image waves

// Fade factor of the image waves at rest position p0, and its gradient (see WaveField.WallWindow).
float WallWindow(float2 p0, out float2 gradient)
{
    float dist = _WallPlane.z - dot(_WallPlane.xy, p0);      // > 0 = out at sea
    float w = exp(-max(dist, 0.0) * _WallPlane.w);
    gradient = dist > 0.0 ? w * _WallPlane.w * _WallPlane.xy : float2(0, 0);
    return w;
}

// Everything the formula gives us for one water particle.
struct GerstnerSample
{
    float3 position;   // displaced position (world space)
    float3 normal;     // unit vector out of the surface
    float  jacobian;   // horizontal squeeze: 1 = none, -> 0 = about to fold
};

// Where is the particle that rests at rest.xy = (x0, z0) at time t?
GerstnerSample EvaluateGerstner(float2 rest, float t)
{
    // Start on the calm sea.
    float3 position = float3(rest.x, _SeaLevel, rest.y);

    // Tangents along x0 and z0: (1,0,0) and (0,0,1) on a flat sea; every wave bends them.
    float3 tangentX = float3(1, 0, 0);
    float3 tangentZ = float3(0, 0, 1);

    int count = (int)_GerstnerCount;
    float2 windowGrad;
    float window = WallWindow(rest, windowGrad);

    // [loop] tells the compiler to keep this as a real loop (count varies at runtime).
    [loop] for (int i = 0; i < MAX_WAVES; i++)
    {
        if (i >= count) break;

        float2 D     = _GerstnerA[i].xy;   // travel direction
        float  k     = _GerstnerA[i].z;    // wavenumber
        float  A     = _GerstnerA[i].w;    // amplitude
        float  omega = _GerstnerB[i].x;    // angular frequency
        float  phase = _GerstnerB[i].y;    // phase offset
        float  s     = _GerstnerB[i].z;    // steepness = k * A
        bool   image = _GerstnerB[i].w > 0.5;   // S4.3: reflected wave, faded near the wall
        float  w     = image ? window : 1.0;
        float2 g     = image ? windowGrad : float2(0, 0);
        float  a = A * w, sw = s * w;

        float theta = k * dot(D, rest) - omega * t + phase;
        float sinT, cosT;
        sincos(theta, sinT, cosT);         // computes both at once (cheaper)

        // Position: move around a circle of radius A (times the fade).
        position.x += D.x * a * cosT;
        position.y +=       a * sinT;
        position.z += D.y * a * cosT;

        // Tangents: exact derivatives (same terms as the C# version, incl. the fade's gradient).
        tangentX += float3(-sw * D.x * D.x * sinT + A * g.x * D.x * cosT, sw * D.x * cosT + A * g.x * sinT, -sw * D.x * D.y * sinT + A * g.x * D.y * cosT);
        tangentZ += float3(-sw * D.x * D.y * sinT + A * g.y * D.x * cosT, sw * D.y * cosT + A * g.y * sinT, -sw * D.y * D.y * sinT + A * g.y * D.y * cosT);
    }

    GerstnerSample result;
    result.position = position;
    result.normal   = normalize(cross(tangentZ, tangentX));   // same order as C#: points up
    result.jacobian = tangentX.x * tangentZ.z - tangentX.z * tangentZ.x;
    return result;
}

#endif // COASTAL_GERSTNER_INCLUDED
