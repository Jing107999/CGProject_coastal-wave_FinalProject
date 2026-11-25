---
layout: post
title: "Part 6: Spray, wet rock, and when to borrow"
date: 2026-11-21
part: 6
permalink: /blog/part-6/
excerpt: "What happens on and above the rock: run-up, wet stone and spray."
---

*This part covers what happens on and above the rock: water running up the stone, the stone getting wet, and spray. I first built all of it myself. Two of my versions looked wrong, so I compared them with methods that already exist in Unity and borrowed what fit, with credit. This post covers six steps; the methods compared are in the report (§3.6).*

![Smoky spray at the foot of the cliff, sinking towards the water]({{ '/assets/part6/s4-6c_spray_falls_3s.jpg' | relative_url }})
*The result of this part: three seconds after a hit, the spray billows at the cliff foot and sinks back towards the water.*

## Step 1: Water running up the rock

When a wave hits a cliff, white water shoots up the face and drains back down. I gave the cliff face its own map, like the foam map but standing upright. Each impact launches a jet that climbs like a thrown ball, higher for stronger hits, and leaves white water on the rock it passes.

```csharp
// RunupSystem.cs: the jet's height this step (thrown upwards, slowed by gravity)
float t = Mathf.Min(u.age, tApex);                       // tApex = v0 / g: it stops at the top
float y = u.y0 + u.v0 * t - 0.5f * gravity * t * t;      // v0 = sqrt(2 g R), R = run-up height
bands[n] = new Vector4(u.z, u.lastY, y, u.strength);     // paint only what it climbed this step
```
**In Unity:** a `Runup` object with the `RunupSystem` component; the rock shader reads its map.
*Source: projectile motion under gravity; the run-up height R grows with the hit's intensity (2.5–18.5 m)*

**Problem (code, caught before the first run):** my first version painted each step's stretch with soft edges. At 300 fps the jet climbs only 6 cm per frame. Each spot would collect several overlapping edges, and more frames would mean more white. Now each step paints exactly the stretch from the last height to the new one, and the stretches join without overlap.

**Result:** the highest point reached was exactly 21.5 m (start 3 m + run-up 18.5 m), and the white area was the same at 300 fps and 30 fps.

![White water on the cliff face behind the impacts]({{ '/assets/part6/s4-4_runup-on.jpg' | relative_url }})
*Run-up on the rock, under a second after a hit.*

**Problem (look):** it read as a broad pale glaze over the stone, more like a reflection than water.

## Step 2: Spray droplets

Next, every impact fires a burst from one Unity particle system: up to 120 droplets thrown up and outwards, falling under gravity and scaled by the hit's intensity. The particles follow my clock, so they freeze when the simulation pauses.

**Problem (code):** droplets fell *through* the sea. They were hidden under the opaque water but still simulated and drawn, on average 7.7 m below the surface. A collision plane at sea level now removes each droplet when it reaches the water.

```csharp
// SprayEmitter.cs: droplets die when they reach the sea
collision.type = ParticleSystemCollisionType.Planes;
collision.SetPlane(0, sea);          // a plane at sea level
collision.lifetimeLoss = 1f;         // touching it ends the particle's life
```
*Source: Unity Manual, "Particle System Collision module"*

![Cliff foot with a few tiny white specks]({{ '/assets/part6/s4-5_spray_hit.jpg' | relative_url }})
*Problem (look): first try, 10–80 droplets of 0.12–1.3 m. From the demo camera, 60 m away, they're specks.*

![Cliff foot with visible white puffs of spray]({{ '/assets/part6/s4-5_spray_hit_v2.jpg' | relative_url }})
*Second try, 20–120 droplets of 0.25–2 m: visible puffs at the impacts, but still bubbles rather than spray.*

## Step 3: Wet rock, borrowed

The glaze from step 1 and the bubbly spray both looked built rather than natural. Before building more, I looked for existing methods. Unity's Shader Graph package ships a sample, "Production Ready Shaders", with a rock shader that gets wet in rain. Its *Wet* subgraph follows the physics: wet stone gets **darker** and **glossier** (Jensen, Legakis & Dorsey 1999).

I kept my run-up map, gave it a second channel for wetness that dries over 20 s, and ported Unity's formula into my rock shader:

```hlsl
// Rock.shader: darker and more saturated where wet (ported from Unity's Wet subgraph)
half3 wetColor = saturate(luma + 1.5 * (albedo - luma)) * 0.2;
albedo = lerp(albedo, wetColor, saturate(_Permeability * wetness));   // permeability 0.6
```
I also ported its drip effect, thin white streaks running down the wet stone. Part 8 explains why I removed it again.

**In Unity:** I imported the sample from the Package Manager to study it. The four textures I use were copied into `Assets/Textures/ThirdParty/` with a README stating source and licence.
*Source: Unity Technologies, Shader Graph "Production Ready Shaders" sample (Unity Companion License); Jensen, Legakis & Dorsey 1999*

**Problem (build):** importing the sample also pulled in TextMesh Pro, which the project doesn't use. Both stay out of git, and the project builds without them.

