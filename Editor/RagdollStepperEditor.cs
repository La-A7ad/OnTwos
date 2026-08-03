using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(RagdollStepper))]
    public sealed class RagdollStepperEditor : UnityEditor.Editor
    {
        private bool _foldCrunch = true;
        private bool _foldFilters = false;
        private bool _foldSettle = false;
        private bool _foldProxy = false;
        private bool _foldDebug = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var stepper = (RagdollStepper)target;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));

            EditorGUILayout.Space(4);
            if (stepper.Profile != null)
                EditorGUILayout.HelpBox("Profile is assigned. Fallback fields below are ignored at runtime.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("No profile assigned. Fallback fields below will be used.", MessageType.None);
            EditorGUILayout.Space(4);

            _foldCrunch = EditorGUILayout.BeginFoldoutHeaderGroup(_foldCrunch, "Ragdoll Crunch");
            if (_foldCrunch)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Tau"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("PositionTau"));
                EditorGUILayout.HelpBox(
                    "Step Rate and Cadence Jitter are profile-only — edit them on the assigned OnTwosProfile under the Ragdoll foldout.",
                    MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldFilters = EditorGUILayout.BeginFoldoutHeaderGroup(_foldFilters, "Bone Filters");
            if (_foldFilters)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneTunings"), true);
                EditorGUILayout.HelpBox(
                    "Reference the Rigidbody transforms on the source ragdoll — the visual proxy " +
                    "is a runtime clone and its transforms do not exist yet at author time.\n\n" +
                    "Takes precedence over the profile's BoneOverrides and ExcludeKeywords.",
                    MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldSettle = EditorGUILayout.BeginFoldoutHeaderGroup(_foldSettle, "Settling Rules");
            if (_foldSettle)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("SettleVelocityThreshold"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("SettleAngularThreshold"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("SettleTime"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("WakeVelocityThreshold"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldProxy = EditorGUILayout.BeginFoldoutHeaderGroup(_foldProxy, "Proxy Settings");
            if (_foldProxy)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("HideSourceRenderers"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("StripProxyComponents"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("EnableVisibilityCulling"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldDebug = EditorGUILayout.BeginFoldoutHeaderGroup(_foldDebug, "Debug Preview");
            if (_foldDebug)
            {
                EditorGUI.indentLevel++;
                if (Application.isPlaying)
                {
                    EditorGUILayout.HelpBox(
                        "The proxy is built on Start(). Inspect the scene hierarchy at runtime " +
                        "for a sibling GameObject named '[OnTwosProxy]'.",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("Available in Play mode.", MessageType.None);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
