## What's still on the roadmap (not done this session)

From the context document's remaining items:

- **Position stepping at runtime in `AnimationStepper`.** Requires a visual
  proxy for `AnimationStepper` parallel to the one `RagdollStepper` builds.
  The profile field (`PositionTau`) and the bake-time half of the feature are
  done. Runtime piece is significant new architecture and was deferred to
  avoid leaving it half-finished.
- **Procedural / non-Animator mode for `AnimationStepper`** (roadmap item 4
  in the context doc).
- **`AnimatorStateWatcher` made optional** (related to above — Animator
  dependency relaxation).
- **Burst / Job System pass over the scheduler pipeline** (roadmap item 6,
  the major perf undertaking).
