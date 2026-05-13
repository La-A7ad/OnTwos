using CrunchyRagdoll.Runtime;
using UnityEditor;
using UnityEngine;

namespace CrunchyRagdoll.Editor
{
    [CustomEditor(typeof(CrunchyRagdollProfile))]
    public sealed class CrunchyRagdollProfileEditor : UnityEditor.Editor
    {
        // Foldout names per architecture spec.
        private static class K
        {
            public const string Global = "Global";
            public const string LiveAnimation = "Live Animation";
            public const string DeathRagdoll = "Death Ragdoll";
            public const string Settling = "Settling";
            public const string Proxy = "Proxy Rig";
            public const string BoneRules = "Bone Rules";
            public const string Diagnostics = "Diagnostics Preview";
        }

        private bool _foldGlobal = true;
        private bool _foldLive = true;
        private bool _foldDeath = true;
        private bool _foldSettling = false;
        private bool _foldProxy = false;
        private bool _foldBones = false;
        private bool _foldDiag = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var profile = (CrunchyRagdollProfile)target;

            DrawHeader();

            _foldGlobal = EditorGUILayout.BeginFoldoutHeaderGroup(_foldGlobal, K.Global);
            if (_foldGlobal)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Global"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldLive = EditorGUILayout.BeginFoldoutHeaderGroup(_foldLive, K.LiveAnimation);
            if (_foldLive)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("LiveAnimation"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldDeath = EditorGUILayout.BeginFoldoutHeaderGroup(_foldDeath, K.DeathRagdoll);
            if (_foldDeath)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("DeathRagdoll"), true);
                if (profile.DeathRagdoll.MaxHoldFrames < profile.DeathRagdoll.MinHoldFrames)
                    EditorGUILayout.HelpBox("Max Hold Frames < Min Hold Frames — every frame will force a snap.", MessageType.Warning);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldSettling = EditorGUILayout.BeginFoldoutHeaderGroup(_foldSettling, K.Settling);
            if (_foldSettling)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Settling"), true);
                if (profile.Settling.WakeVelocityThreshold <= profile.Settling.SettleVelocityThreshold)
                    EditorGUILayout.HelpBox(
                        "Wake threshold <= Settle threshold. The ragdoll will wake on the same noise that should settle it.",
                        MessageType.Warning);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldProxy = EditorGUILayout.BeginFoldoutHeaderGroup(_foldProxy, K.Proxy);
            if (_foldProxy)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Proxy"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldBones = EditorGUILayout.BeginFoldoutHeaderGroup(_foldBones, K.BoneRules);
            if (_foldBones)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneOverrides"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldDiag = EditorGUILayout.BeginFoldoutHeaderGroup(_foldDiag, K.Diagnostics);
            if (_foldDiag)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Live preview is available in Play mode while a CrunchyRagdollAuthoring instance " +
                    "is active in the scene. Use Window → CrunchyRagdoll → Preview to open the " +
                    "telemetry window.",
                    MessageType.Info);
                if (GUILayout.Button("Open Preview Window"))
                    Windows.CrunchyRagdollPreviewWindow.ShowWindow();
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("CrunchyRagdoll Profile", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Tuning preset for the stepped-animation and ragdoll systems. Assign to a " +
                "CrunchyRagdollAuthoring on each enemy prefab that should use these values.",
                MessageType.None);
            EditorGUILayout.Space(4);
        }
    }
}
