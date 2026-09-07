# OnTwos — Research Position

Working notes toward a paper. Written 2026-08-04, superseding the novelty discussion
that used to live in `Context.md`.

The purpose of this document is to keep the *claim* honest and separate from the
*engineering*. `TECHNICAL.md` §8 covers what the system does; this covers what can
actually be argued about it, what the evidence is, and what is still missing.

---

## 1. The one-sentence claim

> Temporal stylization of a live, constraint-solved simulation, where the stylization
> must be causal and must not enter the solver's state.

Narrow enough to defend, specific enough that a literature search either turns something
up or it doesn't. Everything below either supports this or qualifies it.

---

## 2. Two claims, currently bundled — separate them

The project presents one contribution. It has two, and they are supported by very
different amounts of evidence.

### 2a. The architecture claim — supported

*How* to apply temporal quantization to a running physics simulation without corrupting
it: sample the bodies, hold the visual pose, and drive a physics-stripped clone rather
than writing back into the constrained bodies.

The evidence for this is good and is set out in §3.

### 2b. The algorithm claim — currently unsupported, and undercut by our own ablation

*When* to snap: PCHIP fit over a rolling window, extrema detection, arc-length-weighted
candidate placement, deviation threshold.

**The ablation result (2026-08-04).** Removing the spline fit, the extrema detection and
the arc-length placement entirely produced **bit-identical output across 17
configurations and 68,000 frames** at `CadenceJitter = 0`. The reason is structural: the
forced-cadence branch is tested before the Tau-gated branch, so when the two hold bounds
coincide the candidate walk is unreachable.

`CadenceJitter = 0` is the default, the recommended setting, and the one that produces
the classic look. **So in the configuration that matters, the system is equivalent to
"resample every 1/StepRate seconds"** — roughly twenty lines of code with no mathematics
in it.

This is the first thing a reviewer will probe, and it must be reported, not buried. But
it is not fatal, because it is also a research question (§6).

---

## 3. Why the naive approach is impossible, not merely ugly

The strongest support for 2a, and the reason the adjacent literatures don't transfer.

**Engine-level.** Unity's own documentation on joint and ragdoll stability states that
direct transform access should not be used on kinematic bodies jointed to other bodies,
and that joints can "blow up" when forced into configurations for which the constraints
have no solution. That is a statement that the solver has no valid state to converge to
— divergence, not degradation.

**Solver-level.** Work on rod constraints for simplified ragdolls (University of Bath)
notes that the sequential-impulse solvers game engines use already converge slowly on
ragdoll assemblies, because of their deep tree-topology connectivity — and that engines
run a **fixed iteration count** rather than iterating to convergence, so residual joint
error and jitter are present even under normal operation.

**The synthesis, which is the part worth writing down.** Ragdoll constraint solving is
already a marginal numerical regime at baseline. Writing an externally-authored held
pose into it is not adding noise to a stable process; it is injecting a forcing term
into a process already fighting to stay converged on a fixed compute budget.

That is what makes the proxy *necessary* rather than convenient. Cloning a rig is not a
contribution — studios do it for ragdoll blending, shadow proxies and LOD. Cloning a rig
*because writing into an under-converged constraint system is a category error* is.

---

## 4. Literature position

A reasonably broad search-based pass across three adjacent literatures (2026-08-04).
**Not a systematic review** — see the caveat in §7.

| Field | Example | Why it doesn't cover this |
|---|---|---|
| Real-time NPAR / stylized rendering | *Age of Sail*-style real-time stylization at VR framerates | Operates at the shading/silhouette layer, **after** the simulation has produced a valid state. Never touches the dynamics. |
| Motion editing under physical constraints | Spacetime constraints; Gleicher; Popović, *Controlling Physics in Realistic Character Animation* | Modifies motion while preserving physical plausibility — structurally **offline**. Optimizes over an entire known trajectory, with the future available to the solver. Non-causal by design. |
| Keyframe reduction / curve simplification | Douglas–Peucker lineage; Maya/Motion/Nuke curve-simplify tools | This is the prior art for the deviation-threshold walk itself. Offline, batch, with full knowledge of the curve, and optimising for key count rather than for a look. |

