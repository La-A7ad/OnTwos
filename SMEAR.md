# OnTwos — Smear: State of Play

Handoff document for the procedural smear work. Written to be resumable months
later, so it separates **confirmed by test** from **inferred but unverified**.
Where something is unverified it says so — the previous handoff (`Context.md`)
claimed several things were wired that turned out not to be, and that cost real
debugging time.

Last updated: 2026-08-03.

---

## The idea, unchanged

A held pose diverges from the live pose. That divergence is not an error — it is a
vector whose direction is where the motion went and whose magnitude is how far
behind the drawing has fallen. It is exactly what a smear needs, and the stepper
computes it anyway.

Holds and smears are two halves of one 2D technique: you hold to make poses read,
and you smear to keep the motion between them legible. Deriving both from a single
residual is the most distinctive idea in this project, and nothing found so far
undermines it. Only the *rendering* of the smear is unsolved.

---

## What is built and working

### The signal layer — done, technique-agnostic

`AnimationStepper` publishes, gated behind `EnableBoneDivergence`:

| Member | Meaning |
|---|---|
| `Bones` | the bones it drives, in discovery order |
| `BoneExcluded` | index-parallel exclusion flags |
| `BoneDivergence` | per-bone **world-space** tip displacement, raw minus held |
| `BoneTipOffsets` | per-bone tip offset in bone-local space; normalised, the bone's length axis |
| `EnableDivergenceSignal()` | turns the signal on, building buffers on demand if `Start()` already ran |

Two details that matter and were got wrong in earlier designs:

- **Divergence is measured at the bone tip, not the pivot.** A pure rotation
  produces zero displacement *at* the joint. Without a lever arm the signal is
  identically zero no matter how fast the bone swings.
- **Tip offsets average all children, not `GetChild(0)`.** A branch joint (hips,
  chest, shoulders) would otherwise get a direction pointing down whichever limb
  happens to be first in the hierarchy, which is arbitrary. Leaf bones inherit a
  length from their parent, or fingertips and toes read zero.

This layer is the durable part of the work. Any smear technique — bone scale,
shader, ghosting — consumes these same arrays. Building a shader implementation
later does **not** require touching it.

### Bone-scale squash-and-stretch — working, with a known ceiling

`Runtime/SquashStretch.cs`. Confirmed visibly working in-editor.

Scales each bone from its divergence and lets linear blend skinning do the
deformation. No shader, no clone, no extra draw call — the GPU already multiplies
every vertex by its bones' matrices, and those matrices carry scale.

**Its real advantage, which any shader approach must re-solve:** skinning blends
weighted bones, so a vertex influenced 60/40 by two bones gets a blend of both
deformations. **There are no seams at joints, for free.** The per-vertex shader
plan assigns each vertex a single dominant bone, and two vertices either side of a
joint then receive different displacements and tear apart.

**Its ceiling — this is why the shader is back on the table.** `Transform.localScale`
is diagonal in bone-local axes. A true stretch of factor `s` along an arbitrary unit
axis `d` is

```
S = I + (s − 1)·(d dᵀ)
```

which is symmetric but **not diagonal**. The best available diagonal is the diagonal
of that outer product — the squared components `(dx², dy², dz²)`, which is what the
component uses. Squared rather than absolute matters: they sum to 1, so total stretch
is conserved however the axis sits. Using `|d·axis|` lets diagonal motion inflate all
three axes at once and gives a balloon rather than a streak.

What is lost is the off-diagonal shear term, and it cannot be recovered through
`localScale`. Practical consequence, confirmed in testing: stretch is directional when
motion aligns with a bone axis and softens toward uniform inflation as it goes diagonal.

Because a limb's tip moves **perpendicular** to its own length, `AlongMotion` mode tends
to widen limbs rather than extend them, which reads as bulging. `AlongBone` mode
elongates each bone along its own length axis — almost always aligned with a local axis,
so `localScale` expresses it exactly — and that is the mode that reads as a smear. It is
the default.

**Verdict:** good enough to ship for limb elongation. Cannot stretch toward the world
direction of travel. That requires the full bone matrix, which means a shader.

---

## The shader path — blocked, cause unknown

### What happened

`Runtime/CharacterSmear.shadergraph` was edited to fix a real space bug and add a
per-vertex mask, then reverted. The edited version is archived at:

