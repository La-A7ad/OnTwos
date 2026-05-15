using System.Collections.Generic;
using System.Reflection;
using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor.Windows
{
    /// <summary>
    /// EditorWindow that lists every <see cref="OnTwosAuthoring"/> in the active scene
    /// and surfaces basic runtime telemetry (settled flag, frames since snap, bone counts).
    /// Mostly useful for debugging during Play Mode; in Edit Mode it just lists the assets.
    /// </summary>
    public sealed class CrunchyRagdollPreviewWindow : EditorWindow
    {
        private Vector2 _scroll;
        private bool _autoRepaint = true;
        private double _lastRepaint;
        private const double RepaintInterval = 0.25;

        [MenuItem("Window/CrunchyRagdoll/Preview")]
        public static void ShowWindow()
        {
            var win = GetWindow<CrunchyRagdollPreviewWindow>("CrunchyRagdoll");
            win.minSize = new Vector2(320, 220);
            win.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (!_autoRepaint) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRepaint > RepaintInterval)
            {
                _lastRepaint = now;
                Repaint();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("CrunchyRagdoll Preview", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _autoRepaint = EditorGUILayout.ToggleLeft("Auto-repaint", _autoRepaint, GUILayout.Width(140));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", GUILayout.Width(80)))
                    Repaint();
            }

            EditorGUILayout.Space();

            var authoringInstances = FindAuthoringInstances();
            if (authoringInstances.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No OnTwosAuthoring components found in the active scene.\n" +
                    "Add OnTwosAuthoring to a character GameObject and assign a Profile.",
                    MessageType.Info);
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Edit Mode — showing wired components. Enter Play Mode for live telemetry.",
                    MessageType.None);
                EditorGUILayout.Space(4);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var a in authoringInstances)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.ObjectField(a.gameObject.name, a, typeof(OnTwosAuthoring), true);
                        EditorGUI.indentLevel++;
                        var profile = a.Profile;
                        EditorGUILayout.LabelField("Profile",      profile          ? profile.name          : "<none>");
                        EditorGUILayout.LabelField("Animator",     a.AnimatorRoot   ? a.AnimatorRoot.name   : "<none>");
                        EditorGUILayout.LabelField("BoneRoot",     a.BoneRoot       ? a.BoneRoot.name       : "<none>");
                        EditorGUILayout.LabelField("PhysicsRoot",  a.PhysicsRoot    ? a.PhysicsRoot.name    : "<none>");
                        string issue = a.Validate();
                        if (issue != null)
                            EditorGUILayout.HelpBox(issue, MessageType.Warning);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.Space();
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var a in authoringInstances)
            {
                DrawAuthoringBlock(a);
                EditorGUILayout.Space();
            }
            EditorGUILayout.EndScrollView();
        }

        private static List<OnTwosAuthoring> FindAuthoringInstances()
        {
#if UNITY_2023_1_OR_NEWER
            var all = Object.FindObjectsByType<OnTwosAuthoring>(FindObjectsSortMode.None);
#else
            var all = Object.FindObjectsOfType<OnTwosAuthoring>();
#endif
            var list = new List<OnTwosAuthoring>(all.Length);
            list.AddRange(all);
            return list;
        }

        private void DrawAuthoringBlock(OnTwosAuthoring a)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField(a.gameObject.name, a, typeof(OnTwosAuthoring), true);
                    GUILayout.FlexibleSpace();
                    GUI.enabled = EditorApplication.isPlaying && !a.IsRagdollActive;
                    if (GUILayout.Button("Activate", GUILayout.Width(70)))
                        a.ActivateRagdoll();
                    GUI.enabled = EditorApplication.isPlaying && a.IsRagdollActive;
                    if (GUILayout.Button("Deactivate", GUILayout.Width(80)))
                        a.Deactivate();
                    GUI.enabled = true;
                }

                EditorGUI.indentLevel++;

                var profile = a.Profile;
                EditorGUILayout.LabelField("Profile", profile ? profile.name : "<none>");
                EditorGUILayout.LabelField("Animator", a.AnimatorRoot ? a.AnimatorRoot.name : "<none>");
                EditorGUILayout.LabelField("BoneRoot", a.BoneRoot ? a.BoneRoot.name : "<none>");
                EditorGUILayout.LabelField("PhysicsRoot", a.PhysicsRoot ? a.PhysicsRoot.name : "<none>");

                var animStep = a.GetComponent<AnimationStepper>();
                var ragStep = a.GetComponent<RagdollStepper>();

                if (animStep != null)
                    DrawTelemetry("AnimationStepper", animStep);
                if (ragStep != null)
                    DrawTelemetry("RagdollStepper", ragStep);

                EditorGUI.indentLevel--;
            }
        }

        private static void DrawTelemetry(string header, MonoBehaviour mb)
        {
            EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);

            // Best-effort reflection: surface any int/float/bool private field starting with '_'.
            // This keeps the window decoupled from concrete field names.
            var t = mb.GetType();
            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            int shown = 0;
            foreach (var f in fields)
            {
                if (!f.Name.StartsWith("_")) continue;
                var ft = f.FieldType;
                if (ft != typeof(int) && ft != typeof(float) && ft != typeof(bool)) continue;
                object val;
                try { val = f.GetValue(mb); }
                catch { continue; }
                EditorGUILayout.LabelField(f.Name, val == null ? "<null>" : val.ToString());
                if (++shown >= 10) break;
            }
            if (shown == 0)
                EditorGUILayout.LabelField("<no telemetry exposed>");
        }
    }
}
