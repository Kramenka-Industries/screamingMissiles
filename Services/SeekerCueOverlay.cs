using UnityEngine;
using System.Collections.Generic;
using AIM9XMod.Logic;

namespace AIM9XMod.Services
{
    internal sealed class SeekerCueOverlay : MonoBehaviour
    {
        private bool _visible;
        private bool _searchMode;
        private bool _targetSlaved;
        private bool _viewCenterInCone = true;
        private bool _flareInCone;
        private Unit _target;
        private readonly List<Unit> _assignedTargets = new List<Unit>(8);
        private float _strength;
        private float _detectionRate;
        private float _viewedOffBoresightAngleDeg;
        private float _selectedMissileOffBoresightAngleDeg;
        private GUIStyle _style;

        public void SetCueVisible(bool visible, Unit target, float strength, float detectionRate, bool searchMode, bool targetSlaved, bool viewCenterInCone, bool flareInCone, List<Unit> assignedTargets, float viewedOffBoresightAngleDeg, float selectedMissileOffBoresightAngleDeg)
        {
            _visible = visible;
            _target = target;
            _strength = strength;
            _detectionRate = Mathf.Clamp01(detectionRate);
            _searchMode = searchMode;
            _targetSlaved = targetSlaved;
            _viewCenterInCone = viewCenterInCone;
            _flareInCone = flareInCone;
            _viewedOffBoresightAngleDeg = Mathf.Max(0f, viewedOffBoresightAngleDeg);
            _selectedMissileOffBoresightAngleDeg = Mathf.Max(0f, selectedMissileOffBoresightAngleDeg);

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
            float lockRadius = Mathf.Lerp(84f, 148f, Mathf.Clamp01(_strength));

            if (!_targetSlaved && _viewCenterInCone)
            {
                float searchRadius = 220f;
                DrawRing(cx, cy, searchRadius, new Color(0.08f, 1f, 0.08f, 0.42f));
                if (_strength > 0.21f) { // only draw lock radius if we have a lock on candidate
                    DrawRing(cx, cy, lockRadius, new Color(0.08f, 1f, 0.08f, 0.20f));
                }
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
                Vector2 wobbleOffset = ComputeWobbleOffset();
                DrawDiamond(targetScreenPos + wobbleOffset, diamondRadius, new Color(0.08f, 1f, 0.08f, 0.95f));
            }

            bool showDebugText = Plugin.ShowDetectionPercentDebug != null && Plugin.ShowDetectionPercentDebug.Value;
            if (showDebugText)
            {
                string label = _target != null ? _target.unitName : "SEARCH";
                float detectionPct = Mathf.Round(_detectionRate * 100f);
                string debugSuffix = "  [" + detectionPct.ToString("F0") + "%]";
                GUI.Label(new Rect(cx - 180f, cy + lockRadius + 8f, 360f, 24f), "SEEKER: " + label + debugSuffix, _style);
                GUI.Label(new Rect(cx - 180f, cy + lockRadius + 25f, 360f, 24f), "STR: " + _strength.ToString() + debugSuffix, _style);
            }

            if (Plugin.ShowOffBoresightAngleDebug != null && Plugin.ShowOffBoresightAngleDebug.Value)
                DrawOffBoresightDebug(cx, cy, lockRadius);
        }

        private void DrawOffBoresightDebug(float cx, float cy, float lockRadius)
        {
            float viewed = _viewedOffBoresightAngleDeg;
            float selected = _selectedMissileOffBoresightAngleDeg;
            string status = _viewCenterInCone ? "IN-CONE" : "OUT-OF-CONE";

            GUI.Label(new Rect(cx - 220f, cy + lockRadius + 44f, 440f, 24f),
                "OBS VIEW: " + viewed.ToString("F1") + "°  MISSILE: " + selected.ToString("F1") + "°  " + status, _style);

            float gaugeWidth = 320f;
            float gaugeHeight = 10f;
            float gx = cx - gaugeWidth * 0.5f;
            float gy = cy + lockRadius + 68f;

            DrawRect(new Rect(gx, gy, gaugeWidth, gaugeHeight), new Color(0.08f, 1f, 0.08f, 0.18f));

            float normalized = selected > 0.001f ? Mathf.Clamp01(viewed / selected) : 0f;
            Color marker = _viewCenterInCone ? new Color(0.08f, 1f, 0.08f, 0.92f) : new Color(1f, 0.26f, 0.08f, 0.92f);
            DrawRect(new Rect(gx, gy, gaugeWidth * normalized, gaugeHeight), marker);
        }

        private Vector2 ComputeWobbleOffset()
        {
            // Determine effective detection rate for wobble purposes.
            // When flares are in the detection cone the diamond wobbles even at ≥60% detection,
            // treating the situation as if detection was at a moderate sub-60% level (0.3f).
            float effectiveRate = SeekerCueMath.ComputeEffectiveWobbleDetectionRate(_detectionRate, _flareInCone);

            // wobbleFactor is 0 at ≥60% detection and increases toward 1 as detection drops to 0%.
            float wobbleFactor = SeekerCueMath.ComputeWobbleFactor(effectiveRate);
            if (wobbleFactor <= 0f)
                return Vector2.zero;

            float maxOffset = Plugin.WobbleMaxOffset != null ? Plugin.WobbleMaxOffset.Value : 12f;
            float speed     = Plugin.WobbleSpeed     != null ? Plugin.WobbleSpeed.Value     : 1f;

            if (maxOffset <= 0f)
                return Vector2.zero;

            float amplitude = wobbleFactor * maxOffset;
            float t = Time.unscaledTime * speed;

            // Combine several incommensurable sine waves to produce organic, unsteady motion.
            float ox = (Mathf.Sin(t * 1.7f  + 0.50f) * 0.55f
                      + Mathf.Sin(t * 3.13f + 1.20f) * 0.30f
                      + Mathf.Sin(t * 0.71f + 2.10f) * 0.15f) * amplitude;
            float oy = (Mathf.Sin(t * 1.31f + 2.30f) * 0.55f
                      + Mathf.Sin(t * 2.83f + 0.80f) * 0.30f
                      + Mathf.Sin(t * 0.97f + 1.50f) * 0.15f) * amplitude;

            return new Vector2(ox, oy);
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
        private static Texture2D _rectTexture;

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

        private static void DrawRect(Rect rect, Color color)
        {
            if (_rectTexture == null)
            {
                _rectTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _rectTexture.SetPixel(0, 0, Color.white);
                _rectTexture.Apply();
            }

            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _rectTexture);
            GUI.color = previous;
        }
    }
}
