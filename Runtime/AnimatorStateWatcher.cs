using UnityEngine;

namespace OnTwos.Runtime.Utilities
{
    /// <summary>
    /// Watches a single Animator layer for state transitions.
    ///
    /// AnimationStepper uses this to detect when, e.g., a character goes from
    /// idle to jump-attack so it can flush all bone hold buffers — otherwise the
    /// stepper would briefly hold the old idle pose while the new state begins,
    /// making the character appear to face the wrong way at the start of an attack.
    /// </summary>
    public sealed class AnimatorStateWatcher
    {
        private readonly Animator _animator;
        private readonly int _layerIndex;
        private int _lastStateHash;
        private bool _initialized;

        public AnimatorStateWatcher(Animator animator, int layerIndex = 0)
        {
            _animator = animator;
            _layerIndex = layerIndex;
            _lastStateHash = -1;
            _initialized = false;
        }

        public bool IsValid => _animator != null;

        /// <summary>
        /// Returns true if the watched layer transitioned to a different state
        /// since the last call. First call always returns false (no prior state
        /// to compare against).
        /// </summary>
        public bool Poll()
        {
            if (_animator == null) return false;

            int hash = _animator.GetCurrentAnimatorStateInfo(_layerIndex).shortNameHash;

            if (!_initialized)
            {
                _lastStateHash = hash;
                _initialized = true;
                return false;
            }

            if (hash != _lastStateHash)
            {
                _lastStateHash = hash;
                return true;
            }
            return false;
        }

        public void Reset()
        {
            _initialized = false;
            _lastStateHash = -1;
        }
    }
}
