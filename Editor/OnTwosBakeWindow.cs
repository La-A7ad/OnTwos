using UnityEditor;
using UnityEngine;
using OnTwos.Runtime;

namespace OnTwos.Editor.Windows
{
    /// <summary>
    /// EditorWindow for offline-baking an <see cref="AnimationClip"/> through the
    /// PCHIP + DeviationThreshold pipeline, producing a stepped clip.
    ///
    /// This is currently a stub. The runtime path is the priority feature; offline
    /// baking is a future addition. The window exposes the inputs that would be
    /// required and a Bake button that is intentionally disabled.
    /// </summary>
    public sealed class CrunchyRagdollBakeWindow : EditorWindow
    {
        private AnimationClip _sourceClip;
        private OnTwosProfile _profile;
        private string _outputPath = "Assets/BakedClips";

        [MenuItem("Window/CrunchyRagdoll/Bake Clip (preview)")]
        public static void ShowWindow()
        {
            var win = GetWindow<CrunchyRagdollBakeWindow>("CrunchyRagdoll Bake");
            win.minSize = new Vector2(360, 220);
            win.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("CrunchyRagdoll: Bake Animation Clip", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "Offline clip baking is not yet implemented. The runtime crunch path " +
                "(AnimationStepper + RagdollStepper) is fully functional and is the " +
                "intended workflow. This window is a placeholder for a future offline " +
                "bake pipeline that would produce stepped AnimationClip assets at edit time.",
                MessageType.Info);

            EditorGUILayout.Space();

            _sourceClip = (AnimationClip)EditorGUILayout.ObjectField(
                "Source Clip", _sourceClip, typeof(AnimationClip), false);

            _profile = (OnTwosProfile)EditorGUILayout.ObjectField(
                "Profile", _profile, typeof(OnTwosProfile), false);

            _outputPath = EditorGUILayout.TextField("Output Folder", _outputPath);

            EditorGUILayout.Space();

            GUI.enabled = false;
            if (GUILayout.Button("Bake", GUILayout.Height(28)))
            {
                // Reserved for future implementation. The bake would:
                //   1. Sample every curve in _sourceClip at high resolution.
                //   2. Fit per-property PCHIP splines via Pchip.
                //   3. Walk each spline with DeviationThreshold to pick hold frames.
                //   4. Emit a new AnimationClip with the resulting stepped keys.
            }
            GUI.enabled = true;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Status", "Not implemented", EditorStyles.miniLabel);
        }
    }
}
