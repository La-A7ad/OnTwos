using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor.Drawers
{
    [CustomPropertyDrawer(typeof(OnTwosProfile.BoneOverride))]
    public sealed class BoneOverrideDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float h = EditorGUIUtility.singleLineHeight;
            float y = position.y;

            // Single-line compact display: name | exclude | tau-override
            float nameWidth = position.width * 0.45f;
            float boolWidth = position.width * 0.20f;
            float tauWidth = position.width * 0.30f;
            float pad = 4f;

            var nameRect = new Rect(position.x, y, nameWidth, h);
            var boolRect = new Rect(position.x + nameWidth + pad, y, boolWidth, h);
            var tauRect = new Rect(position.x + nameWidth + boolWidth + pad * 2, y, tauWidth - pad, h);

            SerializedProperty nameProp = property.FindPropertyRelative("NameContains");
            SerializedProperty excludeProp = property.FindPropertyRelative("ForceExclude");
            SerializedProperty tauProp = property.FindPropertyRelative("TauOverride");

            nameProp.stringValue = EditorGUI.TextField(nameRect, nameProp.stringValue);

            EditorGUI.LabelField(boolRect, "Excl.");
            var toggleRect = new Rect(boolRect.x + 36, boolRect.y, boolRect.width - 36, h);
            excludeProp.boolValue = EditorGUI.Toggle(toggleRect, excludeProp.boolValue);

            tauProp.floatValue = EditorGUI.FloatField(tauRect, "τ", tauProp.floatValue);

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }
}
