---
layout: post
title: "Part 9: Finishing and measuring"
date: 2026-11-22
part: 9
permalink: /blog/part-9/
excerpt: "The last part: finishing the scene and measuring how fast it runs."
---

*The last part is about finishing the scene and measuring it. I fixed the ground on top of the cliff, took the pictures for the report, measured how fast the finished scene runs, checked my report against what I had actually measured, and gave the scene a sky with clouds. Six steps, and one idea left for later. The results are in the report (§5).*

![A wave hitting the cliff foot: white water, spray and foam lace]({{ '/assets/part9/s5-6_seq_t1.0s_hero.jpg' | relative_url }})
*The report's hero shot: one second after a strong hit (intensity 0.87), from a fixed, repeatable moment of the simulation.*

## Step 1: Flat ground on the cliff top

Looking at the cliff from above for the report, I noticed long straight ridges running inland from the edge.

![Cliff top from above: long parallel stripes running inland from the edge]({{ '/assets/part9/s5-6_top_before.jpg' | relative_url }})
*Before: every notch of the cliff edge was copied straight inland for 160 m.*

The ground's height depended only on the position along the coast, so every bump of the edge repeated all the way back. Now the ground blends from the edge into its own gently rolling surface over 8 m:

```csharp
// CliffGenerator.cs: blend from the edge (w = 0) to rolling ground (w = 1) over topBlend = 8 m
float w = Mathf.SmoothStep(0f, 1f, (xLine - edgeX) / Mathf.Max(0.01f, topBlend));
float x = Mathf.Lerp(xLine, xGrid, w);
float y = Mathf.Lerp(columns[i].top, GroundAt(x, z), w);   // GroundAt depends on x AND z
```

**Problem (numbers):** the pass mark I had set, the height difference between neighbouring columns, was already passing *before* the change. The columns are only 0.2–0.5 m apart, and the ridges are smooth at that spacing. The useful number was the roughness 20 m inland, once the slow rise and fall is removed: 0.595 m before, 0.250 m after.

**Problem (code):** a fan of streaks remained for 15–30 m behind the edge.

![Cliff top after the first fix: smooth far away, a fan of streaks near the edge]({{ '/assets/part9/s5-6_top_after.jpg' | relative_url }})
*First try: smooth far inland, but streaks fanning out behind the edge.*

Switching off the shadows didn't remove them. My straight rows started 1 m behind the edge point furthest inland *on the whole coast*, but behind the headlands the edge is 48 m further out. A few rows had to stretch across that gap, making long thin triangles. Now each stretch of coast starts its rows from a smoothed copy of its *own* edge.

![Cliff top after the second fix: smooth and rolling up to the edge]({{ '/assets/part9/s5-6_top_after2.jpg' | relative_url }})
*Second try: smooth, rolling ground up to the edge.*

## Step 2: Smooth right up to the edge

**Problem (look):** from a high view, the green ground still bent down at the lip in a row of rounded lobes with sharp points between them, hanging over the rock like a tablecloth.

![High view: the ground at the cliff lip bends down in rounded lobes]({{ '/assets/part9/s5-8_before_skirt.jpg' | relative_url }})
*Before: lobes and points along the lip.*

Before changing anything, I named the cause in the picture. The edge height still had the sharp, ridged notches from Part 2: round dips and sharp peaks about 17 m apart, exactly the lobe-and-point shape, and the ground dipped into every notch. I took the notches out of the edge height:

```diff
  // CliffGenerator.cs: the edge height of each column
- top = TopAt(z),       // slow rise and fall + sharp ridged notches
+ top = SlowTopAt(z),   // slow rise and fall only; the rock face below keeps its pillars and clefts
```

**Result:** the lip and the ground 8 m inland now differ by 0.38 m on average (was 1.0 m), with no gaps and no folds. The skyline seen from the sea loses its small notches, but the rock face below still breaks the edge up.

![Close-up of the lip: flat ground running to a clean rock edge]({{ '/assets/part9/s5-8_lip_after.jpg' | relative_url }})
*After: ground running flat to a clean edge, with the rock's pillars and clefts below.*

## Step 3: Pictures I can take again

The report needs pictures of exactly the right moment, a strong hit seen from the right place, and waiting for one in Play mode is luck. So the captures are repeatable. The scene restarts, steps the clock to t = 50 s in fixed steps, and steps on until the detector reports a hit of intensity 0.7 or more in front of the camera. It then renders from named camera positions at 1920 × 1080, without the overlay, the foam preview or the debug buoys.
**In Unity:** the camera positions are orbit settings (target, yaw, pitch, distance); spray is advanced with `ParticleSystem.Simulate` at the same step as the clock.

**Problem (look):** two of my planned views showed the edge of the world, with sky and the ground plane beyond the 400 m of water. I dropped them. I also dropped a reflection on/off pair: in a still frame the two looked the same, and the backwash plot from Part 8 carries that claim instead.

## Step 4: How fast does it run?

The Editor's frame rate is noisy. I measured a **built macOS player**. Started with `-benchmark`, it runs each water-grid size for a 15 s warm-up, records 30 s of frame times, writes them to a file and quits.

**Problem (build):** the first run froze as soon as the player window wasn't in front, because the project doesn't run in the background by default. The benchmark now turns that on for itself only (`Application.runInBackground = true`).
*Source: Unity Scripting API, `Application.runInBackground`*

