# Textures from Unity's Shader Graph samples

`rain_drips.png` and `rain_drips_mask.png` are copied unchanged from the **Shader Graph "Production Ready Shaders" sample** (package `com.unity.shadergraph` 17.6.0, `Weather/Rain/Textures`), © Unity Technologies, licensed under the **Unity Companion License** (https://unity.com/legal/licenses/unity-companion-license).

- `rain_drips.png`: R, G = drip normal (x, y); **B = drip channel mask**; **A = per-drip time offset**.
- `rain_drips_mask.png`: a 1-D pattern that is scrolled down over time to animate the drips.

How we use them (our own code, `Shaders/Rock.shader`): the drip logic of Unity's `Rain_Drips` subgraph, ported to HLSL: `drip = mask(y·tile + speed·(t + A)).r · B`, speed = lerp(lerp(0.2, 0.03, P), lerp(0, 0.0125, P), A) for permeability P. The wet darkening follows Unity's `Wet` subgraph: wet colour = saturate(saturation(albedo, 1.5)) · 0.2, blended by saturate(P · wetness).

## Particle textures (S4.6b)

`cloud.png` (soft cloud puff) and `water_churn_8x4.png` (an 8 × 4 flipbook of churning water) are copied unchanged from the same sample's `Common/Textures` folder (Unity Companion License). Unity uses them for its `WaterfallMist` and `WaterfallSplash` materials. We draw them with URP's built-in **Particles/Unlit** shader (soft particles, flipbook via the Texture Sheet Animation module) on Unity's standard particle system, driven by our wave-break events (`Scripts/Interaction/SprayEmitter.cs`).
