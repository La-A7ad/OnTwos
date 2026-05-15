using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(OnTwosAuthoring))]
    public sealed class OnTwosAuthoringEditor : UnityEditor.Editor
    {
        private bool _foldBindings = true;
        private bool _foldAutoSetup = true;
        private bool _foldBoneMapping = false;
        private bool _foldValidation = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var authoring = (OnTwosAuthoring)target;

            EditorGUILayout.LabelField("OnTwos Authoring", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Attach to the root of any rig you want stepped. AnimationStepper is added " +
                "on Awake. Call ActivateRagdoll() at runtime (or use the button below) to swap " +
                "to the physics-driven path; Deactivate() reverses the transition.",
                MessageType.None);

            // -- Bindings
            _foldBindings = EditorGUILayout.BeginFoldoutHeaderGroup(_foldBindings, "Bindings");
            if (_foldBindings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatorRoot"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneRoot"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("PhysicsRoot"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // -- Auto Setup
            _foldAutoSetup = EditorGUILayout.BeginFoldoutHeaderGroup(_foldAutoSetup, "Auto Setup");
            if (_foldAutoSetup)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("AutoBindOnAwake"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("AutoCreateProxy"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("AddDiagnostics"));

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Try Auto-Bind Now"))
                    {
                        Undo.RecordObject(authoring, "OnTwos Auto-Bind");
                        authoring.AutoResolveBindings();
                        EditorUtility.SetDirty(authoring);
                    }

                    if (GUILayout.Button("Clear Bindings"))
                    {
                        Undo.RecordObject(authoring, "OnTwos Clear Bindings");
                        authoring.AnimatorRoot = null;
                        authoring.BoneRoot = null;
                        authoring.PhysicsRoot = null;
                        EditorUtility.SetDirty(authoring);
                    }
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // -- Bone Mapping
            _foldBoneMapping = EditorGUILayout.BeginFoldoutHeaderGroup(_foldBoneMapping, "Bone Mapping");
            if (_foldBoneMapping)
            {
                EditorGUI.indentLevel++;

                Transform boneRoot    = authoring.BoneRoot    != null ? authoring.BoneRoot    : authoring.transform;
                Transform physicsRoot = authoring.PhysicsRoot != null ? authoring.PhysicsRoot : authoring.transform;
                int boneCount      = boneRoot.GetComponentsInChildren<Transform>(true).Length;
                int rigidbodyCount = physicsRoot.GetComponentsInChildren<Rigidbody>(true).Length;

                EditorGUILayout.LabelField($"Transforms under bone root: {boneCount}");
                EditorGUILayout.LabelField($"Rigidbodies under physics root: {rigidbodyCount}");

                if (rigidbodyCount == 0 && authoring.AutoCreateProxy)
                    EditorGUILayout.HelpBox("AutoCreateProxy is on but no Rigidbodies exist under the physics root. The proxy will be empty.", MessageType.Warning);

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // -- Validation
            _foldValidation = EditorGUILayout.BeginFoldoutHeaderGroup(_foldValidation, "Validation");
            if (_foldValidation)
            {
                EditorGUI.indentLevel++;
                string issue = authoring.Validate();
                if (issue == null)
                    EditorGUILayout.HelpBox("Configuration looks good.", MessageType.Info);
                else
                    EditorGUILayout.HelpBox(issue, MessageType.Warning);

                bool hasPhysics = OnTwosAutoBinder.HasPhysicsBodies(authoring.PhysicsRoot ?? authoring.transform);
                EditorGUILayout.LabelField("Detected Rigidbodies under physics root:", hasPhysics ? "Yes" : "No");
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // -- Play-mode actions
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Runtime Actions", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = !authoring.IsRagdollActive;
                    if (GUILayout.Button("Activate Ragdoll"))
                        authoring.ActivateRagdoll();

                    GUI.enabled = authoring.IsRagdollActive;
                    if (GUILayout.Button("Deactivate"))
                        authoring.Deactivate();

                    GUI.enabled = true;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