**Result:** on an Apple M1 Pro, at 1920 × 1080 with vsync off:

| Water grid | Vertices | Mean frame | 95th percentile | FPS |
|---|---|---|---|---|
| 128 × 128 | 16,641 | 3.45 ms | 3.76 ms | 290 |
| 256 × 256 | 66,049 | 3.75 ms | 4.05 ms | 266 |
| **512 × 512 (used)** | 263,169 | **4.77 ms** | **5.10 ms** | **210** |
| 1024 × 1024 | 1,050,625 | 7.90 ms | 8.47 ms | 127 |

Frame times are steady (the 95th percentile is within 7 % of the mean), and 64 times more water vertices cost only 4.5 ms. The wave-break detector (159 probes) takes about 1 ms per frame in the player.

## Step 5: Checking my own report

I read my report draft against what this blog's measurements actually show, and found claims bigger than my tests.

**Problem (numbers):**
- "Any frame rate": I only tested 10, 30 and about 330 fps, so it now says that.
- "Deep water at the cliff": at 16.5 m (Part 7), the 70 m wave isn't strictly in deep water; a depth-aware model would make it about 5 % slower. The report says so.
- "Six corrections are enough": true out at sea, but the code uses 8 because of the wall (Part 5).
- One figure compared foam lifetimes, but its shots had been taken at different gains and before the lace (Part 8). I re-took all three on the current scene, changing only the lifetime.

## Step 6: A sky with clouds

I moved the main camera up to the height of the cliff top. From there much more sky is in view, and Unity's default sky is a plain gradient with nothing in it.

![The default sky above the cliff: a plain gradient]({{ '/assets/part9/s5-9_lookup_before.jpg' | relative_url }})
*Before: Unity's default sky, an empty gradient.*

I wrote my own sky shader in three layers: a gradient with the *same* colours as the sky the water reflects, which keeps the sea matched to the sky above it; a sun disc with a soft glow; and drifting clouds from fBm noise on a flat, imaginary cloud layer.

```hlsl
// Sky.shader: where this view ray meets a flat cloud layer, and how much cloud is there
float2 uv = dir.xz / max(dir.y, 0.05) * _CloudScale + _CloudSpeed.xy * _Time.y;
float n = Fbm(uv);
float density = smoothstep(1 - _CloudCover, 1 - _CloudCover + _CloudSoft, n);   // cover 0.45
```
**In Unity:** `Sky.mat` with the `Coastal/Sky` shader, set as the scene's skybox under *Lighting → Environment*. The ambient light was refreshed from the new sky.
*Source: fBm noise (Perlin 1985; Quilez, "fBm"); Unity Manual, "Skybox shaders"*

![The new sky with clouds above the cliff]({{ '/assets/part9/s5-9_lookup_after.jpg' | relative_url }})
*After: drifting clouds. Look just above the horizon: faint vertical streaks.*

**Problem (look), left as a known limitation:** just above the horizon, the clouds smear into thin vertical streaks. Near the horizon the division by `dir.y` would blow up, so it is clamped at 0.05. Below that, the cloud position stops changing with height and the noise gets stretched straight up, while my horizon fade still lets 30–40 % of the cloud through. Starting the fade where the clamp starts would remove the streaks; I haven't applied that fix.

**Problem (look):** from the saved camera, which looks 25° down at the cliff, only a thin strip of sky is visible, and that's where the clouds fade out. The clouds show clearly once the view tilts up.

## Left for later

One idea is written but not finished: **splash-back foam**, a small foam patch where the farthest spray droplet lands back on the sea. Those droplets fly without drag or wind, so their landing point can be worked out exactly at launch, with no need to track them. The code and a test exist, but I haven't run the test. The feature was switched off for every picture and benchmark above.

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Ground blend width | — | **8 m** | Edge detail dies out like a real cliff top |
| Ground rows start | 1 m behind the furthest edge point | **smoothed local edge + 0.5 m** | Streaks behind the headlands |
| Edge height | slow + sharp notches | **slow only** | Lobes hanging over the lip |
| Ridges pass mark | neighbour difference < 0.2 m | **roughness 20 m inland** | The first measure passed before the fix |
| Benchmark | Editor | **built player, 15 s warm-up + 30 s** | Editor numbers too noisy |
| Sky | Unity's default gradient | **own shader: gradient + sun + clouds** | An empty sky from the higher camera |
| Cloud cover / size | — | **0.45 / 1.2** | Separate clouds, not a haze or an overcast sky |
| Cloud fade at the horizon | from 0 | **from 0** (known limitation; fix would start it at the clamp, 0.05) | Clouds smear into streaks in the clamp band |

## What I do differently now

- **Check that a pass mark can fail before trusting it.** My first ridge measure passed on the broken version.
- **Make pictures repeatable.** A fixed time and a fixed camera beat waiting for luck.
- **Measure the build, not the Editor**, and keep the benchmark's settings to the benchmark.
- **Read my own claims against my own data.** Most problems were words like "any" and "always".

## Sources

Perlin 1985; Quilez, "fBm". Unity Scripting API: `Application.runInBackground`, `ParticleSystem.Simulate`, `Mathf.SmoothStep`. Unity Manual: "Skybox shaders".

*This is the last part of the series. Thanks for reading.*
