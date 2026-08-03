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
  access. No code. The allocation work that used to block this is done (the hot
  path is now allocation-free in steady state), so this is unblocked.

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
