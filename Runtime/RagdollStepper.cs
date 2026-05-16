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
    /// Previously: TrajectoryRecorder captured raw snapshots, ShouldSnap() did a
    /// naive Quaternion.Angle + Vector3.Distance check against a fixed min/max
    /// hold-frame window. Arc-length, extrema detection, and PCHIP were never
    /// involved in the ragdoll path at all.
    ///
    /// Now: one HoldFrameScheduler per tracked bone, same pipeline as
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
        public Transform PhysicsRoot;

        [Header("Crunch feel")]
        public float Tau         = 12f;    // degrees of rotation before the proxy snaps
        public float PositionTau = 0.08f;  // world units of translation before the proxy snaps

        [Header("Physics settle")]
        public float SettleVelocityThreshold = 0.75f;
        public float SettleAngularThreshold  = 25f;   // deg/s
        public float SettleTime              = 0.35f;
        public float WakeVelocityThreshold   = 3.0f;

        [Header("Proxy rig")]
        public bool HideSourceRenderers  = true;
        public bool StripProxyComponents = true;

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

    if (HideSourceRenderers)
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
            int   n          = _sourceBodies.Length;
            float tau        = ResolveTau();
            int   candidates = Mathf.Clamp(ResolveCandidates(), 1, 4);

            _schedulers    = new HoldFrameScheduler[n];
            _heldPositions = new Vector3[n];
            _heldRotations = new Quaternion[n];

            for (int i = 0; i < n; i++)
            {
                // bufferSize 30: ~0.6 s at 50 Hz FixedUpdate — enough history for
                // PCHIP to fit a smooth curve over a ragdoll's recent trajectory.
                _schedulers[i] = new HoldFrameScheduler(tau, candidates, bufferSize: 30);

                if (_sourceBodies[i] != null)
                {
                    _heldPositions[i] = _sourceBodies[i].position;
                    _heldRotations[i] = _sourceBodies[i].rotation;
                    _schedulers[i].Reset(_sourceBodies[i].rotation);
                }
            }
        }


        // ------------------------------------------------------------------ update

        private void FixedUpdate()
        {
            if (_sourceBodies == null || _sourceBodies.Length == 0 || _visualBones == null)
                return;

            PruneDestroyedBodies();
            if (_sourceBodies.Length == 0) return;

            float t      = Time.fixedTime;
            float liveTau = Profile != null ? Profile.Ragdoll.RagdollTau : Tau;
            float posTau  = Profile != null ? Profile.Ragdoll.RagdollPosTau : PositionTau; //Null checks 

            if (!_initialized)
            {
                for (int i = 0; i < _sourceBodies.Length; i++)
                {
                    if (_sourceBodies[i] == null) continue;
                    _heldPositions[i] = _sourceBodies[i].position;
                    _heldRotations[i] = _sourceBodies[i].rotation;
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
                        _schedulers[i].Reset(_sourceBodies[i].rotation);
                        _heldPositions[i] = _sourceBodies[i].position;
                        _heldRotations[i] = _sourceBodies[i].rotation;
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
                if (_sourceBodies[i] == null || _schedulers[i] == null) continue;

                // Push live tau so config slider changes take effect immediately.
                _schedulers[i].Tau = liveTau;

                Quaternion prevHeld = _heldRotations[i];
                Quaternion newHeld  = _schedulers[i].Update(t, _sourceBodies[i].rotation);
                _heldRotations[i]   = newHeld;

                // When the rotation scheduler snaps, also snap the position —
                // they should move together. The angle check uses a tiny epsilon
                // rather than exact equality to guard against float drift.
                bool rotSnapped = Quaternion.Angle(prevHeld, newHeld) > 0.01f;

                Vector3 currentPos = _sourceBodies[i].position;
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
        stripComponents:      StripProxyComponents,
        forceEnableRenderers: true
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
                }
            }

            int removed = _sourceBodies.Length - newBodies.Count;
            _sourceBodies  = newBodies.ToArray();
            _visualBones   = newBones.ToArray();
            _schedulers    = newSched.ToArray();
            _heldPositions = newHeldPos.ToArray();
            _heldRotations = newHeldRot.ToArray();

            Debug.Log(
                $"[RagdollStepper] {gameObject.name} pruned {removed} bone(s), " +
                $"{_sourceBodies.Length} remaining");

            if (_sourceBodies.Length == 0) return;

            _anchorIndex = PickAnchorIndex(_sourceBodies);

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

private int ResolveCandidates()
    => Profile != null ? Profile.Ragdoll.GaussPoints : 2;

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