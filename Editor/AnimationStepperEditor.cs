using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(AnimationStepper))]
    public sealed class AnimationStepperEditor : UnityEditor.Editor
    {
        private bool _foldCrunch       = true;
        private bool _foldCadence      = true;
        private bool _foldCandidates   = true;
        private bool _foldFilters      = false;
        private bool _foldPerformance  = false;
        private bool _foldDebug        = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var stepper = (AnimationStepper)target;

            // Every condition that gates a HelpBox is evaluated once, here, before any
            // drawing. Evaluating them inline is what desyncs Unity's Layout and Repaint
            // passes: ApplyModifiedProperties() at the end of the Layout pass can change
            // one of these values, so Repaint emits a different number of controls and
            // GUILayout throws "You can't nest Foldout Headers, end it with
            // EndFoldoutHeader", truncating everything after it. Same fix as
            // OnTwosProfileEditor.
            bool animatorDriven  = stepper.Mode == AnimationStepper.StepperMode.AnimatorDriven;
            bool hasProfile      = stepper.Profile != null;
            bool noSmearBone     = stepper.SmearReferenceBone == null;
            bool cullingEnabled  = stepper.EnableVisibilityCulling;

            int minHold = hasProfile ? stepper.Profile.LiveAnimation.MinHoldFrames : stepper.MinHoldFrames;
            int maxHold = Mathf.Max(minHold,
                hasProfile ? stepper.Profile.LiveAnimation.MaxHoldFrames : stepper.MaxHoldFrames);
            bool cadenceLocked = minHold == maxHold;

            // Mode first — it changes what the rest of the inspector means.
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Mode"));

            if (!animatorDriven)
                EditorGUILayout.HelpBox(
                    "AnySource mode: bones are read from whatever drives them each frame. " +
                    "No Animator required. Call FlushAllHolds() manually if your source " +
                    "system has discrete states.",
                    MessageType.None);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneRoot"));

            // Grey out AnimatorRoot / AnimatorLayerIndex when they have no effect.
            GUI.enabled = animatorDriven;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatorRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatorLayerIndex"));
            GUI.enabled = true;

            // Smear reference bone is always used — it has no profile equivalent, so it
            // sits above the fallback notice rather than inside it.
            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("SmearReferenceBone"));
            if (noSmearBone)
                EditorGUILayout.HelpBox(
                    "No smear bone assigned — _SmearDirection/_SmearStrength are not written. " +
                    "Assign a bone that actually moves (Hips), not the Armature root.",
                    MessageType.None);

            EditorGUILayout.Space(4);
            if (hasProfile)
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

            _foldCadence = EditorGUILayout.BeginFoldoutHeaderGroup(_foldCadence, "Cadence");
            if (_foldCadence)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("MinHoldFrames"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("MaxHoldFrames"));

                // minHold / maxHold were resolved at the top of the pass and reflect the
                // profile whenever one is assigned, not the fallback fields above.
                EditorGUILayout.HelpBox(
                    cadenceLocked
                        ? $"Locked cadence: every bone snaps together every {minHold} frames — " +
                          $"{CadenceName(minHold)}. Tau no longer affects timing in this mode."
                        : $"Adaptive cadence ({minHold}-{maxHold} frames). Bones snap independently " +
                          "once Tau is crossed, so they drift out of sync with each other. " +
                          "Set Min equal to Max for a metronomic, in-lockstep look.",
                    cadenceLocked ? MessageType.Info : MessageType.Warning);
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
                if (hasProfile)
                    EditorGUILayout.HelpBox(
                        "ExcludeKeywords above is ignored — the profile's list replaces it outright. " +
                        "ExcludeBones below is always applied.",
                        MessageType.Warning);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ExcludeBones"), true);
                EditorGUILayout.HelpBox(
                    "Exclusion is resolved once in Start(). Changes made during Play mode " +
                    "take effect only after re-entering Play mode.",
                    MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldPerformance = EditorGUILayout.BeginFoldoutHeaderGroup(_foldPerformance, "Performance");
            if (_foldPerformance)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("EnableVisibilityCulling"));
                if (cullingEnabled)
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

        // Traditional animation shorthand for a locked hold length.
        private static string CadenceName(int holdFrames) => holdFrames switch
        {
            1 => "on ones (no stepping)",
            2 => "on twos",
            3 => "on threes",
            4 => "on fours",
            _ => $"on {holdFrames}s"
        };
    }
}
