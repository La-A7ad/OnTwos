using CrunchyRagdoll.Runtime;
using UnityEditor;
using UnityEngine;

namespace CrunchyRagdoll.Editor
{
    [CustomEditor(typeof(RagdollLogger))]
    public sealed class RagdollLoggerEditor : UnityEditor.Editor
    {
        private bool _foldLogging = true;
        private bool _foldOverlay = false;
        private bool _foldExport = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "Diagnostic logger — allocates strings on every interval and " +
                "generates verbose console output. Disable in shipping builds.",
                MessageType.Warning);

            _foldLogging = EditorGUILayout.BeginFoldoutHeaderGroup(_foldLogging, "Logging");
            if (_foldLogging)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("LogInterval"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("MaxLogDuration"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("LogImpacts"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldOverlay = EditorGUILayout.BeginFoldoutHeaderGroup(_foldOverlay, "Overlay");
            if (_foldOverlay)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "No in-game overlay is built in. Pipe Debug.Log output to your own UI " +
                    "if visualization is needed at runtime.",
                    MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _foldExport = EditorGUILayout.BeginFoldoutHeaderGroup(_foldExport, "Export");
            if (_foldExport)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Log lines are written to Unity's standard console / Player.log. " +
                    "Filter by '[CrunchyRagdoll/Logger]' to extract this component's output.",
                    MessageType.None);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
