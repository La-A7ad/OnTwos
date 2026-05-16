# OnTwos — Documentation

## Table of contents

1. [What it does](#1-what-it-does)
2. [Core concepts](#2-core-concepts)
3. [Installation](#3-installation)
4. [Quick setup](#4-quick-setup)
5. [OnTwosProfile reference](#5-ontwosprofile-reference)
6. [AnimationStepper](#6-animationstepper)
7. [RagdollStepper](#7-ragdollstepper)
8. [OnTwosAuthoring](#8-ontwosauthoring)
9. [Bone filtering](#9-bone-filtering)
10. [Tuning guide](#10-tuning-guide)
11. [Events and callbacks](#11-events-and-callbacks)
12. [Editor tools](#12-editor-tools)
13. [Use-case recipes](#13-use-case-recipes) (A–I)
14. [Caveats](#14-caveats)

---

## 1. What it does

OnTwos makes any animated or physics-driven object look like hand-crafted
stop-motion. Instead of outputting a smooth continuous pose every frame, the
system selects specific frames to hold and snaps to them — the same principle
animators call "animating on twos" (one new drawing per two frames of film).

Two independent components handle the two common sources of motion:

| Component | Source of motion | Works on |
|---|---|---|
| `AnimationStepper` | Unity `Animator` **or any bone-driving system** | Animator-driven rigs (default), IK rigs, script-driven bones, cloth, motion matching — anything that writes to bone transforms |
| `RagdollStepper` | Unity `Rigidbody` | Any object with Rigidbody components — joint ragdolls, free rigid bodies, compound colliders, dropped props, vehicles, debris |

Both use the same underlying pipeline: PCHIP spline fitting over a rolling
sample window, followed by arc-length-weighted deviation thresholding to pick
hold frames.

---

## 2. Core concepts

### τ (tau) — the crunchiness threshold

τ is the central tuning parameter. The system holds the current visual pose
until the underlying motion has deviated by more than τ degrees of rotation
(or τ metres for position), then snaps to a new held frame.

- **Low τ** → snaps frequently → smoother, more frames sampled
- **High τ** → holds longer → chunkier, more obvious stop-motion

Typical values:

| τ | Feel |
|---|---|
| 5° | Subtle — noticeable on close inspection |
| 12° | Moderate — clearly stepped, not distracting |
| 30° | Heavy — very obvious stop-motion |

### Hold frames

`MinHoldFrames` and `MaxHoldFrames` clamp how often snaps can occur regardless
of deviation. `MinHoldFrames` prevents sub-frame jitter on fast motion.
`MaxHoldFrames` forces a snap even when the motion hasn't crossed τ, ensuring
the pose never fully freezes on slow or idle sections.

### PCHIP spline

Rather than snapping to raw sampled frames, the system fits a Fritsch-Carlson
PCHIP (Piecewise Cubic Hermite Interpolating Polynomial) spline over a rolling
window of recent samples. Arc-length candidates are placed along the spline so
that snap positions are weighted toward regions of actual motion rather than
sampling noise.

### Visual proxy (RagdollStepper only)

Unity physics cannot be visually stepped in place: writing pose data directly
to a non-kinematic Rigidbody's transform causes constraint errors and
physics drift. `RagdollStepper` solves this by cloning the source GameObject
at startup, stripping physics and scripts from the clone, hiding the source
renderers, and applying stepped poses to the clone each frame. The physics
simulation runs invisibly on the original; only the visual proxy is seen.

This applies to **any** Rigidbody-driven object, not only joint ragdolls.

---

## 3. Installation

Drop the `OnTwos/` folder anywhere under `Assets/` in your project. The two
assembly definitions (`OnTwos.Runtime`, `OnTwos.Editor`) keep editor code out
of player builds automatically.

**Minimum Unity version:** 2021.3 LTS. Unity 6 is supported.

---

## 4. Quick setup

### Animator-driven object (AnimationStepper)

1. `Assets → Create → CrunchyRagdoll → Profile` — create a profile asset.
2. Add `OnTwosAuthoring` to a GameObject that has an `Animator`.
3. Drag the profile onto the `Profile` slot.
4. Press Play. `AnimationStepper` is added automatically by `Awake`.

### Physics-driven object (RagdollStepper)

For **any** object with Rigidbody components — a ragdoll, a falling crate, a
vehicle, a piece of debris:

1. Create a profile as above.
2. Add `OnTwosAuthoring` to the GameObject.
3. Assign the profile.
4. `PhysicsRoot` auto-resolves on Awake to the deepest ancestor that contains
   all Rigidbodies in the hierarchy. You can assign it manually if needed.
5. At runtime, call `ActivateRagdoll()` to switch to physics stepping:
   ```csharp
   GetComponent<OnTwosAuthoring>().ActivateRagdoll();
   ```
6. To switch back (e.g. get-up, revival):
   ```csharp
   GetComponent<OnTwosAuthoring>().Deactivate();
   ```

### Standalone — no OnTwosAuthoring

Both steppers work as standalone components:

```csharp
// Animator-driven stepping
var anim       = gameObject.AddComponent<AnimationStepper>();
anim.Profile   = myProfile;
anim.AnimatorRoot = GetComponent<Animator>();
anim.BoneRoot  = skeletonRoot;

// Physics-driven stepping — works on ANY Rigidbody object
var phys         = gameObject.AddComponent<RagdollStepper>();
phys.Profile     = myProfile;
phys.PhysicsRoot = transform; // or whatever root contains your Rigidbodies
```

---

## 5. OnTwosProfile reference

Create via `Assets → Create → CrunchyRagdoll → Profile`.

### Global

| Field | Default | Description |
|---|---|---|
| `Enabled` | `true` | Master switch. If false, neither stepper does anything. |
| `ResponseCurve` | Linear 0→1 | Remaps normalised motion intensity (0..1) to a τ multiplier. |

### Live Animation

Consumed by `AnimationStepper`.

| Field | Default | Description |
|---|---|---|
| `AnimTau` | `5` | Degrees of rotation before a new snap. See [τ](#τ-tau--the-crunchiness-threshold). |
| `PositionTau` | `0` | Metres of translation before a stepped position snaps. `0` = position stepping disabled (rotation only). Used by the bake window to write `localPosition` curves when set above 0. Runtime position stepping for `AnimationStepper` is a planned feature — set this in the profile to prepare for it. |
| `GaussPoints` | `2` | Arc-length hold candidates per monotone segment. Raise to 3–4 for more expressive snaps on fast motion. |
| `BufferSize` | `30` | Rolling sample window per bone (~0.5 s at 60 Hz). |
| `ExcludeKeywords` | *(empty)* | Bones whose names contain any of these substrings are skipped entirely. Case-insensitive. |

### Ragdoll

Consumed by `RagdollStepper`. Despite the name, applies to any physics-driven
object — the term refers to the stepping technique, not a specific rig type.

| Field | Default | Description |
|---|---|---|
| `RagdollTau` | `12` | Degrees of rotation before the proxy snaps. |
| `RagdollPosTau` | `0.08` | Metres of translation before the proxy snaps. Scale this with your object's world-space size. |
| `MinHoldFrames` | `2` | Minimum physics frames to hold before a snap is allowed. |
| `MaxHoldFrames` | `4` | Maximum physics frames before forcing a snap regardless of deviation. |

### Settling

| Field | Default | Description |
|---|---|---|
| `SettleVelocityThreshold` | `0.75` | Linear speed (m/s) below which a body counts as still. |
| `SettleAngularThreshold` | `25` | Angular speed (deg/s) below which a body counts as still. |
| `SettleTime` | `0.35` | Seconds all bodies must stay below the thresholds before settling is declared. |
| `WakeVelocityThreshold` | `3.0` | Linear speed (m/s) on the anchor body that wakes the proxy after settling. |

### Proxy Rig

| Field | Default | Description |
|---|---|---|
| `SnapshotBufferSize` | `120` | Trajectory snapshot buffer (~2 s at 60 Hz). |
| `HideSourceRenderers` | `true` | Hide the source object's renderers so only the proxy is visible. |
| `StripProxyComponents` | `true` | Remove physics, scripts, and Animators from the proxy clone. |
| `ForceEnableProxyRenderers` | `true` | Re-enable all Renderers on the proxy after build. |

### Bone Rules

`BoneOverrides` entries take precedence over `ExcludeKeywords`.

| Field | Description |
|---|---|
| `NameContains` | Case-insensitive substring matched against bone/transform names. |
| `ForceExclude` | Exclude this bone from stepping regardless of global settings. |
| `TauOverride` | Per-bone τ override. Values ≤ 0 fall back to the profile default. |

---

## 6. AnimationStepper

Reads bone rotations each `LateUpdate`, feeds them through the PCHIP + arc-length
hold scheduler, and writes back the stepped pose.

### Mode

The `Mode` field controls how bones are read:

| Mode | Requires | State flushing |
|---|---|---|
| `AnimatorDriven` *(default)* | An `Animator` in the hierarchy | Automatic — `AnimatorStateWatcher` detects transitions and calls `FlushAllHolds()` |
| `AnySource` | Nothing — reads whatever `localRotation` bones have each frame | Manual — call `FlushAllHolds()` yourself if your source has discrete states |

`AnySource` makes `AnimationStepper` work with IK rigs, script-driven bones,
cloth results baked to transforms, motion matching output, or any system that
writes to bone transforms directly. The `AnimatorRoot` field is ignored when
`AnySource` is set.

### Setup — AnimatorDriven

```csharp
var s          = gameObject.AddComponent<AnimationStepper>();
s.Profile      = profile;
s.AnimatorRoot = GetComponentInChildren<Animator>(); // auto-discovered if null
s.BoneRoot     = boneHierarchyRoot;
```

### Setup — AnySource

```csharp
var s     = gameObject.AddComponent<AnimationStepper>();
s.Mode    = AnimationStepper.StepperMode.AnySource;
s.Profile = profile;
s.BoneRoot = boneHierarchyRoot;
// No Animator needed. Reads localRotation from whatever drives the bones.
```

### Visibility culling

```csharp
s.EnableVisibilityCulling = true;
```

When enabled, the `localRotation` write-back is skipped while every `Renderer`
in the bone hierarchy is off-screen. The schedulers keep running (bones are still
read each frame) so state stays coherent — no visible pop when the rig comes back
on screen. Leave disabled if the hierarchy has no `Renderer` components.

### Deactivating

```csharp
stepper.Deactivate(); // disables the component; continuous poses resume
```

### Manual flush

```csharp
// Call this when your source system makes a large discontinuous jump
// (mode switch, teleport, IK target swap) to prevent ghosting of the
// old pose into the new one.
stepper.FlushAllHolds();
```

### Notes

- `BoneRoot` defaults to the component's own transform if null.
- The bone list is cached at startup. Hierarchy changes after `Start` are not
  picked up automatically.
- Live `AnimTau` slider changes are pushed to schedulers each frame.
  `ExcludeKeywords` and `BoneOverrides` changes are not hot-reloaded.
- In `AnimatorDriven` mode, if no `Animator` is found in the hierarchy a warning
  is logged and state-transition flushing is disabled for that instance.
  The stepper still runs — just without automatic flush on state changes.

---

## 7. RagdollStepper

Snapshots `Rigidbody` world transforms each physics frame, builds a visual
proxy, and replays stepped poses on the proxy.

**Any object with at least one `Rigidbody` works** — joint ragdolls, a single
rigid body, a compound collider setup, a chain of physics objects, or anything
else Unity simulates with Rigidbodies.

### Setup

```csharp
var s          = gameObject.AddComponent<RagdollStepper>();
s.Profile      = profile;
s.PhysicsRoot  = transform; // root that owns all the Rigidbodies to step
```

The proxy is built in `Start`. The source renderers are hidden and the proxy
becomes visible on the same frame.

### Events

```csharp
var stepper = GetComponent<RagdollStepper>();

// Fired once when all Rigidbodies have been still for SettleTime seconds.
stepper.OnSettled += () =>
{
    // swap to a static prop, trigger a dissolve, disable the object, etc.
};

// Fired when the object wakes after settling (external force, collision, etc.).
stepper.OnWoke += () =>
{
    // re-enable interaction, restart an effect, etc.
};
```

Subscribe before or just after adding the component — both events fire on the
main thread from `FixedUpdate`, so Unity API calls from handlers are safe.

### Properties

```csharp
bool      IsSettled  { get; }  // true after settle, false after wake
GameObject VisualProxy { get; } // the transform-only clone
```

### Visibility culling

```csharp
stepper.EnableVisibilityCulling = true;
```

When enabled, `ApplyHeldPoses()` is skipped while every `Renderer` on the
visual proxy is off-screen. The PCHIP schedulers keep running in `FixedUpdate`
so state stays coherent — no visible pop when the proxy comes back into view.
Recommended for scenes with large numbers of simultaneous active ragdolls.
Default is `false` so existing scenes behave identically without opt-in.

Use `VisualProxy` to reparent, attach effects, or destroy the visual
independently of the physics object:

```csharp
// Reparent the proxy into a pool or container
stepper.VisualProxy.transform.SetParent(container, worldPositionStays: true);
```

### Dismemberment / body destruction

If a `Rigidbody` is destroyed or deactivated at runtime (e.g. by a destruction
system), `RagdollStepper` automatically removes it from tracking on the next
`FixedUpdate`. The matching proxy bone is deactivated. The simulation continues
for remaining bodies.

---

## 8. OnTwosAuthoring

An optional wiring component. Connects an `OnTwosProfile` with an
`AnimationStepper` and a `RagdollStepper` in one place. Works on any object.

### Fields

| Field | Default | Description |
|---|---|---|
| `Profile` | — | The `OnTwosProfile` asset to use. |
| `AnimatorRoot` | — | The `Animator` to read state from. Auto-resolved on Awake if null. |
| `BoneRoot` | — | Root of the bone hierarchy for `AnimationStepper`. Auto-resolved if null. |
| `PhysicsRoot` | — | Root of the Rigidbody hierarchy for `RagdollStepper`. Auto-resolved if null. Works with any physics setup. |
| `AutoBindOnAwake` | `true` | Add and configure `AnimationStepper` on `Awake`. |
| `AutoCreateProxy` | `true` | Build the visual proxy when `ActivateRagdoll()` is called. |
| `AddDiagnostics` | `false` | Attach `RagdollLogger` when `ActivateRagdoll()` runs. |

### Methods

```csharp
// Switch to physics-driven stepped motion on any Rigidbody object.
RagdollStepper ActivateRagdoll();

// Reverse ActivateRagdoll: destroy RagdollStepper, restore AnimationStepper.
void Deactivate();

// Re-run the auto-binder heuristics manually.
void AutoResolveBindings();

// Ensure an AnimationStepper exists and is configured. Idempotent.
AnimationStepper AttachAnimationStepper();
```

### Properties

```csharp
bool IsRagdollActive { get; } // true between ActivateRagdoll() and Deactivate()
```

### Auto-binding heuristics

`AutoResolveBindings` is called by `Awake` and by the "Try Auto-Bind" button
in the inspector. Rules:

- **AnimatorRoot** — first `Animator` in the hierarchy.
- **BoneRoot** — for humanoid avatars, the Hips bone; for all other rigs,
  the Animator's own transform. Falls back to the root transform if no
  Animator is found.
- **PhysicsRoot** — deepest ancestor that contains every `Rigidbody` in the
  hierarchy. Works regardless of whether those bodies are jointed or free.

Assign fields manually if the heuristics don't match your setup.

---

## 9. Bone filtering

### ExcludeKeywords

`LiveAnimation.ExcludeKeywords` is a list of case-insensitive substrings.
Any bone whose name contains one of these strings is excluded from
`AnimationStepper` — it always follows the Animator output unstepped.

**Empty by default.** Add entries specific to your rig. Common reasons to
exclude a bone:

- End-effectors that clip geometry when held while a parent moves
- IK targets or aim bones that must track a target continuously
- Attach points that must stay in sync with gameplay logic
- Any bone where continuous motion matters more than the stop-motion effect

```
ExcludeKeywords: [ "ik_target", "weapon_socket", "attach", "camera_bone" ]
```

### BoneOverrides

Per-bone rules that take precedence over `ExcludeKeywords`. Use for per-bone
τ tuning or to force-exclude specific bones by name.

```
BoneOverrides:
  - NameContains: "head"
    TauOverride: 3        # snaps more often than the body
  - NameContains: "prop_hand"
    ForceExclude: true    # always follows the Animator
```

---

## 10. Tuning guide

### Start with AnimTau

With `AnimationStepper` active in Play Mode, adjust `AnimTau` while watching
the object. The right value depends on the animation speed, world-space scale,
and intended aesthetic — there is no universal default. Go up until the
stop-motion feel is visible, then back off until it reads well at your camera
distance.

### Then adjust hold frames

`MaxHoldFrames` prevents the rig from appearing frozen during slow sections.
Raise it if you see long holds on idle animations.

`MinHoldFrames` prevents jitter on fast motion. Raise it if you see rapid
flickering between two nearby poses.

### Physics stepping

`RagdollTau` typically needs to be higher than `AnimTau` — physics motion
tends to be faster and noisier than Animator output.

`RagdollPosTau` should scale with the world-space size of your object. For a
unit-scale character, 0.05–0.15 m is typical. For a large prop or vehicle,
scale up proportionally.

### Settle detection

Increase `SettleTime` if the proxy settles prematurely on bouncy physics.
Lower `SettleVelocityThreshold` if the proxy never settles (bodies still drift
at low speed). Raise `WakeVelocityThreshold` if minor collisions keep waking
an already-settled object.

### ResponseCurve

Leave at the default linear curve until τ is dialled in. Then use it to soften
the effect on slow-moving sections or exaggerate it on fast ones.

---

## 11. Events and callbacks

`RagdollStepper` exposes standard C# `Action` events. They fire on the main
thread from `FixedUpdate` and Unity API calls from handlers are safe.

```csharp
event Action OnSettled; // all bodies still for SettleTime seconds
event Action OnWoke;    // anchor body exceeded WakeVelocityThreshold after settling
```

Subscribe via the `RagdollStepper` reference:

```csharp
// Via OnTwosAuthoring
var stepper = GetComponent<OnTwosAuthoring>().ActivateRagdoll();
stepper.OnSettled += HandleSettled;

// Via direct component
GetComponent<RagdollStepper>().OnSettled += HandleSettled;
```

---

## 12. Editor tools

### Context menu

Right-click the `OnTwosAuthoring` component header in Play Mode:

**ActivateRagdoll (Test)** — calls `ActivateRagdoll()` without writing code.

### Preview window

`Window → CrunchyRagdoll → Preview`:

- **Edit Mode** — lists every `OnTwosAuthoring` in the scene with current
  binding state and any validation warnings, so you can confirm wiring before
  entering Play Mode.
- **Play Mode** — shows live telemetry (internal float/int/bool fields) for
  each active stepper component.

### Profile inspector

Opening any `OnTwosProfile` asset shows foldouts per settings block, with
inline validation warnings (e.g. `MaxHoldFrames < MinHoldFrames`).

### Bake window

`Window → CrunchyRagdoll → Bake Clip`:

Samples a source `AnimationClip` through the full OnTwos stepping pipeline and
saves the result as a new `.anim` asset. The output clip has constant-interpolation
(`step`) keyframes and can play back in any `Animator` without the OnTwos runtime
system present — useful for shipping builds or export.

Fields:

| Field | Description |
|---|---|
| **Source Clip** | The `AnimationClip` to bake. |
| **Skeleton Object** | A scene instance of the rig with an `Animator`. Drag from the Hierarchy, not the Project. |
| **Profile** | Reads `AnimTau`, `GaussPoints`, `BufferSize`, `ExcludeKeywords`, and `PositionTau`. |
| **Output Folder** | Project-relative path where the `.anim` is saved. |
| **Tau Over Time** | An `AnimationCurve` (X = normalised clip time 0..1, Y = τ multiplier). Default flat 1.0 leaves the bake identical to a plain τ bake. Sculpt the curve to make specific moments crunchier or smoother — a spike at an impact frame, a ramp out of a landing. |

When `Profile.LiveAnimation.PositionTau > 0`, the bake also writes
`localPosition` curves with the same snap coupling used by `RagdollStepper` at
runtime: the position snaps when the rotation scheduler snaps, or when
translation drift exceeds `PositionTau` metres — whichever comes first.

---

## 13. Use-case recipes

### A. Animator-only stepped rig

```csharp
var s          = gameObject.AddComponent<AnimationStepper>();
s.Profile      = profile;
s.AnimatorRoot = GetComponentInChildren<Animator>();
s.BoneRoot     = skeletonRoot;
```

Tune `AnimTau`. Done. Works on any Animator-driven hierarchy.

---

### B. Physics prop — a crate dropped from a height

```csharp
// The crate has a single Rigidbody and box Collider.
// Just add OnTwosAuthoring and activate when you drop it.
var authoring = crate.AddComponent<OnTwosAuthoring>();
authoring.Profile = crunchProfile;
authoring.AutoBindOnAwake = false; // no Animator on a crate

// When the crate is released:
authoring.ActivateRagdoll();

// When it settles, remove it or swap to a static prop:
authoring.GetComponent<RagdollStepper>().OnSettled += () => Destroy(crate);
```

---

### C. Joint ragdoll — permanent physics transition

```csharp
var authoring = GetComponent<OnTwosAuthoring>();
var stepper   = authoring.ActivateRagdoll();
stepper.OnSettled += () => Destroy(gameObject);
```

---

### D. Knock-down and get-up

```csharp
IEnumerator KnockDown(float duration)
{
    var authoring = GetComponent<OnTwosAuthoring>();
    authoring.ActivateRagdoll();
    yield return new WaitForSeconds(duration);
    authoring.Deactivate(); // destroys RagdollStepper, restores AnimationStepper
    animator.SetTrigger("GetUp");
}
```

---

### E. IK or procedurally driven rig — AnySource mode

Use this for any rig whose bones are driven by something other than an Animator:
full-body IK, a custom script, cloth baked to transforms, motion matching, etc.

```csharp
var s     = gameObject.AddComponent<AnimationStepper>();
s.Mode    = AnimationStepper.StepperMode.AnySource;
s.Profile = profile;
s.BoneRoot = rootBone;
// No Animator required. The stepper reads localRotation from whatever drives
// the bones each frame — IK, script, cloth, or anything else.

// If your IK system has discrete state changes (e.g. switching targets),
// call this to prevent the old pose ghosting into the new one:
// s.FlushAllHolds();
```

---

### F. Non-humanoid Animator rig (creature, vehicle, mechanical arm)

```csharp
// Works on any Animator-driven hierarchy.
// Set ExcludeKeywords to whatever end-effectors your rig has.
// Leave it empty to step every bone.
var s          = gameObject.AddComponent<AnimationStepper>();
s.Profile      = profile;
s.AnimatorRoot = GetComponentInChildren<Animator>();
s.BoneRoot     = rootBone;
```

---

### G. Physics chain — a hanging lamp, a rope, debris

```csharp
// Any number of connected Rigidbodies. PhysicsRoot auto-finds the common ancestor.
var s         = gameObject.AddComponent<RagdollStepper>();
s.Profile     = profile;
s.PhysicsRoot = transform;
// PhysicsRoot will include every Rigidbody in the hierarchy.
```

---

### H. Access and control the visual proxy

```csharp
var stepper = GetComponent<RagdollStepper>();
stepper.OnSettled += () =>
{
    GameObject proxy = stepper.VisualProxy;

    // Reparent into a persistent prop container
    proxy.transform.SetParent(propContainer, worldPositionStays: true);

    // Attach a VFX
    Instantiate(settleVFXPrefab, proxy.transform.position, Quaternion.identity);
};
```

---

### I. Swap profiles at runtime

```csharp
// Change the crunch feel without rebuilding anything.
authoring.Profile = heavyCrunchProfile;
authoring.AttachAnimationStepper(); // re-pushes the new profile to the stepper
```

---

## 14. Caveats

**Visual proxy is mandatory for the physics stepping path.** Writing stepped
poses directly to non-kinematic Rigidbody transforms causes constraint
accumulation and physics drift under gravity. The proxy approach is the correct
architecture: the original object keeps all physics, hitboxes, and game logic;
the proxy keeps only Transforms and Renderers. This happens automatically when
`ActivateRagdoll()` is called — source renderers are hidden and the proxy
becomes the visible stand-in.

**Hierarchy changes after Start are not tracked.** Both steppers cache their
bone/body lists at startup. Adding or removing transforms at runtime requires
destroying and re-adding the component.

**Settle timing is accurate to one physics frame.** `SettleTime` accumulates
`fixedDeltaTime` in `FixedUpdate`, which is wall-clock time — 0.35 s settles
in 0.35 s regardless of physics rate. The granularity is one physics frame
(~17 ms at 60 Hz, ~33 ms at 30 Hz), which is generally not noticeable.

**Baked clips cover animator-driven animation only.** The bake window produces
a stepped `.anim` asset from an AnimationClip. Physics simulations are
non-deterministic and real-time — they cannot be baked to a file.

**The visual proxy is not serialised.** The proxy is a runtime-only clone.
Do not save a scene while `RagdollStepper` is active and expect the proxy to
persist in Edit Mode. This is intentional — the proxy's value is its live
physics state, which only exists at runtime.