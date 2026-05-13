using CrunchyRagdoll.Runtime;
using CrunchyRagdoll.Runtime.Utilities;
using UnityEditor;
using UnityEngine;

namespace CrunchyRagdoll.Editor
{
    [CustomEditor(typeof(CrunchyRagdollAuthoring))]
    public sealed class CrunchyRagdollAuthoringEditor : UnityEditor.Editor
    {
        private bool _foldBindings = true;
        private bool _foldAutoSetup = true;
        private bool _foldBoneMapping = false;
        private bool _foldValidation = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var authoring = (CrunchyRagdollAuthoring)target;

            EditorGUILayout.LabelField("CrunchyRagdoll Authoring", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Attach to the root of an enemy/character prefab. AnimationStepper is added " +
                "on Awake. Call GoLimp() from your death logic to swap to the ragdoll path.",
                MessageType.None);

            // -- Bindings
            _foldBindings = EditorGUILayout.BeginFoldoutHeaderGroup(_foldBindings, "Bindings");
            if (_foldBindings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatorRoot"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneRoot"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("RagdollRoot"));
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
                        Undo.RecordObject(authoring, "CrunchyRagdoll Auto-Bind");
                        authoring.AutoResolveBindings();
                        EditorUtility.SetDirty(authoring);
                    }

                    if (GUILayout.Button("Clear Bindings"))
                    {
                        Undo.RecordObject(authoring, "CrunchyRagdoll Clear Bindings");
                        authoring.AnimatorRoot = null;
                        authoring.BoneRoot = null;
                        authoring.RagdollRoot = null;
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

                Transform boneRoot = authoring.BoneRoot != null ? authoring.BoneRoot : authoring.transform;
                Transform ragdollRoot = authoring.RagdollRoot != null ? authoring.RagdollRoot : authoring.transform;
                int boneCount = boneRoot.GetComponentsInChildren<Transform>(true).Length;
                int rigidbodyCount = ragdollRoot.GetComponentsInChildren<Rigidbody>(true).Length;

                EditorGUILayout.LabelField($"Transforms under bone root: {boneCount}");
                EditorGUILayout.LabelField($"Rigidbodies under ragdoll root: {rigidbodyCount}");

                if (rigidbodyCount == 0 && authoring.AutoCreateProxy)
                    EditorGUILayout.HelpBox("AutoCreateProxy is on but no Rigidbodies exist under the ragdoll root. The proxy will be empty.", MessageType.Warning);

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

                bool hasRagdoll = CrunchyRagdollAutoBinder.HasRagdoll(authoring.RagdollRoot ?? authoring.transform);
                EditorGUILayout.LabelField("Detected ragdoll joints:", hasRagdoll ? "Yes" : "No");
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
                    if (GUILayout.Button("GoLimp() Now"))
                        authoring.GoLimp();
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
