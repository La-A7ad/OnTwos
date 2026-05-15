using System.Collections.Generic;
using System.IO;
using OnTwos.Runtime;
using OnTwos.Runtime.Math;
using OnTwos.Runtime.Utilities;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor.Windows
{
    /// <summary>
    /// Samples a source AnimationClip through the OnTwos PCHIP + deviation-threshold
    /// pipeline and writes the result as a new stepped AnimationClip asset.
    ///
    /// The output clip plays back identically to the runtime AnimationStepper effect
    /// and does not require the OnTwos system to be present at runtime — useful for
    /// shipping builds where you want to bake the look rather than run it live, or
    /// for exporting the stepped version for use in other tools.
    ///
    /// Physics simulations cannot be baked (they are non-deterministic and real-time).
    /// This window only handles Animator-driven clips.
    /// </summary>
    public sealed class CrunchyRagdollBakeWindow : EditorWindow
    {
        private AnimationClip _sourceClip;
        private GameObject    _skeletonObject;  // must be a scene instance with Animator
        private OnTwosProfile _profile;
        private string        _outputFolder = "Assets/BakedClips";

        // Per-bake tau multiplier curve. Domain: 0..1 (normalised clip time).
        // Range: any positive number; multiplied into Profile.LiveAnimation.AnimTau
        // before each scheduler.Update() call to let artists sculpt crunchiness
        // intentionally — a sharp ramp for an impact moment, a plateau for a
        // sustained section, a downslope back to subtle. Default is flat 1.0
        // so behaviour matches a tau-curve-less bake when untouched.
        private AnimationCurve _tauOverTime = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        private string _lastStatus;
        private bool   _lastSuccess;
        private bool   _isBaking;

        [MenuItem("Window/CrunchyRagdoll/Bake Clip")]
        public static void ShowWindow()
        {
            var win = GetWindow<CrunchyRagdollBakeWindow>("Bake Stepped Clip");
            win.minSize = new Vector2(420, 420);
            win.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("CrunchyRagdoll — Bake Stepped Clip", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.HelpBox(
                "Samples a source AnimationClip through the OnTwos stepping pipeline and " +
                "saves the result as a new .anim asset. The output clip has stepped, constant-" +
                "interpolation keyframes and can be used in any Animator without the OnTwos " +
                "runtime system.\n\n" +
                "Animator-driven clips only — physics simulations cannot be baked.",
                MessageType.Info);

            EditorGUILayout.Space(8);

            _sourceClip = (AnimationClip)EditorGUILayout.ObjectField(
                new GUIContent("Source Clip", "The AnimationClip to bake."),
                _sourceClip, typeof(AnimationClip), false);

            _skeletonObject = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Skeleton Object", "A scene instance of the rig. Must have an Animator " +
                    "and the bone hierarchy the clip drives. Pose does not matter — the source clip " +
                    "will be sampled on it frame by frame."),
                _skeletonObject, typeof(GameObject), true);

            _profile = (OnTwosProfile)EditorGUILayout.ObjectField(
                new GUIContent("Profile", "The OnTwosProfile to read AnimTau, GaussPoints, and " +
                    "BufferSize from. ExcludeKeywords are also applied — baked bones match runtime."),
                _profile, typeof(OnTwosProfile), false);

            _outputFolder = EditorGUILayout.TextField(
                new GUIContent("Output Folder", "Project-relative folder where the baked clip is saved."),
                _outputFolder);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Crunch Sculpting", EditorStyles.boldLabel);

            _tauOverTime = EditorGUILayout.CurveField(
                new GUIContent("Tau Over Time",
                    "Multiplier on the profile's AnimTau, evaluated at each baked frame. " +
                    "X axis = normalised clip time (0..1). Y axis = tau multiplier. " +
                    "Default flat 1.0 = behaviour identical to a tau-curve-less bake. " +
                    "Peak above 1 for chunkier moments; dip below 1 for smoother sections."),
                _tauOverTime,
                Color.cyan,
                new Rect(0f, 0f, 1f, 4f));

            if (_profile != null && _profile.LiveAnimation.PositionTau > 0f)
            {
                EditorGUILayout.HelpBox(
                    $"Position stepping enabled (PositionTau = {_profile.LiveAnimation.PositionTau:F3} m). " +
                    "localPosition curves will be baked alongside localRotation curves.",
                    MessageType.None);
            }

            EditorGUILayout.Space(8);

            string error = GetValidationError();
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Warning);
                GUI.enabled = false;
            }

            if (_isBaking) GUI.enabled = false;
            if (GUILayout.Button(_isBaking ? "Baking…" : "Bake", GUILayout.Height(32)))
                DoBake();

            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_lastStatus))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastStatus, _lastSuccess ? MessageType.Info : MessageType.Error);
            }
        }

        // ------------------------------------------------------------------ validation

        private string GetValidationError()
        {
            if (_sourceClip     == null) return "Assign a Source Clip.";
            if (_skeletonObject == null) return "Assign a Skeleton Object (must be a scene instance, not a project asset).";
            if (_profile        == null) return "Assign a Profile.";
            if (EditorUtility.IsPersistent(_skeletonObject))
                return "Skeleton Object must be a scene instance. Drag it from the Hierarchy, not the Project window.";
            if (_skeletonObject.GetComponentInChildren<Animator>(true) == null)
                return "Skeleton Object has no Animator component.";
            return null;
        }

        // ------------------------------------------------------------------ entry point

        private void DoBake()
        {
            _isBaking   = true;
            _lastStatus = null;
            Repaint();

            try
            {
                string outputPath = RunBake();
                _lastSuccess = true;
                _lastStatus  = $"Saved to {outputPath}";
                Debug.Log($"[OnTwos] Baked clip saved to {outputPath}");
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath));
            }
            catch (System.Exception ex)
            {
                _lastSuccess = false;
                _lastStatus  = $"Bake failed: {ex.Message}";
                Debug.LogError($"[OnTwos] Bake failed:\n{ex}");
            }
            finally
            {
                _isBaking = false;
                if (AnimationMode.InAnimationMode())
                    AnimationMode.StopAnimationMode();
                Repaint();
            }
        }

        // ------------------------------------------------------------------ bake pipeline

        private string RunBake()
        {
            // ---- Step 1: Discover bones that will be stepped ----
            string[] keywords = _profile.LiveAnimation.ExcludeKeywords ?? System.Array.Empty<string>();
            var bones = new List<Transform>();
            foreach (Transform t in _skeletonObject.GetComponentsInChildren<Transform>(true))
            {
                if (t == _skeletonObject.transform) continue;
                if (!BoneFilter.IsExcluded(t, null, keywords))
                    bones.Add(t);
            }

            if (bones.Count == 0)
                throw new System.InvalidOperationException(
                    "No bones found to step. If ExcludeKeywords is filtering everything, clear it.");

            // ---- Step 2: Sample the source clip at every frame ----
            float frameRate   = _sourceClip.frameRate > 0f ? _sourceClip.frameRate : 60f;
            int   totalFrames = Mathf.CeilToInt(_sourceClip.length * frameRate);
            float frameDt     = 1f / frameRate;
            float clipLength  = _sourceClip.length > 0f ? _sourceClip.length : 1f;

            // bone → list of (sampleTime, localRotation, localPosition).
            // Position is captured even when position stepping is disabled — it costs nothing
            // and lets the same sample loop serve both branches without duplication.
            var rawSamples = new Dictionary<Transform, List<(float t, Quaternion rot, Vector3 pos)>>(bones.Count);
            foreach (var b in bones)
                rawSamples[b] = new List<(float, Quaternion, Vector3)>(totalFrames + 1);

            AnimationMode.StartAnimationMode();
            AnimationMode.BeginSampling();
            try
            {
                for (int f = 0; f <= totalFrames; f++)
                {
                    float sampleTime = Mathf.Min(f * frameDt, _sourceClip.length);
                    AnimationMode.SampleAnimationClip(_skeletonObject, _sourceClip, sampleTime);

                    foreach (var bone in bones)
                        if (bone != null)
                            rawSamples[bone].Add((sampleTime, bone.localRotation, bone.localPosition));
                }
            }
            finally
            {
                AnimationMode.EndSampling();
                AnimationMode.StopAnimationMode();
            }

            // ---- Step 3: Run each bone through the PCHIP stepping pipeline ----
            float baseTau     = _profile.LiveAnimation.AnimTau;
            int   gaussPoints = Mathf.Clamp(_profile.LiveAnimation.GaussPoints, 1, 4);
            int   bufferSize  = Mathf.Max(_profile.LiveAnimation.BufferSize, 4);
            float positionTau = Mathf.Max(0f, _profile.LiveAnimation.PositionTau);
            bool  bakePosition = positionTau > 0f;

            var outputClip = new AnimationClip { frameRate = frameRate };

            foreach (var bone in bones)
            {
                var boneSamples = rawSamples[bone];
                if (boneSamples.Count == 0) continue;

                var scheduler = new HoldFrameScheduler(baseTau, gaussPoints, bufferSize);
                scheduler.Reset(boneSamples[0].rot);

                // Collect the scheduler's held output for every input sample, evaluating
                // the tau-over-time curve at each step to let the artist sculpt crunchiness.
                var heldRotFrames = new List<(float t, Quaternion rot)>(boneSamples.Count);
                var heldPosFrames = bakePosition
                    ? new List<(float t, Vector3 pos)>(boneSamples.Count)
                    : null;

                // Position-stepping bookkeeping: hold the position until it has drifted
                // more than positionTau metres from the last held value OR the rotation
                // scheduler emits a snap — same coupling RagdollStepper uses.
                Vector3 heldPos = boneSamples[0].pos;

                foreach (var (st, sr, sp) in boneSamples)
                {
                    // Push the tau multiplier for this frame. Normalise time to 0..1
                    // across the clip so the same curve scales to any clip length.
                    float tNorm = Mathf.Clamp01(st / clipLength);
                    float tauMul = Mathf.Max(0f, _tauOverTime.Evaluate(tNorm));
                    scheduler.Tau = baseTau * tauMul;

                    Quaternion prevHeld = heldRotFrames.Count > 0 ? heldRotFrames[heldRotFrames.Count - 1].rot : sr;
                    Quaternion newHeld  = scheduler.Update(st, sr);
                    heldRotFrames.Add((st, newHeld));

                    if (bakePosition)
                    {
                        bool rotSnapped = Quaternion.Angle(prevHeld, newHeld) > 0.01f;
                        if (rotSnapped || Vector3.Distance(heldPos, sp) >= positionTau)
                            heldPos = sp;
                        heldPosFrames.Add((st, heldPos));
                    }
                }

                string bindPath = AnimationUtility.CalculateTransformPath(bone, _skeletonObject.transform);
                WriteSteppedRotation(outputClip, bindPath, heldRotFrames);
                if (bakePosition)
                    WriteSteppedPosition(outputClip, bindPath, heldPosFrames);
            }

            // ---- Step 4: Save ----
            // Ensure the output folder exists as a project asset path.
            if (!AssetDatabase.IsValidFolder(_outputFolder))
            {
                // Create folder hierarchy if needed.
                string[] parts = _outputFolder.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            string assetPath = $"{_outputFolder}/{_sourceClip.name}_stepped.anim";
            AssetDatabase.CreateAsset(outputClip, assetPath);
            AssetDatabase.SaveAssets();
            return assetPath;
        }

        // ------------------------------------------------------------------ curve helpers

        /// <summary>
        /// Writes constant-interpolation (step) quaternion curves for one bone.
        /// Only writes a key when the held rotation changes, keeping the asset lean.
        /// </summary>
        private static void WriteSteppedRotation(AnimationClip clip, string bindPath,
            List<(float t, Quaternion rot)> frames)
        {
            var cx = new AnimationCurve();
            var cy = new AnimationCurve();
            var cz = new AnimationCurve();
            var cw = new AnimationCurve();

            Quaternion lastWritten = default;
            bool firstKey = true;

            for (int i = 0; i < frames.Count; i++)
            {
                var (t, rot) = frames[i];
                bool isLast  = i == frames.Count - 1;
                bool changed = firstKey || Quaternion.Angle(lastWritten, rot) > 0.001f;

                if (changed || isLast)
                {
                    cx.AddKey(t, rot.x);
                    cy.AddKey(t, rot.y);
                    cz.AddKey(t, rot.z);
                    cw.AddKey(t, rot.w);
                    lastWritten = rot;
                    firstKey = false;
                }
            }

            MakeConstant(cx);
            MakeConstant(cy);
            MakeConstant(cz);
            MakeConstant(cw);

            // Unity reads quaternion components directly from these property names.
            clip.SetCurve(bindPath, typeof(Transform), "localRotation.x", cx);
            clip.SetCurve(bindPath, typeof(Transform), "localRotation.y", cy);
            clip.SetCurve(bindPath, typeof(Transform), "localRotation.z", cz);
            clip.SetCurve(bindPath, typeof(Transform), "localRotation.w", cw);
        }

        /// <summary>
        /// Writes constant-interpolation (step) localPosition curves for one bone.
        /// Same key-budget approach as WriteSteppedRotation: only emit a key when the
        /// held position changes by more than a tiny epsilon, plus the final frame.
        /// </summary>
        private static void WriteSteppedPosition(AnimationClip clip, string bindPath,
            List<(float t, Vector3 pos)> frames)
        {
            var cx = new AnimationCurve();
            var cy = new AnimationCurve();
            var cz = new AnimationCurve();

            Vector3 lastWritten = default;
            bool firstKey = true;

            for (int i = 0; i < frames.Count; i++)
            {
                var (t, pos) = frames[i];
                bool isLast  = i == frames.Count - 1;
                bool changed = firstKey || (pos - lastWritten).sqrMagnitude > 1e-8f;

                if (changed || isLast)
                {
                    cx.AddKey(t, pos.x);
                    cy.AddKey(t, pos.y);
                    cz.AddKey(t, pos.z);
                    lastWritten = pos;
                    firstKey = false;
                }
            }

            MakeConstant(cx);
            MakeConstant(cy);
            MakeConstant(cz);

            clip.SetCurve(bindPath, typeof(Transform), "localPosition.x", cx);
            clip.SetCurve(bindPath, typeof(Transform), "localPosition.y", cy);
            clip.SetCurve(bindPath, typeof(Transform), "localPosition.z", cz);
        }

        /// <summary>
        /// Sets every key in the curve to constant (step) interpolation so values hold
        /// until the next key without any smoothing between them.
        /// </summary>
        private static void MakeConstant(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            }
        }
    }
}