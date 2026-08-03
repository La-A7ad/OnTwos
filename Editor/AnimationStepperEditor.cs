using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor
{
    [CustomEditor(typeof(AnimationStepper))]
    public sealed class AnimationStepperEditor : UnityEditor.Editor
    {
        private bool _foldCrunch       = true;
        private bool _foldOffset       = true;
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
            bool hasOffsetRoot   = stepper.VisualOffsetRoot != null;

            float stepRate = hasProfile ? stepper.Profile.LiveAnimation.StepRate : stepper.StepRate;
            float jitter   = Mathf.Clamp01(hasProfile
                ? stepper.Profile.LiveAnimation.CadenceJitter : stepper.CadenceJitter);
            bool cadenceLocked = jitter <= 0f;

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

            _foldOffset = EditorGUILayout.BeginFoldoutHeaderGroup(_foldOffset, "Visual Offset (foot planting)");
            if (_foldOffset)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("VisualOffsetRoot"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("MaxVisualOffset"));

                if (!hasOffsetRoot)
                {
                    EditorGUILayout.HelpBox(
                        "No offset root — the rig's world position is not held, so feet will " +
                        "slide while the character moves. Rotation stepping still works.",
                        MessageType.None);

                    if (!Application.isPlaying && GUILayout.Button("Create Visual Offset Root"))
                        CreateVisualOffsetRoot(stepper);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "The rig is offset from its colliders by up to Max Visual Offset. " +
                        "Colliders do not move, so hits resolve against the collider rather " +
                        "than the visible mesh — keep this small in a shooter.",
                        MessageType.None);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldCadence = EditorGUILayout.BeginFoldoutHeaderGroup(_foldCadence, "Cadence");
            if (_foldCadence)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("StepRate"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("CadenceJitter"));

                // stepRate / jitter were resolved at the top of the pass and reflect the
                // profile whenever one is assigned, not the fallback fields above.
                EditorGUILayout.HelpBox(
                    cadenceLocked
                        ? $"Locked cadence: {stepRate:F1} poses/sec — {CadenceName(stepRate)}. " +
                          "Every bone snaps on the same beat. Identical at any framerate, " +
                          "and a baked clip will match this."
                        : $"Jittered cadence: {stepRate:F1} poses/sec with {jitter:P0} drift. " +
                          "Bones crossing Tau early snap ahead of the beat and desynchronise. " +
                          "Set Cadence Jitter to 0 for a metronomic, in-lockstep look.",
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

                EditorGUILayout.Space(4);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("BoneTunings"), true);
                EditorGUILayout.HelpBox(
                    "BoneTunings wins over the profile's BoneOverrides and over ExcludeKeywords. " +
                    "Drag bones in directly — no naming convention required, so it works on any rig.\n\n" +
                    "It lives here rather than on the profile because a profile is a shared asset " +
                    "and cannot hold scene references.",
                    MessageType.None);
                EditorGUILayout.HelpBox(
                    "All bone rules are re-resolved during Play mode as soon as you edit them.",
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

        /// <summary>
        /// Inserts a GameObject between the stepper's transform and the rig root, and
        /// assigns it as the visual offset root.
        ///
        /// Author-time only, deliberately. Doing this at runtime would reparent the rig
        /// out from under any script, animation event or IK rig already holding a
        /// reference to a bone's ancestor chain.
        /// </summary>
        private static void CreateVisualOffsetRoot(AnimationStepper stepper)
        {
            Transform rigRoot = stepper.BoneRoot != null ? stepper.BoneRoot : stepper.transform;

            if (rigRoot == stepper.transform)
            {
                EditorUtility.DisplayDialog(
                    "Cannot create offset root",
                    "BoneRoot resolves to the stepper's own transform, so there is nothing to " +
                    "offset independently of the collider.\n\nAssign BoneRoot to the rig's root " +
                    "bone (e.g. the Armature or Hips) first, then try again.",
                    "OK");
                return;
            }

            var offset = new GameObject("VisualOffset");
            Undo.RegisterCreatedObjectUndo(offset, "Create Visual Offset Root");

            Transform originalParent = rigRoot.parent;
            Undo.SetTransformParent(offset.transform, originalParent, "Create Visual Offset Root");
            offset.transform.localPosition = Vector3.zero;
            offset.transform.localRotation = Quaternion.identity;
            offset.transform.localScale    = Vector3.one;

            Undo.SetTransformParent(rigRoot, offset.transform, "Create Visual Offset Root");

            Undo.RecordObject(stepper, "Create Visual Offset Root");
            stepper.VisualOffsetRoot = offset.transform;
            EditorUtility.SetDirty(stepper);

            Selection.activeGameObject = offset;
        }

        // Traditional animation shorthand. The classical terms are relative to 24fps
        // film, so the rate is converted back to "held N frames of 24" to name it.
        private static string CadenceName(float posesPerSecond)
        {
            if (posesPerSecond <= 0f) return "invalid";
            float heldFramesAt24 = 24f / posesPerSecond;
            int n = Mathf.RoundToInt(heldFramesAt24);
            if (Mathf.Abs(heldFramesAt24 - n) > 0.15f)
                return $"~{heldFramesAt24:F1} frames of 24";

            return n switch
            {
                1 => "on ones (no stepping)",
                2 => "on twos",
                3 => "on threes",
                4 => "on fours",
                _ => $"on {n}s"
            };
        }
    }
}
