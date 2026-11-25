---
layout: post
title: "Part 2: Carving a sea cliff"
date: 2026-11-21
part: 2
permalink: /blog/part-2/
excerpt: "Now I'm building up the cliff: from a flat blue wall to a slate coast with rounded headlands."
---

*Now I'm building up the cliff. It went through six versions, from a flat blue wall to a slate coast with rounded headlands. How the cliff is built and shaded is in the report (§3.7).*

![Version 2 of the cliff from the game camera]({{ '/assets/part2/p0-baseline_1_game-camera.jpg' | relative_url }})
![The final cliff from the same camera]({{ '/assets/part2/p7-smooth_1_game-camera.jpg' | relative_url }})
*Same camera, same sea. Top: version 2, regular pillars like organ pipes. Bottom: version 6, slate rock with moss, rust and rounded headlands.*

## Version 1: A wall of layers

I chose a cliff wall instead of a single rock, because it matches the storm photos I used as references and gives the waves a long waterline to hit.

The cliff is a height field turned on its side. For every point along the coast (z) and every height (y), one function says how far the rock sticks out to sea (x), and the whole grid of points becomes one mesh when the scene loads, just like the water.

```csharp
// CliffGenerator.cs: one vertex per (along the coast, height)
float x = WallX(col, y, out int layer, out _, out int pillar);   // how far the rock sticks out
vertices[k] = new Vector3(x, y, col.z);
```
**In Unity:** a `Cliff` object with the `CliffGenerator` component and a `MeshCollider`. `Rock.mat` uses my own `Coastal/Rock` shader, because URP/Lit ignores vertex colours.
*Source: Perlin 1985, 2002 (noise); Unity Scripting API, `Mesh.SetVertices`*

**Problem (look):** from the game camera, the cliff was flat and bluish-grey, although I had coloured its layers brown and tan.

![Cliff face lit only by the sky]({{ '/assets/part2/s2-1_cliff_game-camera.jpg' | relative_url }})
*Version 1: flat and blue.*

The reason was the sun from Part 1: it sat *behind* the cliff, so the face only received blue light from the sky. I moved it low over the sea instead, and now every ledge casts a shadow on the layer below it.
**In Unity:** the `Sun` light's rotation, from (22°, −35°, 0) to (14°, 160°, 0).

## Version 2: One rock, thin layers, dark cracks

Next I put the cliff next to a photo of a real slate cliff. The real rock is one colour in many shades, its layers are thin, and vertical cracks cut the face into pillars. The cracks look dark simply because light can't reach into them.

Version 2 follows that photo: one slate colour, thin layers, vertical joints and a few sea caves. To darken the cracks and caves, each vertex stores how open it is to the sky (*baked ambient occlusion*), and the shader dims the light there.
**In Unity:** I also added a **Debug view** drop-down to `Rock.mat` (*Albedo*, *Sun only*, *Sky only*, *Ambient occlusion*) to look at one effect at a time.

**Problem (code):** the very first debug view I opened showed the next bug.

![Albedo view of version 2: pale pinkish beige]({{ '/assets/part2/s2-1_v2_debug_1_albedo.jpg' | relative_url }})
*I picked dark slate. The albedo view showed pale pinkish beige.*

The project renders in Linear colour space, but Unity passes vertex colours to the shader without converting them. A slate of 0.30 (sRGB) therefore arrived as about 0.58, twice as bright as I meant.

```diff
  // CliffGenerator.cs
- colors[k] = ColorAt(col, y, layer, pillar);
+ // Picked as sRGB; Unity passes vertex colours unconverted and we render in Linear.
+ colors[k] = ColorAt(col, y, layer, pillar).linear;
```
*Source: Unity Manual, "Linear or gamma workflow"; Scripting API, `Color.linear`*

**Problem (look):** even with the right colour, version 2 looked like organ pipes: the joints were too evenly spaced.

![Version 2 with pillars and sea caves]({{ '/assets/part2/s2-1_v2_cliff_game-camera.jpg' | relative_url }})
*Version 2: low sun, one slate colour, joints and caves, but too regular.*

## Version 3: Make it fast first

Version 2 took **2,260 ms** to build, and it rebuilt every time I changed a value in the Inspector, which made trying things slow. Before adding more detail, I made the build faster. Noise that only depends on the position along the coast is now computed once per column, the rows are built in parallel, and the collider only covers the waterline band, where the wave probes of Part 3 will look.

```csharp
// CliffGenerator.cs: each row writes only its own slots, so rows can run in parallel
Parallel.For(0, nz, i => columns[i] = Column(ColumnZ(i)));
Parallel.For(0, ny, j => { /* build row j */ });
```
*Source: .NET API, `Parallel.For`*

**Result:** **401 ms**, 5.6 times faster, with exactly the same cliff.

## Version 4: Irregular stones, an overhang and moss

With a fast build, I went back to the reference photos and took three more things from them. The regular joints became irregular stones (Voronoi cells) with cracks along their borders. The top of the cliff now juts out, while the waves have cut the base back. And a green, wet zone of moss reaches up to where the waves splash.

The mesh is too coarse for the small ridges you see on real stone. Those are added in the shader as a detail normal: the light sees them, but the mesh doesn't need any extra vertices.
**In Unity:** on `Rock.mat`, *Detail Height* 0.05 m, *Fade-out Distance* 60 m.
*Source: Worley 1996; Quilez, "Value noise derivatives"*

