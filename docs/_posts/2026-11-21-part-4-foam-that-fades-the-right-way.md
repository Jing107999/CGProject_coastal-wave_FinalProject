---
layout: post
title: "Part 4: Foam that fades the right way"
date: 2026-11-21
part: 4
permalink: /blog/part-4/
excerpt: "Each wave impact should leave foam that fades over a few seconds."
---

*Each wave impact from Part 3 should leave foam that fades over a few seconds. I built it in five small steps and checked each one against the maths before the next. The model is in the report (§3.5); this post is about getting it to work, and why I couldn't see any foam at first.*

![The same paused frame with foam: a white patch at the foot of the cliff]({{ '/assets/part4/s3-4_hit_foam-on.jpg' | relative_url }})
*The result of this part: half a second after a hit, a white patch at the cliff foot. The red strip in the corner is the foam map itself.*

## Step 1: A map that forgets

Foam has to remember where the sea was hit and how long ago. I store that in a *foam map*, a texture lying flat over the 100 × 260 m strip of sea in front of the cliff. Each texel holds how much foam is there, and the GPU updates it every frame, with two textures taking turns (ping-pong). Every frame, each texel is multiplied by e^(−Δt/τ), with lifetime τ = 4 s.

```hlsl
// FoamUpdate.shader: one step for one texel
foam *= _Decay;                              // _Decay = exp(-dt / tau), computed on the CPU
foam += s.w * exp(-dot(d, d));               // plus any new splats (Gaussian blobs)
```
**In Unity:** a `Foam` object with the `FoamSystem` component, ticked by the clock right after the detector. The map is 394 × 1024 texels of 32-bit floats (0.25 m each). At 300 fps a frame shrinks foam by only 0.08 %, a change too small for 16-bit numbers to hold.
*Source: Callaghan et al. 2013 (foam decay); Unity Scripting API, `Graphics.Blit`, `RenderTexture`*

To test it, I dropped one blob of foam on the map and read its centre back from the GPU every frame for 20 s, at two frame rates.

![Foam at the splat centre against time: two measured runs on top of the exp(-t/4 s) curve]({{ '/assets/part4/s3-1_decay.jpg' | relative_url }})
*~300 fps and 30 fps give the same fade, and both follow the exp(−t/4 s) curve.*

**Problem (code):** the first 30 fps trace ended at t = 21.6 s, although the clock had only reached 20.2 s. GPU readbacks arrive a few frames late, and two requests from the previous run had arrived after I restarted and landed in the new trace.

```diff
  // FoamSystem.cs: tag each readback with the run it belongs to
+ int gen = generation;                                   // Clear() adds 1 on every restart
  AsyncGPUReadback.Request(current, …, request =>
  {
-     trace.Add(new Vector2(t, request.GetData<float>()[0]));
+     if (!request.hasError && gen == generation)         // answer from before a restart? drop it
+         trace.Add(new Vector2(t, request.GetData<float>()[0]));
  });
```
*Source: Unity Scripting API, `AsyncGPUReadback.Request`*

## Step 2: Impacts make foam

Next, each wave-break event from Part 3 adds a soft blob of foam (a *splat*) just in front of the rock. Stronger hits make bigger, brighter splats.

```csharp
// FoamSystem.cs: listens to the detector's WaveBreak event
Vector3 p = e.position + e.outward * splatOutset;        // a little out from the rock face
AddSplatAtWorldPoint(new Vector2(p.x, p.z),
         splatRadius * (0.6f + 0.8f * e.intensity),       // weak hit 0.6x, full hit 1.4x
         splatStrength * e.intensity);
```

To check it, I predicted the foam at one point near the busiest probe from the event list alone. Over 63 s the prediction matched what the GPU stored, even where splats from three neighbouring probes overlapped.

![Foam map with thin red streaks]({{ '/assets/part4/s3-2_map-inset.jpg' | relative_url }}) ![Foam map with softer, wider red patches]({{ '/assets/part4/s3-3_map-inset.jpg' | relative_url }})
*The foam map. Left, step 2: thin streaks, because a crest running along the coast hits neighbouring probes one after another. Right, step 3: the same streaks spread into soft patches.*

## Step 3: Letting foam spread

Real foam drifts apart while it fades. The usual way to model that is diffusion, where each texel moves a little towards the average of its four neighbours.

**Problem (code):** when I worked out what a fixed per-texel blend would do, it turned out to depend on the texel size. On my map it would widen a 2 m splat by about 5 cm over its whole life, which is invisible, and it would change whenever I changed the map's resolution.

