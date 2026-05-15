using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(AnimationStepper))]
    public sealed class AnimationStepperEditor : UnityEditor.Editor
    {
        private bool _foldCrunch = true;
        private bool _foldCandidates = true;
        private bool _foldFilters = false;
        private bool _foldDebug = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var stepper = (AnimationStepper)target;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatorRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneRoot"));

            EditorGUILayout.Space(4);
            if (stepper.Profile != null)
                EditorGUILayout.HelpBox("Profile is assigned. Fallback fields below are ignored at runtime.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("No profile assigned. Fallback fields below will be used.", MessageType.None);
            EditorGUILayout.Space(4);

            _foldCrunch = EditorGUILayout.BeginFoldoutHeaderGroup(_foldCrunch, "Crunch");
            if (_foldCrunch)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Tau"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldCandidates = EditorGUILayout.BeginFoldoutHeaderGroup(_foldCandidates, "Candidate Sampling");
            if (_foldCandidates)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("CandidatesPerSegment"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldFilters = EditorGUILayout.BeginFoldoutHeaderGroup(_foldFilters, "Bone Filters");
            if (_foldFilters)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ExcludeKeywords"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldDebug = EditorGUILayout.BeginFoldoutHeaderGroup(_foldDebug, "Debug Preview");
            if (_foldDebug)
            {
                EditorGUI.indentLevel++;
                if (Application.isPlaying)
                {
                    if (GUILayout.Button("Flush All Holds Now"))
                        stepper.FlushAllHolds();
                    if (GUILayout.Button("Deactivate"))
                        stepper.Deactivate();
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
