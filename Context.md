# OnTwos — Context Handoff

Current as of **2026-08-04**. This replaces the previous handoff, which had drifted
badly enough to cost real debugging time — it claimed several things were wired that
were not. Where something here is unverified, it says so.

Repo: `github.com/La-A7ad/OnTwos`, branch `main`. Unity 6000.3.17f1, URP.

For the research/paper position see `RESEARCH.md`. For the roadmap see `ToDo.md`.
For the smear work see `SMEAR.md`. This document is orientation and current state.

---

## The production model — read this first

This was undocumented until now and it changes how everything should be prioritised.

**The procedural `AnimationStepper` is an authoring tool, not a shipping system.**
Stepping is tuned in Play mode, baked into clips with the Bake Clip window, and the
baked clips drive normal animation states at runtime. No procedural animation stepping
ships.

**Physics ragdolls are the only thing that cannot be baked**, so `RagdollStepper` is
the only OnTwos system that actually runs in a shipped scene. It is therefore the only
one whose runtime cost matters.

Consequences:

- Optimising `AnimationStepper`'s runtime is close to worthless. Optimising the shared
  `HoldFrameScheduler` is worthwhile, because `RagdollStepper` and the **bake window**
  both drive it — bake speed is authoring-loop speed.
- Any change to `HoldFrameScheduler` must preserve bake parity exactly. A baked clip is
  promised to match what Play mode previewed (`BAKING.md`); silently shifting the beat
  would surface weeks later as clips that step on the wrong frames.
- Enemy pooling is planned and is treated as the user's own concern, out of scope here.

## Intended ragdoll lifecycle

Activate on demand → step the physics → settle on the ground and **stop calculating
entirely** while the proxy stays visible → **any** physics force (a punch, an explosion,
another body landing on it) wakes it and it steps that impact.

As of this session the code actually does this. Before it, "settled" only stopped
drawing: the bodies stayed in the PhysX solver forever, and waking was decided from the
heaviest body's velocity alone, so a punch to an outstretched arm moved the ragdoll in
physics while the proxy stayed frozen.

---

## Current state

| Subsystem | State |
|---|---|
| `AnimationStepper` | Working. Authoring-only by design. |
| `RagdollStepper` | Working. Settle→sleep→wake-on-contact lifecycle in place. |
| Bake window | Working; parity with Play mode verified this session. |
| `BoneTuning[]` | New. Per-bone tuning by direct Transform reference. |
| Bone-scale smear (`SquashStretch`) | Working, with a known ceiling — see `SMEAR.md`. |
| Shader smear | **Blocked.** Renders as blobs at the joints, cause unknown. |
| Burst / Jobs | Not started, and mostly demotivated — see `ToDo.md`. |

### The one blocked thing that matters

The shader smear is the only open item that would *add a capability* rather than polish
an existing one, and it is the idea with the most novelty potential (`RESEARCH.md`).
Everything else on the roadmap is performance or refinement.

It is blocked on three diagnostics that take about ten minutes and can only be run in
the editor. They are prepared as assign-and-look, no graph editing required —
`SMEAR.md` §"Not yet tested" has the order and what each outcome means. Start with
assigning `Runtime/CharacterSmear_NoVertexStage.shadergraph`.

---

## Orientation

The project reads as far larger than it is. Measured:

```
actual code (Runtime + Editor)   3,718 lines
comments                         1,286 lines  (22% — high)
documentation                   18,430 words  (~5x the reading burden of the code)
```

3,700 lines is small. The documentation is what makes it feel enormous, and some of it
overlaps. If reading time is the constraint, consolidating the docs is worth more than
touching the code.

### The whole system in four files

Read in this order and you have it — roughly 300 lines:

1. **`HoldFrameScheduler.Update()`** — the algorithm. Sample in, held pose out, and a
   decision in the middle about whether to snap.
2. **`RagdollStepper.FixedUpdate()`** — how that gets driven by physics.
3. **`RagdollProxyBuilder.Build()`** — the clone-and-strip decoupling. Shortest file
   here and the most defensible contribution.
