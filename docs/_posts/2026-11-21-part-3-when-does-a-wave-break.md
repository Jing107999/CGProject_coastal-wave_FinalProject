---
layout: post
title: "Part 3: When does a wave break?"
date: 2026-11-21
part: 3
permalink: /blog/part-3/
excerpt: "With the sea and the cliff in place, the next step is to detect when a wave hits the rock."
---

*With the sea and the cliff in place, the next step is to detect when a wave hits the rock. Real waves break when they get too steep (Michell 1893), but Gerstner waves never do, and they pass straight through the rock. So I wrote a detector that watches the water at the cliff foot and reports where a wave hit and how hard. The method is in the report (§3.4); this post is about getting it to work, in four steps.*

![The demo with its overlay showing the break threshold, events and events per minute]({{ '/assets/part3/s2-5_hud.jpg' | relative_url }})
*The result of this part: threshold 3.0 m, 118 impacts so far, 211 per minute, and the detector costs 0.32 ms per frame.*

## Step 1: Probes along the waterline

I watch the sea with a row of probes. Every 2.5 m along 240 m of coast, a ray is shot at sea level from the sea towards the land, and a probe is placed 0.6 m in front of where it hits the rock. The rock's flattened surface direction there tells the probe which way the cliff faces.

```csharp
// WaveBreakDetector.cs: one probe per ray that hits the cliff
var ray = new Ray(new Vector3(-100f, sea, z), Vector3.right);       // from the sea towards the land
if (!cliff.Raycast(ray, out RaycastHit hit, 400f)) { misses++; continue; }
Vector3 outward = Vector3.ProjectOnPlane(hit.normal, Vector3.up);   // rock normal, flattened
Probes.Add(new Probe { position = hit.point + outward * offset, outward = outward });
```
**In Unity:** a `WaveBreakDetector` component next to the clock (*Spacing* 2.5, *Offset* 0.6). The clock calls it every step, right after the waves move, and each probe asks the height query from Part 1 how high the water is.
*Source: Unity Scripting API, `Collider.Raycast`, `Vector3.ProjectOnPlane`*

![Top view of the cliff with small white probe ticks along its edge]({{ '/assets/part3/s2-2_probes_top.jpg' | relative_url }})
*Top view: 97 probes follow the outline around the headlands and into the bays.*

**Problem (code):** in the Editor there were 97 probes. In Play mode there were **0**.

When Play mode starts, Unity calls every object's `OnEnable` in no fixed order. The cliff builds its collider in its `OnEnable`, and the detector shot its rays in its own. When the detector went first, the rays hit an empty collider. `Start` runs after every `OnEnable`, when the cliff is ready.

```diff
  // WaveBreakDetector.cs
- void OnEnable() => BuildProbes();
+ // Start runs after all OnEnable calls of the scene, so the cliff's collider is ready by then.
+ void Start() => BuildProbes();
```
*Source: Unity Manual, "Order of execution for event functions"*

![Cyan probe dots riding a wave crest at the foot of the cliff]({{ '/assets/part3/s2-2_probes_closeup.jpg' | relative_url }})
*After the fix: probes (cyan dots) riding a crest at the cliff foot. The 97 probes cost 0.31 ms per step.*

## Step 2: From a height to an event

"The water is above 3 m" is a *state*, but I want an *event*: one message per breaking wave. A plain threshold test would fire on every frame the water stays high. Instead, each probe runs a small state machine: it waits for the water to pass 3 m, follows it up, and fires at the peak.

```csharp
// WaveBreakDetector.cs: Rising state
if (p.height > p.peak) p.peak = p.height;   // still rising: remember the highest point
else
{
    Fire(i, p, t);                          // first step going down: the crest just peaked
    p.state = ProbeState.Cooldown;          // re-arm only below threshold - 0.25 m
}
```
The probe re-arms only once the water drops 0.25 m below the threshold (hysteresis), which stops a crest wobbling around 3 m from firing twice. The foam in Part 4 is the first code to listen to these events.

To check it, I recorded every probe's water height for 132 s and counted crests and events.

