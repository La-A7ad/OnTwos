using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(AnimationStepper))]
    public sealed class AnimationStepperEditor : UnityEditor.Editor
    {
        private bool _foldCrunch       = true;
        private bool _foldCandidates   = true;
        private bool _foldFilters      = false;
        private bool _foldPerformance  = false;
        private bool _foldDebug        = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var stepper = (AnimationStepper)target;

            // Mode first — it changes what the rest of the inspector means.
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Mode"));

            bool animatorDriven = stepper.Mode == AnimationStepper.StepperMode.AnimatorDriven;
            if (!animatorDriven)
                EditorGUILayout.HelpBox(
                    "AnySource mode: bones are read from whatever drives them each frame. " +
                    "No Animator required. Call FlushAllHolds() manually if your source " +
                    "system has discrete states.",
                    MessageType.None);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneRoot"));

            // Grey out AnimatorRoot when it has no effect.
            GUI.enabled = animatorDriven;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatorRoot"));
            GUI.enabled = true;

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

            _foldPerformance = EditorGUILayout.BeginFoldoutHeaderGroup(_foldPerformance, "Performance");
            if (_foldPerformance)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("EnableVisibilityCulling"));
                if (stepper.EnableVisibilityCulling)
                    EditorGUILayout.HelpBox(
                        "Bone writes are skipped while every Renderer in the hierarchy is off-screen. " +
                        "Schedulers keep running — no pop on visibility resume. " +
                        "Disable if the rig has no Renderers.",
                        MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldDebug = EditorGUILayout.BeginFoldoutHeaderGroup(_foldDebug, "Debug");
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
