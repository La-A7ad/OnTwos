using CrunchyRagdoll.Runtime;
using UnityEditor;
using UnityEngine;

namespace CrunchyRagdoll.Editor.Drawers
{
    /// <summary>
    /// Compact single-line drawer for <see cref="CrunchyRagdollProfile.ThresholdRule"/>.
    /// Layout: [name filter] [rot τ] [pos τ]
    /// </summary>
    [CustomPropertyDrawer(typeof(CrunchyRagdollProfile.ThresholdRule))]
    public sealed class ThresholdRuleDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float h = EditorGUIUtility.singleLineHeight;
            float pad = 4f;

            // 50% name, 25% rot, 25% pos
            float nameWidth = position.width * 0.50f - pad;
            float rotWidth = position.width * 0.25f - pad;
            float posWidth = position.width * 0.25f;

            var nameRect = new Rect(position.x, position.y, nameWidth, h);
            var rotRect = new Rect(position.x + nameWidth + pad, position.y, rotWidth, h);
            var posRect = new Rect(position.x + nameWidth + rotWidth + pad * 2, position.y, posWidth, h);

            SerializedProperty nameProp = property.FindPropertyRelative("NameContains");
            SerializedProperty rotProp = property.FindPropertyRelative("RotationTau");
            SerializedProperty posProp = property.FindPropertyRelative("PositionTau");

            nameProp.stringValue = EditorGUI.TextField(nameRect, nameProp.stringValue);
            rotProp.floatValue = EditorGUI.FloatField(rotRect, "rot°", rotProp.floatValue);
            posProp.floatValue = EditorGUI.FloatField(posRect, "pos m", posProp.floatValue);

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }
}
