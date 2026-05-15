using OnTwos.Runtime;
using UnityEditor;
using UnityEngine;

namespace OnTwos.Editor.Drawers
{
    /// <summary>
    /// Compact two-column drawer for <see cref="OnTwosProfile.CurveBinding"/>.
    /// Layout: [Target enum dropdown] [AnimationCurve]
    /// </summary>
    [CustomPropertyDrawer(typeof(OnTwosProfile.CurveBinding))]
    public sealed class CurveBindingDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float h = EditorGUIUtility.singleLineHeight;
            float pad = 6f;
            float targetWidth = position.width * 0.40f - pad;
            float curveWidth = position.width * 0.60f;

            var targetRect = new Rect(position.x, position.y, targetWidth, h);
            var curveRect = new Rect(position.x + targetWidth + pad, position.y, curveWidth, h);

            SerializedProperty targetProp = property.FindPropertyRelative("ParameterTarget");
            SerializedProperty curveProp = property.FindPropertyRelative("Curve");

            EditorGUI.PropertyField(targetRect, targetProp, GUIContent.none);
            EditorGUI.PropertyField(curveRect, curveProp, GUIContent.none);

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }
}