Closest-but-not-quite prior art already identified: **Roberts (2018)** and **Rohmer
(2021)**. *(Recorded from the author's own search; not independently verified here.)*

### The gap, stated as a mechanism rather than an absence

Reviewers distrust "nobody has done this." State it as why the available approaches
cannot be adapted:

- Offline motion editing has **both** lookahead **and** an unbounded convergence budget.
  That combination is what lets spacetime methods land on a physically valid result.
- A real-time game solver has **neither**: no future, and a fixed iteration count per
  tick that is already insufficient for deep tree topology.
- So the causal constraint and the numerical constraint are **the same constraint** —
  both are consequences of a real-time budget.

That is a mechanism, and it is much harder to attack than a claim of absence.

---

## 5. What is *not* a contribution

Stated plainly so it doesn't creep into the paper.

- **The deviation-threshold walk.** Decades of prior art (§4). Any novelty claim resting
  here dies in related work.
- **Performance.** The 2026-08-04 optimisation pass took the ragdoll scheduler from
  40.4 µs to 1.5 µs per rig per tick and eliminated 93 KB/frame of allocation. A reviewer
  reads "27× faster" and correctly hears "our implementation was doing 27× more work than
  necessary." Performance belongs in the paper **only** as feasibility evidence for a
  systems claim — *"N simultaneous ragdolls inside a 20 ms physics budget on consumer
  hardware"* — and never as the claim itself.
- **The proxy technique in isolation.** Known engineering. Its *necessity* is the
  contribution, not its existence.
- **Burst / Job System work.** Standard engine practice. Would not be a contribution even
  if it were done, which it isn't.

---

## 6. The missing piece: there is no experiment

The project currently has an implementation and zero experiments. The 2026-08-04
measurements were performance and correctness — engineering validation, not results.

### The question the ablation hands us

> Does content-adaptive hold placement produce a perceptibly better stop-motion look than
> fixed-rate resampling?

Unanswered, and worth answering either way. If **yes**, the entire apparatus is justified
and the paper has shown *why* animators place holds at extremes rather than on a
metronome. If **no**, that is an honest negative result — "the sophisticated approach is
perceptually indistinguishable from the naive one" is genuinely useful and nobody has
published it for this problem.

### Minimum viable evaluation

- **A perceptual study.** Do viewers identify the output as stop-motion? Do they prefer
  it to smooth playback? Can they distinguish adaptive from fixed-rate?
- **The naive baseline, honestly reported.** Fixed-rate resampling vs. adaptive mode.
  This is the comparison that decides whether 2b exists at all.
- **More than N=1.** One Mixamo clip on one rig is a demo. Motion type matters
  structurally here — a walk cycle and a punch have completely different extremum
  distributions, and the algorithm's whole premise is that it spends drawings where the
  motion is interesting.
- **The smear working**, since it is the most distinctive idea (§8).

---

## 7. Anticipated reviewer objections

**"Causal is a choice, not a constraint."** You could delay the proxy by ~100 ms and buy
lookahead; games spend latency budget routinely, and a corpse nobody is controlling is
cheap to delay. This is currently the softest joint in the argument. The answer is
probably that it desynchronises the visual from impact response, audio and gameplay
events — but it needs to be *argued*, not assumed.

**"The gap is real, but is it interesting?"** A documented gap plus a documented failure
mode still does not establish that the output is worth having. Novelty and value are
separate burdens; §6 is the only thing that discharges the second.

**"Is the solver claim yours or the engine's?"** The marginal-convergence result is a
property of sequential-impulse solvers generally. Cite it as established background —
reviewers are sharp about borrowed weight.

**Search coverage.** The §4 pass is search-based, not systematic. The highest-risk
unindexed sources are **GDC talks and studio tech blogs**, **patents**, and **shipped
Unity/Unreal asset-store products**. A stepped-ragdoll plugin would be near-invisible to
academic search and completely fatal to the novelty claim. Worth an hour on the asset
stores and a patent search before committing the framing.

---

## 8. The smear — highest novelty, currently unproven

Holds and smears as the same phenomenon measured two ways: one residual, `raw − held`,
driving both the decision to hold *and* the deformation that covers the gap. Existing
tools treat smear as a separately authored or simulated effect.

This is the idea most likely to be genuinely novel, and it is the only open roadmap item
that would add a capability rather than refine one.

It is also not finished. The signal layer works and the bone-scale technique works with a
known ceiling; the shader path renders as blobs for reasons not yet diagnosed. See
`SMEAR.md`. Until it renders, the "one signal, two techniques" claim is half-delivered.

---

## 9. Calibration

Honest read of where this sits:

- **Without an evaluation** — a working system and no experiments — this is a strong
  engineering project. The artifact is a demo.
- **With the perceptual comparison done** — a legitimate MSc-level contribution and a
  plausible workshop/conference paper.

Realistic venues: **NPAR, Expressive, MIG (Motion in Games), SCA**. Not a SIGGRAPH
main-track paper. A system-plus-evaluation paper at one of those is a respectable and
achievable target.

The gap between those two states is a few weeks of running a comparison, not years of
work. **The experiment is the only thing on the list that moves it.**

---

## 10. Disclosure

`README.md` states that the algorithm, architecture and design are original work by the
author and that the C# implementation was produced with AI assistance. Whether that
disclosure is sufficient for a given submission depends on the institution's and the
venue's policy, which should be checked directly rather than assumed.
