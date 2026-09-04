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

- **The τ walk overwrites extrema it lands on.** The walk advances through every
  candidate exceeding τ and keeps the last one, so an extremum is visited and then
  discarded whenever a later candidate also qualifies. This is why making extrema
  eligible moved peak error in only some configurations. Stopping the walk at an
  extremum would land poses on the extremes reliably, but §4.5 chains deliberately to
  keep the held pose current rather than lagging — an aesthetic trade, not a defect.

- **Extrema are detected up to ten frames late.** `ExtremaDetector` runs every tenth
  frame, so a turning point is on average five and at worst ten frames old before it
  can be a candidate at all. At 60 fps and `StepRate = 12` that is one to two entire
  step intervals. Scanning more often is the obvious fix and directly inflates the
  adaptive path, already ~180× the cost of the locked one.

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
  same speedup. Originally verified bit-identical across 17 step-rate/framerate
  configurations and 68,000 frames including a mid-run `Reset()`, on a scratch A/B
  harness that no longer exists; the in-repo replacement covers 20 configurations and
  is the version that can actually be re-run. Either way it is what preserves parity
  between baked clips and the Play mode preview. Bit-identity is a property of this
  path specifically, which copies the incoming rotation; the general `forceSnap`
  branch evaluates the curve instead and agrees only as a rotation, not as bits.

- **Extrema are snap candidates.** Arc-length placement is strictly interior to each
  monotone segment, so the detected extrema — the segment boundaries themselves —
  were never eligible: the walk could snap either side of a turning point but never
  on it, which is the opposite of spending a pose where the motion happens. Interior
  boundaries are now added to the candidate list, and the merged list is deduplicated
  at one frame, matching `ExtremaDetector.MinSegment`. Locked output is byte-identical
  across all 20 harness configurations — verified by signature diff, not inspection —
  and the hot path stays at 0 B/frame.

  The measured effect is real but not a clean win. Every adaptive configuration's
  output changed, so the fix is not inert. Peak deviation from the source improved in
  two of seven configurations, most at 12 poses/sec and 144 fps (17.69° → 15.08°), was
  unchanged in four, and worsened in one (24.48° → 25.37°). Mean deviation moved by at
  most 0.32° either way. Adaptive cost rose ~2%, 269 → 275 µs per 13-bone rig per tick.
  The two items under Open explain why the benefit is inconsistent.

- **Arc-length candidates use a current LUT.** `ArcLengthCandidates` reads the LUT
  arrays directly and never refits — deliberate, and what keeps placement
  allocation-free — but nothing else brought the spline up to date for the tick
  either. The extrema scan runs every tenth frame and the τ walk's `Evaluate` calls
  come afterwards, so on most ticks candidates were placed against an older LUT, and
  a bone inside its `MinHoldSeconds` window evaluated nothing at all. The newest-knot
  evaluation the forced-snap branch already performed is now hoisted above the extrema
  scan, so the refit happens before anything reads the LUT and the value is reused
  rather than computed twice.

  This was previously recorded here as one frame of staleness. Measured, it reached
  **22.4° of candidate displacement** at a ten-frame gap — wider than a typical τ, so
  it was moving snap decisions rather than rounding them. Unlike the extrema change
  above, the fix is a clean win: peak deviation improved in all seven adaptive
  configurations (best 25.37° → 18.05° at 8 poses/sec) and mean deviation in five of
  seven. Locked output is byte-identical across all 20 harness configurations, so bake
  parity is untouched. Adaptive cost rose 279 → 329 µs per 13-bone rig per tick,
  because ticks that previously evaluated nothing now pay for a refit; allocation
  stays at 0 B/frame.

- **EditMode test harness.** `Tests/` drives `HoldFrameScheduler` directly with
  synthetic rotation streams — no scene, Animator or Play mode. Covers locked-cadence
  behaviour across a 20-configuration step-rate × framerate grid (every emitted pose
  is bitwise a raw input sample; the beat holds its grid without cumulative drift;
  output is constant between snaps), steady-state allocation, and how often
  evaluating at the newest knot reproduces the input bitwise. 89 cases. The
  performance and parity numbers in this file and in `TECHNICAL.md` §7 previously had
  no artifact behind them and could not be re-run; now they can.

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