```
Assets/OnTwos/Archive~/CharacterSmear_NormalMasked.shadergraph.archived
```

(`Archive~` has a trailing tilde, so Unity ignores the folder entirely — no import, no
`.meta`. Git tracks it normally.)

The archived graph computes:

```
dirObj = Transform(_SmearDirection, World → Object, Direction, normalised)
mask   = saturate( dot(objectNormal, dirObj) )
offset = dirObj × mask × _SmearStrength × _SmearGain
vertex = objectPosition + offset
```

It was structurally validated: parses cleanly, no dangling `m_Id` references, no
duplicate ObjectIds, every edge resolving to a slot that exists. Node schemas were
copied from real ShaderGraphs in `Library/PackageCache` rather than reconstructed.

**It fixed two genuine bugs in the original graph**, both still worth keeping when the
shader path resumes:

1. **Space mismatch.** C# pushes `_SmearDirection` in **world** space
   (`Transform.TransformDirection`), while `Position` and `VertexDescription.Position`
   are object space. Without a `Transform` node the smear pushes sideways relative to
   the real motion as soon as the character turns. The original graph had no Transform
   node despite `Context.md` claiming the fix was applied.
2. **Uniform offset cannot smear.** `_SmearDirection × _SmearStrength` is one vec3
   identical for every vertex. Adding a constant vector to every vertex is by definition
   a *translation* — the mesh slides rigidly. A smear requires different vertices to
   move by different amounts. The normal-based mask was the cheapest way to get
   per-vertex variation with stock nodes.

### The blocker

**With the smear shader applied, the character renders as disconnected blobs clustered
at the joints** — the signature of skinning not being applied, vertices collapsing
toward bone origins.

Confirmed by test:

- `_SmearGain = 0` does **not** fix it. This is important: zeroing gain removes the
  offset *value* but not the custom vertex stage. It does not exonerate the shader,
  and an earlier conclusion that it did was wrong and wasted time.
- Changing **Mesh Deformation** away from GPU Batched (`meshDeformation: 2` in
  `ProjectSettings/ProjectSettings.asset`, adjacent to `gpuSkinning`) does **not** fix
  it. The GPU-batched-deformation hypothesis is dead.
- No console errors at any point.

**Not yet tested — run these first when resuming. The assets are now prepared, so
each one is an assign-and-look, not an edit.**

Run them in this order and stop at the first one that renders correctly.

1. **Assign `Runtime/CharacterSmear_NoVertexStage.shadergraph`.**
   This is the active graph with exactly one edge removed — `Add →
   VertexDescription.Position`. Every node, property and other edge is byte-identical;
   the Position block itself is still present, just unconnected. Nothing else about
   the graph changed, so a difference in output isolates the vertex stage and nothing
   else.
   - **Renders correctly** → *any* connected vertex position block breaks skinning on
     this setup. Vertex displacement is dead as an approach here regardless of the
     maths inside it, and the answer is bone scale (already working) or ghosting.
   - **Still blobs** → the vertex stage is exonerated. The fault is upstream — the
     material, the rig, or the mesh import. Go to 2.

2. **Assign a stock `Universal Render Pipeline/Lit` material.**
   - **Renders correctly** → the fault is in the graph, not the rig or the import.
   - **Still blobs** → the fault is the rig or the FBX import, and the shader was never
     the problem. Check `optimizeGameObjects`, the humanoid avatar, and the bone
     bindposes before touching the graph again.

3. **Confirm the pipeline is live at all** — set
   `AnimationStepper.DebugForceSmearStrength` to `5` in the Inspector. That bypasses
   the computed value and pushes a constant `_SmearDirection`/`_SmearStrength`, so the
   mesh should visibly displace. This separates *"nothing is connected"* from *"the
   value is too small to see"* — the two look identical on screen, and `Context.md`'s
   "can't notice the difference" was never resolved into which one it was. Set back to
   `-1` when done.

Until (1) is answered, nothing about the shader path should be assumed.

**Note on the active graph, confirmed by reading the file:** it contains
`Position → Multiply(_SmearDirection × _SmearStrength) → Add → VertexDescription.Position`
and **no `Transform` node**. The world/object space mismatch described above is
therefore live in the current file — `Context.md`'s claim that the fix had been applied
was wrong. This does not explain the blobs (a wrong-space offset would slide the mesh,
not collapse it toward the joints), but it does mean the space fix still has to be
reapplied once the blocker is resolved.

