# Roadmap

Status audited against the code on 2026-08-03. Items that turned out to be
already implemented have been removed rather than left to rot — the previous
version of this file listed `AnySource` mode and optional `AnimatorStateWatcher`
as pending long after both shipped, which cost real time to rediscover.

## Open

- **Runtime per-bone position stepping in `AnimationStepper`.**
  `LiveAnimation.PositionTau` is bake-time only; the runtime animation path steps
  rotation only. Character-level position holding already works via
  `AnimationStepper.VisualOffsetRoot` (this is what fixes foot sliding), so what
  remains is genuinely per-bone translation, which needs a runtime visual proxy
  parallel to the one `RagdollStepper` builds. Significant new architecture.

- **Burst / Job System pass over the scheduler pipeline.**
  Plan exists: `NativeArray` restructuring, gather→job→scatter for transform
  access. No code. Both blockers are gone (the hot path is allocation-free and
  locked cadence no longer runs the pipeline at all), but so is most of the
  motivation: in the default locked configuration there is now very little left to
  jobify. Worth revisiting only if `CadenceJitter > 0` becomes a shipping
  configuration, where the full spline pipeline does still run per bone per tick.

- **Parallel processing of multiple ragdolls.**
  The ragdoll-specific framing of the item above: distribute
  `RagdollStepper.FixedUpdate()` across cores so a burst of simultaneous deaths
  doesn't spike a frame. Needs synchronisation around shared state and care with
  main-thread-only Unity APIs.

- **Smear rendering.** See `SMEAR.md` — the signal layer and the bone-scale
  technique are done and working; the shader path is blocked on an unexplained
  skinning failure with three untested diagnostics queued.

- **Chain coherence.** Each bone still decides independently. Invisible at
  `CadenceJitter = 0`; above zero a parent and child can snap on different frames
  and briefly bend a limb in a way the source motion never did.

- **Arc-length candidates use a one-frame-stale LUT.** Candidates are generated
  before the spline is rebuilt by the evaluation calls that follow, so they are
  placed against the previous frame's LUT. Small at animation sample rates, but a
  real off-by-one-frame in the reparameterisation.

## Done

- **Ragdoll settle/wake lifecycle.** Settling now puts every tracked body to
  `Sleep()` and returns from `FixedUpdate` immediately, so a resting corpse costs
  nothing in either script time or the PhysX solver — previously `_settled` only
  short-circuited the visual writes while the joint island stayed in the solver
  forever. Waking is read from `Rigidbody.IsSleeping()` instead of a velocity
  threshold on the heaviest body, which fixes a real defect: settling considered
  every body but waking considered one, so an impact that moved an outstretched limb
  without shifting the hips left the proxy frozen at its settled pose while the
  ragdoll visibly moved underneath it. Jointed bodies share one PhysX island, so any
  contact on any limb now wakes the whole rig. `WakeVelocityThreshold`, `AnchorWoke()`
  and the anchor-index bookkeeping are gone.

- **Locked-cadence fast path.** `forceSnap` is tested before the Tau branch, so at
  `CadenceJitter = 0` the candidate walk is unreachable and the spline refit, the
  80-point arc-length LUT and the extrema scan were computed and discarded every bone
  every tick. `HoldFrameScheduler` now detects the locked case and skips the pipeline
  while preserving every timing-relevant branch. **40.4 µs → 1.5 µs per 13-bone rig
  per tick** (50 ragdolls: 2.02 ms → 0.08 ms of a 20 ms budget), and baking gets the
  same speedup. Verified bit-identical across 17 step-rate/framerate configurations
  and 68,000 frames including a mid-run `Reset()`, which is what preserves parity
  between baked clips and the Play mode preview.

- **Redundant proxy writes removed.** `FixedUpdate` and `LateUpdate` together asked to
  write the proxy pose 50-110 times a second to express ~12 actual pose changes. A
  dirty flag now gates both. Bones excluded from stepping still write every tick, as
  they must — they follow physics unstepped.

- **Hot-path allocation pass.** `Pchip` refits in place, `MonotoneCubicSampler`
  fuses dedup into its ring-buffer unroll and reuses every scratch array,
  `HoldFrameScheduler` pools its boundary/candidate lists, `ExtremaDetector`
  scans all four quaternion components in one pass with no closures, and
  `BoneRuleSet` resolves bone rules once instead of re-deriving them from bone
  names every frame. Measured on a 60-bone rig at locked cadence: **93 KB/frame
  → 0 B/frame** in steady state (5.3 MB/s → 0). Locked-cadence output is
  bit-identical to before; adaptive modes differ by at most 0.04° on a handful of
  frames, from the extrema merge now being global across components rather than
  per-component.

- **`BoneTuning[]` per-bone tuning by direct Transform reference.** Rig-agnostic
  drag-and-drop with `Exclude`, `TauOverride` and `ResponseCurveOverride`. Lives
  on the stepper components, not on `OnTwosProfile` — a profile is a shared asset
  and Unity cannot serialise a scene reference on a ScriptableObject.

- **`RagdollStepper` prune desync.** `PruneDestroyedBodies()` rebuilt every
  index-parallel array except `_excluded` and `_rawRotations`, so after a limb was
  destroyed exclusion flags applied to the wrong bones and motion intensity read
  garbage. Exclusion is now owned by `BoneRuleSet` and re-resolved on prune, which
  makes that class of desync unrepresentable; `_rawRotations` is compacted.

- **Snap event exposed.** `HoldFrameScheduler.DidSnap` reports whether the last
  `Update` emitted a new pose. Replaces `RagdollStepper`'s angle-epsilon inference,
  which could not distinguish a real cadence snap on a barely-moving body from no
  snap at all. Also unblocks the ghost-pose smear option.

- **Dead code removed.** `TrajectoryRecorder` (plus `RigidbodySnapshot` /
  `RigidbodySnapshotFrame` and the `SnapshotBufferSize` profile knob) captured
  every rigidbody every `FixedUpdate` and was never read. `DeviationThreshold` had
  no callers — `HoldFrameScheduler` inlines its own threshold walk.
  `RagdollStepper.PhysicsRoot` was assigned but never used; the proxy builder
  always clones the whole GameObject by design, so that renderers living outside
  the physics hierarchy survive into the proxy.

- **Procedural / non-Animator mode** (`StepperMode.AnySource`) — shipped.
- **`AnimatorStateWatcher` optional** — shipped; `_stateWatcher` is null-safe.
