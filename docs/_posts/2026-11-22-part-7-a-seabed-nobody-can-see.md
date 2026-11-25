---
layout: post
title: "Part 7: A seabed nobody can see"
date: 2026-11-22
part: 7
permalink: /blog/part-7/
excerpt: "Next comes a seabed under the water, which turned out to be invisible."
---

*Next comes a seabed under the water. It turned out to be invisible, and I built it anyway. While checking it, I found a much bigger problem: half the coast had been ignored since Part 3. Four steps; the deep-water argument is in the report (§3.1).*

![Spray and foam at the far north end of the cliff]({{ '/assets/part7/s4-10b_north-end_play.jpg' | relative_url }})
*The result of this part: spray and foam at the north end of the coast, 170 m from the middle, where nothing had ever happened before.*

## Step 1: Deciding what kind of seabed

Before writing any code, I checked two things.

**First, would anyone see it?** My water is opaque, so a seabed under it is hidden. To see sand, the water would have to become see-through, and even then light fades quickly in sea water: about 37 % of it gets through 4.5 m of water, and 5 % through 14 m.

**Second, does it fit my waves?** Since Part 1, my waves follow the *deep-water* rules, where their speed doesn't depend on depth. That is only true where the water is deeper than half a wavelength, 35 m for my 70 m swell. Over a shallow seabed, real waves slow down, steepen and break, and my model does none of that.

| Depth at the cliff foot | Speed error, 70 m wave | 48 m wave | Shorter waves |
|---|---|---|---|
| 35 m | 0.2 % | 0 | 0 |
| 17 m | 4.6 % | 1.2 % | ≤ 0.2 % |
| 14 m | 7.8 % | 2.5 % | ≤ 0.5 % |

A seabed shallow enough to see would contradict my waves, and one that fits my waves is too deep to see. I chose the deep one: a cliff plunging into deep water, consistent with the rest of the model (Sunamura 1992: deep water at a cliff foot gives standing waves, not breakers).

## Step 2: Building the seabed

The seabed is a 400 × 400 m grid (2 m cells), built in code when the scene starts, like the water. It lies at −35 m out at sea and rises to −16.5 m at the cliff foot. The rise starts from the cliff's own outline, so it follows the headlands and bays.

```csharp
// SeabedGenerator.cs: deep out at sea, rising towards the cliff
float d = Mathf.Max(faceX - x, 0f);                                         // distance from the rock
float h = deepLevel + (footLevel - deepLevel) * Mathf.Exp(-d / shelfWidth);  // -35 m -> -16.5 m
return h + detail * n;                                                       // +/- 1 m of noise
```
**In Unity:** a `Seabed` object with the `SeabedGenerator` component. `Seabed.mat` reuses my `Coastal/Rock` shader with sand-to-pebble vertex colours.

The cliff's bottom edge is at −18 m, so the seabed must cover it everywhere. After building, the generator measures how deeply that edge is buried at 401 points along the coast and warns if any is exposed.

**Problem (code):** the check failed at **10 of 401 points**, by up to 0.66 m. I had measured the shelf from the cliff face at −16.5 m, but in places the cliff's *base* juts out up to 3.7 m further, where the seabed had already started to drop.

```diff
  // SeabedGenerator.cs: where the shelf starts
- public float FaceX(float z) => cliff.WallX(z, footLevel);
+ // whichever sticks out further: the face at the shelf depth or at the cliff's bottom edge
+ public float FaceX(float z) => Mathf.Min(cliff.WallX(z, footLevel), cliff.WallX(z, cliff.bottom));
```

**Result:** buried everywhere, by at least 0.50 m. The gap would have been invisible from almost any angle, and under opaque water anyway; only the built-in check found it.

![Seabed meeting the cliff foot with no gap, water hidden]({{ '/assets/part7/s4-10_seabed_cliff-foot.jpg' | relative_url }})
*With the water switched off for the picture: the floor meets the rock with no gap, and the cliff's shadow falls on it.*

![Seabed overview following the headlands and bays]({{ '/assets/part7/s4-10_seabed_overview.jpg' | relative_url }})
*From above (water off): the shelf follows the headlands and bays.*

