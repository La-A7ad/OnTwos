using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(OnTwosProfile))]
    public sealed class OnTwosProfileEditor : UnityEditor.Editor
    {
        private static class K
        {
            public const string Global        = "Global";
            public const string LiveAnimation = "Live Animation";
            public const string Ragdoll       = "Ragdoll";
            public const string Settling      = "Settling";
            public const string Proxy         = "Proxy Rig";
            public const string BoneRules     = "Bone Rules";
            public const string Diagnostics   = "Diagnostics Preview";
        }

        private bool _foldGlobal    = true;
        private bool _foldLive      = true;
        private bool _foldRagdoll   = true;
        private bool _foldSettling  = false;
        private bool _foldProxy     = false;
        private bool _foldBones     = false;
        private bool _foldDiag      = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var profile = (OnTwosProfile)target;

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

            _foldRagdoll = EditorGUILayout.BeginFoldoutHeaderGroup(_foldRagdoll, K.Ragdoll);
            if (_foldRagdoll)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Ragdoll"), true);
                if (profile.Ragdoll.MaxHoldFrames < profile.Ragdoll.MinHoldFrames)
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
                        "Wake threshold <= Settle threshold. The rig will wake on the same noise that should settle it.",
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
                    "Live telemetry is available in Play Mode while an OnTwosAuthoring instance " +
                    "is active in the scene. Use Window → CrunchyRagdoll → Preview.",
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
                "Tuning preset for the stepped-animation and ragdoll systems. " +
                "Assign to an OnTwosAuthoring component on any rig that should use these values.",
                MessageType.None);
            EditorGUILayout.Space(4);
        }
    }
}