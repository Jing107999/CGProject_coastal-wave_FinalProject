---
layout: post
title: "Part 8: The water that turns back"
date: 2026-11-22
part: 8
permalink: /blog/part-8/
excerpt: "Now I'm measuring the water that flows back from the cliff, and fixing how the foam looks."
---

*Now I'm measuring the water that flows back out to sea after it hits the cliff, and fixing how the foam looks. The measurement matched the theory to a tenth of a percent, but the foam had more problems: it sat still while the water moved, it hardly showed and then showed too much, and it left the cliff foot bare between waves. Six steps, including one fix that removed the wrong thing. The measurement is in the report (§3.3, §5).*

![Foam at the cliff foot from above: dense at the rock, breaking into lace further out]({{ '/assets/part8/s5-4_high_b_value.jpg' | relative_url }})
*The result of this part: foam dense at the rock, breaking into lace further out, and lace on the crests of incoming waves.*

## Step 1: Measuring the water that turns back

Since Part 5 my cliff reflects waves: every long wave heading for the rock gets a mirrored copy travelling back out at half its height. I wanted a number for this *backwash*.

There is an honest limit here. My waves come from a formula, and nothing in the scene pushes water against the rock and lets it bounce. So I can't *discover* how real backwash behaves; I can only check that my code produces exactly the motion the theory predicts. That is a verification, and the report calls it one.

I worked out the expected speed profile by hand and fixed a pass mark of **2 %** before running. An editor experiment then sampled the water's speed towards and away from the rock every metre, 0–120 m out, over 20 simulated minutes. It ran three cases: my cliff, the same sea with reflection off (a control that must come out flat), and a textbook single wave against a perfect wall.
**In Unity:** *Coastal → Experiments → E3 Backwash Velocity* (Play mode only; it reads values and changes nothing).

![Back-and-forth water speed against distance from the cliff, three cases, measured dots on theory lines]({{ '/assets/part8/e3_backwash_profile.jpg' | relative_url }})
*Dots: measured. Lines: theory. All three cases stay well inside the 2 % pass mark.*

**Surprise (numbers):** the water right at the cliff moves *less*, not more: 1.05 m/s, 38 % less than out at sea. The strongest back-and-forth flow is about 14 m out (2.18 m/s), where the reflected wave adds to the incoming one.

## Step 2: Foam that rides the water

With those numbers in mind, I watched the cliff foot again.

**Problem (look):** the foam didn't move with the water. A white patch stayed on one spot while waves slid underneath it, like a sticker on glass, with the water sloshing up to 2 m/s right there.

The foam map from Part 4 lies flat over the world, and the water shader looked it up at each pixel's *world* position. But the water mesh already knows where each pixel's water would sit on a calm sea (its *rest* position). If the map is indexed by that, a patch stays on the same water and goes wherever the waves move it. It needs no extra simulation step, and it is essentially one line:

```diff
  // Water.shader: look the foam up on the water, not on the world
- float foam = SampleFoam(input.positionWS.xz);   // this point of the world
+ float foam = SampleFoam(input.restXZ);          // this bit of water, wherever the waves move it
```
*Source: Lagrangian vs Eulerian description of a fluid; the alternative, moving the map every frame (Stam 1999), would have blurred it*

**Problem (numbers):** the impact foam still landed, faded and spread exactly as before, but the whitecap check failed: cover jumped from the calibrated 1 % to 1.7 %. That 1 % had been measured on the smaller foam map of Part 5. I first re-measured the old shader on today's map (0.96 %). The jump was real, and it has a physical reason: foam on a squeezed crest now spreads out again with the water as the crest passes. Lowering the whitecap rate from 0.2 to 0.12 per second brought it back to 1.01 %.

## Step 3: White dots on the rock

Flying the camera along the cliff, I noticed white specks on the wet rock, scattered over the moss like dandruff.

**Problem (look), and a wrong fix:** two effects could draw white specks there: the droplet layer of my spray, and the drips running down the wet rock from Part 6. I guessed droplets and removed that layer. Then I took a closer screenshot.

![Close-up of the cliff with rows of short white ticks on the wet rock]({{ '/assets/part8/s5-3_before_2.jpg' | relative_url }})
*Not round dots but short white ticks, all the same length, in rows about 2.9 m apart.*

That spacing is exactly how often the drip texture repeats on the rock. Each drip channel is only 2–3 cm wide, thinner than a pixel at that distance, so every moving drip head became a lone white tick. I put the droplets back and removed the drips. The darker, glossier wet stone still reads as wet on its own.

![The same kind of close-up after removing the drips]({{ '/assets/part8/s5-3_after_rock_close.jpg' | relative_url }})
*After: wet, dark stone, no ticks.*

## Step 4: Making the foam visible, but not too visible

Next I worked on how the foam is *drawn*, which took three rounds.

**Problem (look): a faint rectangle.** Up close, the foam map's straight seaward border showed through the water. Whitecap trails spread and fade into a thin, even film over the whole map, and that tinted the water grey right up to the map's edge. Now values below a floor draw nothing, and the map fades out over its last 15 m:

```hlsl
// Water.shader: the foam map as drawn (the map itself is unchanged)
float edge = smoothstep(0.0, _FoamEdgeFade, min(toEdge.x, toEdge.y));   // fade out near the border
return max(SampleFoam(worldXZ) - _FoamFloor, 0.0) * edge;               // floor 0.02 hides the film
```