Instead, I set the spreading rate in physical units, **D = 0.34 m²/s**, and derived the per-texel weight from it. That raised a second problem: on a slow frame the weight goes above 1, and foam can turn negative. Such frames are now split into smaller steps:

```csharp
// FoamSystem.cs: weight per frame from a physical spreading rate
float alpha = 4f * diffusion * dt / (h * h);                          // h = texel size in metres
int n = Mathf.Clamp(Mathf.CeilToInt(alpha / MaxAlpha), 1, MaxSubsteps); // split if alpha > 0.9
material.SetFloat(DecayId, Mathf.Exp(-dt / n / Mathf.Max(0.01f, decayTime)));   // n substeps multiply to exp(-dt/tau)
```
*Source: explicit finite-difference heat equation (5-point stencil); the derivation is in the report (§3.5)*

A spreading blob has an exact answer to compare with. The measured centre matched it at ~300, 30 and 10 fps; at 10 fps each frame was split into 3 steps.

## Step 4: Foam on the water

So far the foam only existed in the red preview in the corner. I added it to the water shader, which looks up the map at each pixel's position and blends towards a sunlit white.

```hlsl
// Water.shader (first version): map value -> how much of the pixel is foam
float coverage = saturate(SampleFoam(worldXZ) * _FoamGain);     // _FoamGain = 3
color = lerp(color, foamColor, coverage);
```

**Problem (look):** I still didn't see any foam, from the default camera or from above.

![Top view: the cliff's overhanging cap hides the water at its foot]({{ '/assets/part4/s3-4_top_shaded.jpg' | relative_url }})
*From above, the cliff's overhanging cap (from Part 2) hides the water at its foot, where all the foam is. The map in the corner shows plenty of it.*

The foam was there, just hidden under the overhang. The real test was one paused frame from the sea, half a second after a hit, with the foam switched off and on:

![Same paused frame with foam off: no patch]({{ '/assets/part4/s3-4_hit_foam-off.jpg' | relative_url }})
*Foam gain 0: no patch.*

![Same paused frame in the foam-map debug view: a heat blob exactly where the patch is]({{ '/assets/part4/s3-4_hit_foam-map.jpg' | relative_url }})
*Debug view (key F): the heat blob sits exactly where the white patch is in the first picture, so the map and the shader agree on positions.*

**Problem (look, left for later):** the foam is a smooth, featureless blob, while real foam is lacy and streaky. That comes in Part 8.

## Step 5: A lifetime knob, and the whole map

Last, keys **3** and **4** shorten or lengthen τ (× 0.8 or × 1.25, never below 0.25 s), and the overlay shows τ, D and the splat count.

So far I had only checked single points. The stronger test is the whole map: spreading only moves foam around, so with no new splats the *total* must fall as exactly e^(−t/τ). I ran 30 s of real impacts, stopped them, and summed the map. With τ = 4 s and 2.56 s (key 3 twice), both uncapped and at 30 fps, the total followed the curve in all four runs.

**Problem (build):** both 30 fps runs first failed because the clock stood still. I had clicked another window, and the Unity Editor stops advancing frames when it isn't in front. Now I check that `Time.frameCount` is moving before trusting a timed measurement.

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Foam map format | 16-bit | **32-bit float** | 16-bit can't hold a 0.08 % change per frame |
| Lifetime τ | 2.56, 4 s | **4 s** (later 10 s, see Part 8) | Foam fades in a few seconds |
| Spreading | per-texel blend (≈ 0.02 m²/s) | **D = 0.34 m²/s** | A splat grows from 2.2 m to 3.2 m over its life |
| Max weight per step | — | **0.9**, then split the frame | No negative foam on slow frames |
| Splat radius / strength | — | **2.2 m / 0.55** (later 3 m / 0.9, see Part 8) | Streaks along the cliff |
| `_FoamGain` | — | **3** (later 6, see Part 8) | A typical fresh splat covers ~70 % |

## What I do differently now

- **Check the fade against its formula, at two frame rates.** If they agree, the frame rate can't leak in.
- **Tag results that arrive late** with the run they belong to.
- **Set physical units, not per-texel numbers.** Then changing the resolution doesn't change the look.
- **When I can't see something, check whether it's missing or hidden.** The debug view showed the foam was there all along.

## Sources

Callaghan et al. 2013 (whitecap foam decay). Unity Scripting API: `Graphics.Blit`, `RenderTexture`, `AsyncGPUReadback`.

**Next:** [Part 5]({{ '/blog/part-5/' | relative_url }}). Foam only at the impact isn't enough: the whole sea should react to the cliff.
