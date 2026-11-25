# Textures from Unity's Shader Graph samples

The four textures in this folder are copied unchanged from the **Shader Graph "Production Ready Shaders" sample** (package `com.unity.shadergraph` 17.6.0), © Unity Technologies, licensed under the **Unity Companion License** (https://unity.com/legal/licenses/unity-companion-license).

## Spray textures (in use)

`cloud.png` (soft cloud puff) and `water_churn_8x4.png` (an 8 × 4 flipbook of churning water) come from the sample's `Common/Textures` folder. Unity uses them for its `WaterfallMist` and `WaterfallSplash` materials. We draw them with URP's built-in **Particles/Unlit** shader (soft particles, flipbook via the Texture Sheet Animation module) on Unity's standard particle system, driven by our wave-break events (`Scripts/Interaction/SprayEmitter.cs`).

## Rain-drip textures (no longer used)

`rain_drips.png` and `rain_drips_mask.png` come from the sample's `Weather/Rain/Textures` folder. We used them for a while to draw water drips running down the wet rock, with the drip logic of Unity's `Rain_Drips` subgraph ported to HLSL in `Shaders/Rock.shader`. The effect was later removed: at viewing distance the thin drips showed as rows of short white ticks rather than water. The files stay here because `Rock.mat` still references them.

## Wet rock (formula only, no texture)

The wet darkening in `Shaders/Rock.shader` follows Unity's `Wet` subgraph from the same sample: wet colour = saturate(saturation(albedo, 1.5)) · 0.2, blended by saturate(permeability · wetness).
