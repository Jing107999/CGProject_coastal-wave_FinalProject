---
layout: post
title: "Part 5: Making the sea notice the cliff"
date: 2026-11-21
part: 5
permalink: /blog/part-5/
excerpt: "Now I'm making the sea react to the cliff."
---

*Now I'm making the sea react to the cliff. After Part 4 the foam worked, but in places the water still looked as if it passed straight through the rock. My waves are formulas and don't know the cliff exists, and real fluid–rock coupling is far beyond real time. So in five steps I hide the seam and fake the reaction. The methods are in the report (§3.3–3.5).*

![Water at the cliff foot with a continuous foam band and white water where it meets the rock]({{ '/assets/part5/s4-7_every-hit_1.jpg' | relative_url }})
*The result of this part: a continuous band of foam along the whole coast, with spray where the water first meets the cliff.*

## Look first, then search

Before changing anything, I wrote down what looked wrong on screen and then read how others handle it. Real-time water usually keeps the analytic sea and fakes the contact with foam at the edge, foam on steep crests, reflected waves and spray. I did them in order of cost, smallest visible fix first.
*Related work: Tessendorf 2001, 2004 (iWave); Yuksel et al. 2007 (wave particles); Jeschke & Wojtan 2017 (wave packets)*

## Step 1: A white edge where water touches rock

The seam is the hard line where a crest cuts into the stone. Real water would foam there. I wanted a thin white band wherever the water is close to the rock.

**Problem (code):** the usual way is to compare the water's depth with the scene's depth buffer. But my water is opaque and writes its own depth, so that would just measure the water against itself.

The cliff is a height field (Part 2). I baked its shape into a small texture instead. The water shader looks up how far each pixel is from the rock at its own height:

```hlsl
// Water.shader: distance from this water pixel to the rock face, from the baked cliff map
float w  = SAMPLE_TEXTURE2D(_CliffFaceTex, sampler_CliffFaceTex, uv).r;   // rock x at this (z, y)
float slope = (w1 - w0) / (2.0 * dz);                                     // how the face turns along the coast
return (w - p.x) / sqrt(1.0 + slope * slope);                            // perpendicular distance
```
**In Unity:** a `CliffFaceMap` component bakes the map (2048 × 128 samples) once the cliff is built. On `Water.mat`, *Contact Foam Width* 0.9 m and *Strength* 0.85.

![Cliff foot without contact foam: a hard seam]({{ '/assets/part5/s4-1_demo-view_no-contact_crop.jpg' | relative_url }})
![Same frame with contact foam: a thin white edge along the rock]({{ '/assets/part5/s4-1_demo-view_contact-paused_crop.jpg' | relative_url }})
*Same paused frame. Top: the hard seam where water cuts into rock. Bottom: a thin white edge along the contact.*

## Step 2: Foam on breaking crests

Real crests start to foam before they reach the cliff. The usual signal is how squeezed the surface is (the Jacobian from Part 1): where it drops below a threshold, the crest foams.

```hlsl
// Water.shader: crest foam where the surface is squeezed
float crest = saturate((_CrestFoamBias - g.jacobian) * _CrestFoamStrength);
```

**Problem (numbers):** I started from the textbook limit, where a wave breaks at a height-to-length ratio of 0.142 (Michell 1893). That works out to J < 0.55. Measured on 40,000 points of my sea, it gave foam on **0.03 %** of the surface, which is invisible. My sea stays below the breaking limit by design, and real crests break *before* the limit anyway.

