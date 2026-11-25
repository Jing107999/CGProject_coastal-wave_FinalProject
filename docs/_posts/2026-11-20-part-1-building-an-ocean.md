---
layout: post
title: "Part 1: Building an ocean"
date: 2026-11-20
part: 1
permalink: /blog/part-1/
excerpt: "To set up the scene of ocean waves colliding with a cliff, I will first implement the ocean."
---

*To set up the scene of ocean waves colliding with a cliff, I will first implement the ocean. The wave maths is in the report (§3.1–3.2); this post is about getting it to work, in six steps.*

![A storm sea with sharp crests and a row of orange buoys]({{ '/assets/part1/s1-4_play_storm-sea_camera.jpg' | relative_url }})
*The result of this part: ten Gerstner waves moving the water grid. The orange buoys are test points on the waves.*

## Step 1: A scene and a clock

I started from Unity's empty URP template and removed its sample content. Then I added a camera, a sun, a sky with fog, and one object that keeps time. Every moving part reads this one clock, so pausing or slowing it affects the scripts and the shaders.

```csharp
// CoastalSimulation.cs: seconds since the last frame, clamped, then scaled by the speed setting
float dt = Mathf.Min(Time.deltaTime, maxStep) * timeScale;
Step(dt);   // advances t and sends it to every shader as _SimTime
```
**In Unity:** an empty `Simulation` object with the `CoastalSimulation` component (*Paused*, *Time Scale*, *Max Step*).
*Source: Unity Scripting API, `Time.deltaTime`, `Shader.SetGlobalFloat`*

**Problem (look, left for later):** the sun was to the right of the camera, and no glitter on the water could reach it. The right angle depends on how the sea and the rock look, and neither existed yet, so I left it for Part 2.

## Step 2: A grid for the water

The sea surface is one big mesh built in code when the scene loads: 400 × 400 m, with 512 squares per side. The squares are packed closer in the middle, where the rock will stand (0.20 m apart), and spread out towards the edges (1.95 m), where the fog hides them. The whole grid builds in 25 ms.
**In Unity:** a `Water` object with the `WaterSurface` component: *Resolution* 512, *Centre Bias* 0.75, *Size* 400.

![Top view of the water grid wireframe at low resolution, lines dense in the middle]({{ '/assets/part1/s1-2_wireframe_top-down_res64_illustration.jpg' | relative_url }})
*The grid from above. At 512 × 512 the lines merge into a solid black square; this figure uses 64 × 64. You can see them crowd towards the middle.*

## Step 3: Waves on the CPU, checked with buoys

Next I wrote the waves in C#: ten Gerstner waves added together, from a 70 m swell down to 1.8 m ripples. Before drawing anything, I wanted to know the maths was right. I added a row of small orange "buoys" that ride the waves, draw their path and log what they measure. With a single test wave, they matched the theory:

| | Measured | Theory |
|---|---|---|
| Orbit radius | 1.59 m | 1.59 m |
| Period | 3.58 s | 3.58 s |
| Crest speed | 5.58 m/s | 5.59 m/s |

![A single wave profile with pointed crests and flat troughs]({{ '/assets/part1/s1-3_test-wave_s0.9_sharp-crests.jpg' | relative_url }})
*A steep test wave: pointed crests and flat troughs, the shape that makes the sea look stormy.*

**Problem (code):** each buoy should draw a full circle, but the trails were short arcs. The trail kept the last 240 *frames*. The Editor ran at 430 fps, so that was only 0.55 s of a 3.6 s orbit. On a slower computer the same code would have drawn longer arcs. I switched to storing a point every 0.02 s of simulation time.

```diff
  // WaveProbes.cs
- trails[i][trailHead] = p;                                    // every frame
+ bool storeTrail = t - lastTrailTime >= trailSampleInterval;  // every 0.02 s of sim time
+ if (storeTrail) trails[i][trailHead] = p;
```

![Yellow orbit trails drawn as short arcs]({{ '/assets/part1/s1-3_test-wave_side_orbits.jpg' | relative_url }})
![Nine complete yellow circles, one per buoy]({{ '/assets/part1/s1-3_trails_after_last.jpg' | relative_url }})
*Before: short arcs. After: a full circle per buoy, at any frame rate.*

## Step 4: The same waves on the GPU

With the CPU side checked, I wrote the water shader using the same formula as the C# code. The vertex shader moves every vertex of the grid, and the fragment shader makes it look like water by mixing a dark body colour, the reflected sky and a sun glint, weighted by Fresnel (Schlick's approximation).

```hlsl
// Gerstner.hlsl: each wave moves a point of the water around a circle of radius A
float theta = k * dot(D, rest) - omega * t + phase;
sincos(theta, sinT, cosT);
position.x += D.x * a * cosT;
position.y +=       a * sinT;
position.z += D.y * a * cosT;
```
**In Unity:** `Water.mat` switched from URP/Lit to my `Coastal/Water` shader. In the URP asset I raised MSAA from 1× to 4× against jagged crests, and the shadow distance from 50 m to 160 m.
*Source: Gerstner 1802; Fournier & Reeves 1986; Finch 2004 (GPU Gems, ch. 1); Schlick 1994*