---

## Environment facts (verified)

| | |
|---|---|
| Unity | 6000.3.17f1, URP |
| `gpuSkinning` | `1` |
| `meshDeformation` | `2` (GPU Batched) — changing it had no effect |
| Character | Mixamo, `Assets/Silly Dancing.fbx` |
| FBX import | `animationType: 3` (Humanoid), `useFileScale: 1`, `importBlendShapes: 1`, `optimizeGameObjects: 0` |
| Movement | `GoldSrcMovement.cs` + `CharacterController`, **no root motion** anywhere in project content |

The no-root-motion fact matters: gameplay position is entirely owned by the
CharacterController, so no baked clip can ever plant a foot, and any visual/logical
position split is unopposed.

---

## If resuming the shader path

Order matters. Do not start at step 2.

1. **Resolve the blocker.** Run the three untested experiments above. If a connected
   vertex position block is fundamentally incompatible here, stop — the answer is
   ghosting or bone scale, not shaders.
2. **Restore the archived graph's two fixes** — the `Transform` node and the per-vertex
   mask. Both are correct and independent of the blocker.
3. **Per-bone vectors.** Upload `BoneDivergence` to a `ComputeBuffer`/`StructuredBuffer`
   sized to `Bones.Length`. Must be `Release()`d in `OnDestroy` or it leaks native memory.
4. **Bone index per vertex.** Bake the dominant bone index into UV2 once from
   `mesh.boneWeights`. **Match by `Transform` reference, not index** —
   `SkinnedMeshRenderer.bones[]` and `AnimationStepper.Bones` are not guaranteed to be in
   the same order.
5. **Custom Function node.** Stock Shader Graph cannot index a `StructuredBuffer`; this
   part cannot be pure nodes.
6. **Solve the joint seam.** Dominant-bone-only assignment gives adjacent vertices either
   side of a joint different displacements under fast motion, and they visibly separate.
   Blending the top two bone weights is the contained fix. Note that bone-scale gets this
   free — if the shader path cannot beat bone-scale on directionality by enough to justify
   re-solving seams, it is not worth finishing.

Also unresolved from the original design: Unity resolves skinning before a standard
`SkinnedMeshRenderer`'s vertex stage, so `BLENDWEIGHTS`/`BLENDINDICES` are **not**
readable in a normal Shader Graph vertex stage. The `Linear Blend Skinning` node works
only with Entities Graphics/DOTS. The UV2 bake exists specifically to work around this.

---

## Alternative not yet tried: ghost poses

Render the outgoing held pose fading out over the hold interval — literally what a smear
frame is in 2D, the object drawn at several positions at once. Would reuse
`RagdollProxyBuilder`, which already clones a rig, strips it to renderers, and path-maps
bones.

Rejected as the *primary* technique on cost: a persistent clone roughly doubles
per-character skinned cost — another skinning dispatch, another draw call, another
transform hierarchy evaluated every frame — which is a bad trade for a cosmetic effect on
every walking enemy. Still viable as a **selective accent**: gated on high divergence,
capped to a small global pool, for finishers and heavy attacks where doubling one
character's cost is affordable.

Ghosts need a snap event. `HoldFrameScheduler.DidSnap` now provides it — it reports
whether the last `Update` emitted a new held pose, by either the forced-cadence or the
Tau-crossing path. Note that this is not inferable from outside by comparing successive
held poses: a barely-moving bone still snaps on the cadence, and an angle-epsilon
comparison reads that as no snap. `RagdollStepper` used to make exactly that mistake.

---

## Files

| Path | State |
|---|---|
| `Runtime/SquashStretch.cs` | working; bone-scale technique |
| `Runtime/AnimationStepper.cs` | publishes the divergence signal |
| `Runtime/CharacterSmear.shadergraph` | **original**, restored; whole-mesh translate only, no Transform node |
| `Runtime/CharacterSmear_NoVertexStage.shadergraph` | diagnostic 1 — identical but with the vertex Position edge cut |
| `Archive~/CharacterSmear_NormalMasked.shadergraph.archived` | edited version with the space fix and mask; blocked |
| `TECHNICAL.md` §6 | the smear concept in the wider architecture |