**Problem (numbers): too faint.** The foam-map debug view showed the foam I wanted, but the shaded water hardly showed it. The drawing turned the map value into white 3 times over, while most real foam is under 0.1. I raised the gain from 3 to 12.

**Problem (numbers): too narrow, then too wide.** The foam was a thin band hugging the rock. I fixed a target first, visible foam reaching 8–15 m out from the rock, and wrote a small experiment to measure it. Bigger, stronger, longer-lived splats gave **19 m**, as a flat white band:

![A solid flat white band along the whole cliff foot]({{ '/assets/part8/s4-10e_high_after.jpg' | relative_url }})
*Too much: a flat band 19 m wide. It reads as paint or snow, not foam.*

I had predicted the lifetime would set the width, but a sweep showed it hardly does (18–20 m for lifetimes of 4–7 s). The splat *radius* does: 3 m gave **11 m**, inside the target.

The band was still flat white. Last, I broke it up with a noise pattern (three layers of ridged noise) that rides the waves and drifts with the foam map. Dense foam stays white, and as it thins, it tears into islands and streaks.

![Foam band at the cliff foot breaking into lace towards its outer edge]({{ '/assets/part8/s4-10f_high_patches.jpg' | relative_url }})
*Splat radius 3 m, with the noise pattern: dense at the rock, breaking into lace further out.*

## Step 5: The cliff foot went bare between waves

**Problem (look):** in photos of real cliffs, the water at the foot is white almost all the time. Mine flashed white at each impact and went back to dark water before the next wave arrived, and the cliff kept blinking.

The simplest idea was to make foam live longer. For a medium hit to stay white until the next one 10 s later, the lifetime needs to be about 9 s, and mine was 5 s. I measured 5, 10 and 15 s on the same scene:

| Lifetime | Cliff foot covered | Foam width | Out at sea |
|---|---|---|---|
| 5 s | 72 % | ≈ 13 m | clean |
| 10 s | 90 % | ≈ 15 m | faint haze starting |
| 15 s | 96 % | ≈ 16 m | haze visible |

![Close-up with lifetime 5 s: dark gaps at the cliff foot]({{ '/assets/part8/s5-5_close_tau5.jpg' | relative_url }})
*5 s: dark gaps between hits.*

![Close-up with lifetime 15 s: a solid sheet with thin cracks]({{ '/assets/part8/s5-5_close_tau15.jpg' | relative_url }})
*15 s: foam piles up until the pattern can only cut thin cracks. It looks like cracked ice.*

![Close-up with lifetime 10 s and lower gain: dense foam with flecks and holes]({{ '/assets/part8/s5-5_close_tau10_gain6.jpg' | relative_url }})
*The choice: 10 s, and the gain halved (12 → 6), so piled-up foam still breaks into flecks and holes.*

## Step 6: Lace everywhere

Two kinds of foam still skipped the lace pattern: the narrow band where water touches rock, and the crest foam on incoming waves. Close up they looked like a painted strip and grey smudges. I now combine all foam amounts first and send them through the one pattern, so there's no seam between patterns. Only a thin line right at the rock stays solid white.

**Problem (code):** I also tried a cellular pattern, where cell borders become threads (Worley 1996). It came out inside out, because my coverage rule reads a *high* pattern value as a hole, and the cellular pattern is high on its threads. Even inverted, it gave near-solid white with diamond-shaped holes. Making it work would need its own coverage rule, so I removed it rather than keep code I hadn't tuned.

**Result:** near the rock the change is tiny, since after step 5 the foam already covers it 90 % of the time. It shows on the **approaching waves**, whose crest foam now forms as lace instead of grey smudges (the top picture, lower left).

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Foam lookup | world position | **rest position** | Foam stuck to the world while the water moved |
| Whitecap rate | 0.2 /s | **0.12 /s** | Cover 1.7 % → 1.01 % after the change above |
| Wet-rock drips | on | **removed** | Thin drips became rows of white ticks |
| Foam floor / edge fade | 0 / 0 | **0.02 / 15 m** | Hid the map's border |
| Foam gain | 3 → 12 | **6** | Too faint, then piled-up foam went solid |
| Splat radius | 2.2, 2.5, 4, 5 m | **3 m** | Width 11 m, inside the 8–15 m target |
| Splat strength / outset | 0.55 / 1 m | **0.9 / 1.5 m** | Brighter centres, less hidden by the contact band |
| Foam lifetime | 4, 5, 6, 7, 15 s | **10 s** | Cliff foot white 90 % of the time |
| Lace pattern | value noise vs cellular | **value noise** | Cellular came out inside out |

## What I do differently now

- **A measurement earns its place when it changes the picture.** The backwash numbers made me notice the frozen foam.
- **Check that a failing test's baseline still describes today's scene.**
- **Name the cause in the picture before acting.** The "dots" were ticks, and the ticks were drips.
- **Set the target before tuning, then sweep.** My guess about which knob sets the width was wrong; the sweep showed it in one run.
- **Remove what doesn't work.** The drips and the cellular pattern both went.

## Sources

Dean & Dalrymple 1991 (standing waves at a wall); Stam 1999 ("Stable Fluids"); Worley 1996 (cellular texture).

**Next:** [Part 9]({{ '/blog/part-9/' | relative_url }}). Flat ground on the cliff top, benchmarks, and checking my own report.
