# OnTwos — Baked Workflow

How to take a stepped look from Play-mode experiment to shipped animation clips,
and the rules that keep it looking right once it's in an Animator.

For the algorithm see `TECHNICAL.md`; for component and field reference see
`DOCUMENTATION.md`.

---

## Why bake at all

Running the stepper live is the flexible option — it steps blend trees, IK,
physics and anything else computed at runtime. Baking trades that flexibility for
three things:

1. **Zero runtime cost.** The output is a normal `.anim` with constant-interpolation
   keys. It plays in any Animator with no OnTwos code present.
2. **Framerate independence, for free.** A baked clip's keys sit at fixed *times*.
   Unity samples clips by time and holds a constant key until the next one, so a
   clip baked at 12 poses/sec shows 12 poses/sec at 30, 60 or 144 fps.
3. **Determinism.** Every playthrough shows exactly the same holds. No variance
   from framerate, load hitches, or where a scheduler happened to be in its window.

The runtime stepper remains the right tool for anything you cannot know ahead of
time. Both paths now share one cadence model, so Play mode is a truthful preview
of what a bake will produce.

---

## Clips are rig-specific

**A baked clip belongs to the rig it was baked against.** The bake writes raw
transform bindings:

```csharp
clip.SetCurve(bindPath, typeof(Transform), "localRotation.x", cx);
```

Those are generic transform curves keyed by hierarchy path (`"Hips/Spine/LeftArm"`),
not Humanoid muscle curves. Consequences:

- The clip will not retarget to a different avatar.
- It must be played on a rig whose hierarchy paths match the one it was baked on.
- Renaming or reparenting a bone after baking silently breaks that bone's curve.

This is a constraint, not a defect — it's what lets the bake reproduce the exact
stepped result. Plan for it:

- **Bake per rig.** When you introduce a new character, re-bake its clips against
  *that* rig. Do not assume a clip baked on the test model transfers.
- **Name baked clips after the rig**, e.g. `Grunt_Walk_stepped`, not `Walk_stepped`.
  The default output name is `<sourceClip>_stepped`, so rename or use per-rig
  output folders.
- **Keep the unstepped source clip.** Stepping is destructive in the bake. You will
  want to re-bake at a different rate or τ, and you cannot recover the original
  motion from a stepped clip.

---

## Transitions: cut, don't crossfade

This is the rule that matters most, and it's the one that will bite you first.

**Blending destroys stepping for the entire duration of the blend.** Constant
interpolation is a property of the *curve*; blending happens on the *sampled value*.
During an Animator transition:

```
poseA  = sample(clipA, tA)          // correctly held, stepped
poseB  = sample(clipB, tB)          // correctly held, stepped
output = Lerp(poseA, poseB, weight) // weight moves smoothly, every frame
```

Both inputs are piecewise-constant. The weight is not. So the output moves smoothly
every frame for the whole transition, and you get **stepped → smooth → stepped**.
The smooth window lands exactly where the viewer is already looking, because that's
where the action changes.

A 0.25 s transition at 12 poses/sec spans three held poses. It is very visible.

**So:**

- Set transition duration to **0** between stepped states. In the Animator window,
  select the transition and set *Transition Duration* to 0; in code use
  `Animator.CrossFade(state, 0f)` or `Animator.Play(state)`.
- This is also the stylistically correct choice. Hand-drawn animation cuts between
  actions; it does not dissolve. Hard cuts read as deliberate and reinforce the look
  rather than fighting it.
- If a blend is genuinely unavoidable, keep it **shorter than one step interval**
  (< 1/StepRate seconds) so at most one pose is affected.

## Keep the cadence consistent across clips

Bake every clip for a given character at the same `StepRate`. If one clip is 12
poses/sec and another is 8, a cut between them produces an audible-looking rhythm
change that reads as a hitch rather than an accent.

Deliberate exceptions are fine and are a real technique — a slower rate on an idle,
a faster one during a fast attack — but make it a decision, not an accident.

## Layers, IK and additive motion will be smooth

Anything applied on top of a stepped base plays every frame and will visually fight
it: an additive layer, a Look-At, foot IK, a procedural recoil. Either exclude those
bones from stepping so they're consistently smooth, or accept the mix. What reads
worst is a bone that is *mostly* stepped with a smooth correction on top.

## Root motion and position

If root motion drives gameplay, **do not bake stepped position onto the root or
hips.** Stepped root motion makes the character's collider lurch forward in discrete
jumps, which breaks movement feel and collision response.

More importantly: **baking cannot fix foot sliding.** Sliding comes from the
character's *world* position advancing while leg rotations are held, and a clip only
drives bone-local transforms. Baking `PositionTau` steps the hips relative to a
still-smoothly-moving character; it does not plant the foot.

To plant feet, use `AnimationStepper.VisualOffsetRoot` at runtime. It holds the rig's
world position for the duration of each step while the CharacterController keeps
moving, so a planted foot is genuinely static in world space. That is a small runtime
component and is compatible with baked clips — bake the rotations, run the offset live.

Note it also offsets the rendered rig from its colliders, which do not move. In a
shooter, shots resolve against the collider, so keep `MaxVisualOffset` small
(0.05 m is a reasonable start).

---

## Recommended workflow

1. Set up the rig with `OnTwosAuthoring` and a profile, and tune **live** in Play
   mode until the look is right. Iteration is much faster than re-baking.
2. Note the `StepRate`, `AnimTau`, `CadenceJitter` and any `BoneOverrides`.
3. Open **Window → CrunchyRagdoll → Bake Clip**. Assign the source clip, a *scene
   instance* of the rig, and the same profile.
4. Check the cadence readout in the window — it reports poses/sec and confirms the
   result is independent of the source clip's frame rate.
5. Bake. Output lands in the configured folder as `<clip>_stepped.anim`.
6. Swap the baked clip into the Animator and **set all its transition durations to 0**.
7. Keep the source clip. Re-bake rather than editing stepped output.

## Checklist

- [ ] Baked against the rig it will actually play on
- [ ] Clip named so its rig is obvious
- [ ] Unstepped source retained
- [ ] All transitions into and out of the state set to 0 duration
- [ ] Same `StepRate` as sibling clips on that character
- [ ] Root/hips excluded from position baking if root motion drives gameplay
- [ ] Feet handled by `VisualOffsetRoot` at runtime, not by `PositionTau`
- [ ] Additive layers and IK either excluded from stepping or accepted as smooth

---

## Troubleshooting

**"The baked clip looks different from Play mode."**
Both paths share one cadence model now, so the usual causes are configuration drift:
a different profile assigned in the bake window than on the rig, or a `Tau Over Time`
curve in the bake window that isn't flat. The bake honours `BoneOverrides` and
per-bone τ, so those are no longer a source of divergence.

**"Stepping disappears when the character changes state."**
Transition duration is non-zero. See *Transitions* above.

**"The character slides while walking."**
Expected — no baked clip can fix this. Use `VisualOffsetRoot`.

**"Nothing is stepped in the output."**
`StepRate` is too high (near or above the source clip's frame rate leaves nothing to
hold), or `ExcludeKeywords` is filtering every bone. The bake throws explicitly if it
finds no bones to step.

**"One bone doesn't step like it did at runtime."**
Check `BoneOverrides` for a `ForceExclude` or a per-bone τ override matching that
bone's name. Both are applied at bake time.