From now on the CPU decides where things are and the GPU decides what you see, so the two have to agree. I compared them at 256 points, and on average they were 0.012 mm apart.

![Close-up of orange buoys cut in half by the water surface]({{ '/assets/part1/s1-4_parity_buoy-closeup.jpg' | relative_url }})
*The CPU moves the buoys, the GPU draws the water: every buoy is cut exactly in half.*

## Step 5: How high is the water *here*?

The rock won't move, so later I need the water height at fixed points. But Gerstner waves move water sideways as well as up, which means the water above a fixed point started somewhere else. I find that starting point by guessing and correcting, a fixed-point iteration:

```csharp
// WaveField.cs: find the rest position whose particle is now at (x, z)
float x0 = x, z0 = z;                       // first guess: no sideways shift
for (int it = 0; it < iterations; it++)
{
    Vector2 h = HorizontalShift(x0, z0, t); // shift this guess would cause
    x0 = x - h.x;  z0 = z - h.y;            // correct the guess by it
}
```

To see it working, I pinned two spheres to one spot on the sea, one using the corrected height and one using the naive height.

**Problem (build):** the editor script that builds my scene stopped with `This cannot be used during play mode`. I had left Unity in Play mode, paused, and a script can't save the scene while it is playing. Now every step of the script checks for Play mode first and stops with a clear message.

![Red sphere floating above the water, white sphere sitting in the surface below it]({{ '/assets/part1/s1-5_height-marker_closeup.jpg' | relative_url }})
*Red: the naive height, floating above the water. White, just below it: the corrected height, sitting in the surface. The naive error reached 2 m.*

How many corrections are enough? I fixed the rule before measuring: 95 % of errors under 1 mm, and all of them under 1 cm.

![Height error against number of iterations, one line per sea steepness]({{ '/assets/part1/E1_height_error_vs_iterations.jpg' | relative_url }})
*Error against the number of corrections. For my sea, **6** is the smallest number that passes.*

## Step 6: A camera and controls

Last, I made the scene explorable: an orbit camera (right-drag, scroll, WASD/QE), keys for pause, speed, steepness and debug views, and a small overlay. The camera keeps itself above the waves with the height query from step 5.

**Problem (code):** when I held Q to fly low over the water, the camera stopped at the surface, but the point it was looking at kept sinking. The view slowly tilted until it stared into a wave.

```diff
  // OrbitCamera.cs
+ target.y = Mathf.Max(target.y, waveField.seaLevel - targetDepthLimit);  // target: at most 0.5 m under
  float water = waveField.HeightAt(position.x, position.z, waveField.SimTime);
  position.y = Mathf.Max(position.y, water + clearance);                  // clearance 1.5 → 0.6 m
```

**Problem (build):** even after the fix, the camera still stayed 1.5 m above the water. A component keeps the values saved in the scene, so changing the default in code doesn't affect objects that already exist. I set 0.6 in the Inspector and saved the scene.
*Source: Unity Manual, "Script serialization"*

![Camera view filled by a dark wave face]({{ '/assets/part1/s1-6_camera-low-above-waves.jpg' | relative_url }})
![Low view at wave level with the horizon and two buoys]({{ '/assets/part1/s1-6_after-fix_lowest-view.jpg' | relative_url }})
*Before: the camera looks into a wave. After: a low view with the horizon still visible.*

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Water grid (squares per side) | 256, 1024 (compared on paper) | **512** | 0.20 m spacing near the rock |
| Trail sampling | every frame | **every 0.02 s** | Same trail at any frame rate |
| MSAA | 1× | **4×** | Jagged crest edges |
| Shadow distance | 50 m | **160 m** | Shadows across the whole view |
| Height corrections | 0 … 8 | **6** (later 8, see Part 5) | Smallest number that passed the rule |
| Steepness key, maximum | 1.5× | **1.1×** | Above this, 6 corrections aren't accurate |
| Camera clearance | 1.5 m | **0.6 m** | Low views close to the water |
| Camera target depth | no limit | **0.5 m** below sea level | The view can't sink into the water |

## What I do differently now

- **Check the maths before drawing it.** The buoys matched the theory before any shader existed.
- **Count time, not frames.** Anything stored per frame changes with the computer.
- **Decide what "good enough" means before measuring.** That is how I chose 6 corrections, rather than a number that just felt right.
- **Test the way a person uses it.** My automated test set the camera position directly, so it missed what happens when someone holds a key.

## Sources

Gerstner 1802; Fournier & Reeves 1986; Peachey 1986; Finch 2004 (GPU Gems, ch. 1); Schlick 1994. Unity Scripting API: `Time.deltaTime`, `Shader.SetGlobalFloat`. Unity Manual: "Script serialization".

**Next:** [Part 2]({{ '/blog/part-2/' | relative_url }}). The sea needs something to hit: a cliff.
