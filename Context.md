# OnTwos — Context Handoff

> **Historical document, superseded in places.** Kept for the design reasoning and
> the research/novelty discussion, which still stand. For current status read
> `ToDo.md` (roadmap), `TECHNICAL.md` §11 (known gaps) and `SMEAR.md` (the smear
> work) instead — those are maintained; this is not.
>
> Specifically stale as of 2026-08-03: the bug list below is largely fixed. The
> `OnTwosProfileEditor` IMGUI desync (#5), the hot-path GC allocation (#3) and the
> `PruneDestroyedBodies` desync are all resolved. #7's `_observedMaxSpeed` decay
> concern is moot — that self-calibrating version was never committed, and the
> shipped code divides by `Mathf.Max(1f, MaxDegreesPerSecond)`, so there is no
> underflow to guard against. The `BoneTuning[]` list described as "designed, not
> implemented" is now built, though it lives on the stepper components rather than
> the profile — a ScriptableObject cannot hold a scene reference.

Repo: `github.com/La-A7ad/OnTwos` (main branch). Unity 6000.3.17f1. Likely URP (assumed from Shader Graph use, not explicitly confirmed).

## What This Project Is

A Unity package that takes normally-smooth motion (Animator, IK, physics) and forces it to update only every N frames instead of every frame, holding the rest. This recreates the "shoot on twos/threes" technique from hand-drawn/stop-motion animation, and the same technique Spider-Verse uses (Miles animated on twos while Peter runs on ones in the same shot; Spider-Punk on threes with his jacket on a separate rate from his body).

Two entry components:
- **`AnimationStepper`** (Runtime/AnimationStepper.cs) — bone/Animator-driven rigs
- **`RagdollStepper`** — physics/Rigidbody-driven rigs. Needs a visual proxy (`RagdollProxyBuilder`) because writing `localRotation` directly onto a non-kinematic Rigidbody under a `CharacterJoint` fights the physics solver. The proxy clones the skin, strips physics, and is driven from snapshots instead. This proxy-decoupling trick is probably the single most defensible "novel-ish" piece of the whole project if this ever becomes a paper.

## Core Algorithm (HoldFrameScheduler.cs)

Per bone, per frame: fits a PCHIP curve through recent rotation samples, finds extrema (turning points), places arc-length candidates within each monotone segment, and snaps to a new held pose when deviation from the current held pose exceeds `Tau` (degrees). One `HoldFrameScheduler` instance per bone, fully independent per-bone state, confirmed by direct code read.

**Cadence lock (`MinHoldFrames`/`MaxHoldFrames`)**: added to `OnTwosProfile.LiveAnimationSettings` and wired into `AnimationStepper` via `ResolveMinHoldFrames()`/`ResolveMaxHoldFrames()`. Critical mechanism: in `HoldFrameScheduler.Update()`, `forceSnap` (Max reached) is checked *before* `allowSnap` (Tau-gated), so when **Min == Max exactly**, `forceSnap` always wins, bypassing Tau entirely, and every bone snaps in perfect lockstep since all counters start at 0 in `Start()` together. This is what gives true metronomic "on twos" cadence. When Min < Max, bones can snap early via Tau-crossing during the gap window, causing per-bone desync, this reads as stutter if unintentional, but is also a legitimate *intentional* effect (proposed name: `CadenceJitter`) for glitch/desync looks, same idea as Spider-Punk's jacket running on its own rate.

**Not yet built**: per-bone tuning currently only works via `BoneOverride[]` (keyword-matched `NameContains`, `TauOverride`, `ForceExclude`). User explicitly rejected automatic detection (e.g. `HumanBodyBones`-based foot exclusion) and wants a manual, rig-agnostic `BoneTuning[]` list instead: direct `Transform` references (drag-and-drop, not string matching), each with `Exclude`, `TauOverride`, and `ResponseCurveOverride` (the last doesn't exist anywhere yet, `ResponseCurve` is currently global-only on `Profile.Global`). Not implemented, just designed.

**Multi-component warning**: putting a second `AnimationStepper` on a child limb transform would NOT cleanly scope to that limb, it would overlap with the root stepper's `GetComponentsInChildren` walk and both would fight over the same bones' `localRotation` every frame. Stick to one component per rig; per-bone variation comes from the `BoneTuning`/override list, not multiple component instances.

## Bugs Found (Confirmed in Code, Not Speculation)

1. **Doc drift**: `Samples~/README.md` says `Assets → Create → OnTwos → Profile`, actual menu path (verified in `OnTwosProfile.cs`'s `[CreateAssetMenu]`) is `CrunchyRagdoll/Profile`.
2. **`ToDo.md` is stale**: claims "Procedural/non-Animator mode" and "AnimatorStateWatcher made optional" are still pending, both are actually implemented (`StepperMode.AnySource` exists and works; `_stateWatcher` is null-safe). Genuinely still open: **position stepping at runtime** (PositionTau is bake-only, never read by `AnimationStepper` at runtime) and **Burst/Job System pass** (zero references to `Unity.Jobs`/`Unity.Burst` anywhere).
3. **Real GC allocation problem in the hot path**: `HoldFrameScheduler.Update()` allocates two `new List<float>` every call, every bone, every frame. `ExtremaDetector.FindForBone()` allocates up to four `List<float>` every ~10 frames per bone. `MonotoneCubicSampler.ArcLengthCandidates()` allocates a fresh `float[]` every call. This is very likely the actual CPU cost driver at scale (many ragdolls), not raw math, and should be profiled/fixed (pooled/reused buffers) *before* reaching for Jobs/Burst parallelization.
4. **`OnTwosAuthoring`'s `AutoCreateProxy` warning is a false alarm for pure animation testing.** It only concerns the ragdoll/physics proxy path (needs Rigidbodies to clone), has nothing to do with `AnimatorRoot`/`AnimationStepper`. Uncheck `AutoCreateProxy` if not using ragdoll, or just ignore the warning.
5. **`OnTwosProfileEditor.cs` has a genuine Unity IMGUI bug**: two conditionally-drawn `HelpBox` calls (Ragdoll Min>Max warning, Settling threshold warning) can differ between Unity's Layout and Repaint passes (classic trigger: dragging a slider value across the comparison boundary mid-frame), causing a GUILayout stack desync that throws "You can't nest Foldout Headers, end it with EndFoldoutHeader" and cuts off rendering of everything after it (including `ExcludeKeywords` and `BoneOverrides`). **Immediate workaround**: comment out `[CustomEditor(typeof(OnTwosProfile))]` to fall back to the default Inspector. **Real fix**: cache both booleans once at the top of `OnInspectorGUI()` instead of evaluating them inline where they're used.
6. **`ExcludeKeywords` gotcha**: `AnimationStepper` has its own local `ExcludeKeywords` field that does *nothing* once a `Profile` is assigned (`ResolveExcludeKeywords()` is a ternary, not a merge, Profile always wins outright). Also, exclusion is computed once in `Start()`, editing the profile's array mid-Play-mode has no effect until you stop and re-enter Play.
7. **`ComputeMotionIntensity` was framerate-dependent**: originally divided raw degrees-per-frame by a fixed `45f`. Patched to degrees-per-second (`/ Time.deltaTime`) with a new serialized `MaxDegreesPerSecond` field. Later upgraded to a self-calibrating `_observedMaxSpeed` (decays via `* 0.999f` per frame, rises to match new peaks) so it doesn't need per-clip manual retuning. **Needs a hard floor** (`Mathf.Max(_observedMaxSpeed, MaxDegreesPerSecond * 0.1f)`) to prevent geometric decay eventually underflowing to exactly 0.0 during a long idle (~24 min at 60fps), which would cause a divide-by-zero/NaN. Not confirmed whether this floor line actually made it into the file.
8. **Foot clipping**: two possible causes distinguished. (a) Chain incoherence from asynchronous per-bone snapping, likely mitigated once `Min == Max` is locked, since the whole chain now moves together. (b) Foot IK getting re-frozen by the stepper if ankle/toe bones aren't excluded, IK corrects every frame and the stepper would discard that correction on held frames. Fix for (b): add `"foot"`/`"toe"` to the **Profile's** `ExcludeKeywords` (see gotcha #6).

## Smear Shader — Current Status

**Concept**: derive smear entirely from the same held-vs-raw divergence that already drives stepping (no separate system, no hand-authored smear frames, procedural by construction). This is the "one piece of live data, two classic 2D techniques for free" idea, considered the most genuinely interesting part of the whole project in the innovation discussion below.

**What's actually implemented and confirmed correct (whole-mesh MVP only)**:
- `AnimationStepper` computes `_rootSmearVector` from `_bones[0]` each `LateUpdate()`, pushes `_SmearDirection` (normalized) and `_SmearStrength` (magnitude) via `MaterialPropertyBlock` to all `SkinnedMeshRenderer`s.
- Shader Graph: `Position` (Object space) + `Transform` node (World→Object, Direction) applied to `_SmearDirection`, multiplied by `_SmearStrength`, added to `Position`, feeds Vertex Position. Reviewed via screenshot and confirmed correctly wired after three bugs were fixed: missing `_propertyBlock = new MaterialPropertyBlock()` in `Start()` (was throwing NullReferenceException every frame, meaning nothing rendered), a world/object space mismatch (fixed by the Transform node), and a squared-magnitude bug (SmearDirection originally carried full magnitude *and* SmearStrength duplicated it, fixed by normalizing direction separately from strength).

**Still open, nothing further implemented**:
- `_bones[0]` is very likely the non-moving Armature root, not the Hips bone that actually carries motion. Recommended fix (not confirmed applied): replace with a manually assigned `public Transform SmearReferenceBone`.
- User reported "can't notice the difference." Recommended test: temporarily hardcode `_SmearStrength = 5f` to confirm the pipeline is live before judging the real computed value.
- **Structurally confirmed**: the whole-mesh version can only ever produce a rigid slide of the entire mesh (same vector added to every vertex), never a real smear (different vertices need different displacement amounts). This was always meant as a wiring proof, not the final look.
- **The real per-bone/per-vertex version is fully designed but zero code exists for it.** Plan:
  1. Per-bone `_smearVectors[i] = rawTip - heldTip`, where `heldTip`/`rawTip` are `bone.TransformPoint(rotation * tipOffset)`. Tip offset is necessary because pure rotation produces zero displacement *at* the pivot, only away from it, this is the actual justification, not an approximation shortcut.
  2. `_boneTipOffsets[i]` cached once in `Start()`, direction toward first child; should be the **average of all children** for branching joints (hips, shoulders), not just `GetChild(0)`, otherwise branch joints get a nonsense direction (given as a fix, not confirmed applied).
  3. `ComputeBuffer` sized dynamically as `_bones.Length` (not a hardcoded shader array), so it works on any rig regardless of bone count. Needs `_smearBuffer.Release()` in `OnDestroy()` to avoid a native memory leak (given, not confirmed applied).
  4. **Key correction from earlier in the thread**: Unity resolves skinning *before* a material's shader runs for a standard (non-DOTS) `SkinnedMeshRenderer`, so `BLENDWEIGHTS`/`BLENDINDICES` are NOT readable in a normal Shader Graph vertex stage (the `Linear Blend Skinning` node only works with Entities Graphics/DOTS). Workaround: bake each vertex's **dominant** bone index into UV2 once via C# (`mesh.boneWeights` + `smr.bones[]`, matched against the stepper's own `_bones[]` array by Transform reference, since `SkinnedMeshRenderer.bones[]` and `_bones[]` are NOT guaranteed to be in the same order). Shader reads `UV2.x`, casts to int, indexes into `_SmearVectors[boneIndex]`.
  5. **Known, unresolved limitation, user independently flagged this as worse than first described**: dominant-bone-only assignment will show a visible seam/gap at joint boundaries under heavy/fast motion, since two nearly-coincident vertices on either side of a joint can be assigned to different bones with different smear vectors, pulling them apart by different amounts. Not yet solved. Possible future fix: blend top-2 bones instead of picking one, contained upgrade, not a redesign.

**Tensor discussion (settled, don't revisit unless building the upgrade)**: current whole-mesh operation is vector math (`Position + Direction × Strength`), correctly so. A tensor (3×3 matrix, stretch-and-squash with volume preservation) would NOT fix the granularity problem (uniform mesh-wide movement vs. per-vertex), that's solved by the per-bone/UV2 system above regardless of vector-vs-tensor. A tensor is a legitimate *future* upgrade for making a single bone's own contribution look like real stretch-and-squash rather than a flat offset, layered on top of per-bone granularity, not a substitute for it.

## Research/Novelty Discussion (For If This Becomes a Paper)

- Core deviation-threshold curve-simplification algorithm is **not novel**, decades of prior art (2008 IEEE keyframe-reduction paper, Maya/Motion/Nuke tools, Douglas-Peucker lineage). A related-work section would make the core algorithm look like reimplementation.
- What's more defensible: doing this **online/streaming** (no future knowledge, per-bone, live) rather than offline batch simplification of a known curve, and targeting **stylization** rather than data compression, that's closer to NPAR (non-photorealistic rendering) research territory, though no exact prior-art hit was found for this specific combination.
- Honest problem: once `Min == Max` is locked to fix stutter, `Tau`/`GaussPoints`/`ResponseCurve` go inert for timing purposes (forceSnap bypasses Tau entirely), the working configuration degenerates to "just update every Nth frame," which anyone could write without any of the PCHIP/extrema machinery. The sophisticated math only earns its keep in the *adaptive* (non-locked) case.
- Parallelization (Jobs/Burst) is not itself a research contribution, standard Unity practice. It matters as evidence supporting a systems claim ("scales to N simultaneous ragdolls"), not as the claim itself. Should profile and fix the GC allocation bugs above first, that's likely the actual bottleneck, not raw CPU math.
- CUDA was correctly ruled out (Nvidia-only, would exclude AMD/Steam Deck/Mac). The smear `ComputeBuffer`/Shader Graph work and Jobs/Burst are both cross-vendor already, no changes needed there.
- **Final honest verdict given**: the idea (deriving both stepping and smear from one shared held-vs-raw divergence signal, plus the physics-proxy decoupling trick) is genuinely worth pursuing. But as of the last message in this thread, nothing in the per-bone smear chain is confirmed running. "Building something worthwhile" is accurate; "have built" is not, yet.

## Immediate Next Steps (Where This Left Off)

1. Confirm whole-mesh smear is actually visible (hardcoded `_SmearStrength = 5f` test) and swap `_bones[0]` for a manual `SmearReferenceBone` field.
2. If confirmed working, implement the full per-bone/UV2 system (steps 1-4 above), none of it is in the file yet.
3. Decide whether the joint-boundary seam from dominant-bone-only assignment is acceptable or needs top-2 blending.
4. Separately, still-open from earlier: the `BoneTuning[]` rig-agnostic manual override list (replacing keyword matching) hasn't been built either.
5. Separately, still open: profiling the actual GC allocation cost before any Jobs/Burst work, and adding the `_observedMaxSpeed` hard floor.

## Old ToDo.md Roadmap Items — Honest Status Audit

Six items were carried over from an older ToDo. Real status, checked against the code, not assumed:

1. **Position stepping at runtime (`AnimationStepper`)** — still fully unbuilt. `PositionTau` exists as a profile field and is used at bake time only, `AnimationStepper.cs` never reads it at runtime. Needs a runtime visual-proxy architecture parallel to `RagdollStepper`'s, deferred as a significant undertaking, not started.
2. **Procedural / non-Animator mode (`AnySource`)** — already done, but predates this conversation entirely. `StepperMode.AnySource` was already fully implemented in the existing code; the only actual work here was correcting the stale `ToDo.md` claim that it wasn't done.
3. **`AnimatorStateWatcher` made optional** — same as above, already implemented and working before this conversation, `ToDo.md` was simply out of date.
4. **Burst / Job System pass** — not implemented, only analyzed. A specific architectural plan exists (NativeArray restructuring, gather→job→scatter for Transform access), but no code was written. More useful finding: real per-frame heap allocations in the hot path (`HoldFrameScheduler.Update()`, `ExtremaDetector.FindForBone()`, `MonotoneCubicSampler.ArcLengthCandidates()`) are probably the actual bottleneck and should be profiled/fixed before attempting the Burst conversion.
5. **Parallel processing of multiple ragdolls (`RagdollStepper.FixedUpdate()`)** — not implemented, same unfinished category as item 4, just the ragdoll-specific framing of the identical open work (Job System/Task parallelism, thread safety around shared resources, handling simultaneous multi-ragdoll death bursts). Nothing written.
6. **Smearing** — the only item with real forward motion, but not finished. Fixed: a `NullReferenceException` blocker (`_propertyBlock` never initialized), a world/object space mismatch in the Shader Graph, and a squared-magnitude bug. Confirmed correct: the whole-mesh Shader Graph wiring (verified via screenshot). Still unconfirmed: whether it's actually visible in Play mode, `_bones[0]` (likely a non-moving root) is the suspected culprit, untested. Not started at all: the real per-bone/UV2 version, no code exists for it yet.

**Bottom line**: of the six, two (items 2-3) were already complete before this conversation and only needed documentation fixed, three (items 1, 4, 5) are exactly as unbuilt as when the ToDo was written, and one (item 6) has real but incomplete progress. If resuming, smearing is the only thread with actual momentum, everything else is a cold start.
