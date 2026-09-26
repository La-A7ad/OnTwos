# OnTwos — test suite

## What this covers, and what it does not

"Every way it could break" is not a reachable target and this does not claim it.
What it does claim: **every failure mode that can be reproduced without an imported
asset is now covered by a test that fails when it regresses.** The gaps are listed at
the bottom rather than left implicit.

Six of these started life as failing regression tests for defects found in the
2026-09-26 audit. All six defects are now fixed and the tests are ordinary guards — the
suite should be green. See "Regression guards" below for what each one protects.

---

## Prerequisites

### 1. A host Unity project

This repository is the package, not a project. The tests need somewhere to run.

**If OnTwos lives under `Assets/`** (the documented install), nothing further is needed —
Unity picks up the assemblies automatically.

**If OnTwos is consumed as a UPM package** (under `Packages/`, or via a local `file:`
dependency), test assemblies inside packages are hidden from the Test Runner unless the
package is declared testable. Add this to the host project's `Packages/manifest.json`:

```json
{
  "dependencies": { },
  "testables": [ "com.la-a7ad.ontwos" ]
}
```

Use whatever `name` the package's `package.json` declares. This is the single most
common reason a package's tests do not appear in the Test Runner window.

### 2. Unity Test Framework

Package Manager → `com.unity.test-framework`, 1.3.x or newer for Unity 6. Already
required by the existing EditMode tests, so it is almost certainly installed. Confirm
via **Window → General → Test Runner** — if that menu item is missing, the package is not.

### 3. Let Unity generate the `.meta` files, then commit them

The new files are checked in without `.meta`. Open the project once and Unity will
generate them. **Do not hand-write them** — a fabricated GUID that collides with an
existing asset is painful to unpick. Because this repo is used as a submodule, the
generated `.meta` files must then be committed, same as the existing ones.

New files needing meta generation:

```
Tests/SchedulerProbe.cs
Tests/PchipTests.cs
Tests/MonotoneCubicSamplerTests.cs
Tests/QuaternionSignNormTests.cs
Tests/ExtremaDetectorTests.cs
Tests/SchedulerContractTests.cs
Tests/BoneRuleSetTests.cs
Tests/README.md
Tests/PlayMode/                       (folder)
Tests/PlayMode/OnTwos.Tests.PlayMode.asmdef
Tests/PlayMode/RigFactory.cs
Tests/PlayMode/ProxyBuilderTests.cs
Tests/PlayMode/RagdollLifecycleTests.cs
Tests/PlayMode/AnimationStepperTests.cs
```

### 4. Default physics settings

The PlayMode tests build rigs from primitives and drop them on a floor, so they depend
on **Project Settings → Physics** being close to stock:

| Setting | Needed |
|---|---|
| Gravity | non-zero and downward (stock `(0, -9.81, 0)`) |
| Default Solver Iterations | stock (6) or higher |
| Sleep Threshold | stock (0.005); a much higher value makes bodies sleep before the stepper settles them |
| Auto Simulation / Simulation Mode | **Fixed Update** (the default). `Script` mode stops physics advancing and every settle test will time out |

`Time.fixedDeltaTime` is pinned to `0.02` per fixture and restored in teardown, so the
project's own value does not matter and is not disturbed.

### 5. Nothing else

No test scene, no prefabs, no imported rigs, no `AnimatorController`. Every rig is built
procedurally in `RigFactory`. This is a deliberate constraint: a suite that depends on
project content cannot run in CI and turns every failure into "is the code wrong, or did
the asset move?"

---

## Running

### In the editor

**Window → General → Test Runner**, then the **EditMode** or **PlayMode** tab.

PlayMode tests enter play mode. The editor must not be paused and the project must
compile cleanly first — a compile error hides the whole suite rather than failing it.

### From the command line

```sh
# EditMode
Unity -batchmode -projectPath <host-project> \
      -runTests -testPlatform EditMode \
      -testResults editmode-results.xml -logFile -

# PlayMode
Unity -batchmode -projectPath <host-project> \
      -runTests -testPlatform PlayMode \
      -testResults playmode-results.xml -logFile -
```

`-nographics` is safe here: no test asserts on `Renderer.isVisible`, because that
returns false under `-nographics` and would make the visibility-culling test pass for
the wrong reason.

---

## Layout