Instead, I calibrated it against real seas. My longest wave matches a wind of about 9 m/s, and at that wind about **0.7 %** of the ocean is covered in whitecaps (Monahan & O'Muircheartaigh 1980). J < 0.71 gives 0.6–0.7 %. As with components, the existing material kept the old 0.55, so I set 0.71 on `Water.mat` by hand.

![Steep crests in front of the cliff with soft white patches]({{ '/assets/part5/s4-2_crest-on.jpg' | relative_url }})
*Soft white patches on the steepest crests in front of the cliff; open water stays clean.*

## Step 3: Waves that bounce back

A real cliff reflects waves. I used the method of images: for each long wave heading into the cliff, I add a mirrored copy as if it came from behind the rock, at half the height (R = 0.5), fading out with distance. The cliff is treated as one flat mirror at x = 25 m.

```csharp
// WaveField.cs: the mirrored copy of a wave travelling into the wall
dx = c.dx - 2f * dn * n.x,                    // mirror the direction
a  = c.a * reflectionCoefficient,             // half the height
phase = c.phase + 2f * c.k * dn * wallOffset, // same phase as the incoming wave on the mirror
```
*Source: method of images for a vertical wall (Dean & Dalrymple 1991)*

**Problem (numbers):** reflecting all four waves longer than 15 m pushed the total steepness at the wall to **1.02**. Above 1 the surface can fold through itself, and the height query from Part 1 stops converging. Reflecting only the two longest (70 and 48 m) gives 0.912 and keeps most of the visible effect. Near the wall, the height query then needed 8 corrections instead of 6 to stay under 1 cm.

On a single test wave the wall behaved like a real rigid wall: the height there was (1 + R) times the wave, as expected.

**Mistake (my prediction):** I expected the taller water in front of the cliff to give more crest foam there. When I measured it, both near the wall and in the open sea had 0.42 %. At the wall the sideways motions cancel, so the surface gets taller but not more squeezed.

## Step 4: Every collision should spray

With reflection and spray in place (spray is Part 6), foam and spray appeared in the bays, but hardly at all where the water first hits the headlands. With waves this strong, every collision should splash.

**Problem (numbers):** I counted impacts per group of probes over 120 s:

| Probes | Threshold 3 m | Threshold 2 m |
|---|---|---|
| Headland tips | 12 impacts, 16 of 25 probes with none | **153**, none without |
| Middle | 297 | 598 |
| Bay backs | 21, 10 of 25 with none | **187**, none without |

There were two causes. The flat mirror puts the tallest water at x = 25 m, while the headland tips sit 10–17 m in front of it, where the reflected 70 m wave *lowers* the crests. And a 3 m threshold only counts the biggest waves. I lowered the threshold to 2 m and widened *Intensity Range* to 3 m: small hits now make small splashes, and the biggest make full plumes.

A real coast is the other way round: headlands get the *most* energy, because waves bend towards them over a shallowing seabed. My deep-water sea can't do that, and the report lists it as a limitation.

## Step 5: Foam that travels with the waves

My crest foam switched on and off as each crest passed, while real whitecaps leave trails. Now the foam map from Part 4 also *collects* crest foam each frame. There it fades and spreads like impact foam, and the whole map drifts slowly.

```hlsl
// FoamUpdate.shader: steep crests feed the foam map, so they leave trails
float J = EvaluateGerstner(rest, _SimTime).jacobian;
foam += _WhitecapAmount * saturate(_WhitecapBias - J);   // _WhitecapAmount = rate * dt
```
*Source: Dupuy & Bruneton 2012 (whitecaps accumulated over time)*

**Problem (numbers):** with a rate of 1.6 per second, trails covered **8.1 %** of the coastal water, a white sea. Letting foam persist multiplies the 0.7 % of instant crest foam roughly tenfold. A rate of 0.2 per second gives about 1 %.

![Foam-map debug view: a network of faint trails over the coastal water]({{ '/assets/part5/s4-8_trails_foam-map-view.jpg' | relative_url }})
*The foam map (debug view, key F): a faint network of trails over the coastal water, on top of the impact foam at the cliff.*

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Cliff map resolution | 1024 × 64 | **2048 × 128** | Mean distance error 0.26 → 0.17 m |
| Crest foam threshold (J) | 0.55 (textbook) | **0.71** | 0.03 % → 0.7 % of the sea, as in real seas at 9 m/s wind |
| Waves reflected | all ≥ 15 m | **≥ 40 m** (two waves), R = 0.5 | Steepness at the wall 1.02 → 0.912 |
| Height corrections | 6 | **8** | Near the wall 6 gave errors up to 29 mm |
| Break threshold | 3 m | **2 m** | Headland tips had almost no impacts |
| *Intensity Range* | 0.9 m | **3 m** | 2 m hits splash softly, 5 m hits fully |
| Whitecap rate | 1.6 /s | **0.2 /s** | 8.1 % → ~1 % of the coastal water |

## What I do differently now

- **Look first, then search.** Writing down what looked wrong before reading about fixes kept me from building things I didn't need.
- **Treat textbook values as a starting point.** 0.55 is correct physics, and invisible in my sea.
- **Measure predictions, especially the ones I like.** Reflection didn't change the crest foam, and a single mirror moved the impacts away from the headlands.

## Sources

Michell 1893; Monahan & O'Muircheartaigh 1980; Dean & Dalrymple 1991; Tessendorf 2001, 2004; Dupuy & Bruneton 2012; Yuksel, House & Keyser 2007; Jeschke & Wojtan 2017.

**Next:** [Part 6]({{ '/blog/part-6/' | relative_url }}). Water running up the rock, wet stone and spray.
