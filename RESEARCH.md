# OnTwos — Research Position

Working notes toward a paper. Written 2026-08-04, superseding the novelty discussion
that used to live in `Context.md`.

The purpose of this document is to keep the *claim* honest and separate from the
*engineering*. `TECHNICAL.md` §8 covers what the system does; this covers what can
actually be argued about it, what the evidence is, and what is still missing.

---

## 1. The one-sentence claim

> Stop-motion stylization of real-time character motion does not require
> content-adaptive hold placement — at any cadence a viewer accepts as stepped, the
> pose budget is not scarce relative to the motion's structure — but it does require
> decoupling the displayed pose from the simulated state, because a constraint solver
> cannot absorb an externally authored pose.

**Working title:** *Holds Without Scarcity: Content-Adaptive Pose Placement Does Not
Improve Stepped Character Animation.*

This is a claim, not a topic. The previous version of this section read "temporal
stylization of a live, constraint-solved simulation, where the stylization must be causal
and must not enter the solver's state" — which names the subject of the paper without
asserting anything that could turn out to be false. Everything below either supports the
sentence above or qualifies it.

Both halves have evidence and they are not equally strong. The negative half is measured
and owned (§2a). The positive half is currently argued from the engine's documentation
and the sequential-impulse literature (§3); it becomes owned when the proxy ablation is
run, which is why that ablation is now the highest-value outstanding experiment rather
than a nice-to-have (§6).

### Why the negative half leads

If adaptive placement does not help, the correct system is the simple one. The paper is
not confessing that its machinery turned out to be inert — it is establishing that the
machinery should not be built, and identifying the one non-obvious thing that must be.
That is a prescriptive result, and it is what makes §2a worth reading rather than merely
worth disclosing.

It also defuses the softest objection in §7. "Causal is a choice, not a constraint"
presumes that lookahead would improve placement. Placement is not the lever, so the
objection loses most of its force without the latency argument having to be won.

---

## 2. One claim, two halves

This section used to read "two claims, currently bundled — separate them", with an
architecture claim that was supported and an algorithm claim that was not. That framing
was wrong after the 2026-09-04 measurement, and it was making the paper weaker than its
own evidence: it presented the strongest result in the project as a failure to support a
secondary claim.

There is one claim (§1). Its negative half is measured, and is the reason to publish. Its
positive half is the architecture.

### 2a. The negative half — measured, and the reason to publish

*When* to snap: PCHIP fit over a rolling window, extrema detection, arc-length-weighted
candidate placement, deviation threshold. The premise behind all of it is that holds
should be spent where the motion is. Two measurements say that premise does not operate
as described.

**The ablation result (2026-08-04).** Removing the spline fit, the extrema detection and
the arc-length placement entirely produced **bit-identical output across 17
configurations and 68,000 frames** at `CadenceJitter = 0`. The reason is structural: the
forced-cadence branch is tested before the Tau-gated branch, so when the two hold bounds
coincide the candidate walk is unreachable.

`CadenceJitter = 0` is the default, the recommended setting, and the one that produces
the classic look. **So in the configuration that matters, the system is equivalent to
"resample every 1/StepRate seconds"** — roughly twenty lines of code with no mathematics
in it.

Worth stating plainly, because it is evidence rather than embarrassment: the system
shipped with the sophisticated path unreachable at its default setting, and nobody
noticed from the output. The ablation had been running in production all along.

**The placement result (2026-09-04).** The ablation only covers `CadenceJitter = 0`, where
the candidate walk never runs at all. The obvious rejoinder is that the mathematics earns
its keep *above* zero jitter, where it does run. That has now been measured, and it does
not.

At a matched pose budget — adaptive first, then fixed-rate resampling at whatever pose
count adaptive chose, so neither can win by simply snapping more — adaptive placement does
not land held poses closer to the motion's true extrema than a uniform metronome does.
Across 36 configurations spanning two motion densities and a swept budget, the differences
are mostly under a degree and inconsistent in sign, and adaptive loses more often than it
wins on both directions measured: precision (is a typical drawing spent on an extreme) and
coverage (is every extreme represented by some drawing). Ground truth was the analytic
derivative of the test motion's own angle function, never `ExtremaDetector` — scoring the
system against its own opinion would only have established that it agrees with itself.
`Tests/ExtremaAlignmentTests.cs` runs it.

The mechanism is more useful than the null. **At any cadence a viewer accepts as stepped
animation, the pose budget runs 7–39× the extremum count**, so both strategies already
cover every extremum and there is nothing left to allocate. The "spend drawings where the
motion is" intuition assumes a scarcity that the operating regime does not have. Reaching
even one pose per extremum required a deliberately high-frequency motion, and the result
there was mixed rather than favourable. That is a structural argument, and structural
arguments survive review better than null results do.

