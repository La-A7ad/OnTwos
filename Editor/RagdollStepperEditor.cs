using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(RagdollStepper))]
    public sealed class RagdollStepperEditor : UnityEditor.Editor
    {
        private bool _foldCrunch = true;
        private bool _foldBuffer = false;
        private bool _foldSettle = false;
        private bool _foldProxy = false;
        private bool _foldDebug = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var stepper = (RagdollStepper)target;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PhysicsRoot"));

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
                    "MinHoldFrames and MaxHoldFrames are profile-only — edit them on the assigned OnTwosProfile under the Ragdoll foldout.",
                    MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldBuffer = EditorGUILayout.BeginFoldoutHeaderGroup(_foldBuffer, "Snapshot Buffer");
            if (_foldBuffer)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "SnapshotBufferSize is profile-only — edit it on the assigned OnTwosProfile under the Proxy foldout.",
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