**Result:** after a single jet, the wetness faded exactly as expected for a 20 s drying time.

![Cliff face darker where the waves reached, with spray droplets]({{ '/assets/part6/s4-6a_wet-rock.jpg' | relative_url }})
*The glaze is gone. Where the water ran up, the stone is darker and slightly glossy.*

## Step 4: Smoky spray, in layers

From a distance, real spray from a big impact looks like smoke. The Shader Graph sample's spray shader animates a fixed mesh, like a waterfall, so it can't react to my impacts. I kept its textures and used URP's built-in *Particles/Unlit* shader, with soft edges where puffs touch rock or water. Each impact now fires four layers:

| Layer | Per hit | Size | Lives | Moves |
|---|---|---|---|---|
| Droplets | 10–60 | 0.1–0.45 m | 1–2 s | thrown, falls |
| Splash | 4–24 | 1–3.5 m | 0.6–1.3 s | animated churn texture |
| Plume | 3–14 | 3–7 m | 3–5 s | billows, slowed by air |
| Mist | 1–6 | 8–16 m | 6–10 s | drifts with the wind |

**Problem (look):** the splash drew as opaque grey squares. The churn texture is greyscale with no transparency channel, and Unity's sample uses it as a mask. Importing it with *Alpha Source: From Gray Scale* fixed it.
*Source: Unity Manual, "Texture import settings"; URP "Particles Unlit shader"*

![Smoky plumes rising up the cliff]({{ '/assets/part6/s4-6b_spray_b.jpg' | relative_url }})
*Smoky plumes 2.4 s after a hit. They look right in a still, but they were rising.*

## Step 5: Spray falls like water

In motion, the plume and mist rose like hot air. But spray is water: it is thrown up, slowed by gravity and falls back, with the air holding fine droplets to a slow fall.

**Problem (numbers):** the rising came from my own settings. The plume had only 15 % of gravity, and the mist had *negative* gravity and an upward wind. Now the plume has full gravity, its air drag eases from 12 m/s (the launch gets through) to 2.5 m/s (a slow fall), and the wind is horizontal only.

```csharp
// SprayEmitter.cs: the plume layer
gravity = 1f, alpha = 0.45f, drag = 0.35f, dragLimit = 2.5f, dragLimitStart = 12f,   // launch through, then fall slowly
```

**Problem (code):** after that, the mist disappeared completely. The sea-level plane from step 2 uses each particle's *size*, so a 10 m mist puff launched at 3 m "touched" the sea at once and died. Particles now collide by their centre (`collision.radiusScale = 0.001f`).

**Result:** plumes now rise to 7–9 m and then fall at about 3 m/s, and mist settles at about 1 m/s.

![Spray one second after a hit, billowing at the cliff foot]({{ '/assets/part6/s4-6c_spray_falls_1s.jpg' | relative_url }})
*One second after a hit. The top picture shows the same spray two seconds later, sinking.*

## Step 6: Testing Unity's drag

The fall depends on Unity's air-drag setting. If Unity applied it once per frame, the spray would fall differently at 30 fps (my video) than at 300 fps (the Editor). I tested it before relying on it.

I fixed the rule first. One particle is launched straight up and stepped by hand at 1/10 to 1/240 s per frame. The highest point must be within 5 % of the ballistic value, or the test itself is wrong, and the fall speed at 1/30 and 1/60 s must be within 10 % of 1/240 s.

**Result:** the high point was within 2.1 % of the ballistic value, and the fall speed at 30 fps differed from 240 fps by only 1.9 %. Unity scales the drag by the frame time, so the spray in a 30 fps video matches the Editor.

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Run-up band edges | soft, 0.3 m | **exact stretch per step** | Soft edges stacked up at high frame rates |
| Droplets per hit / size | 10–80, 0.12–1.3 m | **20–120, 0.25–2 m** (then 10–60 in layers) | Specks at 60 m |
| Rock on run-up | white albedo | **darker + glossier (wet)** | White read as a glaze |
| Wet drying time | — | **20 s** | Stone stays wet between hits |
| Plume gravity | 0.15 | **1**, drag limit 12 → 2.5 m/s | Spray rose like hot air |
| Mist gravity | −0.02, upward wind | **0.3**, horizontal wind | Water doesn't float up |
| Collision size | particle radius | **centre** (`radiusScale` 0.001) | Big mist puffs died at launch |

## What I do differently now

- **Look for existing methods before building more.** The wet-rock model and the soft particles already existed; my job was to fit them to my event-driven scene.
- **Credit what I borrow**, in the code, the project files and here.
- **When something looks wrong, ask what the physics says.** Spray is water, so it has to fall.
- **Test the engine too.** Unity's drag was fine, but I only know because I measured it.

## Sources

Jensen, Legakis & Dorsey 1999 (wet materials). Unity Technologies: Shader Graph "Production Ready Shaders" sample (Unity Companion License). Unity Manual: Particle System Collision module, Limit Velocity over Lifetime module, Texture import settings; URP Particles Unlit shader.

**Next:** [Part 7]({{ '/blog/part-7/' | relative_url }}). A seabed that nobody can see, and why I built it anyway.
