using System.Collections.Generic;
using CrunchyRagdoll.Runtime.Recording;
using CrunchyRagdoll.Runtime.Utilities;
using UnityEngine;

namespace CrunchyRagdoll.Runtime
{
    /// <summary>
    /// Crunchy ragdoll driver.
    ///
    /// The physics rig stays authoritative and invisible. On enable, we clone
    /// the hierarchy, strip the clone down to renderers only, and drive that
    /// visible proxy with stepped hold-frames sampled from the live rigidbodies.
    ///
    /// This keeps Unity physics clean while still producing the low-framerate,
    /// chunky motion the system is designed for. Direct transform writes into
    /// non-kinematic Rigidbody bones cause CharacterJoint constraint error to
    /// accumulate, which is why we never do that.
    /// </summary>
    [AddComponentMenu("CrunchyRagdoll/Ragdoll Stepper")]
    public sealed class RagdollStepper : MonoBehaviour, ICrunchyComponent
    {
        [Tooltip("Optional profile asset. Field values are read live every FixedUpdate so " +
                 "editing the profile takes effect immediately without restarting.")]
        public CrunchyRagdollProfile Profile;

        [Tooltip("Root of the ragdoll rig containing the Rigidbodies. " +
                 "If null, uses the GameObject this component is on.")]
        public Transform RagdollRoot;

        [Header("Fallback (used when Profile is null)")]
        public float Tau = 12f;
        public float PositionTau = 0.08f;
        public int MinimumHoldFrames = 2;
        public int MaximumHoldFrames = 4;

        [Header("Settle (fallback)")]
        public float SettleVelocityThreshold = 0.75f;
        public float SettleAngularThreshold = 25f;
        public float SettleTime = 0.35f;
        public float WakeVelocityThreshold = 3.0f;

        [Header("Proxy (fallback)")]
        public int SnapshotBufferSize = 120;
        public bool HideSourceRenderers = true;
        public bool StripProxyComponents = true;
        public bool ForceEnableProxyRenderers = true;

        private Rigidbody[] _sourceBodies;
        private Transform[] _visualBones;
        private TrajectoryRecorder _recorder;
        private RigidbodySnapshotFrame _heldFrame;
        private GameObject _visualProxyRoot;

        private int _anchorIndex;
        private bool _initialized;
        private bool _settled;
        private float _settleTimer;
        private int _framesSinceSnap;
        private float _startTime;
        private Renderer[] _sourceRenderers;
        private Animator _sourceAnimator;

        private void Start()
        {
            _startTime = Time.fixedTime;
            GameObject rigSource = RagdollRoot != null ? RagdollRoot.gameObject : gameObject;

            _sourceAnimator = rigSource.GetComponentInChildren<Animator>(true);

            // Build the proxy FIRST. Instantiate copies component state, so
            // anything we change on the source after this point won't leak into
            // the clone.
            var proxy = RagdollProxyBuilder.Build(
                rigSource,
                stripComponents: ResolveStripProxyComponents(),
                forceEnableRenderers: ResolveForceEnableProxyRenderers());

            _visualProxyRoot = proxy.Proxy;
            _sourceBodies = proxy.SourceBodies;
            _visualBones = proxy.VisualBones;

            if (_sourceAnimator != null)
                _sourceAnimator.enabled = false;

            if (ResolveHideSourceRenderers())
            {
                _sourceRenderers = rigSource.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < _sourceRenderers.Length; i++)
                {
                    if (_sourceRenderers[i] != null)
                        _sourceRenderers[i].enabled = false;
                }
            }

            if (_sourceBodies == null || _sourceBodies.Length == 0 ||
                _visualBones == null || _visualBones.Length == 0)
            {
                Debug.LogWarning($"[CrunchyRagdoll/RagdollStepper] {gameObject.name} — " +
                                 "no tracked ragdoll bodies found. Disabling.");
                enabled = false;
                return;
            }

            _anchorIndex = RagdollProxyBuilder.PickAnchorIndex(_sourceBodies);
            _recorder = new TrajectoryRecorder(
                Mathf.Max(8, ResolveSnapshotBufferSize()),
                _sourceBodies.Length);

            Debug.Log($"[CrunchyRagdoll/RagdollStepper] {gameObject.name} — " +
                      $"{_sourceBodies.Length} tracked bones, crunchy visual proxy enabled");
        }