It also generalises past this system. The budget-versus-extremum-count argument is a
statement about the problem, not about this implementation, which is what makes it worth
publishing rather than merely worth disclosing.

**The numbers above predate a cadence fix and must be re-measured (2026-09-26).** The τ
walk stamped the hold clock with the winning candidate's own time, which could be up to a
full sample window older than the previous snap; the next tick then measured an inflated
`heldFor`, tripped the forced-cadence branch and emitted a step inside the
`MinHoldSeconds` guard. That path is reachable only above zero jitter — which is exactly
the regime this measurement runs in, at jitter 0.5 and 1.0. The matched-budget design
most likely absorbs it, because the fixed-rate arm is given whatever pose count adaptive
produced, spurious steps included. "Most likely" is not good enough for a number a paper
rests on. The defect is fixed and `Tests/ExtremaAlignmentTests.cs` should be re-run before
these figures are quoted anywhere.

Stated against overclaiming in the other direction: two synthetic motions, both sinusoidal
about a fixed axis, one τ, one bone with no chain, and an objective proxy for the premise
rather than for perceptual quality. The result says the stated mechanism does not operate
as described. It does not say the output looks the same — that is still §6.

### 2b. The positive half — the architecture

*How* to apply temporal quantization to a running physics simulation without corrupting
it: sample the bodies, hold the visual pose, and drive a physics-stripped clone rather
than writing back into the constrained bodies.

The argument is set out in §3 and is currently **borrowed** — Unity's own documentation
plus the sequential-impulse convergence literature. §7 already anticipates a reviewer
asking whether the solver claim is the author's or the engine's, and as things stand the
honest answer is the engine's. The fix is to measure it: write the held pose directly
into the non-kinematic bodies and report the divergence. Until that ablation is run, the
surviving half of the claim rests on other people's results.

The literature supports the *pattern*. The Capell line at UW and, more recently, PhysRig
all assume that the representation one simulates and the representation one displays need
not be identical — but for deformation realism, not for temporal stylization, and never
because the solver would otherwise diverge. Separation as a numerical necessity rather
than a modelling convenience is the narrow thing to claim, and it is narrow enough to
defend.

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

Closest-but-not-quite prior art, now identified properly in
`OnTwos_Literature_Review_AI_Readable.md`: **Roberts et al. (2019)**, *Optimal and
Interactive Keyframe Selection for Motion Capture* (Computational Visual Media 5,
171–191) — the closest peer-reviewed precedent for deviation-driven pose selection, and
offline by construction; and **Rohmer et al. (2021)**, *Velocity Skinning for Real-time
Stylized Skeletal Animation* (arXiv 2104.04934) — real-time stylization layered over a
standard rig, but velocity-driven deformation rather than temporal quantization.

That review supersedes this table as the bibliography of record. Three caveats carry
over from it and must be closed before submission: Shoemake and Dam et al. were verified
through secondary summaries rather than publisher pages; So & Baciu (2005) and Çakmak &
Capin (2011) are cited through Roberts' related-work section rather than read; and the
Capell line is cited from one project page rather than from the papers.

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

## 6. The missing piece: the evaluation is objective only

The project has an implementation, one objective experiment (§2a), and no perceptual data
at all. The 2026-08-04 measurements were performance and correctness — engineering
validation, not results. The 2026-09-04 placement measurement *is* a result, but it is a
proxy: it scores where poses land relative to the motion, not what a viewer sees.

### The question the ablation hands us

> Does content-adaptive hold placement produce a perceptibly better stop-motion look than
> fixed-rate resampling?

**The objective half is now answered, and the answer is no** — adaptive placement does not
put poses nearer the extremes than a metronome, and at realistic budgets there is no
scarcity for it to exploit (§2a). The perceptual half is still open, and it is the half
that decides the claim. "Lands in the same places" and "looks the same" are different
statements: a stop-motion read could plausibly depend on hold *duration* structure, on
where the beat sits relative to the action, or on properties this metric does not capture
at all.

Worth answering either way, and the incentive has not changed. If viewers **can** tell
them apart despite the placement measurement, that is the more interesting paper — the
apparatus does something the obvious metric fails to see, and finding out what would be a
genuine contribution. If they **cannot**, the negative result is complete rather than
half-stated: "the sophisticated approach is perceptually indistinguishable from the naive
one" is genuinely useful and nobody has published it for this problem.

### Minimum viable evaluation

- **A perceptual study.** Do viewers identify the output as stop-motion? Do they prefer
  it to smooth playback? Can they distinguish adaptive from fixed-rate?
