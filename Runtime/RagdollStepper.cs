using System;
using System.Collections.Generic;
using UnityEngine;
using OnTwos.Runtime.Math;
using OnTwos.Runtime.Utilities;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Crunchy ragdoll driver — uses the full PCHIP pipeline per bone.
    ///
    /// One HoldFrameScheduler per tracked bone, same pipeline as
    /// AnimationStepper — PCHIP fit over a rolling window, extrema via Brent's
    /// method, arc-length candidate placement, deviation threshold. The only
    /// difference from the animation path is that samples come from Rigidbody
    /// world-rotation rather than Animator bone localRotation.
    ///
    /// Position is coupled to the rotation snap: when the scheduler emits a new
    /// held rotation, the held position also snaps. An independent PositionTau
    /// override catches cases where the body translates significantly without
    /// rotating (sliding along a flat surface).
    ///
    /// Settle detection reads velocities directly from the live Rigidbodies —
    /// no snapshot frame needed for that path.
    /// </summary>
    public class RagdollStepper : MonoBehaviour
    {
        // ------------------------------------------------------------------ public fields

        public OnTwosProfile Profile;

        [Tooltip("Bones tuned individually by direct reference. Takes precedence over the " +
                 "profile's BoneOverrides and over ExcludeKeywords. Reference the bodies on " +
                 "the source ragdoll, not the visual proxy — the proxy is built at runtime.")]
        public BoneTuning[] BoneTunings = Array.Empty<BoneTuning>();

        [Header("Crunch feel")]
        public float Tau         = 12f;    // degrees of rotation before the proxy snaps
        public float PositionTau = 0.08f;  // world units of translation before the proxy snaps

        [Range(30f, 1440f)]
        [Tooltip("Rotation speed, in degrees per second, that maps to ResponseCurve input 1.0. " +
                 "Only used when a Profile with a ResponseCurve is assigned.")]
        public float MaxDegreesPerSecond = 360f;

        [Header("Physics settle")]
        public float SettleVelocityThreshold = 0.75f;
        public float SettleAngularThreshold  = 25f;   // deg/s
        public float SettleTime              = 0.35f;
        public float WakeVelocityThreshold   = 3.0f;

        [Header("Proxy rig")]
        public bool HideSourceRenderers  = true;
        public bool StripProxyComponents = true;
        public bool ForceEnableProxyRenderers = true;

        [Tooltip("When true, ApplyHeldPoses() is skipped while every Renderer on the visual " +
                 "proxy is off-screen. The PCHIP schedulers keep running (samples are still " +
                 "consumed and state stays coherent) so there is no visible pop when the " +
                 "proxy becomes visible again — only the per-frame pose writes are skipped. " +
                 "Default off so existing scenes behave identically; enable on large hordes.")]
        public bool EnableVisibilityCulling = false;

        // ------------------------------------------------------------------ events

        /// <summary>
        /// Fired once when all tracked bodies have been still for <see cref="SettleTime"/>
        /// seconds. Use this to trigger dissolves, despawns, prop swaps, or any
        /// post-ragdoll logic without polling <see cref="IsSettled"/> every frame.
        /// </summary>
        public event Action OnSettled;

        /// <summary>
        /// Fired when the ragdoll wakes after having settled (e.g. the body is
        /// struck or kicked). Not fired on the initial activation.
        /// </summary>
        public event Action OnWoke;

        // ------------------------------------------------------------------ properties

        /// <summary>True once all tracked bodies have been still for <see cref="SettleTime"/> seconds.</summary>
        public bool IsSettled => _settled;

        /// <summary>
        /// The transform-only visual proxy created by this stepper, or null before
        /// <see cref="Start"/> has run. Use this to reparent the proxy, attach effects,
        /// or destroy it independently of the source rig.
        /// </summary>
        public GameObject VisualProxy => _visualProxyRoot;

        // ------------------------------------------------------------------ private state

        // One scheduler per tracked Rigidbody — drives rotation via the full
        // PCHIP → extrema → arc-length → deviation-threshold pipeline.
        private HoldFrameScheduler[] _schedulers;
        private Vector3[]    _heldPositions;
        private Quaternion[] _heldRotations;  // previous scheduler output, used to detect snaps

        private Rigidbody[]  _sourceBodies;
        private Transform[]  _visualBones;
        private GameObject   _visualProxyRoot;

        private int   _anchorIndex;
        private bool  _initialized;
        private bool  _settled;
        private float _settleTimer;
        private float _startTime;

        private Renderer[] _sourceRenderers;
        private Animator   _sourceAnimator;
        // Exclusion is NOT mirrored into a local array. It used to be, and prune rebuilt
        // every parallel array except that one, so after a limb was destroyed the flags
        // applied to the wrong bones. BoneRuleSet owns the resolved flags and re-resolves
        // whenever the body set changes, which makes that desync unrepresentable.
        private Quaternion[] _rawRotations;

        // Source-body transforms, index-parallel to _sourceBodies. Held as their own
        // array because BoneRuleSet resolves against Transforms and rebuilding this
        // list every frame would defeat the point of caching the resolution.
        private Transform[] _bodyTransforms;

        // Resolves exclusion / per-bone tau / per-bone response curve, re-running only
        // when the rules actually change. See BoneRuleSet.
        private readonly BoneRuleSet _rules = new BoneRuleSet();

        // Cached set of every Renderer on the visual proxy. Used by visibility culling
        // to early-exit ApplyHeldPoses when none of them are on-screen. Populated once
        // after BuildVisualProxy() finishes. Polling Renderer.isVisible is preferred
        // here over OnBecameVisible/Invisible callbacks: callbacks fire only on state
        // changes, so multi-renderer setups need explicit bootstrap and ref-counting
        // to track which renderers started visible. Polling sidesteps both issues —
        // an early-exit loop over a handful of renderers is well under a microsecond.
        private Renderer[] _proxyRenderers;

        // ------------------------------------------------------------------ lifecycle

        private void Start()
{
    _startTime      = Time.fixedTime;
    _sourceAnimator = GetComponentInChildren<Animator>(true);

    BuildVisualProxy();   // now also populates _sourceBodies and _visualBones

    if (_visualProxyRoot != null)
        _proxyRenderers = _visualProxyRoot.GetComponentsInChildren<Renderer>(true);

    if (_sourceAnimator != null)
        _sourceAnimator.enabled = false;

    if (ResolveHideSourceRenderers())
    {
        _sourceRenderers = GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < _sourceRenderers.Length; i++)
            if (_sourceRenderers[i] != null)
                _sourceRenderers[i].enabled = false;
    }

    // _sourceBodies and _visualBones already set by BuildVisualProxy()
    if (_sourceBodies == null || _sourceBodies.Length == 0 ||
        _visualBones  == null || _visualBones.Length  == 0)
    {
        Debug.LogWarning($"[RagdollStepper] {gameObject.name} — no tracked ragdoll bodies found.");
        enabled = false;
        return;
    }

    _anchorIndex = RagdollProxyBuilder.PickAnchorIndex(_sourceBodies);
    InitSchedulers();

    Debug.Log(
        $"[RagdollStepper] {gameObject.name} — {_sourceBodies.Length} tracked bones, " +
        $"PCHIP pipeline active (τ={ResolveTau()}°)");
}

        private void InitSchedulers()
        {
            int n = _sourceBodies.Length;
            float tau = ResolveTau();
            int candidates = Mathf.Clamp(ResolveCandidates(), 1, 4);
            int bufferSize = ResolveBufferSize();
            float maxHold = ResolveMaxHoldSeconds();
            float minHold = ResolveMinHoldSeconds(maxHold);
            _schedulers = new HoldFrameScheduler[n];
            _heldPositions = new Vector3[n];
            _heldRotations = new Quaternion[n];
            _rawRotations = new Quaternion[n];

            RebuildBodyTransforms();
            SyncBoneRules();

            for (int i = 0; i < n; i++)
            {
                Rigidbody rb = _sourceBodies[i];
                if (rb == null) continue;

                _heldPositions[i] = rb.position;
                _heldRotations[i] = rb.rotation;
                _rawRotations[i] = rb.rotation;

                if (_rules.Excluded[i])
                    continue;

                float boneTau = _rules.TauOverride[i] > 0f ? _rules.TauOverride[i] : tau;
                _schedulers[i] = new HoldFrameScheduler(boneTau, candidates, bufferSize);
                _schedulers[i].CandidatesPerSegment = candidates;
                _schedulers[i].MinHoldSeconds = minHold;
                _schedulers[i].MaxHoldSeconds = maxHold;
                _schedulers[i].Reset(rb.rotation);
            }
        }


        // ------------------------------------------------------------------ update

        private void FixedUpdate()
        {
            if (_sourceBodies == null || _sourceBodies.Length == 0 || _visualBones == null)
                return;
            if (Profile != null && Profile.Global != null && !Profile.Global.Enabled)
                return;

            PruneDestroyedBodies();
            if (_sourceBodies.Length == 0) return;

            float t = Time.fixedTime;
            float liveTau = ResolveTau();
            float posTau  = ResolvePositionTau();
            int liveCandidates = ResolveCandidates();
            float liveMaxHold = ResolveMaxHoldSeconds();
            float liveMinHold = ResolveMinHoldSeconds(liveMaxHold);

            // No-op unless the profile or tuning list was edited, so live tuning in Play
            // mode keeps working without paying for re-resolution every tick.
            SyncBoneRules();

            if (!_initialized)
            {
                for (int i = 0; i < _sourceBodies.Length; i++)
                {
                    if (_sourceBodies[i] == null) continue;
                    _heldPositions[i] = _sourceBodies[i].position;
                    _heldRotations[i] = _sourceBodies[i].rotation;
                    _rawRotations[i] = _sourceBodies[i].rotation;
                    if (_schedulers[i] != null)
                        _schedulers[i].Reset(_sourceBodies[i].rotation);
                }
                _initialized = true;
                ApplyHeldPoses();
                return;
            }

            if (_settled)
            {
                if (AnchorWoke())
                {
                    _settled     = false;
                    _settleTimer = 0f;

                    // Reseed schedulers from current physics state so the
                    // PCHIP window doesn't try to fit across the settle gap.
                    for (int i = 0; i < _sourceBodies.Length; i++)
                    {
                        if (_sourceBodies[i] == null) continue;
                        _heldPositions[i] = _sourceBodies[i].position;
                        _heldRotations[i] = _sourceBodies[i].rotation;
                        _rawRotations[i] = _sourceBodies[i].rotation;
                        if (_schedulers[i] != null)
                            _schedulers[i].Reset(_sourceBodies[i].rotation);
                    }

                    Debug.Log($"[RagdollStepper] {gameObject.name} woke at t+{t - _startTime:F2}s");
                    OnWoke?.Invoke();
                }
                else
                {
                    ApplyHeldPoses();
                    return;
                }
            }

            UpdateSettleState();

            // Run the PCHIP pipeline for every tracked bone.
            for (int i = 0; i < _sourceBodies.Length; i++)
            {
                Rigidbody rb = _sourceBodies[i];
                if (rb == null) continue;

                Quaternion currentRot = rb.rotation;
                Vector3 currentPos = rb.position;

                if (_rules.Excluded != null && i < _rules.Excluded.Length && _rules.Excluded[i])
                {
                    _heldRotations[i] = currentRot;
                    _heldPositions[i] = currentPos;
                    _rawRotations[i] = currentRot;
                    continue;
                }

                if (_schedulers[i] == null) continue;

                float response = ResolveResponseMultiplier(ComputeMotionIntensity(i, currentRot), _rules.ResponseCurve[i]);
                float baseTau  = _rules.TauOverride[i] > 0f ? _rules.TauOverride[i] : liveTau;
                float boneTau  = baseTau * response;
                int boneCandidates = Mathf.Clamp(Mathf.RoundToInt(liveCandidates * response), 1, 4);

                _schedulers[i].Tau = boneTau;
                _schedulers[i].CandidatesPerSegment = boneCandidates;
                _schedulers[i].MinHoldSeconds = liveMinHold;
                _schedulers[i].MaxHoldSeconds = liveMaxHold;

                _heldRotations[i] = _schedulers[i].Update(t, currentRot);
                _rawRotations[i]  = currentRot;

                // When the rotation scheduler snaps, also snap the position — they
                // should move together. Read from the scheduler rather than comparing
                // poses with an angle epsilon: a body that is barely rotating still
                // snaps on the cadence, and the epsilon read that as "no snap" and left
                // the position held until PositionTau happened to trip on its own.
                bool rotSnapped = _schedulers[i].DidSnap;

                if (rotSnapped || Vector3.Distance(_heldPositions[i], currentPos) >= posTau)
                    _heldPositions[i] = currentPos;
            }

            ApplyHeldPoses();
        }

        private void LateUpdate()
        {
            // Keep the proxy locked even if the render frame lands after FixedUpdate.
            if (_initialized)
                ApplyHeldPoses();
        }

        private void ApplyHeldPoses()
        {
            if (_visualBones == null || _heldPositions == null || _heldRotations == null) return;

            // Visibility culling: skip the per-bone Transform writes while the proxy is
            // entirely off-screen. The PCHIP schedulers in FixedUpdate keep running so
            // state stays coherent — when visibility resumes, the very next frame's
            // ApplyHeldPoses snaps to the up-to-date held pose with no visible pop.
            if (EnableVisibilityCulling && !IsProxyVisible()) return;

            int count = Mathf.Min(_visualBones.Length, _heldPositions.Length);
            for (int i = 0; i < count; i++)
            {
                if (_visualBones[i] == null) continue;
                _visualBones[i].position = _heldPositions[i];
                _visualBones[i].rotation = _heldRotations[i];
            }
        }

        // Returns true if any Renderer on the visual proxy is currently on-screen.
        // Early-exits on the first visible renderer — average cost is well below a
        // microsecond for typical rig renderer counts (~5–30).
        private bool IsProxyVisible()
        {
            if (_proxyRenderers == null) return true;
            for (int i = 0; i < _proxyRenderers.Length; i++)
            {
                var r = _proxyRenderers[i];
                if (r != null && r.isVisible) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ settle
        private void UpdateSettleState()
{
    if (AllBonesStill())
    {
        _settleTimer += Time.fixedDeltaTime;
        if (_settleTimer >= ResolveSettleTime())
        {
            _settled = true;
            Debug.Log($"[RagdollStepper] {gameObject.name} settled at t+{Time.fixedTime - _startTime:F2}s");
            OnSettled?.Invoke();
        }
    }
    else
    {
        _settleTimer = 0f;
    }
}

private bool AllBonesStill()
{
    for (int i = 0; i < _sourceBodies.Length; i++)
    {
        Rigidbody rb = _sourceBodies[i];
        if (rb == null) continue;
#if UNITY_6000_0_OR_NEWER
        float linSpeed = rb.linearVelocity.magnitude;
#else
        float linSpeed = rb.velocity.magnitude;
#endif
        if (linSpeed > ResolveSettleVelocity() ||
            rb.angularVelocity.magnitude * Mathf.Rad2Deg > ResolveSettleAngular())
            return false;
    }
    return true;
}

private bool AnchorWoke()
{
    if (_anchorIndex >= _sourceBodies.Length) return false;
    Rigidbody rb = _sourceBodies[_anchorIndex];
    if (rb == null) return false;
#if UNITY_6000_0_OR_NEWER
    float linSpeed = rb.linearVelocity.magnitude;
#else
    float linSpeed = rb.velocity.magnitude;
#endif
    float wakeVel = ResolveWakeVelocity();
    return linSpeed >= wakeVel ||
           rb.angularVelocity.magnitude * Mathf.Rad2Deg >= wakeVel * 6f;
}
        // ------------------------------------------------------------------ proxy build

        private void BuildVisualProxy()
{
    var result = RagdollProxyBuilder.Build(
        gameObject,
        stripComponents:      ResolveStripProxyComponents(),
        forceEnableRenderers: ResolveForceEnableProxyRenderers()
    );

    _visualProxyRoot = result.Proxy;
    _sourceBodies    = result.SourceBodies;
    _visualBones     = result.VisualBones;
}
        // ------------------------------------------------------------------ prune

        /// <summary>
        /// Removes bodies that have been destroyed or deactivated (e.g. by the
        /// game's dismemberment or destruction system). Also prunes the parallel
        /// scheduler, heldPosition, and heldRotation arrays.
        /// </summary>
        private void PruneDestroyedBodies()
        {
            bool dirty = false;
            for (int i = 0; i < _sourceBodies.Length; i++)
            {
                if (_sourceBodies[i] == null || !_sourceBodies[i].gameObject.activeInHierarchy)
                { dirty = true; break; }
            }
            if (!dirty) return;

            var newBodies  = new List<Rigidbody>(_sourceBodies.Length);
            var newBones   = new List<Transform>(_visualBones.Length);
            var newSched   = new List<HoldFrameScheduler>(_schedulers.Length);
            var newHeldPos = new List<Vector3>(_heldPositions.Length);
            var newHeldRot = new List<Quaternion>(_heldRotations.Length);
            // _rawRotations is index-parallel to the arrays above and must be compacted
            // in the same pass. Omitting it left ComputeMotionIntensity differencing
            // against another bone's rotation after any limb was destroyed.
            var newRawRot = new List<Quaternion>(_rawRotations != null ? _rawRotations.Length : 0);

            for (int i = 0; i < _sourceBodies.Length; i++)
            {
                bool gone = _sourceBodies[i] == null ||
                            !_sourceBodies[i].gameObject.activeInHierarchy;
                if (gone)
                {
                    if (_visualBones[i] != null)
                        _visualBones[i].gameObject.SetActive(false);
                }
                else
                {
                    newBodies.Add(_sourceBodies[i]);
                    newBones.Add(_visualBones[i]);
                    newSched.Add(_schedulers[i]);
                    newHeldPos.Add(_heldPositions[i]);
                    newHeldRot.Add(_heldRotations[i]);
                    newRawRot.Add(_rawRotations != null && i < _rawRotations.Length
                        ? _rawRotations[i]
                        : _sourceBodies[i].rotation);
                }
            }

            int removed = _sourceBodies.Length - newBodies.Count;
            _sourceBodies  = newBodies.ToArray();
            _visualBones   = newBones.ToArray();
            _schedulers    = newSched.ToArray();
            _heldPositions = newHeldPos.ToArray();
            _heldRotations = newHeldRot.ToArray();
            _rawRotations  = newRawRot.ToArray();

            Debug.Log(
                $"[RagdollStepper] {gameObject.name} pruned {removed} bone(s), " +
                $"{_sourceBodies.Length} remaining");

            if (_sourceBodies.Length == 0) return;

            _anchorIndex = PickAnchorIndex(_sourceBodies);

            // The rule set resolves against _bodyTransforms, so it has to be rebuilt and
            // re-synced here too — otherwise the resolved arrays stay at the pre-prune
            // length and every index past the removed bone reads the wrong bone's rules.
            RebuildBodyTransforms();
            SyncBoneRules();

            // Reinitialize so the next FixedUpdate seeds the schedulers cleanly
            // rather than continuing from stale window state.
            _initialized = false;
        }

        // ------------------------------------------------------------------ helpers

        private static string GetPath(Transform root, Transform target)
        {
            if (root == null || target == null) return string.Empty;
            if (root == target) return string.Empty;

            var stack = new Stack<string>(8);
            Transform current = target;
            while (current != null && current != root)
            {
                stack.Push(current.name);
                current = current.parent;
            }
            return current == null ? string.Empty : string.Join("/", stack.ToArray());
        }

        private static int PickAnchorIndex(Rigidbody[] bodies)
        {
            int best = 0;
            float bestMass = bodies[0] != null ? bodies[0].mass : float.MinValue;
            for (int i = 1; i < bodies.Length; i++)
            {
                if (bodies[i] != null && bodies[i].mass > bestMass)
                { bestMass = bodies[i].mass; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------ cleanup

        private void OnDestroy() => CleanupProxy();
        private void OnDisable()
        {
            if (_sourceRenderers != null)
                foreach (var r in _sourceRenderers)
                    if (r != null) r.enabled = true;
        }

        private void CleanupProxy()
        {
            if (_visualProxyRoot != null) { Destroy(_visualProxyRoot); _visualProxyRoot = null; }
            if (_sourceRenderers != null)
                foreach (var r in _sourceRenderers)
                    if (r != null) r.enabled = true;
        }

        // ------------ RESOLVERS

        private float ResolveTau()
    => Profile != null ? Profile.Ragdoll.RagdollTau : Tau;

private float ResolvePositionTau()
    => Profile != null ? Profile.Ragdoll.RagdollPosTau : PositionTau;

private int ResolveCandidates()
    => Profile != null ? Profile.LiveAnimation.GaussPoints : 2;

private int ResolveBufferSize()
    => Profile != null ? Mathf.Max(4, Profile.LiveAnimation.BufferSize) : 30;

// Cadence bounds, in seconds. Profile-only by design — RagdollStepperEditor directs
// the user to the profile's Ragdoll foldout for these. Without them the schedulers
// keep HoldFrameScheduler's 0 / +Infinity defaults and the cadence never engages.
//
// Seconds rather than physics ticks matters less here than on the animation path
// (FixedUpdate already runs at a fixed rate), but it keeps one StepRate meaning the
// same thing across both steppers and the bake window.
private float ResolveMaxHoldSeconds()
{
    float rate = Profile != null ? Profile.Ragdoll.StepRate : 12f;
    return 1f / Mathf.Max(0.01f, rate);
}

private float ResolveMinHoldSeconds(float maxHoldSeconds)
{
    float jitter = Profile != null ? Profile.Ragdoll.CadenceJitter : 0f;
    return maxHoldSeconds * (1f - Mathf.Clamp01(jitter));
}

private bool ResolveHideSourceRenderers()
    => Profile != null ? Profile.Proxy.HideSourceRenderers : HideSourceRenderers;

private bool ResolveStripProxyComponents()
    => Profile != null ? Profile.Proxy.StripProxyComponents : StripProxyComponents;

private bool ResolveForceEnableProxyRenderers()
    => Profile != null ? Profile.Proxy.ForceEnableProxyRenderers : ForceEnableProxyRenderers;

private string[] ResolveExcludeKeywords()
    => Profile != null ? Profile.LiveAnimation.ExcludeKeywords : Array.Empty<string>();

private OnTwosProfile.BoneOverride[] ResolveBoneOverrides()
    => Profile != null ? Profile.BoneOverrides : Array.Empty<OnTwosProfile.BoneOverride>();

// Index-parallel Transform view of _sourceBodies, for BoneRuleSet to resolve against.
// Rebuilt on init and after a prune — the two points where the body set changes.
private void RebuildBodyTransforms()
{
    int n = _sourceBodies?.Length ?? 0;
    if (_bodyTransforms == null || _bodyTransforms.Length != n)
        _bodyTransforms = new Transform[n];

    for (int i = 0; i < n; i++)
        _bodyTransforms[i] = _sourceBodies[i] != null ? _sourceBodies[i].transform : null;
}

private bool SyncBoneRules()
    => _rules.Sync(_bodyTransforms, null, ResolveExcludeKeywords(),
                   ResolveBoneOverrides(), BoneTunings);

/// <summary>
/// Motion intensity → Tau multiplier. A per-bone curve wins over the profile's
/// global one; passing null selects the global curve.
/// </summary>
private float ResolveResponseMultiplier(float motionIntensity, AnimationCurve boneCurve)
{
    AnimationCurve curve = boneCurve
        ?? (Profile != null && Profile.Global != null ? Profile.Global.ResponseCurve : null);

    if (curve == null || curve.length == 0) return 1f;

    float multiplier = curve.Evaluate(Mathf.Clamp01(motionIntensity));
    return Mathf.Max(0.05f, multiplier);
}

private float ComputeMotionIntensity(int bodyIndex, Quaternion currentRaw)
{
    if (_rawRotations == null || bodyIndex < 0 || bodyIndex >= _rawRotations.Length)
        return 0f;

    Quaternion previousRaw = _rawRotations[bodyIndex];

    // Degrees per SECOND — see AnimationStepper.ComputeMotionIntensity. Uses
    // fixedDeltaTime because this path is driven from FixedUpdate.
    float dt = Mathf.Max(Time.fixedDeltaTime, 1e-5f);
    float degreesPerSecond = Quaternion.Angle(previousRaw, currentRaw) / dt;
    return Mathf.Clamp01(degreesPerSecond / Mathf.Max(1f, MaxDegreesPerSecond));
}

private float ResolveSettleVelocity()
    => Profile != null ? Profile.Settling.SettleVelocityThreshold : SettleVelocityThreshold;

private float ResolveSettleAngular()
    => Profile != null ? Profile.Settling.SettleAngularThreshold : SettleAngularThreshold;

private float ResolveSettleTime()
    => Profile != null ? Profile.Settling.SettleTime : SettleTime;

private float ResolveWakeVelocity()
    => Profile != null ? Profile.Settling.WakeVelocityThreshold : WakeVelocityThreshold;
    }
}