## Step 3: The white rectangle

**Problem (look):** in an Edit-mode screenshot after a Play session, a white rectangle sat on the water in front of the middle of the cliff. It looked as if the seabed only covered part of the terrain.

![Water with a large flat white rectangle in front of the cliff]({{ '/assets/part7/s4-10_with-water_same-view.jpg' | relative_url }})
*Edit mode after Play: a white rectangle on the water. It isn't the seabed.*

It was the foam map's area. When Play mode stops, the foam map is freed, but the global value that tells the water *where* the map is stayed set. In Edit mode the water then read a texture that no longer existed and turned white. I had seen this once before, noted it as "not investigated" and moved on. The note was right about the cause but wrong to leave it, because it misled the next look at the scene.

```diff
  // FoamSystem.cs: OnDisable
  Release();
+ // Tell shaders "no foam map", or the water keeps sampling the freed map in Edit mode.
+ Shader.SetGlobalVector(FoamRectId, Vector4.zero);
```

## Step 4: Half the coast had been ignored

The screenshot also showed something real: the ends of the coast never got impacts. The cliff and the water run 400 m along the coast, from −200 to +200 m, but every interaction had been sized for the demo view in the middle:

| System | Covered | Since |
|---|---|---|
| Wave-break probes | −120 … +120 m | Part 3 |
| Foam map | −130 … +130 m | Part 4 |
| Contact-foam map, run-up | −130 … +130 m | Parts 5, 6 |
| Cliff, water, seabed | −200 … +200 m | — |

No measurement caught it, because every count only looked at the probes that existed. I extended all four to the full coast at the same detail per metre: 159 probes instead of 97, and longer maps.

**Problem (build):** my scene-setup script would rebuild these objects from scratch and cut the links from foam, run-up and spray to the detector. Instead I added an editor menu item that copies only the new values onto the existing objects. The scene's diff showed only the intended changes.

**Result:** impacts per probe over 128 s:

| Along the coast | −200…−130 (new) | −130…−65 | −65…0 | 0…65 | 65…130 | 130…200 (new) |
|---|---|---|---|---|---|---|
| Impacts per probe | **12.5** | 11.5 | 10.4 | 9.3 | 11.3 | **11.6** |

The ends now get as many hits as the middle. The detector costs 0.90 ms instead of 0.57 ms, with no change in frame rate.

![The same view after Play mode: no white rectangle, foam along the whole coast]({{ '/assets/part7/s4-10b_edit-mode_after-play.jpg' | relative_url }})
*After both fixes: no white rectangle, and foam along the whole coast.*

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Depth out at sea | −22 m (considered) | **−35 m** | Deep water for all my waves (0.2 % speed error) |
| Depth at the cliff foot | −14 m (considered) | **−16.5 m** | 70 m wave off by < 5 %, others ≈ 0 |
| Seabed noise | ±1.5 m (considered) | **±1 m** | Keeps the cliff's −18 m edge buried |
| Shelf start | face at −16.5 m | **face at −16.5 m or at the base, whichever is further out** | 10 points were exposed |
| Probes along the coast | −120…+120 m (97) | **−197.5…+197.5 m (159)** | Ends had no impacts |
| Foam, contact and run-up maps | −130…+130 m | **−200…+200 m**, same detail per metre | Same reason |

## What I do differently now

- **Ask whether a feature will be seen, and whether it fits the model, before building it.** Here the answers were "barely" and "only if deep", and that decided the design.
- **Build checks into generators.** A gap under opaque water is invisible; a number isn't.
- **Don't leave "not investigated" notes lying around.** An unexplained artefact will mislead the next person who looks, including me.
- **Re-check old limits when the scene grows.** The probes were sized for a 240 m view, and the cliff grew to 400 m without me noticing.

## Sources

Sunamura 1992 (rocky coasts); deep-water dispersion, ω² = gk·tanh(kh) (Dean & Dalrymple 1991). Unity Scripting API: `Shader.SetGlobalVector`.

**Next:** [Part 8]({{ '/blog/part-8/' | relative_url }}). Water that turns back after hitting the cliff, and foam that finally rides on it.
