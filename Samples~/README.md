# CrunchyRagdoll — Samples

This folder is reserved for sample prefabs (`DemoEnemy.prefab`, `DemoRagdoll.prefab`)
demonstrating typical CrunchyRagdoll setups. They are not pre-authored in this drop
because prefab YAML referencing humanoid rigs is fragile across Unity versions and
across the user's specific imported character. Setting them up by hand takes about
two minutes and is described below.

> **Note on `Samples~`:** the trailing `~` is intentional. Unity treats folders
> suffixed with `~` as hidden from the asset database. If you want the samples to
> show up in your project, **rename this folder to `Samples`** (drop the `~`).
> Most users won't need to do that — these are demo scaffolds, not core assets.

---

## DemoEnemy — live animation crunch

Goal: a character running its `Animator` normally, but visually updated in stepped
frames so on-screen motion looks stop-motion.

1. Drag any humanoid character with an `Animator` into the scene.
2. Add `CrunchyRagdollAuthoring` to the root GameObject.
3. **Create a profile:** `Assets → Create → CrunchyRagdoll → Profile`. Save it as
   `DemoProfile.asset`.
4. On the `CrunchyRagdollAuthoring` inspector:
   - Drag `DemoProfile` into the `Profile` field.
   - Leave `Animator Root`, `Bone Root`, and `Ragdoll Root` empty —
     `AutoBindOnAwake` will fill them.
   - Enable `Auto Bind On Awake`.
5. Add an `AnimationStepper` component to the same GameObject. The stepper will
   read its tuning from the profile.
6. Hit Play. The character animates with visible stepping.

Adjust `LiveAnimation.AnimTau` on the profile to control how "crunchy" the motion
looks — higher τ means longer holds and more pronounced stepping.

---

## DemoRagdoll — stepped death ragdoll

Goal: a character whose existing ragdoll is captured into a visual proxy that
plays back in stepped frames, so the ragdoll tumble has stop-motion feel.

1. Same starting point as DemoEnemy — a character with `Animator` and a working
   ragdoll (Rigidbody + Collider + Joints on every bone).
2. Add `CrunchyRagdollAuthoring` and assign `DemoProfile`.
3. Add a `RagdollStepper` component.
4. From your gameplay code, kill the character normally (whatever your game does
   to enable the ragdoll), then call:

   ```csharp
   GetComponent<CrunchyRagdollAuthoring>().GoLimp();
   ```

   That single call tells the `RagdollStepper` to build the visual proxy, hide the
   source renderers, and start the snapshot/replay loop.
5. Hit Play, kill the character, watch the stepped ragdoll.

If your character does not have a ready-made ragdoll, you can generate one quickly
via `GameObject → 3D Object → Ragdoll...` and assign joints from there.

---

## Troubleshooting

- **Proxy bones drift sideways:** make sure `StripProxyComponents` is enabled
  on the profile's `Proxy` settings. Writing to rigidbody-driven bones causes
  CharacterJoint constraint error to accumulate; the proxy must be transforms only.
- **Source character keeps rendering through the proxy:** enable
  `HideSourceRenderers` on the profile.
- **Ragdoll never settles:** the settling logic requires *every* tracked bone
  below the velocity/angular thresholds simultaneously — not just the anchor.
  Loosen `SettleVelocityThreshold` and `SettleAngularThreshold` if needed.
- **Bones snap to world origin:** a destroyed Rigidbody is being snapshotted.
  Make sure `RagdollStepper.PruneDestroyedBodies` is running before captures (it
  does so automatically; this only matters if you've forked the source).
