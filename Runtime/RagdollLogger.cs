using System.Text;
using OnTwos.Runtime.Utilities;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
namespace OnTwos.Runtime
{
    /// <summary>
    /// Diagnostic logger attached alongside RagdollStepper. Captures the data
    /// needed to tune RagdollStepper thresholds:
    ///   - Per-bone mass and initial velocity snapshot at the start of recording
    ///   - Ground impact events (bone name, impact speed, timestamp)
    ///   - Per-bone velocity magnitude sampled every LogInterval seconds
    ///   - Final settled velocities
    ///
    /// Disable or remove this component before shipping — it allocates strings
    /// every LogInterval seconds and generates verbose log output.
    /// </summary>
    [AddComponentMenu("CrunchyRagdoll/Ragdoll Logger")]
    public sealed class RagdollLogger : MonoBehaviour, IOnTwosComponent
    {
        [Tooltip("How often to emit a velocity sample line (seconds).")]
        public float LogInterval = 0.1f;

        [Tooltip("Stop sampling after this many seconds to avoid logspam on long deaths.")]
        public float MaxLogDuration = 5f;

        [Tooltip("If true, attach CollisionListeners to every ragdoll bone to record impacts.")]
        public bool LogImpacts = true;

        private Rigidbody[] _rigidbodies;
        private float _startTime;
        private float _lastLogTime;
        private bool _done;

        private void Start()
        {
            _rigidbodies = GetComponentsInChildren<Rigidbody>();
            _startTime = Time.time;
            _lastLogTime = _startTime;

            LogStartSnapshot();

            if (LogImpacts)
            {
                foreach (Rigidbody rb in _rigidbodies)
                {
                    if (rb == null) continue;
                    var listener = rb.gameObject.AddComponent<CollisionListener>();
                    listener.BoneName = rb.name;
                    listener.StartTime = _startTime;
                }
            }
        }

        private void FixedUpdate()
        {
            if (_done) return;

            float elapsed = Time.fixedTime - _startTime;
            if (elapsed > MaxLogDuration)
            {
                LogFinalSnapshot();
                _done = true;
                return;
            }

            if (Time.fixedTime - _lastLogTime < LogInterval) return;
            _lastLogTime = Time.fixedTime;

            var sb = new StringBuilder();
            sb.Append($"[CrunchyRagdoll/Logger] {gameObject.name} t={elapsed:F3}s |");

            foreach (Rigidbody rb in _rigidbodies)
            {
                if (rb == null) continue;
#if UNITY_6000_0_OR_NEWER
                float linSpeed = rb.linearVelocity.magnitude;
#else
                float linSpeed = rb.velocity.magnitude;
#endif
                float angSpeed = rb.angularVelocity.magnitude * Mathf.Rad2Deg;
                sb.Append($" {rb.name}: lin={linSpeed:F3} ang={angSpeed:F1}deg/s |");
            }

            Debug.Log(sb.ToString());
        }

        private void LogStartSnapshot()
        {
            Debug.Log($"[CrunchyRagdoll/Logger] === START SNAPSHOT: {gameObject.name} at t={_startTime:F3} ===");
            Debug.Log($"[CrunchyRagdoll/Logger] gravity={Physics.gravity}");

            foreach (Rigidbody rb in _rigidbodies)
            {
                if (rb == null) continue;
#if UNITY_6000_0_OR_NEWER
                Vector3 v = rb.linearVelocity;
#else
                Vector3 v = rb.velocity;
#endif
                Debug.Log(
                    $"[CrunchyRagdoll/Logger]   bone={rb.name} " +
                    $"mass={rb.mass:F3} " +
                    $"vel={v} (mag={v.magnitude:F3}) " +
                    $"angVel={rb.angularVelocity} (mag={rb.angularVelocity.magnitude * Mathf.Rad2Deg:F1}deg/s)");
            }
        }

        private void LogFinalSnapshot()
        {
            float elapsed = Time.time - _startTime;
            Debug.Log($"[CrunchyRagdoll/Logger] === FINAL SNAPSHOT: {gameObject.name} at t+{elapsed:F3}s ===");

            foreach (Rigidbody rb in _rigidbodies)
            {
                if (rb == null) continue;
#if UNITY_6000_0_OR_NEWER
                float linSpeed = rb.linearVelocity.magnitude;
#else
                float linSpeed = rb.velocity.magnitude;
#endif
                Debug.Log(
                    $"[CrunchyRagdoll/Logger]   bone={rb.name} " +
                    $"vel={linSpeed:F3}m/s " +
                    $"ang={rb.angularVelocity.magnitude * Mathf.Rad2Deg:F1}deg/s");
            }
        }
    }

    /// <summary>
    /// Attached to individual ragdoll bones to capture ground contact events.
    /// </summary>
    public sealed class CollisionListener : MonoBehaviour
    {
        public string BoneName;
        public float StartTime;

        private void OnCollisionEnter(Collision collision)
        {
            float impactSpeed = collision.relativeVelocity.magnitude;
            float elapsed = Time.time - StartTime;

            Debug.Log(
                $"[CrunchyRagdoll/Logger] IMPACT: {BoneName} hit '{collision.gameObject.name}' " +
                $"speed={impactSpeed:F3}m/s at t+{elapsed:F3}s");

            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
#if UNITY_6000_0_OR_NEWER
                float v = rb.linearVelocity.magnitude;
#else
                float v = rb.velocity.magnitude;
#endif
                Debug.Log(
                    $"[CrunchyRagdoll/Logger]   post-impact vel={v:F3}m/s " +
                    $"ang={rb.angularVelocity.magnitude * Mathf.Rad2Deg:F1}deg/s");
            }
        }
    }
}
#endif