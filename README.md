# CrunchyRagdoll

Stop-motion / stepped animation for live humanoids and death ragdolls, ported from
the [CrunchyRagdoll BepInEx mod for ULTRAKILL](https://thunderstore.io/) into a
self-contained Unity asset.

The idea: sample the underlying continuous motion (animator output or rigidbody
trajectory), fit it with monotone cubic splines, and replay it in **stepped frames**
chosen by an arc-length-weighted deviation threshold. The result reads as
hand-animated stop-motion rather than smooth linear interpolation.

## What you get

- **`AnimationStepper`** — applies the stepping to a live `Animator`-driven rig.
  The animator keeps running normally; the stepper just visually quantizes the
  output bones each frame.
- **`RagdollStepper`** — snapshots a physics ragdoll, builds a transform-only
  visual proxy, hides the source, and replays the snapshot stream in stepped
  frames. Settles when motion drops below threshold; wakes if hit again.
- **`CrunchyRagdollProfile`** — a ScriptableObject holding all the tuning
  (τ values, hold-frame bounds, settle thresholds, bone-exclusion keywords,
  per-bone overrides). Drop one onto an authoring component and you're done.
- **`CrunchyRagdollAuthoring`** — single MonoBehaviour entry point. Replaces the
  Harmony patch in the original mod with a clean `GoLimp()` method you call from
  your own death code.

## Install

Drop the `CrunchyRagdoll` folder anywhere under `Assets/` in your project. The
two assembly definitions (`CrunchyRagdoll.Runtime`, `CrunchyRagdoll.Editor`) keep
editor code out of player builds automatically.

Unity 2021.3 LTS and newer should work. Unity 6 is supported — the runtime uses
`#if UNITY_6000_0_OR_NEWER` to select `Rigidbody.linearVelocity` vs the legacy
`Rigidbody.velocity`.

## Quick start

1. `Assets → Create → CrunchyRagdoll → Profile` to make a profile asset.
2. Add `CrunchyRagdollAuthoring` to a humanoid character GameObject.
3. Drag the profile onto the authoring's `Profile` slot.
4. Add `AnimationStepper` and/or `RagdollStepper` to the same GameObject.
5. For ragdoll: call `GetComponent<CrunchyRagdollAuthoring>().GoLimp()` from
   whatever your game does on death.

## Folder layout

```
CrunchyRagdoll/
├── Runtime/        — runtime code, separate asmdef, ships in player builds
├── Editor/         — custom inspectors, drawers, preview window
└── Samples~/       — demo prefab setup walkthrough (see Samples~/README.md)
```

## Caveats

- **Demo prefabs are not pre-authored.** `Samples~/` contains a step-by-step
  README. Setting up the two demo prefabs manually takes about two minutes and
  produces something more reliable than auto-generated YAML referencing
  imported humanoid rigs.
- **Offline clip baking is a stub.** `CrunchyRagdollBakeWindow` exposes the API
  surface for baking stepped `AnimationClip` assets at edit time, but the bake
  itself is not yet implemented. The runtime path is the priority.
- **Visual proxy is mandatory for the ragdoll path.** Writing `localRotation`
  directly to non-kinematic bone Rigidbodies causes CharacterJoint constraint
  error to accumulate, and the bodies drift sideways under gravity. The
  `RagdollStepper` always builds a proxy at the scene root with all physics and
  CrunchyRagdoll components stripped, and writes the stepped pose there.

## Algorithm summary

For each tracked bone, every frame:

1. Sample the source value (rotation / position / animator pose).
2. Append to a `MonotoneCubicSampler` rolling buffer; fit a Fritsch-Carlson PCHIP
   spline over the recent window.
3. Walk the spline forward using `DeviationThreshold` — emit a new "snap" when
   accumulated deviation (rotation degrees or position meters) exceeds τ, with
   candidate placements weighted by arc length.
4. `HoldFrameScheduler` clamps the resulting snap rate to `[MinHoldFrames,
   MaxHoldFrames]` so you never get sub-frame jitter or completely frozen output.
5. Apply the chosen snap pose to the visible bones; everything else holds.

For ragdolls, the snapshot capture writes positions and orientations of every
tracked rigidbody into a circular buffer, and the proxy applies stepped frames
from that buffer instead of from a fitted spline — physics drives the motion,
the stepper just visually quantizes it.

## License / origin

Algorithmic content and structure are direct ports from the CrunchyRagdoll
ULTRAKILL mod, generalized to be game-agnostic. ULTRAKILL-specific Harmony patches
and `Enemy.isZombie` hooks have been removed; the equivalent extension point is
`CrunchyRagdollAuthoring.GoLimp()`.

## Where to look

- Profile inspector: open any `CrunchyRagdollProfile` asset.
- Live telemetry: `Window → CrunchyRagdoll → Preview` (during Play Mode).
- Per-component foldouts: select an authoring or stepper in the scene.