![Water height at one probe over 132 s, with dots where events fired]({{ '/assets/part3/s2-3_trace_busiest.jpg' | relative_url }})
*Water height at the busiest probe. Every crest that passes the threshold gets exactly one dot, at its peak.*

**Result:** across all 97 probes, **447 crests and 447 events**, with none missed and none counted twice.

**Surprise (numbers):** I expected events to be rare, since a single probe hardly ever sees 3 m. But the 97 probes together saw 3.4 events per second. I kept the threshold and made it adjustable in step 4.

## Step 3: Waves that pass through rock

A probe on a headland flank facing *away* from the swell still sees tall crests, although no real water would hit the rock there. At each peak I therefore check which way the water moves. At the top of its circle a water particle moves forward, in the wave's direction:

```csharp
// WaveBreakDetector.cs: is the water moving into the rock?
var flow = new Vector3(velocity.x, 0f, velocity.z);
exposure = flow.sqrMagnitude > 1e-6f ? Vector3.Dot(flow.normalized, -p.outward) : 0f;   // 1 head-on, 0 sliding past
if (exposure < minExposure) { p.rejectCount++; RejectedEvents++; return; }
intensity *= Mathf.Sqrt(exposure);                      // glancing hits are weaker
```

Each event also gets an intensity from 0.2 (just over the threshold) to 1 (*Intensity Range* metres higher), for the foam and spray later.

**Problem (numbers):** with the default range of 1.2 m, the intensity could never reach 1. The tallest crests at my cliff are about 3.9 m, only 0.9 m above the threshold. I set *Intensity Range* to 0.9.

**Result:** only 6 of 487 crests were rejected, all on three probes on flanks facing 75–82° away from the swell. The tallest quarter of crests averaged intensity 0.70, the lowest quarter 0.23.

**Checking the checker:** one run had 488 crests in my recording but 487 decisions. One crest had peaked at 3.0017 m *between* two of my 0.05 s samples, and the detector, which checks every frame, had fired correctly. The other two were still rising when I stopped.

## Step 4: A knob for the threshold

Last, keys **1** and **2** change the threshold in 0.1 m steps. The overlay counts events per minute *since the last change*; a rate averaged from the start would hide a change for minutes.

To compare thresholds fairly, I restarted the clock before each run, so all three saw the same sea for 60 s:

| Threshold | Impacts | Rate |
|---|---|---|
| 2.5 m | 411 | 397 / min |
| 3.0 m | 219 | 213 / min |
| 3.5 m | 50 | 48 / min |

Each extra half metre cuts the rate sharply: only a few crests come close to the sea's maximum of 4.26 m. The detector's cost doesn't depend on the threshold.

## Numbers I tried

| Field | Tried | Final | Why |
|---|---|---|---|
| Probe spacing | — | **2.5 m**, 0.6 m in front of the rock | 97 probes; 0.31 ms per step |
| Threshold | 2.5, 3.0, 3.5 m | **3.0 m** (later 2.0 m, see Part 5) | 213 impacts per minute: busy but not constant |
| Hysteresis | — | **0.25 m** | No double fires in 447 crests |
| Min. time between events | 1 s | **1 s** (never needed) | Shortest real gap was 5.4 s |
| `minExposure` | — | **0.15** | Rejects only flanks facing away from the swell |
| *Intensity Range* | 1.2 m | **0.9 m** (later 3 m, see Part 5) | With 1.2 m the tallest crest only reached 0.8 |

## What I do differently now

- **Use `Start` when objects need each other**, not `OnEnable`.
- **Check every event against the raw data.** 447 out of 447 is a result; "looks right" isn't.
- **Compare settings on the same sea.**
- **Start with the minimum**, and test each piece before adding the next.

## Sources

Michell 1893 (wave breaking). Unity Manual: "Order of execution for event functions". Unity Scripting API: `Collider.Raycast`, `Vector3.ProjectOnPlane`.

**Next:** [Part 4]({{ '/blog/part-4/' | relative_url }}). The impacts are there; now they need to leave foam that fades.
