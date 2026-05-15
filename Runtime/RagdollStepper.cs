using System.Collections.Generic;
using UnityEngine;

namespace CrunchyRagdoll
{
    /// <summary>
    /// Crunchy ragdoll driver — now using the full PCHIP pipeline per bone.
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
        [Header("Crunch feel")]
        public float Tau         = 12f;    // degrees of rotation before the proxy snaps
        public float PositionTau = 0.08f;  // world units of translation before the proxy snaps

        [Header("Physics settle")]
        public float SettleVelocityThreshold = 0.75f;
        public float SettleAngularThreshold  = 25f;   // deg/s
        public float SettleTime              = 0.35f;
        public float WakeVelocityThreshold   = 3.0f;

        [Header("Proxy rig")]
        public bool HideSourceRenderers = true;
        public bool StripProxyComponents = true;

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

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            _startTime     = Time.fixedTime;
            _sourceAnimator = GetComponentInChildren<Animator>(true);

            BuildVisualProxy();   // must be first — Instantiate copies component state

            if (_sourceAnimator != null)
                _sourceAnimator.enabled = false;

            if (HideSourceRenderers)
            {
                _sourceRenderers = GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < _sourceRenderers.Length; i++)
                    if (_sourceRenderers[i] != null)
                        _sourceRenderers[i].enabled = false;
            }

            CacheTrackedBodiesAndBones();

            if (_sourceBodies == null || _sourceBodies.Length == 0 ||
                _visualBones  == null || _visualBones.Length  == 0)
            {
                Plugin.Log.LogWarning($"[RagdollStepper] {gameObject.name} — no tracked ragdoll bodies found.");
                enabled = false;
                return;
            }

            _anchorIndex = PickAnchorIndex(_sourceBodies);
            InitSchedulers();

            Plugin.Log.LogInfo(
                $"[RagdollStepper] {gameObject.name} — {_sourceBodies.Length} tracked bones, " +
                $"PCHIP pipeline active (τ={Tau}°)");
        }

        private void InitSchedulers()
        {
            int   n          = _sourceBodies.Length;
            float tau        = Plugin.RagdollTau;
            int   candidates = Mathf.Clamp(Plugin.GaussPoints, 1, 4);

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
            float liveTau = Plugin.RagdollTau;
            float posTau  = Plugin.RagdollPosTau;

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
                    _settled      = false;
                    _settleTimer  = 0f;

                    // Reseed schedulers from current physics state so the
                    // PCHIP window doesn't try to fit across the settle gap.
                    for (int i = 0; i < _sourceBodies.Length; i++)
                    {
                        if (_sourceBodies[i] == null) continue;
                        _schedulers[i].Reset(_sourceBodies[i].rotation);
                        _heldPositions[i] = _sourceBodies[i].position;
                        _heldRotations[i] = _sourceBodies[i].rotation;
                    }

                    Plugin.Log.LogInfo(
                        $"[RagdollStepper] {gameObject.name} woke at t+{t - _startTime:F2}s");
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

            int count = Mathf.Min(_visualBones.Length, _heldPositions.Length);
            for (int i = 0; i < count; i++)
            {
                if (_visualBones[i] == null) continue;
                _visualBones[i].position = _heldPositions[i];
                _visualBones[i].rotation = _heldRotations[i];
            }
        }

        // ------------------------------------------------------------------ settle

        private void UpdateSettleState()
        {
            if (AllBonesStill())
            {
                _settleTimer += Time.fixedDeltaTime;
                if (_settleTimer >= SettleTime)
                {
                    _settled = true;
                    Plugin.Log.LogInfo(
                        $"[RagdollStepper] {gameObject.name} settled at t+{Time.fixedTime - _startTime:F2}s");
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
                if (rb.velocity.magnitude          > SettleVelocityThreshold ||
                    rb.angularVelocity.magnitude * Mathf.Rad2Deg > SettleAngularThreshold)
                    return false;
            }
            return true;
        }

        private bool AnchorWoke()
        {
            if (_anchorIndex >= _sourceBodies.Length) return false;
            Rigidbody rb = _sourceBodies[_anchorIndex];
            if (rb == null) return false;
            return rb.velocity.magnitude          >= WakeVelocityThreshold ||
                   rb.angularVelocity.magnitude * Mathf.Rad2Deg >= WakeVelocityThreshold * 6f;
        }

        // ------------------------------------------------------------------ proxy build

        private void BuildVisualProxy()
        {
            GameObject clone = Instantiate(gameObject, transform.position, transform.rotation, null);
            clone.name = gameObject.name + " [OnTwosProxy]";
            clone.SetActive(false);

            // Kill our own components before SetActive(true) — DestroyImmediate is safe
            // on inactive objects and prevents the clone spawning its own proxy.
            foreach (var c in clone.GetComponentsInChildren<RagdollStepper>(true))
                if (c != null) DestroyImmediate(c);
            foreach (var c in clone.GetComponentsInChildren<RagdollLogger>(true))
                if (c != null) DestroyImmediate(c);
            foreach (var c in clone.GetComponentsInChildren<AnimationStepper>(true))
                if (c != null) DestroyImmediate(c);

            if (StripProxyComponents)
                StripToRenderersOnly(clone);

            clone.SetActive(true);

            foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                if (r is SkinnedMeshRenderer smr)
                    smr.updateWhenOffscreen = true;
            }

            _visualProxyRoot = clone;
        }

        private void CacheTrackedBodiesAndBones()
        {
            Rigidbody[] allBodies    = GetComponentsInChildren<Rigidbody>(true);
            Transform   proxyRoot    = _visualProxyRoot.transform;
            Transform[] proxyTransforms = _visualProxyRoot.GetComponentsInChildren<Transform>(true);

            var proxyLookup = new Dictionary<string, Transform>(proxyTransforms.Length);
            foreach (Transform t in proxyTransforms)
            {
                if (t == null) continue;
                string path = GetPath(proxyRoot, t);
                proxyLookup[path] = t;
            }

            var sourceList = new List<Rigidbody>(allBodies.Length);
            var visualList = new List<Transform>(allBodies.Length);

            foreach (Rigidbody rb in allBodies)
            {
                if (rb == null) continue;
                string path = GetPath(transform, rb.transform);
                if (!proxyLookup.TryGetValue(path, out Transform proxyBone) || proxyBone == null)
                {
                    Plugin.Log.LogWarning($"[RagdollStepper] Missing proxy match for '{path}'");
                    continue;
                }
                sourceList.Add(rb);
                visualList.Add(proxyBone);
            }

            _sourceBodies = sourceList.ToArray();
            _visualBones  = visualList.ToArray();
        }

        // ------------------------------------------------------------------ prune

        /// <summary>
        /// Removes bones destroyed/deactivated by ULTRAKILL's dismemberment system.
        /// Also prunes the parallel scheduler, heldPosition, and heldRotation arrays.
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

            var newBodies   = new List<Rigidbody>(_sourceBodies.Length);
            var newBones    = new List<Transform>(_visualBones.Length);
            var newSched    = new List<HoldFrameScheduler>(_schedulers.Length);
            var newHeldPos  = new List<Vector3>(_heldPositions.Length);
            var newHeldRot  = new List<Quaternion>(_heldRotations.Length);

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

            Plugin.Log.LogInfo(
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

        private static void StripToRenderersOnly(GameObject root)
        {
            foreach (var j in root.GetComponentsInChildren<Joint>(true))
                if (j != null) Destroy(j);
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                if (rb != null) Destroy(rb);
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
                if (c != null) Destroy(c);
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) DestroyImmediate(mb);
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                if (a != null) DestroyImmediate(a);
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

        private void OnDestroy()  => CleanupProxy();
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
    }
}
