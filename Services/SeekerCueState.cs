using System.Collections.Generic;
using UnityEngine;

namespace AIM9XMod.Services
{
    internal struct CueTargetInfo
    {
        public Unit target;
        public float score;
        public float angleDeg;
        public float distance;
        public float heat;
        public float timestamp;
    }

    internal static class SeekerCueState
    {
        private static readonly Dictionary<int, CueTargetInfo> LoalCueByMissileId = new Dictionary<int, CueTargetInfo>();

        private static CueTargetInfo _prelaunchCue;
        private static bool _hasPrelaunchCue;
        private static Vector3 _viewDirectionWorld = Vector3.forward;
        private static float _viewDirectionTimestamp;

        public static Vector3 ViewDirectionWorld => _viewDirectionWorld;

        public static void SetViewDirection(Vector3 worldDirection)
        {
            if (worldDirection.sqrMagnitude < 0.0001f)
                return;

            _viewDirectionWorld = worldDirection.normalized;
            _viewDirectionTimestamp = Time.unscaledTime;
        }

        public static bool TryGetViewDirection(out Vector3 worldDirection, float maxAgeSeconds = 0.5f)
        {
            worldDirection = _viewDirectionWorld;
            if (Time.unscaledTime - _viewDirectionTimestamp > maxAgeSeconds)
                return false;

            return true;
        }

        public static void SetPrelaunchCue(Unit target, float score, float angleDeg, float distance, float heat)
        {
            if (target == null)
            {
                ClearPrelaunchCue();
                return;
            }

            _prelaunchCue = new CueTargetInfo
            {
                target = target,
                score = score,
                angleDeg = angleDeg,
                distance = distance,
                heat = heat,
                timestamp = Time.unscaledTime
            };
            _hasPrelaunchCue = true;
        }

        public static void ClearPrelaunchCue()
        {
            _hasPrelaunchCue = false;
            _prelaunchCue = default;
        }

        public static bool TryGetPrelaunchCue(out CueTargetInfo cue, float maxAgeSeconds = 0.35f)
        {
            if (_hasPrelaunchCue && Time.unscaledTime - _prelaunchCue.timestamp <= maxAgeSeconds)
            {
                cue = _prelaunchCue;
                return true;
            }

            cue = default;
            return false;
        }

        public static void ReportLoalCandidate(Missile missile, Unit target, float score, float angleDeg, float distance, float heat)
        {
            if (missile == null)
                return;

            int missileId = missile.GetInstanceID();

            if (target == null)
            {
                LoalCueByMissileId.Remove(missileId);
                return;
            }

            LoalCueByMissileId[missileId] = new CueTargetInfo
            {
                target = target,
                score = score,
                angleDeg = angleDeg,
                distance = distance,
                heat = heat,
                timestamp = Time.unscaledTime
            };
        }

        public static void RemoveMissile(Missile missile)
        {
            if (missile == null)
                return;

            LoalCueByMissileId.Remove(missile.GetInstanceID());
        }

        public static bool TryGetBestCue(out CueTargetInfo cue, float maxLoalCueAgeSeconds = 1.0f, float maxPrelaunchCueAgeSeconds = 0.35f)
        {
            PruneLoal(maxLoalCueAgeSeconds);

            if (TryGetNewestLoalCue(out cue))
                return true;

            if (_hasPrelaunchCue && Time.unscaledTime - _prelaunchCue.timestamp <= maxPrelaunchCueAgeSeconds)
            {
                cue = _prelaunchCue;
                return true;
            }

            cue = default;
            return false;
        }

        private static bool TryGetNewestLoalCue(out CueTargetInfo cue)
        {
            cue = default;
            float newest = -1f;

            foreach (var pair in LoalCueByMissileId)
            {
                var candidate = pair.Value;
                if (candidate.target == null)
                    continue;

                if (candidate.timestamp > newest)
                {
                    newest = candidate.timestamp;
                    cue = candidate;
                }
            }

            return newest >= 0f;
        }

        private static void PruneLoal(float maxAgeSeconds)
        {
            if (LoalCueByMissileId.Count == 0)
                return;

            float cutoff = Time.unscaledTime - maxAgeSeconds;
            var staleIds = new List<int>();

            foreach (var pair in LoalCueByMissileId)
            {
                if (pair.Value.target == null || pair.Value.timestamp < cutoff)
                    staleIds.Add(pair.Key);
            }

            for (int i = 0; i < staleIds.Count; i++)
                LoalCueByMissileId.Remove(staleIds[i]);
        }
    }
}