**Problem (code):** after adding the moss, the cliff looked exactly the same. My eye couldn't tell me why, so I measured the average vertex colour at each height instead. The amount of green was **0 at every height**.

The cause was a function with a familiar name. Unity's `Mathf.SmoothStep(from, to, t)` is not the same as the shader's `smoothstep(edge0, edge1, x)`: it blends *between* `from` and `to`. I had used it the shader way in all four colour masks, so they had been wrong since version 1.

```diff
  // CliffGenerator.cs: GLSL/HLSL-style smoothstep, written once and reused
+ static float Smooth(float edge0, float edge1, float x)
+     => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(edge0, edge1, x));
- float rust = Mathf.SmoothStep(0.48f, 0.62f, streaks) * …
+ float rust = Smooth(0.48f, 0.62f, streaks) * Smooth(0.45f, 0.6f, patches) * rustAmount;
```
*Source: Unity Scripting API, `Mathf.SmoothStep`; Microsoft HLSL reference, `smoothstep`*

After the fix, the moss appeared right in the splash zone. The rust, which uses the same kind of mask, was suddenly too strong, so I turned it down.

## Version 5: Fewer, bigger shapes

**Problem (look):** seen from above, the coastline had turned spiky. With noise added at every scale, the outline looked like a saw blade.

An aerial photo of a real cliff coast showed what was missing: a few big, rounded headlands with curved bays between them. I removed two noise layers and placed five rounded headlands instead. The fine detail didn't disappear; it moved from the outline to the surface, in the stones, cracks, moss and rust.

![Top view of the coastline: spiky outline vs headlands and bays]({{ '/assets/part2/compare_r4_4_top-outline.jpg' | relative_url }})
*From above. Left: spiky outline. Right: five headlands with bays between them.*

## Version 6: Smooth joins

**Problem (look):** the headland tips still poked out. Each bay was a circle, and where two circles meet they form a sharp point. I changed the bay shape to end flat.

```diff
  // CliffGenerator.cs: bay shape between two headlands
- float bay = bayDepth * Mathf.Sqrt(1f - v * v);       // circle: vertical slope at the ends
+ float bay = bayDepth * Mathf.Pow(1f - v * v, 1.5f);  // round in the middle, flat at the ends
```

![Top view of the coastline: pointed tips vs rounded headlands]({{ '/assets/part2/compare_r5_4_top-outline.jpg' | relative_url }})
*Left: pointed headland tips. Right: rounded headlands and smooth bays.*

![Four debug views of the cliff in a grid]({{ '/assets/part2/p7-smooth_6_debug_grid.jpg' | relative_url }})
*The final cliff in the four debug views: albedo, sun only, sky only and ambient occlusion.*

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Sun rotation | (22°, −35°) | **(14°, 160°)** | Light came from behind the cliff |
| Grid (along × up) | 640 × 280 | **800 × 360** | Thin layers need finer rows |
| Layer thickness | 0.7–3.8 m | **0.25–0.9 m** | Thick bands looked like stripes of paint |
| Deep clefts | 25 % | **8 %** | Too many dark slots |
| Rifts in the coastline | 12 | **0** | Spiky from above |
| Stone relief | ±1.6 m | **±1.0 m** | Stones stuck out too far |
| `rustAmount` | 0.75 | **0.4** | Bright orange once the mask worked |
| `junctionRounding` | 4 m | **6 m** | Softer joins between headlands |
| Fine ridges | in the mesh | **detail normal** | No extra vertices |

## What I do differently now

- **Look at one effect at a time.** The albedo view showed the colour bug at once.
- **Measure when my eye can't tell.** A number per height found a bug that had been there since version 1.
- **Make it fast before adding detail.** A 401 ms rebuild is what made the later versions easy to try.
- **Keep big shapes simple and put the detail on the surface.** Less noise in the outline gave a more believable coast.

## Sources

**Photos I studied** (linked, not reproduced): R1 storm wave on a layered cliff *(123RF)*; R2 [dark slate cliff](https://st.focusedcollection.com/13735766/i/650/focused_167580008-stock-photo-waves-crashing-into-rock-cliffs.jpg) *(Focused Collection)*; R3 overhanging cliff at sunset *(Dreamstime)*; R4 [fractured coast, Cascais](https://stock.adobe.com/dk/images/drone-aerial-top-view-of-sea-waves-hitting-rocks-on-the-beach-with-turquoise-sea-water-amazing-rock-cliff-seascape-in-the-portuguese-coastline-cascais/250549204) *(Adobe Stock)*; R5 jointed granite *(stock image)*; R6 [Bunda Cliffs](https://www.gettyimages.com/detail/photo/aerial-view-of-the-bunda-cliffs-nullarbor-plains-royalty-free-image/1182599352) *(Getty Images)*.

**Techniques:** Perlin noise and fBm *(Perlin 1985, 2002)*; cellular stones *(Worley 1996; The Book of Shaders, ch. 12)*; crack borders, domain warping, smooth maximum and noise derivatives *(Quilez)*; ridged noise *(Musgrave, in Ebert et al. 2003)*. Why a height field can't do true overhangs: *Dey et al. 2017; Paris et al. 2019; Galin et al. 2009*.

**Next:** [Part 3]({{ '/blog/part-3/' | relative_url }}). Waves still pass through the cliff. Next, the sea learns where the rock is.