4. **`MonotoneCubicSampler`** — the rolling window and the arc-length table.

`Pchip.cs` and `ExtremaDetector.cs` you do not need to understand. They are published
algorithms — Fritsch–Carlson (1980) and Brent's method — and you cite them rather than
defend them.

`AnimationStepper.cs` (759 lines) and `RagdollStepper.cs` (691) are the only genuinely
oversized files. Each does about six jobs: discovery, filtering, the per-frame loop, a
stack of `Resolve*` helpers, visual offset, the divergence signal, culling, proxy
lifecycle. They are where you would get lost, and they are the obvious split candidates.

---

## What changed on 2026-08-04

Two batches. **No features were added.** Bugs were fixed and things were made faster —
which is an accurate description of the project's recent trajectory generally.

### Correctness and cleanup

- **Prune desync.** `PruneDestroyedBodies()` rebuilt every index-parallel array except
  `_excluded` and `_rawRotations`, so destroying a limb left exclusion flags applied to
  the wrong bones. Exclusion is now owned by `BoneRuleSet` and re-resolved on prune,
  which makes that class of desync unrepresentable.
- **Snap event exposed.** `HoldFrameScheduler.DidSnap`. Replaces an angle-epsilon
  inference that could not distinguish a cadence snap on a barely-moving body from no
  snap at all.
- **Dead code removed.** `TrajectoryRecorder` (+ `RigidbodySnapshot`,
  `RigidbodySnapshotFrame`, the `SnapshotBufferSize` knob) captured every rigidbody
  every `FixedUpdate` and was never read. `DeviationThreshold` had no callers.
  `RagdollStepper.PhysicsRoot` was assigned and never used.
- **`BoneTuning[]`** — per-bone `Exclude` / `TauOverride` / `ResponseCurveOverride` by
  direct Transform reference. Lives on the stepper components, **not** on the profile:
  a profile is a shared asset and Unity nulls scene references on a ScriptableObject.

### Performance

- **Allocation pass.** 93 KB/frame → **0 B/frame** on a 60-bone rig in steady state.
- **Locked-cadence fast path.** At `CadenceJitter = 0` the forced beat always fires
  before the Tau branch, so the spline fit, the 80-point arc-length LUT and the extrema
  scan were computed and discarded every bone every tick. Now skipped.
  **40.4 µs → 1.5 µs** per 13-bone rig per tick.
- **Settle → sleep**, wake from `Rigidbody.IsSleeping()`. A resting corpse now costs
  nothing in either script time or the solver.
- **Redundant proxy writes** gated behind a dirty flag.

### Verification

An A/B harness runs the pre-session and current schedulers side by side on identical
input and compares frame by frame. Locked cadence: **0 mismatches across 17
configurations and 68,000 frames**, including a mid-run `Reset()`. That is what
protects bake parity.

**The harness currently lives in `/tmp` and will be lost.** Moving it into the repo as
an EditMode test is outstanding and worth doing — it lets correctness be checked by
running something rather than by reading code.

---

## Known traps

- **A one-body "ragdoll" fails silently.** A rig with a single Rigidbody on the root and
  no colliders logs `1 tracked bones` as ordinary info and then free-falls forever. This
  cost an hour of debugging on 2026-08-04 before the logger's velocity trace made it
  obvious (a clean 9.81 m/s² line with zero angular velocity — nothing was ever hit).
  Adding a loud warning for `< 2` bodies, and for tracked bodies with no collider, is
  offered but not implemented.
- **Sleep does not catch direct transform writes.** A settled body repositioned by
  writing its `Transform` bypasses PhysX and will not wake. Call `WakeUp()`.
- **Settling is now an abrupt freeze**, because `Sleep()` cuts micro-motion dead. If it
  pops, tighten `SettleVelocityThreshold` or lengthen `SettleTime`.
- **Branding is inconsistent.** Menu paths and log prefixes still say `CrunchyRagdoll`
  after the rebrand to OnTwos. Docs match the code; finishing the rename would move
  users' menu entries, so it was left as a decision rather than done.
