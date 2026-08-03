# OnTwos — Technical Reference

A ground-up explanation of how OnTwos works: the pipeline, the mathematics, the
architectural decisions and why they were made, an honest assessment of what is
and isn't novel, and what it means for the people who'd use it.

This is the *engineering* document. For setup, field reference and tuning recipes
see `DOCUMENTATION.md`; for a five-minute overview see `README.md`.

---

## Table of contents

1. [The problem](#1-the-problem)
2. [System overview](#2-system-overview)
3. [The per-frame pipeline](#3-the-per-frame-pipeline)
4. [The mathematics](#4-the-mathematics)
   - 4.1 [Quaternion hemisphere normalisation](#41-quaternion-hemisphere-normalisation)
   - 4.2 [PCHIP interpolation](#42-pchip-interpolation-fritschcarlson)
   - 4.3 [Extrema detection](#43-extrema-detection-brents-method)
   - 4.4 [Arc-length reparameterisation](#44-arc-length-reparameterisation)
   - 4.5 [The deviation threshold](#45-the-deviation-threshold)
   - 4.6 [Cadence locking](#46-cadence-locking)
5. [The physics path and proxy decoupling](#5-the-physics-path-and-proxy-decoupling)
6. [Deriving smear from the same signal](#6-deriving-smear-from-the-same-signal)
7. [Complexity and performance](#7-complexity-and-performance)
8. [What actually sets it apart](#8-what-actually-sets-it-apart)
9. [Significance for game developers](#9-significance-for-game-developers)
10. [Significance for animators](#10-significance-for-animators)
11. [Known gaps and limitations](#11-known-gaps-and-limitations)

---

## 1. The problem

Hand-drawn animation is rarely drawn at the projection rate. At 24 fps, drawing a
new image every frame ("on ones") is expensive and, for most motion, unnecessary.
Animators instead draw on **twos** — one new drawing held for two frames — or on
**threes**, and reserve ones for motion fast enough that the eye would otherwise
strobe. The held frame isn't a compromise; it's a deliberate readability device.
It gives the eye a stable pose to land on, and it makes the *transitions* between
poses read as deliberate accents.

3D animation defaults to the opposite: the renderer interpolates a new pose every
frame, forever. Motion is technically smoother and often reads as weightless and
characterless — the "floaty CG" problem. *Spider-Man: Into the Spider-Verse* made
the alternative mainstream by animating Miles on twos while Peter ran on ones in
the same shot, using cadence itself to characterise.

Reproducing this in a game engine is harder than it sounds, and the difficulty is
what shapes this entire codebase.

**Why you can't just skip frames.** The naive approach — update the rig every N
frames — produces a mechanical, metronomic result that ignores the content of the
motion. A real animator doesn't hold for a fixed count. They hold *through* the
slow, low-information parts of a motion and spend drawings *at* the extremes: the
top of an arc, the moment of impact, the reversal of a swing. Holds should be
placed where the motion is boring and broken where it is interesting.

**Why you can't precompute it.** Classical keyframe-reduction algorithms solve
essentially this problem, but they are **offline and batch**: they are handed a
complete curve, with full knowledge of the future, and simplify it globally. A
game rig has no such curve. The pose arriving this frame is the product of blend
trees, IK solvers, physics, motion matching and gameplay state, none of which
existed a frame ago. There is nothing to precompute.

So the actual problem OnTwos solves is:

> Given a live stream of rotations with no knowledge of the future, decide *this
> frame* whether to hold the current pose or snap to a new one, such that holds
> land where the motion is uninformative and snaps land on the motion's extremes.

Everything below follows from that constraint.

---

## 2. System overview

Two entry points, sharing one algorithmic core.

| Component | Source of motion | Update tick | Writes to |
|---|---|---|---|
| `AnimationStepper` | Animator, IK, script, motion matching — anything driving `Transform.localRotation` | `LateUpdate` | the bones themselves |
| `RagdollStepper` | `Rigidbody` world rotation from PhysX | `FixedUpdate` | a cloned visual proxy |

Both allocate **one `HoldFrameScheduler` per bone**, and each scheduler owns
entirely independent state — its own sample ring buffer, its own spline fits, its
own hold clock. No bone's decision is coupled to another's. This is what allows
different limbs to run at different cadences, and it's also what makes the locked
mode work: independence plus a shared timebase produces exact lockstep, without
any explicit synchronisation.

The supporting cast:

```
Runtime/
  AnimationStepper.cs      entry point, animator/procedural path
  RagdollStepper.cs        entry point, physics path
  HoldFrameScheduler.cs    per-bone orchestrator — the algorithm proper
  MonotoneCubicSampler.cs  rolling sample window + spline cache + arc-length LUT
  Pchip.cs                 scalar monotone cubic interpolant
  ExtremaDetector.cs       derivative zero-crossing finder (Brent's method)
  QuaternionSignNorm.cs    hemisphere consistency for quaternion sequences
  RagdollProxyBuilder.cs   builds the physics-decoupled visual clone
  BonePathCache.cs         O(1) hierarchy-to-hierarchy bone mapping
  BoneFilter.cs            name-based exclusion + tau override (bake-time path)
  BoneRuleSet.cs           resolves all bone rules once, index-parallel to bones
  BoneTuning.cs            per-bone tuning by direct Transform reference
  OnTwosProfile.cs         ScriptableObject tuning asset
  OnTwosAuthoring.cs       wiring component + animated↔physics handoff
```

---

## 3. The per-frame pipeline

For one bone, one frame, inside `HoldFrameScheduler.Update(time, rotation)`:

```
  1. Append (time, rotation) to a fixed-capacity ring buffer.
  2. Measure elapsed time since the last snap.
  3. If fewer than 4 samples exist, pass the pose through unmodified.
  4. Every 10th frame: refit the spline and locate all motion extrema
     over the current window.  (Cached and reused on the other 9.)
  5. Partition the window at those extrema into monotone segments.
  6. Within each segment, place n candidate hold times at equal
     ROTATION-ANGLE intervals (not equal time).
  7. Sort candidates chronologically.
  8. Decide:
       forceSnap?  -> snap to the newest pose, advance the beat
       allowSnap?  -> walk candidates; snap wherever angular deviation
                      from the currently-held pose exceeds tau
       otherwise   -> hold
  9. Slide the window forward; return the held pose.
```

The caller writes the returned quaternion back to the bone. On a held frame the
value is bit-identical to the previous frame, so the bone does not move at all —
this is a true hold, not a slow interpolation.

Steps 4–7 are the interesting part, and they exist to answer one question: *if
we're going to snap, when is the best moment?* Steps 8 answers *whether*.

---

## 4. The mathematics

### 4.1 Quaternion hemisphere normalisation

`QuaternionSignNorm.cs`

A quaternion `q` and its negation `−q` encode the **same rotation** — the unit
quaternions double-cover SO(3). Any system sampling rotations over time will
eventually produce a sequence that flips hemisphere, because nothing forces the
source to be consistent.

This is harmless until you interpolate per-component. Fitting a spline through
`q` then `−q` produces a curve that travels the long way around the 4-sphere: a
≈360° detour that the interpolant reproduces faithfully, appearing as a violent
spin spike in the output.

The fix is to walk the sequence and negate any sample whose dot product with its
predecessor is negative:

```
for i in 1..n-1:
    if dot(q[i-1], q[i]) < 0:
        q[i] = -q[i]
```

After this pass all consecutive samples lie on the same hemisphere and
per-component fitting is well-behaved. This runs on every spline rebuild, before
any fitting.

**Consequence:** because interpolating four components independently does not
preserve unit length, every evaluation must renormalise:

```
q = normalize(Quaternion(px(t), py(t), pz(t), pw(t)))
```

This is a deliberate trade. True quaternion interpolation (SLERP-based splines,
SQUAD) stays on the manifold but is far more expensive and has no closed-form
derivative that's cheap to root-find. Component-wise PCHIP plus renormalisation
is an approximation, and the approximation error is negligible at the sample
densities involved (adjacent frames of animation are a few degrees apart).

### 4.2 PCHIP interpolation (Fritsch–Carlson)

`Pchip.cs`

Given the sample window, we need a continuous curve to evaluate and
differentiate between samples. The choice of interpolant is not arbitrary.

**Why not a natural cubic spline?** It's C² and smooth, but it **overshoots**.
Feed it a pose that accelerates and stops, and the spline swings past the final
value before settling. On a rotation curve that's a limb visibly overextending
past a pose the animator authored — reading as a pop.

**Why not Lagrange/polynomial fitting?** Runge's phenomenon: oscillation near the
window edges that grows with sample count.

**Why PCHIP?** Piecewise Cubic Hermite Interpolating Polynomial with
Fritsch–Carlson tangents is **monotonicity-preserving**. If the data is monotone
over an interval, so is the interpolant. It cannot overshoot. It is C¹ (value and
first derivative continuous), which is exactly enough — we need a continuous
derivative to root-find on, and nothing needs C².

Critically for this application: **PCHIP places all extrema at knots.** Between
two knots the cubic is monotone by construction. That's the property step 4 of
the pipeline depends on, and it's what makes "find the extrema" a well-posed,
cheap question rather than a global optimisation.

**Construction.** Each cubic segment uses the standard Hermite basis, with `s`
the normalised position within the segment and `h` the segment width:

```
h00(s) =  2s³ − 3s² + 1        h10(s) =  s³ − 2s² + s
h01(s) = −2s³ + 3s²            h11(s) =  s³ − s²

p(t) = h00·y[i] + h10·h·m[i] + h01·y[i+1] + h11·h·m[i+1]
```

Analytic derivative, used by the extrema detector:

```
p'(t) = (6s²−6s)/h · y[i] + (3s²−4s+1) · m[i]
      + (−6s²+6s)/h · y[i+1] + (3s²−2s) · m[i+1]
```

The tangents `m[i]` are what make it monotone, computed in two stages.

*Stage 1 — initial tangents.* With secants `d[i] = (y[i+1]−y[i]) / (x[i+1]−x[i])`,
interior knots use a weighted harmonic mean of the adjacent secants:

```
if d[i-1] · d[i] <= 0:          # secants disagree in sign
    m[i] = 0                    # an extremum sits exactly at this knot
else:
    w0 = 2·h1 + h0
    w1 = h1 + 2·h0
    m[i] = (w0 + w1) / (w0/d[i-1] + w1/d[i])
```

The harmonic mean is the key: it's dominated by the *smaller* secant, so a
tangent can never exceed what the local data supports. Setting `m[i] = 0` where
secants disagree is what pins extrema exactly to knots.

Endpoints use a one-sided three-point estimate with a shape-preserving clamp:

```
m = ((2·h0 + h1)·d0 − h0·d1) / (h0 + h1)

if m·d0 <= 0:                              m = 0          # would reverse direction
if d0·d1 <= 0 and |m| > |3·d0|:            m = 3·d0       # cap overshoot
```

*Stage 2 — the Fritsch–Carlson clamp.* Stage 1 alone doesn't guarantee
monotonicity. For each segment, with `α = m[i]/d[i]` and `β = m[i+1]/d[i]`, the
sufficient condition is that `(α, β)` lies within a circle of radius 3:

```
if α² + β² > 9:
    τ = 3 / sqrt(α² + β²)
    m[i]   = τ·α·d[i]
    m[i+1] = τ·β·d[i]
```

This projects the tangent pair radially back onto the circle, scaling both by the
same factor so the segment's shape is preserved while monotonicity is restored.
Flat segments (`d[i] == 0`) force both tangents to zero, since any nonzero
tangent on a constant segment must overshoot.

> Fritsch, F.N. & Carlson, R.E. (1980). "Monotone Piecewise Cubic Interpolation."
> *SIAM Journal on Numerical Analysis* 17(2), 238–246.

Four independent scalar PCHIP fits — one per quaternion component — form the
rotation curve.

### 4.3 Extrema detection (Brent's method)

`ExtremaDetector.cs`

Motion extrema — where a bone reverses, peaks, or momentarily stops — are the
frames an animator would spend a drawing on. Mathematically they're zeros of the
derivative. Finding them is a two-stage root-find:

**Stage 1 — bracketing.** Sample `p'(t)` at fixed intervals (default 1/60 s)
across the window and look for sign changes. A sign change between consecutive
samples brackets at least one root.

**Stage 2 — refinement.** Each bracket is refined by **Brent's method**, which
combines three techniques and switches adaptively:

- *Inverse quadratic interpolation* when the three most recent points have
  distinct function values — fits `t` as a quadratic in `f` and jumps to `f = 0`:

  ```
  s = a·fb·fc / ((fa−fb)(fa−fc))
    + b·fa·fc / ((fb−fa)(fb−fc))
    + c·fa·fb / ((fc−fa)(fc−fb))
  ```

- *Secant method* when values coincide and the quadratic is degenerate.
- *Bisection* whenever the interpolated step fails an acceptability test —
  outside the bracket, or not converging fast enough relative to the previous
  step.

The result is guaranteed convergence (bisection's property) with superlinear
speed near the root (interpolation's property). Tolerance is 1e-4 seconds, well
under a frame, capped at 50 iterations.

This runs independently on all four quaternion components, because a bone's
motion changes character when *any* component reverses. The four result sets are
merged, sorted, and deduplicated — extrema closer than one frame (0.016 s) are
collapsed, since they're numerically indistinguishable and would produce
degenerate zero-width segments.

**Throttling.** This is the most expensive stage, so it runs only every 10th
frame; the other 9 reuse the cached result. Because the window slides forward
every frame while the cache does not, cached extrema are filtered against the
current window `(tStart, tEnd)` before use — without that filter, stale extrema
from before the window start produce unsorted, out-of-range segment boundaries
that corrupt candidate placement.

### 4.4 Arc-length reparameterisation

`MonotoneCubicSampler.ArcLengthCandidates()`

This is the piece that makes the adaptive mode behave like an animator rather
than a metronome, and it's the most under-appreciated part of the design.

Having partitioned the window into monotone segments, we need candidate times
*within* each segment where a snap could land. The obvious choice — space them
evenly in time — is wrong, and wrong in a specific way:

> Equal-time spacing allocates the same number of candidates to a segment where
> the bone barely moved as to one where it swung 90°. Slow sections get
> oversampled with redundant candidates; fast sections get starved exactly where
> the motion is most interesting.

The fix is to space candidates evenly in **rotation angle travelled** — arc
length along the curve — rather than in time. A segment covering 90° of rotation
receives candidates every 30°; a segment covering 3° receives them every 1°. Hold
opportunities are distributed by *how much happened*, not *how long it took*.

Naively this requires solving `arclength(t) = target` per candidate, which means
integrating the curve. Instead, a lookup table is built once per spline rebuild:

```
LUT: 80 samples across the window
  lutTimes[i]     = evenly spaced times
  lutCumAngles[i] = lutCumAngles[i-1] + Quaternion.Angle(q(t[i-1]), q(t[i]))
```

`lutCumAngles` is cumulative angular distance from the window start, and it is
**monotone non-decreasing** by construction. Monotonicity is what makes the table
invertible — the same array serves both directions:

```
angA = lerp(lutTimes -> lutCumAngles, a)        # time  -> angle
angB = lerp(lutTimes -> lutCumAngles, b)

for i in 1..n:
    target  = angA + (angB − angA) · i/(n+1)
    time[i] = lerp(lutCumAngles -> lutTimes, target)   # angle -> time
```

Each lookup is a binary search plus a linear interpolation: **O(log 80)**, no
curve evaluation at query time. If a segment's total angle is below 1e-5 the
curve is degenerate there and it falls back to equal-time spacing.

Both LUT arrays are preallocated in the constructor and overwritten in place on
every rebuild — one of the few places in the runtime that's genuinely allocation-free.

### 4.5 The deviation threshold

The actual hold decision. **τ (tau)**, in degrees, is the amount of angular
deviation tolerated before the held pose is considered stale:

```
for t in candidates (chronological):
    q = evaluate(t)
    if angle(held, q) > tau:
        held = q                 # snap
        lastSnapTime = t         # stamp the candidate's own time
```

Note it compares against the *currently held* pose, not the previous sample. That
makes it an accumulating error bound rather than a per-frame velocity gate: many
tiny movements that sum past τ will eventually trigger a snap, while a single
large movement triggers one immediately. Low τ tracks the source closely (subtle
stepping); high τ holds through larger deviations (chunky stop-motion).

The walk chains within a single frame — if the window contains several
candidates that each cross τ from the previous snap, the held pose advances
through all of them, ending at the most recent. This keeps the output current
rather than lagging by however many candidates were queued.

This is deviation-threshold curve simplification, and it's the part of the
algorithm with the deepest prior art — see §8.

### 4.6 Cadence locking

`StepRate` / `CadenceJitter`

The τ gate alone is *adaptive*: each bone snaps when its own motion warrants it.
That produces organic results but not the metronomic beat that reads as
traditional "on twos", because every bone is on its own schedule and the rig
never lands on a shared frame.

Two time bounds constrain it, derived from the user-facing controls:

```
MaxHoldSeconds = 1 / StepRate                          # the step interval
MinHoldSeconds = MaxHoldSeconds · (1 − CadenceJitter)  # earliest a snap may occur
```

- **`MinHoldSeconds`** — a floor. No snap within this window, regardless of τ.
  Suppresses jitter on fast motion.
- **`MaxHoldSeconds`** — a ceiling. Snap after it regardless of τ. Prevents a
  frozen pose during slow or near-static motion.

**Why seconds and not tick counts.** Counting `Update()` calls makes the cadence
depend on whoever drives the scheduler. `AnimationStepper` ticks once per rendered
frame, so "hold 2 ticks" is 72 poses/sec at 144 fps but 15 at 30 fps — a 4.8×
swing in the signature look across ordinary hardware. `RagdollStepper` ticks on the
fixed 50 Hz physics clock, and the bake window ticks once per clip frame; the same
setting meant three different things. All three already pass a real timestamp into
`Update()`, so gating on elapsed time makes one `StepRate` mean the same thing
everywhere, and makes a baked clip match what Play mode previewed.

The evaluation order is load-bearing:

```
heldFor   = time − lastSnapTime
forceSnap = heldFor >= MaxHoldSeconds
allowSnap = heldFor >= MinHoldSeconds

if forceSnap:        snap to newest pose        # checked FIRST
elif allowSnap:      walk candidates against tau
else:                hold
```

Because `forceSnap` is tested before the τ-gated branch, **`CadenceJitter = 0`
makes the two bounds equal and bypasses τ entirely**, producing an exact cadence.
Every scheduler seeds `lastSnapTime` from the same timestamp in the same
`Start()`/`Reset()` pass, so all bones share a phase and snap on precisely the same
frames. On snap, the timestamp advances by whole step intervals rather than being
assigned the current time, so the beat stays locked to a fixed grid instead of
drifting forward by the frame overshoot each step.

`StepRate = 12` is the classic "on twos" — 24 fps film with each drawing held two
frames. `8` is on threes.

Raising `CadenceJitter` opens a window in which a bone *may* snap early via τ.
Bones with fast motion will; bones with slow motion won't. The rig desynchronises.
This reads as stutter when unintended — but it is also a legitimate deliberate
effect, and it's the same idea as Spider-Punk's jacket running on a different rate
from his body.

**An honest note on what locking costs.** With `CadenceJitter = 0`, `forceSnap`
assigns `held = evaluate(tEnd)`, where `tEnd` is the timestamp of the sample just
added — the newest knot. Since PCHIP interpolates its knots exactly, this returns
the raw incoming rotation. In locked mode the output is therefore *exactly*
"resample the raw pose every 1/StepRate seconds", and the spline, extrema detection
and arc-length machinery contribute nothing to the result. All of that
sophistication earns its keep only when jitter is above zero, where τ decides both
**whether**
and **where** to snap. This is worth knowing before concluding the algorithm
isn't doing anything: in the most common configuration, it isn't.

---

## 5. The physics path and proxy decoupling

`RagdollStepper.cs`, `RagdollProxyBuilder.cs`

Stepping a ragdoll cannot be done the same way as stepping an Animator, and the
reason is worth spelling out because the workaround is arguably the most
defensible engineering idea in the project.

**The failure.** A ragdoll bone carries a non-kinematic `Rigidbody` constrained
by a `CharacterJoint`. If you hold its pose by writing `localRotation` directly,
PhysX sees a body that has teleported away from where its constraint says it
should be. The solver responds with a corrective impulse. That impulse is applied
every `FixedUpdate` and *accumulates*. Bodies that should fall straight down
begin accelerating sideways; the ragdoll doesn't just look wrong, it becomes
physically unstable and can diverge entirely.

**The resolution — decouple simulation from presentation.** At startup the rig is
cloned. The clone is stripped of all joints, rigidbodies, colliders, animators and
behaviours, leaving pure renderable geometry and a transform hierarchy. The
original continues simulating, untouched and never written to. The stepped poses
are written to the *clone*.

```
   source rig (physics)          visual proxy (transforms only)
   ─────────────────────         ─────────────────────────────
   Rigidbody + CharacterJoint    Transform
   simulated by PhysX            written by RagdollStepper
   never written to      ──────► sampled each FixedUpdate
   renderers hidden              renderers visible
```

Physics stays authoritative and correct. Stepping becomes purely a display
transformation. The two never fight because they no longer touch the same data.

Implementation details that matter:

- **The proxy is parented to scene root, not to the source.** If it were a child,
  anything that disables or destroys the source — object pooling, hit reactions,
  dismemberment — would take the proxy with it mid-animation. Scene-root parenting
  makes proxy lifetime explicit and independent.
- **Bone correspondence is path-based, not index-based.** `BonePathCache` builds a
  `"Hips/Spine/LeftArm"`-style key for every transform, giving O(1) source→proxy
  lookup. Index correspondence would silently break the moment a hierarchy gained
  or lost a node.
- **Recursion guard.** Every OnTwos MonoBehaviour implements the marker interface
  `IOnTwosComponent`. `Instantiate` copies all components including OnTwos's own,
  so without this the clone would build its own proxy — and so on. They're
  destroyed on the clone *before* it is activated, so their `Start` never runs.
- **Position coupling.** Position snaps when rotation snaps, so a bone doesn't
  slide while its orientation is frozen. An independent `PositionTau` catches the
  case where a body translates significantly without rotating — sliding along a
  flat surface.
- **Settle detection.** When all bodies stay below linear and angular velocity
  thresholds for `SettleTime`, the ragdoll is declared settled, the proxy locks,
  and `OnSettled` fires. A separate, higher wake threshold on the heaviest body
  ("anchor") resumes simulation if something disturbs it, reseeding the schedulers
  so the spline doesn't try to fit across the settle gap.

**The animated↔physics handoff.** The same solver-fighting failure applies before
activation: while a rig is animator-driven, `AnimationStepper` writes
`localRotation` to bones that may already carry non-kinematic rigidbodies. The
Ragdoll Wizard creates bodies non-kinematic by default, so this is the common
case. `OnTwosAuthoring` therefore holds all bodies kinematic while animated and
releases them — disabling the Animator first, zeroing velocities after the flip,
since PhysX discards velocity writes to a kinematic body — at activation.

---

## 6. Deriving smear from the same signal

A held pose diverges from the live pose. That divergence is not an error to be
minimised — it is *information*, and it is exactly the information a smear needs.

Traditional 2D animation pairs holds with **smears**: on the frame a fast motion
resolves, the artist draws the limb stretched along its path of travel. Holds and
smears are two halves of one technique — you hold to make poses read, and you
smear to keep the motion between them legible.

OnTwos already computes both poses every frame. The residual

```
smear = f(raw) − f(held)
```

is a vector whose direction is the axis of divergence and whose magnitude is how
far the display has fallen behind reality. Feed direction and magnitude to a
vertex shader and the mesh stretches along its own motion — with no separate
system, no hand-authored smear frames, and no additional sampling. The
stylisation falls out of data the stepping algorithm produces anyway.

One subtlety in the current implementation: `localRotation` composes onto the
*parent's* frame, not the bone's own, so the transform to world space must be
taken through `bone.parent`. Using the bone itself rotates the result through the
wrong basis.

**Status.** The whole-mesh version is implemented and correct: one vector, applied
uniformly, pushed to every `SkinnedMeshRenderer` via `MaterialPropertyBlock`. By
construction that can only produce a rigid slide of the entire mesh — every vertex
displaced identically — which is a wiring proof, not a smear. Real smear requires
per-vertex displacement, which requires per-bone vectors. That is designed but not
built; see §11.

---

## 7. Complexity and performance

Per bone, per frame, with window size `w` (default 30) and LUT size `L` (80):

| Stage | Cost | Frequency |
|---|---|---|
| Ring buffer append | O(1) | every frame |
| PCHIP refit (4 components) | O(w) | every frame |
| Arc-length LUT rebuild | O(L) quaternion evals | every frame |
| Extrema detection | O(4 · w/dt) scan + Brent refinements | every 10th frame |
| Candidate placement | O(n · log L) | every frame |
| Threshold walk | O(candidates) | every frame |

Asymptotically this is cheap — everything is linear in a small constant window,
and there is no global optimisation anywhere. The LUT is the main reason: it
converts what would be repeated numerical integration into two array lookups.

**The real cost used to be allocation, not arithmetic**, and the hot path is now
allocation-free in steady state. What it allocated, and what replaced it:

- `MonotoneCubicSampler.RebuildIfDirty()` allocated sample arrays, deduplication
  lists and their `ToArray()` copies, four component arrays, and four `Pchip`
  objects each allocating two more — roughly 20+ allocations, every frame, because
  every `Add()` marks the cache dirty. Now: every buffer is allocated once in the
  constructor and reused, deduplication is fused into the ring-buffer unroll so no
  intermediate array exists at all, and the four `Pchip` objects are refitted in
  place via `Pchip.Fit()` rather than reconstructed.
- `HoldFrameScheduler.Update()` allocated two `List<float>` per frame plus a fresh
  `float[]` per segment from `ArcLengthCandidates`. Now: both lists are fields that
  are `Clear()`ed (which keeps the backing array), and `ArcLengthCandidates` writes
  into a caller-owned buffer and returns a count.
- `ExtremaDetector.FindForBone()` allocated four closures and several lists every
  10th frame, and called `Derivative` — which evaluates all four quaternion
  components regardless — once per component, discarding three quarters of each
  result. Now: one fused scan over all four components, no closures (the component
  is an index, not a `Func`), and a `[ThreadStatic]` scratch list.
- Bone rules resolved `bone.name` and `ToLowerInvariant()` per bone per frame.
  Now: `BoneRuleSet` resolves exclusion, per-bone tau and per-bone response curve
  into index-parallel arrays and re-resolves only when the rules actually change,
  detected by reference comparison. Live editing in Play mode still works.

Measured on a 60-bone rig at locked cadence, 600 frames after warm-up:
**93 KB/frame → 0 B/frame** (5.3 MB/s → 0 at 60 fps). Locked-cadence output is
bit-identical to the previous implementation; the adaptive modes differ by at most
0.04° on a handful of frames, because extrema are now merged globally across
components rather than filtered per-component first.

This mattered before any Job System or Burst work, not after: parallelising an
allocation-bound workload does not help, and Burst cannot compile code that
allocates managed memory at all. That path is now unblocked.

Both steppers support optional visibility culling: when every renderer on the rig
is offscreen, the pose *writes* are skipped while the schedulers keep running, so
internal state stays coherent and there is no pop when the rig returns to view.

---

## 8. What actually sets it apart

An honest assessment, separating what's genuinely distinctive from what isn't.

**What is not novel.** The core mechanism — walk a curve, emit a new key when
deviation from the last exceeds a threshold — is decades old. It is the
Douglas–Peucker lineage applied to animation curves, it is what keyframe-reduction
research has published since at least the 2000s, and it is what the curve-simplify
tools in Maya, Motion and Nuke already do. Any claim of novelty resting on the
threshold walk alone would not survive a literature review.

**What is distinctive:**

1. **It runs online, without future knowledge.** This is the substantive
   difference. Every comparable tool is offline batch simplification of a fully
   known curve. OnTwos operates on a rolling window containing only the past, and
   must commit to a decision *this frame* that it can never revise. That constraint
   is what forces the whole architecture — the sliding window, the per-frame refit,
   the cached-and-filtered extrema. It's a genuinely harder problem than the offline
   version, and it's the reason the offline solutions can't simply be reused.

2. **The objective is stylisation, not compression.** Keyframe reduction minimises
   key count subject to an error bound; success is measured in bytes saved. OnTwos
   maximises a *look*; success is measured by whether it reads as hand-animated.
   Same mechanism, different objective function — which changes what "correct"
   means. This places it closer to non-photorealistic rendering than to compression.

3. **Arc-length candidate placement.** Distributing hold opportunities by rotation
   angle travelled rather than elapsed time is what makes the adaptive mode track
   the *content* of motion. This is the piece that most directly encodes "spend
   drawings where things happen."

4. **Physics-proxy decoupling.** Applying a temporal stylisation to a live physics
   simulation without corrupting it is a real engineering problem with a clean
   solution here. Stepping a ragdoll is otherwise impossible — not merely
   inaccurate, but divergent.

5. **One signal, two techniques.** Holds and smears are the same phenomenon
   measured two ways. Deriving both from a single held-vs-raw residual is elegant
   and, as far as I'm aware, not something existing tools do — they treat smear as
   a separate authored or simulated effect.

6. **Per-bone independence with optional lockstep.** Independent schedulers that
   *can* be phase-locked give both the metronomic classical look and deliberate
   desynchronisation, from one mechanism.

The honest summary: the *algorithm* is a reimplementation of well-trodden ground;
the *application* — online, per-bone, stylisation-targeted, physics-capable, with
smear falling out for free — is where the contribution actually lives.

---

## 9. Significance for game developers

**It works on motion that cannot be baked.** This is the practical argument. A
stepped animation clip can be authored in a DCC tool and imported — but that only
covers motion known ahead of time. It does not cover blend trees mixing locomotion
states by velocity, IK adjusting to uneven ground, ragdolls, motion matching, or
procedural secondary motion. All of those are computed at runtime, and all of them
can be stepped by this system because it operates on the *result*, whatever
produced it. `AnySource` mode requires no Animator at all — it steps whatever
writes to the bones.

**Style becomes a runtime parameter.** τ and cadence are live values, not baked
properties. They can be driven by gameplay: sharpen the cadence during a finisher,
soften it in cutscenes, run enemies at a coarser cadence than the player to
separate them visually, cross-fade profiles on state change. A baked clip can't do
any of this.

**Cost scales with what's visible.** One scheduler per bone per rig, offscreen
rigs skip their writes, and exclusion lists remove bones that don't need stepping.
The current allocation behaviour is the limiting factor for large crowds (§7) and
is a solvable engineering problem, not an algorithmic one.

**There's a shipping escape hatch.** The bake window runs the identical pipeline
offline and writes a standard `.anim` with constant-interpolation keys. If a
particular rig doesn't need runtime adaptivity, bake it and ship a clip with no
runtime dependency at all — same look, zero cost.

**Stylisation as a production strategy.** A stepped rig reads as intentional in a
way a low-framerate one does not. For small teams, cadence is one of the few
levers that buys a distinctive visual identity without buying more animation
hours — the look comes from *when* poses are shown, not from authoring more of
them.

---

## 10. Significance for animators

**It makes a century-old craft vocabulary available in 3D.** "On ones", "on twos",
"on threes" are terms animators already think in. In hand-drawn work the choice is
inseparable from the drawing process. In 3D it has historically meant either
manually stepping keys — laborious and destructive to edit — or accepting the
engine's every-frame interpolation. This makes cadence a **property you set on a
rig**, adjustable at any point, non-destructive.

**Cadence becomes characterisation.** *Spider-Verse*'s central insight was that
different characters can run at different rates in the same shot, and that the
difference *reads* — Miles on twos looks less certain than Peter on ones, before
either does anything. Per-bone control extends this within a single character:
Spider-Punk's jacket on its own rate from his body. Here that's a `BoneOverride`
entry, not a separate animation pass.

**Holds land on the motion, not on a metronome.** In adaptive mode the extrema
detection is doing something an animator would recognise as correct: holding
through the parts of a motion that carry little information, and spending
snaps at the extremes — the top of the arc, the reversal, the contact. It is
approximating the judgement of where to spend a drawing.

**Selective exclusion solves the classic problem.** The standard objection to
stepping a 3D character is that held feet break ground contact and slide, and held
IK gets discarded on hold frames. Keyword or per-bone exclusion keeps ankles and
toes on ones — corrected every frame, planted — while the body runs on twos. This
is the same compromise hand-drawn animators make when they hold a body and redraw
a contact point.

**It's non-destructive and reversible.** τ and cadence are inspector values.
Nothing in the source animation is modified — the stepping is a display layer over
the original curves. Turning the profile off returns the rig to its authored
motion exactly. Experimentation costs nothing, which matters when the parameter
being tuned is a subjective look.

---

## 11. Known gaps and limitations

Stated plainly, because a technical document that omits them isn't useful.

**Algorithmic**

- **Locked cadence bypasses the mathematics.** As established in §4.6, when
  `CadenceJitter = 0` the output is exactly "resample every 1/StepRate seconds"
  and the spline,
  extrema and arc-length work contribute nothing. Since locked cadence is the
  configuration that produces the classic look, the sophisticated path is not the
  one most users will run.
- **Arc-length candidates use a one-frame-stale LUT.** Within `Update()`,
  candidates are generated before the spline is rebuilt by the evaluation calls
  that follow, so they're placed against the previous frame's LUT. At animation
  sample rates the error is small, but it is a real off-by-one-frame in the
  reparameterisation.
- **Chain coherence is not modelled.** Each bone decides independently. A locked
  cadence moves the whole chain together so this rarely shows, but with jitter above
  zero a parent and child can snap on different frames, briefly bending a limb in a
  way the source motion never did.

**Performance**

- No Burst or Job System support. A plan exists (NativeArray restructuring,
  gather→job→scatter for transform access) but no code. The allocation work that
  used to block it is done (§7), so it is now unblocked rather than premature.

**Unbuilt features**

- **Runtime position stepping (bone-level).** `LiveAnimation.PositionTau` is
  bake-only; `RagdollStepper` uses the separate `Ragdoll.RagdollPosTau`. The
  animation path steps rotation only at the bone level. Character-level position
  holding is handled by `AnimationStepper.VisualOffsetRoot`, which solves foot
  sliding; per-bone position stepping at runtime remains unbuilt.
- **Per-bone smear.** Only the whole-mesh version exists, which can produce a rigid
  slide but not a true smear (§6). The per-bone design — per-bone displacement
  vectors from bone-tip offsets, a `ComputeBuffer` sized to bone count, and
  dominant-bone indices baked into UV2 because Unity resolves skinning before a
  standard `SkinnedMeshRenderer`'s vertex stage runs — is specified but unwritten.
  A known unsolved issue in that design: dominant-bone-only assignment will show
  seams at joint boundaries under fast motion, since vertices either side of a
  joint receive different displacements. Blending the top two bone weights would
  address it.

**Resolved since this document was first written**

- `TrajectoryRecorder` (with `RigidbodySnapshot`, `RigidbodySnapshotFrame` and the
  `SnapshotBufferSize` profile knob) captured every rigidbody every `FixedUpdate`
  and was never read. Removed.
- `DeviationThreshold.Walk()` had no callers — `HoldFrameScheduler` inlines its own
  threshold walk. Removed.
- `RagdollStepper.PhysicsRoot` was assigned but never read. Removed; the proxy
  builder clones the whole GameObject deliberately, so that renderers outside the
  physics hierarchy survive into the proxy.
- `RagdollStepper.PruneDestroyedBodies()` rebuilt the body, bone, scheduler and
  held-pose arrays when a limb was destroyed, but not `_excluded` or
  `_rawRotations`, leaving both index-misaligned against the rest. Exclusion is now
  owned by `BoneRuleSet` and re-resolved on prune, so that half cannot desync at
  all; `_rawRotations` is compacted in the same pass as everything else.
- The bake window applied keyword exclusion but not `BoneOverrides`. Fixed — it now
  passes the override list through.
- `OnTwosProfileEditor` evaluated two `HelpBox` conditions inline, so dragging a
  slider across a comparison boundary could emit a different number of controls in
  Unity's Repaint pass than Layout measured, desyncing the `GUILayout` stack and
  truncating everything below it. Both conditions are now evaluated once at the top
  of `OnInspectorGUI`.
