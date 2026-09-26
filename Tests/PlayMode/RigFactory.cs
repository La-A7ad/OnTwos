using System.Collections.Generic;
using OnTwos.Runtime;
using UnityEngine;

namespace OnTwos.Tests.PlayMode
{
    /// <summary>
    /// Builds every rig these tests need procedurally, at runtime, from primitives.
    ///
    /// Deliberately no prefabs and no imported assets. A test suite that depends on an
    /// imported humanoid rig cannot run in CI, breaks when the asset is reimported, and
    /// makes every failure ambiguous between "the code is wrong" and "the asset moved".
    /// Everything here is reproducible from source.
    /// </summary>
    public static class RigFactory
    {
        /// <summary>Tracks everything spawned so a fixture can tear the scene down.</summary>
        public sealed class Scope
        {
            private readonly List<GameObject> _objects = new List<GameObject>();

            public T Track<T>(T go) where T : Object
            {
                if (go is GameObject g) _objects.Add(g);
                else if (go is Component c) _objects.Add(c.gameObject);
                return go;
            }

            public void Dispose()
            {
                foreach (GameObject go in _objects)
                    if (go != null) Object.Destroy(go);
                _objects.Clear();
            }
        }

        /// <summary>A large static box to land on, so bodies actually come to rest.</summary>
        public static GameObject CreateFloor(Scope scope)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "TestFloor";
            floor.transform.position   = new Vector3(0f, -0.5f, 0f);
            floor.transform.localScale = new Vector3(60f, 1f, 60f);
            return scope.Track(floor);
        }

        /// <summary>One Rigidbody with a collider and a renderer — the simplest valid rig.</summary>
        public static GameObject CreateSingleBody(Scope scope, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "SingleBody";
            go.transform.position = position;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 1f;
            return scope.Track(go);
        }

        /// <summary>
        /// A jointed chain, which is what "ragdoll" means for the code under test:
        /// several Rigidbodies sharing one PhysX island via CharacterJoints. Sleep and
        /// wake propagate across that island, which is the behaviour the lifecycle
        /// tests depend on.
        /// </summary>
        public static GameObject CreateJointedChain(Scope scope, int links, Vector3 origin)
        {
            var root = new GameObject($"Chain{links}");
            root.transform.position = origin;
            scope.Track(root);

            Rigidbody previous = null;
            Transform parent = root.transform;

            for (int i = 0; i < links; i++)
            {
                var link = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                link.name = $"Link{i}";
                link.transform.SetParent(parent, false);
                link.transform.localPosition = new Vector3(0f, i == 0 ? 0f : -0.5f, 0f);
                link.transform.localScale    = new Vector3(0.3f, 0.25f, 0.3f);

                var rb = link.AddComponent<Rigidbody>();
                rb.mass = 1f;

                if (previous != null)
                {
                    var joint = link.AddComponent<CharacterJoint>();
                    joint.connectedBody = previous;
                }

                previous = rb;
                parent   = link.transform;
            }

            return root;
        }

        /// <summary>
        /// A bone hierarchy with no Rigidbodies and no Animator, for driving
        /// AnimationStepper in AnySource mode. A SkinnedMeshRenderer is not required —
        /// the stepper reads and writes localRotation and only consults Renderers for
        /// visibility culling.
        /// </summary>
        public static Transform CreateBoneChain(Scope scope, int bones)
        {
            var root = new GameObject("RigRoot");
            scope.Track(root);

            Transform parent = root.transform;
            for (int i = 0; i < bones; i++)
            {
                var bone = new GameObject($"Bone{i}");
                bone.transform.SetParent(parent, false);
                bone.transform.localPosition = new Vector3(0f, 0.25f, 0f);
                parent = bone.transform;
            }

            // One renderer so visibility culling has something to consult.
            var vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vis.transform.SetParent(root.transform, false);
            vis.transform.localScale = Vector3.one * 0.1f;

            return root.transform;
        }

        /// <summary>A runtime profile. Not an asset, so nothing is written to disk.</summary>
        public static OnTwosProfile CreateProfile(
            float ragdollTau = 12f,
            float stepRate   = 12f,
            float jitter     = 0f,
            float settleTime = 0.1f,
            float settleVelocity = 0.75f,
            float settleAngular  = 25f)
        {
            var p = ScriptableObject.CreateInstance<OnTwosProfile>();
            p.Ragdoll.RagdollTau    = ragdollTau;
            p.Ragdoll.StepRate      = stepRate;
            p.Ragdoll.CadenceJitter = jitter;
            p.Settling.SettleTime              = settleTime;
            p.Settling.SettleVelocityThreshold = settleVelocity;
            p.Settling.SettleAngularThreshold  = settleAngular;
            return p;
        }

        /// <summary>Counts Unity lifecycle callbacks, for the proxy-build regression.</summary>
        public sealed class LifecycleProbe : MonoBehaviour
        {
            public static int Awakes;
            public static int Enables;
            public static int Disables;
            public static int Destroys;

            public static void ResetCounts() { Awakes = Enables = Disables = Destroys = 0; }

            private void Awake()     => Awakes++;
            private void OnEnable()  => Enables++;
            private void OnDisable() => Disables++;
            private void OnDestroy() => Destroys++;
        }
    }
}
