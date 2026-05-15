# OnTwos

Stop-motion / stepped animation for live humanoids and death ragdolls in Unity.
A self-contained asset implementing PCHIP-based motion quantization for
animator-driven rigs and physics ragdolls.

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
- **`OnTwosProfile`** — a ScriptableObject holding all the tuning
  (τ values, hold-frame bounds, settle thresholds, bone-exclusion keywords,
  per-bone overrides). Drop one onto an authoring component and you're done.
- **`OnTwosAuthoring`** — single MonoBehaviour entry point. Call `GoLimp()`
  from your own death code to trigger the ragdoll stepper.

## Install

Drop the `OnTwos` folder anywhere under `Assets/` in your project. The
two assembly definitions (`OnTwos.Runtime`, `OnTwos.Editor`) keep
editor code out of player builds automatically.

Unity 2021.3 LTS and newer should work. Unity 6 is supported — the runtime uses
`#if UNITY_6000_0_OR_NEWER` to select `Rigidbody.linearVelocity` vs the legacy
`Rigidbody.velocity`.

## Quick start

1. `Assets → Create → OnTwos → Profile` to make a profile asset.
2. Add `OnTwosAuthoring` to a humanoid character GameObject.
3. Drag the profile onto the authoring's `Profile` slot.
4. Add `AnimationStepper` and/or `RagdollStepper` to the same GameObject.
5. For ragdoll: call `GetComponent<OnTwosAuthoring>().GoLimp()` from
   whatever your game does on death.

## Folder layout

```
OnTwos/
├── Runtime/        — runtime code, separate asmdef, ships in player builds
├── Editor/         — custom inspectors, drawers, preview window
└── Samples~/       — demo prefab setup walkthrough (see Samples~/README.md)
```

## Caveats

- **Demo prefabs are not pre-authored.** `Samples~/` contains a step-by-step
  README. Setting up the two demo prefabs manually takes about two minutes and
  produces something more reliable than auto-generated YAML referencing
  imported humanoid rigs.
- **Offline clip baking is a stub.** `OnTwosBakeWindow` exposes the API
  surface for baking stepped `AnimationClip` assets at edit time, but the bake
  itself is not yet implemented. The runtime path is the priority.
- **Visual proxy is mandatory for the ragdoll path.** Writing `localRotation`
  directly to non-kinematic bone Rigidbodies causes CharacterJoint constraint
  error to accumulate, and the bodies drift sideways under gravity. The
  `RagdollStepper` always builds a proxy at the scene root with all physics and
  OnTwos components stripped, and writes the stepped pose there.

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

## License

MIT — see below.

The algorithm, architecture, and design of OnTwos are original work by the
author. C# implementation was produced with AI assistance.

```
MIT License

Copyright (c) 2026 Yusuf Hosam

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Where to look

- Profile inspector: open any `OnTwosProfile` asset.
- Live telemetry: `Window → OnTwos → Preview` (during Play Mode).
- Per-component foldouts: select an authoring or stepper in the scene.