```
Tests/
├── OnTwos.Tests.asmdef            EditMode — pure logic, no scene, no Play mode
│   ├── SyntheticMotion.cs         deterministic rotation streams (pre-existing)
│   ├── SchedulerProbe.cs          reflection reader for private scheduler state
│   ├── PchipTests.cs              interpolation, no-overshoot, input contract
│   ├── MonotoneCubicSamplerTests  ring buffer, LUT validity, candidate placement
│   ├── QuaternionSignNormTests    the double-cover trap
│   ├── ExtremaDetectorTests       turning points vs. analytic ground truth
│   ├── SchedulerContractTests     DidSnap, Reset, cadence invariants, degenerate input
│   ├── BoneRuleSetTests           rule precedence and live-edit change detection
│   ├── LockedParityTests          cadence parity grid (pre-existing)
│   ├── AdaptiveDeltaTests         adaptive output baseline (pre-existing)
│   ├── AllocationTests            steady-state allocation (pre-existing)
│   ├── EvaluateIdentityTests      newest-knot bit identity (pre-existing)
│   └── ExtremaAlignmentTests      the RESEARCH.md §2a measurement (pre-existing)
│
└── PlayMode/
    └── OnTwos.Tests.PlayMode.asmdef   PlayMode — scene, physics, frame loop
        ├── RigFactory.cs              procedural rigs, profiles, lifecycle probe
        ├── ProxyBuilderTests.cs       clone/strip/pair, lifetime independence
        ├── RagdollLifecycleTests.cs   settle → sleep → wake, prune, exclusion
        └── AnimationStepperTests.cs   AnySource stepping, flush, exclusion, divergence
```

---

## Regression guards

These six were written as failing tests against defects found on 2026-09-26, then the
defects were fixed. They exist to notice if any of it comes back.

| Test | What it guards |
|---|---|
| `SchedulerContractTests.Adaptive_SnapsAreNeverCloserThanMinHoldSeconds` | The tau walk stamped `_lastSnapTime` with the winning candidate's time, which could be up to one sample window (~0.5 s) older than the previous snap. `heldFor` was then inflated and a step fired inside the `MinHoldSeconds` guard. Candidates at or before the last snap are now skipped. |
| `SchedulerContractTests.Adaptive_TheHoldClockNeverMovesBackwards` | The same defect read directly off the field, so a failure names the cause. |
| `SchedulerContractTests.Adaptive_NeverHoldsAPoseOlderThanTheWindow` | The visual half of the same defect: on a reversing motion the last qualifying candidate could come from the far end of the buffer, so the proxy displayed a pose from half a second earlier. |
| `BoneRuleSetTests.Sync_DetectsAnInPlaceEditToABoneTuningValue` | `Sync` compared `_tunings` by reference only, so editing `TauOverride` or `Exclude` on an existing element never re-resolved. Values are now snapshotted and compared. |
| `BoneRuleSetTests.Sync_DetectsAnInPlaceEditToABoneOverrideValue` | `OverridesChanged` compared only `NameContains`, so `ForceExclude` and `TauOverride` edits were invisible. |
| `ProxyBuilderTests.Build_DoesNotRunGameScriptLifecycleOnTheClone` | `Instantiate` of an active source ran the game's own `Awake`/`OnEnable` on the clone before the strip pass. The clone is now built inside a deactivated holder. |

The first three matter beyond the bug: the adaptive path is what `ExtremaAlignmentTests`
measures for `RESEARCH.md` §2a, so **that sweep must be re-run on the fixed behaviour
before its numbers go into a paper.**

---

## Not covered

Stated plainly, because a test suite that implies more coverage than it has is worse
than one that admits its edges.

- **The bake window** (`Editor/OnTwosBakeWindow.cs`). Needs an `AnimationClip` asset and
  a rig instance, and writes `.anim` files to disk. Testable, but only with fixture
  assets — which is the dependency everything else here avoids. **Bake parity is
  currently asserted by `LockedParityTests` on the scheduler alone, not end to end.**
- **`AnimatorDriven` mode and `AnimatorStateWatcher`.** Needs an imported rig and an
  `AnimatorController`. `AnySource` exercises the same read-step-write path.
- **`SquashStretch` and the smear path.** The bone-scale technique is visual, and the
  shader path does not render correctly yet (`SMEAR.md`).
- **`OnTwosAuthoring`, `OnTwosAutoBinder`.** The auto-binding heuristics are testable
  procedurally and are the obvious next addition.
- **Editor inspectors and the preview window.** No coverage; the `GUILayout` desync
  class of bug documented in `TECHNICAL.md` §11 would not be caught.
- **Cross-platform determinism.** PhysX results differ between platforms and Unity
  versions. Every PlayMode assertion here is a tolerance or a timeout, never an exact
  pose, for exactly this reason.
- **Whether any of it looks right.** No test can answer that; see `RESEARCH.md` §6.