- **The naive baseline, honestly reported.** Fixed-rate resampling vs. adaptive mode.
  This is the comparison that decides how far the negative half generalises. *Objectively done (§2a) —
  adaptive does not place poses better. The perceptual comparison is still owed.*
- **More than N=1.** One Mixamo clip on one rig is a demo. Motion type matters
  structurally here — a walk cycle and a punch have completely different extremum
  distributions, and the algorithm's whole premise is that it spends drawings where the
  motion is interesting. *§2a used two synthetic motions chosen for extremum density, not
  captured motion, which is the limitation most likely to be challenged.*
- **The smear working**, since it is the most distinctive idea (§8).

---

## 7. Anticipated reviewer objections

**"Causal is a choice, not a constraint."** You could delay the proxy by ~100 ms and buy
lookahead; games spend latency budget routinely, and a corpse nobody is controlling is
cheap to delay. This used to be the softest joint in the argument and §2a has largely
taken the weight off it: the objection presumes lookahead would improve *placement*, and
placement is measurably not the lever. The desynchronisation answer — impact response,
audio, gameplay events — is still worth making, but it no longer has to be won.

**"The gap is real, but is it interesting?"** A documented gap plus a documented failure
mode still does not establish that the output is worth having. Novelty and value are
separate burdens; §6 is the only thing that discharges the second.

**"Is the solver claim yours or the engine's?"** The marginal-convergence result is a
property of sequential-impulse solvers generally, and as long as §2b rests on it alone
the honest answer is "the engine's". Cite it as established background — reviewers are
sharp about borrowed weight — and run the proxy ablation so there is an owned
measurement sitting next to the borrowed one.

**Search coverage — still open, and the literature review does not close it.** The
review in `OnTwos_Literature_Review_AI_Readable.md` is an academic pass: publisher pages,
arXiv, university project pages. Its negative finding is explicitly about the
*peer-reviewed* literature. The highest-risk sources named here are none of those —
**GDC talks and studio tech blogs**, **patents**, and **shipped Unity/Unreal asset-store
products**. A stepped-ragdoll plugin would be near-invisible to academic search and
completely fatal to the novelty claim, and nothing run so far would have seen it. Still
worth an hour on the asset stores and a patent search, and it should happen before any
more time goes into the framing.

**Missing craft literature.** The review is strong on numerical analysis and physics
systems and silent on the timing tradition the system imitates. There is no Lasseter
(1987), *Principles of Traditional Animation Applied to 3D Computer Animation* — the
canonical citation for holds and timing as craft — nothing on limited animation, and
*Spider-Verse* is invoked as motivation in `TECHNICAL.md` §10 with no reference despite
having published production talks. At an NPAR-family venue, missing the literature that
defines the look being reproduced is more conspicuous than missing a spline paper.

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

Honest read of where this sits. This used to say the perceptual study was the only thing
that moved the project; §2a and the proxy ablation both move it too, and they do not need
human subjects.

- **As it stands** — a working system, one owned negative result with a structural
  explanation, and a borrowed argument for the architecture. Publishable at a workshop
  once the borrowed half is measured.
- **With the proxy ablation** — both halves of §1 owned. This is the short paper.
- **With the perceptual comparison as well** — the sufficiency claim stated perceptually
  as well as objectively, on real captured motion. The better paper, and the slower one.

Realistic venues: **NPAR, Expressive, MIG (Motion in Games), SCA**. Not a SIGGRAPH
main-track paper.

### The fork

Two papers are available and they have very different schedules.

**Short — no human subjects, weeks.** §2a plus the proxy ablation. *Adaptive placement
does not beat a metronome, here is the structural reason, and here is the architecture
that makes stepping a live simulation possible at all — with the naive approach measured
as divergent.* Needs: the asset-store hour (§7), the proxy ablation, the component-wise
PCHIP error number, citation cleanup, and writing.

**Long — with the study, months.** Everything in §6. Gated on ethics approval, a stimulus
pipeline that does not exist (every measurement to date drives `HoldFrameScheduler`
directly, with no rig, renderer or video path), and real captured motion.

The fork does not change §1. The perceptual result strengthens the same claim rather than
replacing it, and the short paper's work is all work the long paper needs anyway — so it
can be decided on the calendar rather than on the argument. Ethics is the long pole; if
the long fork is wanted, that submission should start before the stimulus pipeline, not
after.

---

## 10. Disclosure

`README.md` states that the algorithm, architecture and design are original work by the
author and that the C# implementation was produced with AI assistance. Whether that
disclosure is sufficient for a given submission depends on the institution's and the
venue's policy, which should be checked directly rather than assumed.
