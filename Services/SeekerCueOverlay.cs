using UnityEngine;
using System.Collections.Generic;

namespace AIM9XMod.Services
{
    internal sealed class SeekerCueOverlay : MonoBehaviour
    {
        private bool _visible;
        private bool _searchMode;
        private bool _targetSlaved;
        private bool _viewCenterInCone = true;
        private Unit _target;
        private readonly List<Unit> _assignedTargets = new List<Unit>(8);
        private float _strength;
        private float _detectionRate;
        private GUIStyle _style;

        public void SetCueVisible(bool visible, Unit target, float strength, float detectionRate, bool searchMode, bool targetSlaved, bool viewCenterInCone, List<Unit> assignedTargets)
        {
            _visible = visible;
            _target = target;
            _strength = strength;
            _detectionRate = Mathf.Clamp01(detectionRate);
            _searchMode = searchMode;
            _targetSlaved = targetSlaved;
            _viewCenterInCone = viewCenterInCone;

            _assignedTargets.Clear();
            if (assignedTargets != null)
            {
                for (int i = 0; i < assignedTargets.Count; i++)
                {
                    Unit u = assignedTargets[i];
                    if (u != null)
                        _assignedTargets.Add(u);
                }
            }
        }

        private void OnGUI()
        {
            if (!_visible)
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.alignment = TextAnchor.MiddleCenter;
                _style.fontSize = 14;
                _style.normal.textColor = new Color(0.1f, 1f, 0.1f, 0.92f);
            }

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float lockRadius = Mathf.Lerp(42f, 74f, Mathf.Clamp01(_strength));

            if (!_targetSlaved && _viewCenterInCone)
            {
                float searchRadius = 116f;
                float expandedLockRadius = lockRadius * 2f;
                DrawRing(cx, cy, searchRadius, new Color(0.08f, 1f, 0.08f, 0.42f));
                DrawRing(cx, cy, expandedLockRadius, new Color(0.08f, 1f, 0.08f, 0.62f));
            }

            bool drewAssignedTargets = false;
            if (_assignedTargets.Count > 0)
            {
                drewAssignedTargets = true;
                int maxDraw = Mathf.Min(_assignedTargets.Count, 12);
                for (int i = 0; i < maxDraw; i++)
                {
                    Unit assignmentTarget = _assignedTargets[i];
                    if (assignmentTarget == null)
                        continue;

                    if (!TryGetTargetScreenPosition(assignmentTarget, out var assignmentPos))
                        continue;

                    float t = maxDraw > 1 ? i / (float)(maxDraw - 1) : 0f;
                    float diamondRadius = Mathf.Lerp(14f, 9f, t);
                    float alpha = Mathf.Lerp(0.96f, 0.7f, t);
                    DrawDiamond(assignmentPos, diamondRadius, new Color(0.08f, 1f, 0.08f, alpha));
                }
            }

            if (!drewAssignedTargets && _target != null && TryGetTargetScreenPosition(_target, out var targetScreenPos))
            {
                float diamondRadius = Mathf.Lerp(8f, 16f, Mathf.Clamp01(_strength));
                DrawDiamond(targetScreenPos, diamondRadius, new Color(0.08f, 1f, 0.08f, 0.95f));
            }

            bool showDebugText = Plugin.ShowDetectionPercentDebug != null && Plugin.ShowDetectionPercentDebug.Value;
            if (showDebugText)
            {
                string label = _target != null ? _target.unitName : "SEARCH";
                float detectionPct = Mathf.Round(_detectionRate * 100f);
                string debugSuffix = "  [" + detectionPct.ToString("F0") + "%]";
                GUI.Label(new Rect(cx - 180f, cy + lockRadius + 8f, 360f, 24f), "SEEKER: " + label + debugSuffix, _style);
            }
        }

        private static bool TryGetTargetScreenPosition(Unit target, out Vector2 position)
        {
            position = default;
            if (target == null)
                return false;

            var cam = Camera.main;
            if (cam == null)
                return false;

            Vector3 world = target.transform.position;
            Vector3 screen = cam.WorldToScreenPoint(world);
            if (screen.z <= 0f)
                return false;

            float x = screen.x;
            float y = Screen.height - screen.y;

            if (x < 0f || x > Screen.width || y < 0f || y > Screen.height)
                return false;

            position = new Vector2(x, y);
            return true;
        }

        private static void DrawRing(float cx, float cy, float radius, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;

            const int segments = 56;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments;
                float a1 = (i + 1) * Mathf.PI * 2f / segments;

                float x0 = cx + Mathf.Cos(a0) * radius;
                float y0 = cy + Mathf.Sin(a0) * radius;
                float x1 = cx + Mathf.Cos(a1) * radius;
                float y1 = cy + Mathf.Sin(a1) * radius;

                DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), 2f);
            }

            DrawLine(new Vector2(cx - 10f, cy), new Vector2(cx + 10f, cy), 2f);
            DrawLine(new Vector2(cx, cy - 10f), new Vector2(cx, cy + 10f), 2f);

            GUI.color = previous;
        }

        private static void DrawDiamond(Vector2 center, float radius, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;

            var top = new Vector2(center.x, center.y - radius);
            var right = new Vector2(center.x + radius, center.y);
            var bottom = new Vector2(center.x, center.y + radius);
            var left = new Vector2(center.x - radius, center.y);

            DrawLine(top, right, 2f);
            DrawLine(right, bottom, 2f);
            DrawLine(bottom, left, 2f);
            DrawLine(left, top, 2f);

            GUI.color = previous;
        }

        private static Texture2D _lineTexture;

        private static void DrawLine(Vector2 p0, Vector2 p1, float thickness)
        {
            if (_lineTexture == null)
            {
                _lineTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _lineTexture.SetPixel(0, 0, Color.white);
                _lineTexture.Apply();
            }

            Vector2 d = p1 - p0;
            float angle = Mathf.Rad2Deg * Mathf.Atan2(d.y, d.x);
            float length = d.magnitude;

            Matrix4x4 matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, p0);
            GUI.DrawTexture(new Rect(p0.x, p0.y, length, thickness), _lineTexture);
            GUI.matrix = matrix;
        }
    }
}