        private void FixedUpdate()
        {
            if (Profile != null && !Profile.Global.Enabled) return;
            if (_sourceBodies == null || _sourceBodies.Length == 0 || _visualBones == null)
                return;

            // Prune bones destroyed or deactivated externally (e.g. dismemberment).
            // Must run before Capture — a destroyed Rigidbody records default(snapshot)
            // which has position (0,0,0). ApplyToTransforms would then snap that proxy
            // bone to world origin, producing visible drift on dead-body hits.
            PruneDestroyedBodies();
            if (_sourceBodies.Length == 0) return;

            RigidbodySnapshotFrame rawFrame = _recorder.Capture(_sourceBodies, Time.fixedTime);

            if (!_initialized)
            {
                _heldFrame = rawFrame.Clone();
                _framesSinceSnap = 0;
                _settled = false;
                _settleTimer = 0f;
                _initialized = true;
                ApplyHeldFrame();
                return;
            }

            if (_settled)
            {
                if (AnchorWoke(rawFrame))
                {
                    _settled = false;
                    _settleTimer = 0f;
                    _heldFrame.CopyFrom(rawFrame);
                    _framesSinceSnap = 0;
                    Debug.Log($"[CrunchyRagdoll/RagdollStepper] {gameObject.name} woke " +
                              $"at t+{Time.fixedTime - _startTime:F2}s");
                }
                else
                {
                    ApplyHeldFrame();
                    return;
                }
            }

            UpdateSettleState(rawFrame);

            if (ShouldSnap(rawFrame))
            {
                _heldFrame.CopyFrom(rawFrame);
                _framesSinceSnap = 0;
            }
            else
            {
                _framesSinceSnap++;
            }

            ApplyHeldFrame();
        }

        private void LateUpdate()
        {
            // Keep the proxy visually locked even if the render frame lands after
            // one or more FixedUpdates. The proxy is separate from the physics rig,
            // so this is safe.
            if (_initialized)
                ApplyHeldFrame();
        }

        private void ApplyHeldFrame()
        {
            if (_heldFrame == null || _visualBones == null) return;
            _heldFrame.ApplyToTransforms(_visualBones);
        }

        private bool ShouldSnap(RigidbodySnapshotFrame rawFrame)
        {
            int minHold = ResolveMinHold();
            int maxHold = ResolveMaxHold();
            float rotTau = ResolveTau();
            float posTau = ResolvePosTau();

            if (_framesSinceSnap < minHold) return false;
            if (_framesSinceSnap >= maxHold) return true;

            RigidbodySnapshot heldAnchor = _heldFrame[_anchorIndex];
            RigidbodySnapshot rawAnchor = rawFrame[_anchorIndex];

            float positionDelta = Vector3.Distance(heldAnchor.Position, rawAnchor.Position);
            float rotationDelta = Quaternion.Angle(heldAnchor.Rotation, rawAnchor.Rotation);

            return positionDelta >= posTau || rotationDelta >= rotTau;
        }

        private void UpdateSettleState(RigidbodySnapshotFrame rawFrame)
        {
            if (AnchorStill(rawFrame))
            {
                _settleTimer += Time.fixedDeltaTime;
                if (_settleTimer >= ResolveSettleTime())
                {
                    _settled = true;
                    Debug.Log($"[CrunchyRagdoll/RagdollStepper] {gameObject.name} settled " +
                              $"at t+{Time.fixedTime - _startTime:F2}s");
                }
            }
            else
            {
                _settleTimer = 0f;
            }
        }

        private bool AnchorStill(RigidbodySnapshotFrame rawFrame)
        {
            float vTh = ResolveSettleVel();
            float aTh = ResolveSettleAng();
            // Require ALL tracked bones below threshold before settling.
            // Anchor-only would freeze the proxy with limbs still flailing.
            for (int i = 0; i < _sourceBodies.Length; i++)
            {
                RigidbodySnapshot snap = rawFrame[i];
                if (snap.Velocity.magnitude > vTh ||
                    snap.AngularVelocity.magnitude * Mathf.Rad2Deg > aTh)
                    return false;
            }
            return true;
        }

        private bool AnchorWoke(RigidbodySnapshotFrame rawFrame)
        {
            RigidbodySnapshot snap = rawFrame[_anchorIndex];
            float wake = ResolveWakeVel();
            return snap.Velocity.magnitude >= wake ||
                   snap.AngularVelocity.magnitude * Mathf.Rad2Deg >= (wake * 6f);
        }

        /// <summary>
        /// Removes bones that have been destroyed or deactivated externally
        /// (dismemberment, decapitation, pooling). Detects this before Capture
        /// so we never record a default snapshot (position 0,0,0) and snap a
        /// proxy bone to world origin.
        /// </summary>
        private void PruneDestroyedBodies()
        {
            bool dirty = false;
            for (int i = 0; i < _sourceBodies.Length; i++)
            {
                if (_sourceBodies[i] == null || !_sourceBodies[i].gameObject.activeInHierarchy)
                {
                    dirty = true;
                    break;
                }
            }
            if (!dirty) return;

            var newBodies = new List<Rigidbody>(_sourceBodies.Length);
            var newBones = new List<Transform>(_visualBones.Length);

            for (int i = 0; i < _sourceBodies.Length; i++)
            {
                bool sourceGone = _sourceBodies[i] == null ||
                                  !_sourceBodies[i].gameObject.activeInHierarchy;

                if (sourceGone)
                {
                    // Hide the matching proxy bone — don't destroy it, sibling
                    // bones are still attached to it as part of the hierarchy.
                    if (_visualBones[i] != null)
                        _visualBones[i].gameObject.SetActive(false);
                }
                else
                {
                    newBodies.Add(_sourceBodies[i]);
                    newBones.Add(_visualBones[i]);
                }
            }

            int removedCount = _sourceBodies.Length - newBodies.Count;
            _sourceBodies = newBodies.ToArray();
            _visualBones = newBones.ToArray();

            Debug.Log($"[CrunchyRagdoll/RagdollStepper] {gameObject.name} pruned " +
                      $"{removedCount} destroyed bone(s), {_sourceBodies.Length} remaining");

            if (_sourceBodies.Length == 0) return;

            _anchorIndex = RagdollProxyBuilder.PickAnchorIndex(_sourceBodies);
            _recorder = new TrajectoryRecorder(
                Mathf.Max(8, ResolveSnapshotBufferSize()),
                _sourceBodies.Length);

            // Re-seed _heldFrame on the next Capture rather than CopyFrom a
            // frame with the old (larger) body count.
            _heldFrame = null;
            _initialized = false;
        }

        private void OnDestroy()
        {
            CleanupProxy();
        }

        private void OnDisable()
        {
            // When the source is just deactivated (pooling, hit reactions), the
            // FixedUpdate/LateUpdate won't run while this component is disabled,
            // and the proxy freezes at its last pose. That is the desired behaviour
            // — the proxy outlives the source.
            //
            // Restore source renderers in case the source gets re-enabled for reuse.
            if (_sourceRenderers != null)
            {
                for (int i = 0; i < _sourceRenderers.Length; i++)
                {
                    if (_sourceRenderers[i] != null)
                        _sourceRenderers[i].enabled = true;
                }
            }
        }

        private void CleanupProxy()
        {
            if (_visualProxyRoot != null)
            {
                Destroy(_visualProxyRoot);
                _visualProxyRoot = null;
            }

            if (_sourceRenderers != null)
            {
                for (int i = 0; i < _sourceRenderers.Length; i++)
                {
                    if (_sourceRenderers[i] != null)
                        _sourceRenderers[i].enabled = true;
                }
            }
        }

        // -------------- profile-or-fallback resolvers --------------

        private float ResolveTau() => Profile != null ? Profile.DeathRagdoll.RagdollTau : Tau;
        private float ResolvePosTau() => Profile != null ? Profile.DeathRagdoll.RagdollPosTau : PositionTau;
        private int ResolveMinHold() => Profile != null ? Profile.DeathRagdoll.MinHoldFrames : MinimumHoldFrames;
        private int ResolveMaxHold() => Profile != null ? Profile.DeathRagdoll.MaxHoldFrames : MaximumHoldFrames;
        private float ResolveSettleVel() => Profile != null ? Profile.Settling.SettleVelocityThreshold : SettleVelocityThreshold;
        private float ResolveSettleAng() => Profile != null ? Profile.Settling.SettleAngularThreshold : SettleAngularThreshold;
        private float ResolveSettleTime() => Profile != null ? Profile.Settling.SettleTime : SettleTime;
        private float ResolveWakeVel() => Profile != null ? Profile.Settling.WakeVelocityThreshold : WakeVelocityThreshold;
        private int ResolveSnapshotBufferSize() => Profile != null ? Profile.Proxy.SnapshotBufferSize : SnapshotBufferSize;
        private bool ResolveHideSourceRenderers() => Profile != null ? Profile.Proxy.HideSourceRenderers : HideSourceRenderers;
        private bool ResolveStripProxyComponents() => Profile != null ? Profile.Proxy.StripProxyComponents : StripProxyComponents;
        private bool ResolveForceEnableProxyRenderers() => Profile != null ? Profile.Proxy.ForceEnableProxyRenderers : ForceEnableProxyRenderers;
    }
}
