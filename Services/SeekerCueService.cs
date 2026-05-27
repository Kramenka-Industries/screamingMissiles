using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using AIM9XMod.Logic;

namespace AIM9XMod.Services
{
    internal sealed class SeekerCueService : MonoBehaviour
    {
        private readonly List<Unit> _scratchCandidates = new List<Unit>(128);

        private AudioSource _standbySource;
        private AudioSource _lockSource;
        private AudioClip _standbyClip;
        private AudioClip _lockClip;
        private AudioClip _flaredLockClip;
        private AudioClip _wavStandbyClip;
        private AudioClip _wavLockClip;
        private AudioClip _wavFlaredLockClip;
        private SeekerCueOverlay _overlay;

        private float _nextScanTime;
        private float _launchMuteUntil;
        private int _lastInFlightIrCount;
        private float _configuredGrowlVolume = 0.6f;
        private static readonly Dictionary<string, float> ResolverTraceNextByKey = new Dictionary<string, float>(64);
        private static float _nextStateTraceTime;
        private static readonly Dictionary<string, MemberInfo> RawMemberCache = new Dictionary<string, MemberInfo>();
        private static readonly HashSet<string> RawMemberMissCache = new HashSet<string>();
        private static readonly object RawMemberCacheLock = new object();
        private static readonly FieldInfo HudTargetListField = typeof(CombatHUD).GetField("targetList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private void Awake()
        {
            _standbySource = gameObject.AddComponent<AudioSource>();
            _standbySource.playOnAwake = false;
            _standbySource.loop = true;
            _standbySource.spatialBlend = 0f;

            _lockSource = gameObject.AddComponent<AudioSource>();
            _lockSource.playOnAwake = false;
            _lockSource.loop = true;
            _lockSource.spatialBlend = 0f;

            _standbyClip = BuildStandbyClip();
            _lockClip = BuildLockClip();
            _flaredLockClip = BuildFlaredLockClip();
            _standbySource.clip = _standbyClip;
            _lockSource.clip = _lockClip;

            RefreshConfiguredAudioValues();
            if (_standbySource != null)
                _standbySource.volume = _configuredGrowlVolume;
            if (_lockSource != null)
                _lockSource.volume = _configuredGrowlVolume;

            StartCoroutine(LoadWavProfileAsync());

            _overlay = gameObject.AddComponent<SeekerCueOverlay>();
        }

        private void OnDestroy()
        {
            if (_standbySource != null)
            {
                _standbySource.Stop();
                _standbySource.clip = null;
                Destroy(_standbySource);
                _standbySource = null;
            }

            if (_lockSource != null)
            {
                _lockSource.Stop();
                _lockSource.clip = null;
                Destroy(_lockSource);
                _lockSource = null;
            }

            if (_standbyClip != null)
            {
                Destroy(_standbyClip);
                _standbyClip = null;
            }

            if (_lockClip != null)
            {
                Destroy(_lockClip);
                _lockClip = null;
            }

            if (_flaredLockClip != null)
            {
                Destroy(_flaredLockClip);
                _flaredLockClip = null;
            }

            if (_wavStandbyClip != null)
            {
                Destroy(_wavStandbyClip);
                _wavStandbyClip = null;
            }

            if (_wavLockClip != null)
            {
                Destroy(_wavLockClip);
                _wavLockClip = null;
            }

            if (_wavFlaredLockClip != null)
            {
                Destroy(_wavFlaredLockClip);
                _wavFlaredLockClip = null;
            }

            if (_overlay != null)
            {
                Destroy(_overlay);
                _overlay = null;
            }
        }

        private void Update()
        {
            RefreshConfiguredAudioValues();

            if (!Plugin.EnableSeekerGrowl.Value)
            {
                SilenceGrowl();
                SeekerCueState.ClearPrelaunchCue();
                if (_overlay != null)
                    _overlay.SetCueVisible(false, null, 0f, 0f, false, false, true, false, null);
                return;
            }

            var camera = ResolveCamera();
            if (camera != null)
                SeekerCueState.SetViewDirection(camera.transform.forward);

            bool hasWeaponContext = TryGetPlayerIrContext(out var ownAircraft, out var ws, out var viewDir);
            bool targetSlavedMode = false;
            bool hasRadarHardLock = false;
            string selectedTargetName = "none";
            List<Unit> assignmentTargets = null;

            if (hasWeaponContext)
            {
                assignmentTargets = GetLaunchAssignableTargets(ownAircraft, ws);

                Unit selectedTarget;
                hasRadarHardLock = TryGetRadarHardLockState(ownAircraft, ws);
                if (TryGetActiveSelectedTarget(ownAircraft, ws, out selectedTarget))
                {
                    if (IsWithinAircraftForwardCone(ownAircraft, selectedTarget, ws))
                    {
                        targetSlavedMode = true;
                        selectedTargetName = selectedTarget != null ? selectedTarget.unitName : "null";
                        RunSelectedTargetCue(ownAircraft, ws, selectedTarget);
                    }
                    else
                    {
                        targetSlavedMode = false;
                        selectedTargetName = selectedTarget != null ? selectedTarget.unitName + " (outside-cone)" : "null";
                        SeekerCueState.ClearPrelaunchCue();
                    }
                }
                else if (hasRadarHardLock)
                {
                    // Hard lock exists, but without an in-cone resolved target we suppress
                    // prelaunch seeker feedback and keep launch behavior in LOAL mode.
                    targetSlavedMode = false;
                    SeekerCueState.ClearPrelaunchCue();
                    TraceResolver("hardlock-no-unit", "Radar hard lock active, but no in-cone Unit resolved this frame. Prelaunch feedback suppressed.");
                }
                else
                {
                    RunPrelaunchCueScan(ownAircraft, ws, viewDir);
                }
            }
            else
            {
                SeekerCueState.ClearPrelaunchCue();
            }

            TraceRuntimeState(hasWeaponContext, hasRadarHardLock, targetSlavedMode, selectedTargetName);

            UpdateGrowlFromCue(hasWeaponContext, ownAircraft, ws, viewDir, targetSlavedMode, hasRadarHardLock, assignmentTargets);
        }

        private void RunSelectedTargetCue(Aircraft ownAircraft, WeaponStation ws, Unit selectedTarget)
        {
            if (ownAircraft == null || ws == null || selectedTarget == null)
            {
                SeekerCueState.ClearPrelaunchCue();
                return;
            }

            var weaponInfo = ws.WeaponInfo;
            if (weaponInfo == null)
            {
                SeekerCueState.ClearPrelaunchCue();
                return;
            }

            Vector3 origin = ownAircraft.transform.position;
            Vector3 toTarget = selectedTarget.transform.position - origin;
            float distance = toTarget.magnitude;
            float maxRange = weaponInfo.targetRequirements.maxRange;
            float heat = GetTotalIRIntensity(selectedTarget);

            bool inRange = distance >= 50f && distance <= maxRange;
            bool hasLos = !Physics.Linecast(origin, selectedTarget.transform.position, 64);

            // Keep cue pinned to selected target so target-slaved behavior never falls back
            // to pilot-view-based feedback while a target is actively selected.
            if (!inRange || !hasLos)
                heat *= 0.2f;

            // Selected-target slaving treats seeker pointing error as near-zero.
            float score = distance * 0.0012f - heat * 0.25f;
            SeekerCueState.SetPrelaunchCue(selectedTarget, score, 0f, distance, heat);
        }

        private void RunPrelaunchCueScan(Aircraft ownAircraft, WeaponStation ws, Vector3 viewDir)
        {
            if (Time.unscaledTime < _nextScanTime)
                return;

            _nextScanTime = Time.unscaledTime + 0.05f;

            var weaponInfo = ws.WeaponInfo;
            if (weaponInfo == null)
            {
                SeekerCueState.ClearPrelaunchCue();
                return;
            }

            float maxRange = weaponInfo.targetRequirements.maxRange;
            float cueAngle = Mathf.Max(1f, Plugin.PrelaunchCueAngle.Value);
            var ownHq = ownAircraft != null ? ownAircraft.NetworkHQ : null;

            float offBoresightAngle = Plugin.GetOffBoresightAngle(weaponInfo.name);
            offBoresightAngle = Mathf.Max(1f, offBoresightAngle);

            Unit bestUnit = null;
            float bestScore = float.MaxValue;
            float bestAngle = 0f;
            float bestDistance = 0f;
            float bestHeat = 0f;

            _scratchCandidates.Clear();
            _scratchCandidates.AddRange(UnitRegistry.allUnits);

            Vector3 origin = ownAircraft.transform.position;
            for (int i = 0; i < _scratchCandidates.Count; i++)
            {
                var unit = _scratchCandidates[i];
                if (!IsValidTarget(unit, ownAircraft, ownHq))
                    continue;

                Vector3 toTarget = unit.transform.position - origin;
                float dist = toTarget.magnitude;
                if (dist < 50f || dist > maxRange)
                    continue;

                float forwardAngle = Vector3.Angle(ownAircraft.transform.forward, toTarget);
                if (forwardAngle > offBoresightAngle)
                    continue;

                Vector3 dir = toTarget / Mathf.Max(0.001f, dist);
                float angle = Vector3.Angle(viewDir, dir);
                if (angle > cueAngle)
                    continue;

                if (Physics.Linecast(origin, unit.transform.position, 64))
                    continue;

                float heat = GetTotalIRIntensity(unit);
                if (heat <= 0.05f)
                    continue;

                float score = angle + dist * 0.0012f - heat * 0.25f;
                if (score < bestScore)
                {
                    bestUnit = unit;
                    bestScore = score;
                    bestAngle = angle;
                    bestDistance = dist;
                    bestHeat = heat;
                }
            }

            SeekerCueState.SetPrelaunchCue(bestUnit, bestScore, bestAngle, bestDistance, bestHeat);
        }

        private void UpdateGrowlFromCue(bool irMissileSelected, Aircraft ownAircraft, WeaponStation ws, Vector3 viewDir, bool targetSlavedMode, bool hasRadarHardLock, List<Unit> assignmentTargets)
        {
            bool viewCenterInCone = IsViewCenterWithinAircraftForwardCone(ownAircraft, viewDir, ws);

            // When radar lock is active but the target has left the offboresight angle area,
            // hide the manual view lock circles so they don't mislead the pilot.
            if (hasRadarHardLock && !targetSlavedMode)
                viewCenterInCone = false;

            if (!irMissileSelected)
            {
                SilenceGrowl();
                if (_overlay != null)
                    _overlay.SetCueVisible(false, null, 0f, 0f, false, false, viewCenterInCone, false, null);
                _lastInFlightIrCount = 0;
                return;
            }

            int inFlightCount = CountActiveLaunchedIrMissiles(ownAircraft);
            if (inFlightCount > _lastInFlightIrCount)
                _launchMuteUntil = Time.unscaledTime + Mathf.Max(0f, Plugin.LaunchMuteSeconds.Value);

            _lastInFlightIrCount = inFlightCount;

            CueTargetInfo cue;
            bool hasCue = targetSlavedMode
                ? SeekerCueState.TryGetPrelaunchCue(out cue)
                : SeekerCueState.TryGetBestCue(out cue);
            float cueStrength = hasCue ? ComputeCueStrength(cue) : 0f;

            float detectionRate = 0f;
            if (hasCue)
            {
                if (targetSlavedMode)
                {
                    float maxRangeForDetection = 12000f;
                    if (ws != null && ws.WeaponInfo != null)
                        maxRangeForDetection = ws.WeaponInfo.targetRequirements.maxRange;
                    detectionRate = SeekerCueMath.ComputeTargetSlavedDetectionRate(cue.heat, cue.distance, maxRangeForDetection);
                }
                else
                {
                    float cueCone = Plugin.PrelaunchCueAngle.Value;
                    detectionRate = SeekerCueMath.ComputeSearchDetectionRate(cue.angleDeg, cue.heat, cueCone);
                }
            }
            bool highDetection = detectionRate >= 0.6f;
            bool searchMode = !highDetection && !targetSlavedMode;

            // Compute flare-in-cone early so it can be forwarded to the overlay even during
            // the launch-mute window, giving the wobble its correct flare-awareness state.
            float maxRange = 12000f;
            float coneAngle = Mathf.Max(1f, Plugin.PrelaunchCueAngle.Value);
            if (ws != null && ws.WeaponInfo != null)
                maxRange = ws.WeaponInfo.targetRequirements.maxRange;

            Vector3 origin = ownAircraft != null ? ownAircraft.transform.position : Vector3.zero;
            Vector3 detectionDir = viewDir;
            if (targetSlavedMode && hasCue && cue.target != null)
            {
                Vector3 toCueTarget = cue.target.transform.position - origin;
                if (toCueTarget.sqrMagnitude > 1f)
                    detectionDir = toCueTarget.normalized;
            }

            bool flareInCone = ownAircraft != null && HasFlareInDetectionCone(origin, detectionDir, maxRange, coneAngle);

            bool launchMuted = Time.unscaledTime < _launchMuteUntil;
            if (launchMuted)
            {
                SilenceGrowl();
                if (_overlay != null)
                    _overlay.SetCueVisible(true, hasCue ? cue.target : null, Mathf.Max(0.2f, cueStrength), detectionRate, searchMode, targetSlavedMode, viewCenterInCone, flareInCone, assignmentTargets);
                return;
            }

            AudioClip desiredLockClip = flareInCone
                ? (_wavFlaredLockClip != null ? _wavFlaredLockClip : _flaredLockClip)
                : (_wavLockClip != null ? _wavLockClip : _lockClip);

            AudioClip desiredCagedClip = _wavStandbyClip != null ? _wavStandbyClip : _standbyClip;

            float masterVolume = _configuredGrowlVolume;

            // Thresholded blend: <=30% pure caged, >=60% pure uncaged.
            float uncagedMix = SeekerCueMath.ComputeUncagedMix(detectionRate);
            float cagedMix = 1f - uncagedMix;

            if (_standbySource != null)
            {
                if (_standbySource.clip != desiredCagedClip)
                    _standbySource.clip = desiredCagedClip;

                _standbySource.pitch = 1f;
                _standbySource.volume = masterVolume * cagedMix;

                if (_standbySource.clip != null)
                {
                    if (!_standbySource.isPlaying)
                        _standbySource.Play();
                }
                else if (_standbySource.isPlaying)
                {
                    _standbySource.Stop();
                }
            }

            if (_lockSource != null)
            {
                if (_lockSource.clip != desiredLockClip)
                    _lockSource.clip = desiredLockClip;

                _lockSource.pitch = 1f;
                _lockSource.volume = masterVolume * uncagedMix;

                if (_lockSource.clip != null)
                {
                    if (!_lockSource.isPlaying)
                        _lockSource.Play();
                }
                else if (_lockSource.isPlaying)
                {
                    _lockSource.Stop();
                }
            }

            if (_overlay != null)
                _overlay.SetCueVisible(true, hasCue ? cue.target : null, Mathf.Max(0.2f, cueStrength), detectionRate, searchMode, targetSlavedMode, viewCenterInCone, flareInCone, assignmentTargets);
        }

        private static bool IsViewCenterWithinAircraftForwardCone(Aircraft ownAircraft, Vector3 viewDir, WeaponStation ws)
        {
            if (ownAircraft == null)
                return false;

            if (viewDir.sqrMagnitude <= 0.0001f)
                return false;

            if (ws == null || ws.WeaponInfo == null)
                return false;

            float offBoresightAngle = Plugin.GetOffBoresightAngle(ws.WeaponInfo.name);
            offBoresightAngle = Mathf.Max(1f, offBoresightAngle);

            return Vector3.Angle(ownAircraft.transform.forward, viewDir.normalized) <= offBoresightAngle;
        }

        private void SilenceGrowl()
        {
            if (_standbySource != null)
            {
                _standbySource.volume = 0f;
                if (_standbySource.isPlaying)
                    _standbySource.Stop();
            }

            if (_lockSource != null)
            {
                _lockSource.volume = 0f;
                if (_lockSource.isPlaying)
                    _lockSource.Stop();
            }
        }

        private void RefreshConfiguredAudioValues()
        {
            if (Plugin.GrowlVolume != null)
                _configuredGrowlVolume = Mathf.Clamp01(Plugin.GrowlVolume.Value);
            else
                _configuredGrowlVolume = 0.6f;
        }

        private static List<Unit> GetLaunchAssignableTargets(Aircraft ownAircraft, WeaponStation ws)
        {
            if (ownAircraft == null || ws == null || ws.Ammo <= 0)
                return null;

            List<Unit> hudTargets;
            if (!TryGetHudTargetList(out hudTargets) || hudTargets == null || hudTargets.Count == 0)
                return null;

            int maxAssignable = Mathf.Max(0, ws.Ammo);
            if (maxAssignable <= 0)
                return null;

            float offBoresightAngle = Plugin.GetOffBoresightAngle(ws.WeaponInfo.name);
            offBoresightAngle = Mathf.Max(1f, offBoresightAngle);
            Vector3 ownPosition = ownAircraft.transform.position;
            Vector3 ownForward = ownAircraft.transform.forward;

            FactionHQ ownHq = ownAircraft.NetworkHQ;
            var selected = new List<Unit>(Mathf.Min(maxAssignable, hudTargets.Count));
            var seen = new HashSet<PersistentID>();

            // Target list behaves as LIFO; iterate from end for launch assignment order.
            for (int i = hudTargets.Count - 1; i >= 0 && selected.Count < maxAssignable; i--)
            {
                Unit candidate = hudTargets[i];
                if (!IsValidTarget(candidate, ownAircraft, ownHq))
                    continue;

                Vector3 toCandidate = candidate.transform.position - ownPosition;
                if (Vector3.Angle(ownForward, toCandidate) > offBoresightAngle)
                    continue;

                if (!seen.Add(candidate.persistentID))
                    continue;

                selected.Add(candidate);
            }

            return selected.Count > 0 ? selected : null;
        }

        private static bool TryGetPlayerIrContext(out Aircraft ownAircraft, out WeaponStation ws, out Vector3 viewDirection)
        {
            ownAircraft = null;
            ws = null;
            viewDirection = Vector3.forward;

            if (!GameManager.GetLocalAircraft(out ownAircraft) || ownAircraft == null)
                return false;

            if (!IsPlayerAircraftAvailableForCueing(ownAircraft))
                return false;

            var hud = SceneSingleton<CombatHUD>.i;
            if (hud == null)
                return false;

            ws = hud.GetWeaponStation();
            if (ws == null || ws.Ammo <= 0 || ws.WeaponInfo == null || ws.WeaponInfo.weaponPrefab == null)
                return false;

            if (ws.WeaponInfo.weaponPrefab.GetComponentInChildren<IRSeeker>(true) == null)
                return false;

            var cam = ResolveCameraStatic();
            if (cam == null)
                return false;

            viewDirection = cam.transform.forward;
            return true;
        }

        private static bool IsPlayerAircraftAvailableForCueing(Aircraft ownAircraft)
        {
            if (ownAircraft == null || ownAircraft.disabled)
                return false;

            string[] hiddenWhenTrueFlags =
            {
                "isDead",
                "IsDead",
                "dead",
                "Dead",
                "pilotDead",
                "PilotDead",
                "isEjecting",
                "IsEjecting",
                "ejecting",
                "Ejecting",
                "pilotEjected",
                "PilotEjected",
                "hasEjected",
                "HasEjected",
                "isBailingOut",
                "IsBailingOut",
                "bailingOut",
                "BailingOut"
            };

            for (int i = 0; i < hiddenWhenTrueFlags.Length; i++)
            {
                bool flagValue;
                if (TryGetBoolFromMember(ownAircraft, hiddenWhenTrueFlags[i], out flagValue) && flagValue)
                    return false;
            }

            string[] requiredTrueFlags =
            {
                "isPlayerControlled",
                "IsPlayerControlled",
                "playerControlled",
                "PlayerControlled",
                "hasPlayerControl",
                "HasPlayerControl",
                "isLocalPlayerControlled",
                "IsLocalPlayerControlled",
                "hasPilot",
                "HasPilot",
                "pilotPresent",
                "PilotPresent"
            };

            for (int i = 0; i < requiredTrueFlags.Length; i++)
            {
                bool flagValue;
                if (TryGetBoolFromMember(ownAircraft, requiredTrueFlags[i], out flagValue) && !flagValue)
                    return false;
            }

            return true;
        }

        private static Camera ResolveCamera()
        {
            return ResolveCameraStatic();
        }

        private static Camera ResolveCameraStatic()
        {
            try
            {
                var camState = SceneSingleton<CameraStateManager>.i;
                if (camState != null)
                    return camState.GetComponentInChildren<Camera>();
            }
            catch (Exception)
            {
            }

            return Camera.main;
        }

        private static bool IsValidTarget(Unit unit, Aircraft ownAircraft, FactionHQ ownHq)
        {
            if (unit == null || unit.disabled)
                return false;

            if (ownAircraft != null && unit.persistentID == ownAircraft.persistentID)
                return false;

            if (ownHq != null && unit.NetworkHQ == ownHq)
                return false;

            return unit.HasIRSignature();
        }

        private static bool IsWithinAircraftForwardCone(Aircraft ownAircraft, Unit target, WeaponStation ws)
        {
            if (ownAircraft == null || target == null)
                return false;

            Vector3 toTarget = target.transform.position - ownAircraft.transform.position;
            if (toTarget.sqrMagnitude <= 1f)
                return true;

            if (ws == null || ws.WeaponInfo == null)
                return false;

            float offBoresightAngle = Plugin.GetOffBoresightAngle(ws.WeaponInfo.name);
            offBoresightAngle = Mathf.Max(1f, offBoresightAngle);
            return Vector3.Angle(ownAircraft.transform.forward, toTarget) <= offBoresightAngle;
        }

        private static bool TryGetHudTargetList(out List<Unit> targets)
        {
            targets = null;

            var hud = SceneSingleton<CombatHUD>.i;
            if (hud == null || HudTargetListField == null)
                return false;

            try
            {
                var raw = HudTargetListField.GetValue(hud) as List<Unit>;
                if (raw == null || raw.Count == 0)
                    return false;

                targets = raw;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryResolveUnit(object candidate, out Unit unit, int depth = 0)
        {
            unit = null;
            if (candidate == null || depth > 2)
                return false;

            // Fast-fail common framework/UI objects that cannot represent gameplay targets.
            Type candidateType = candidate.GetType();
            string ns = candidateType.Namespace ?? string.Empty;
            if (ns.StartsWith("System", StringComparison.Ordinal)
                || ns.StartsWith("UnityEngine", StringComparison.Ordinal))
                return false;

            if (candidate is Unit directUnit)
            {
                unit = directUnit;
                return true;
            }

            if (candidate is PersistentID pid)
                return UnitRegistry.TryGetUnit(new PersistentID?(pid), out unit);

            if (candidate is Nullable<PersistentID>)
            {
                var npid = (PersistentID?)candidate;
                if (npid.HasValue)
                    return UnitRegistry.TryGetUnit(npid, out unit);
            }

            if (candidate is int intId)
            {
                for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
                {
                    var maybe = UnitRegistry.allUnits[i];
                    if (maybe != null && maybe.GetInstanceID() == intId)
                    {
                        unit = maybe;
                        return true;
                    }
                }
            }

            string[] nestedNames =
            {
                "unit",
                "Unit",
                "target",
                "Target",
                "targetUnit",
                "TargetUnit",
                "lockedTarget",
                "LockedTarget",
                "persistentID",
                "PersistentID",
                "unitId",
                "UnitId"
            };

            for (int i = 0; i < nestedNames.Length; i++)
            {
                object nested;
                if (TryGetRawMemberValue(candidate, nestedNames[i], out nested)
                    && TryResolveUnit(nested, out unit, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetRawMemberValue(object instance, string memberName, out object value)
        {
            value = null;
            if (instance == null || string.IsNullOrWhiteSpace(memberName))
                return false;

            Type type = instance.GetType();
            string key = type.FullName + "::" + memberName;

            MemberInfo cachedMember;
            lock (RawMemberCacheLock)
            {
                if (RawMemberCache.TryGetValue(key, out cachedMember))
                    return TryReadMemberValue(instance, cachedMember, out value);

                if (RawMemberMissCache.Contains(key))
                    return false;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null)
            {
                lock (RawMemberCacheLock)
                    RawMemberCache[key] = field;
                return TryReadMemberValue(instance, field, out value);
            }

            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
            {
                lock (RawMemberCacheLock)
                    RawMemberCache[key] = property;
                return TryReadMemberValue(instance, property, out value);
            }

            MethodInfo method = type.GetMethod(memberName, flags, null, Type.EmptyTypes, null);
            if (method != null && method.ReturnType != typeof(void))
            {
                lock (RawMemberCacheLock)
                    RawMemberCache[key] = method;
                return TryReadMemberValue(instance, method, out value);
            }

            lock (RawMemberCacheLock)
                RawMemberMissCache.Add(key);

            return false;
        }

        private static bool TryReadMemberValue(object instance, MemberInfo member, out object value)
        {
            value = null;
            try
            {
                if (member is FieldInfo field)
                {
                    value = field.GetValue(instance);
                    return value != null;
                }

                if (member is PropertyInfo property)
                {
                    value = property.GetValue(instance, null);
                    return value != null;
                }

                if (member is MethodInfo method)
                {
                    value = method.Invoke(instance, null);
                    return value != null;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static bool TryGetUnitFromMember(object instance, string memberName, out Unit unit)
        {
            unit = null;

            object raw;
            if (!TryGetRawMemberValue(instance, memberName, out raw))
                return false;

            return TryResolveUnit(raw, out unit);
        }

        private static bool TryGetBoolFromMember(object instance, string memberName, out bool value)
        {
            value = false;
            object raw;
            if (!TryGetRawMemberValue(instance, memberName, out raw) || raw == null)
                return false;

            if (raw is bool b)
            {
                value = b;
                return true;
            }

            try
            {
                value = Convert.ToBoolean(raw);
                return true;
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static string GetSourceLabel(object source)
        {
            return source != null ? source.GetType().Name : "null";
        }

        private static void TraceResolver(string key, string message)
        {
            if (Plugin.ShowDetectionPercentDebug == null || !Plugin.ShowDetectionPercentDebug.Value)
                return;

            float now = Time.unscaledTime;
            if (string.IsNullOrEmpty(key))
                key = "default";

            float nextAllowed;
            if (ResolverTraceNextByKey.TryGetValue(key, out nextAllowed) && now < nextAllowed)
                return;

            ResolverTraceNextByKey[key] = now + 0.75f;
            Plugin.Log?.LogInfo("[SeekerDebug] " + message);
        }

        private static void TraceRuntimeState(bool hasWeaponContext, bool hasRadarHardLock, bool targetSlavedMode, string selectedTargetName)
        {
            if (Plugin.ShowDetectionPercentDebug == null || !Plugin.ShowDetectionPercentDebug.Value)
                return;

            float now = Time.unscaledTime;
            if (now < _nextStateTraceTime)
                return;

            _nextStateTraceTime = now + 1.5f;
            string message = "[SeekerState] weaponContext=" + hasWeaponContext
                + " hardLock=" + hasRadarHardLock
                + " slavedMode=" + targetSlavedMode
                + " selectedTarget=" + (string.IsNullOrEmpty(selectedTargetName) ? "none" : selectedTargetName);
            Plugin.Log?.LogInfo(message);
        }

        private static bool IsLikelyTargetMemberName(string memberName)
        {
            if (string.IsNullOrWhiteSpace(memberName))
                return false;

            string name = memberName.ToLowerInvariant();
            return name.Contains("target") || name.Contains("lock") || name.Contains("track") || name.Contains("selected");
        }

        private static bool IsLikelyLockFlagName(string memberName)
        {
            if (string.IsNullOrWhiteSpace(memberName))
                return false;

            string name = memberName.ToLowerInvariant();
            if (!name.Contains("lock"))
                return false;

            // Filter noisy members that are unlikely to represent live lock state.
            if (name.Contains("block") || name.Contains("clock") || name.Contains("unlock"))
                return false;

            return true;
        }

        private static bool TryGetTargetViaReflectionSweep(object source, Aircraft ownAircraft, FactionHQ ownHq, out Unit target)
        {
            target = null;
            if (source == null)
                return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = source.GetType();

            var fields = type.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (!IsLikelyTargetMemberName(field.Name))
                    continue;

                object raw;
                try { raw = field.GetValue(source); }
                catch (Exception) { continue; }

                Unit candidate;
                if (TryResolveUnit(raw, out candidate) && IsValidTarget(candidate, ownAircraft, ownHq))
                {
                    target = candidate;
                    TraceResolver("reflect-field-" + type.Name + "-" + field.Name,
                        "Target resolved via field " + type.Name + "." + field.Name + " -> " + candidate.unitName);
                    return true;
                }
            }

            var properties = type.GetProperties(flags);
            for (int i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0 || !IsLikelyTargetMemberName(prop.Name))
                    continue;

                object raw;
                try { raw = prop.GetValue(source, null); }
                catch (Exception) { continue; }

                Unit candidate;
                if (TryResolveUnit(raw, out candidate) && IsValidTarget(candidate, ownAircraft, ownHq))
                {
                    target = candidate;
                    TraceResolver("reflect-prop-" + type.Name + "-" + prop.Name,
                        "Target resolved via property " + type.Name + "." + prop.Name + " -> " + candidate.unitName);
                    return true;
                }
            }

            var methods = type.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.GetParameters().Length != 0 || !IsLikelyTargetMemberName(method.Name))
                    continue;

                object raw;
                try { raw = method.Invoke(source, null); }
                catch (Exception) { continue; }

                Unit candidate;
                if (TryResolveUnit(raw, out candidate) && IsValidTarget(candidate, ownAircraft, ownHq))
                {
                    target = candidate;
                    TraceResolver("reflect-method-" + type.Name + "-" + method.Name,
                        "Target resolved via method " + type.Name + "." + method.Name + "() -> " + candidate.unitName);
                    return true;
                }
            }

            return false;
        }

        private static bool HasTrueLockFlagViaReflection(object source)
        {
            if (source == null)
                return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = source.GetType();

            var fields = type.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field.FieldType != typeof(bool) || !IsLikelyLockFlagName(field.Name))
                    continue;

                try
                {
                    if ((bool)field.GetValue(source))
                        return true;
                }
                catch (Exception)
                {
                }
            }

            var properties = type.GetProperties(flags);
            for (int i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0 || prop.PropertyType != typeof(bool) || !IsLikelyLockFlagName(prop.Name))
                    continue;

                try
                {
                    if ((bool)prop.GetValue(source, null))
                        return true;
                }
                catch (Exception)
                {
                }
            }

            var methods = type.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (method.ReturnType != typeof(bool) || method.GetParameters().Length != 0 || !IsLikelyLockFlagName(method.Name))
                    continue;

                try
                {
                    if ((bool)method.Invoke(source, null))
                        return true;
                }
                catch (Exception)
                {
                }
            }

            return false;
        }

        private static bool TryGetRadarHardLockState(Aircraft ownAircraft, WeaponStation ws)
        {
            List<Unit> hudTargets;
            if (TryGetHudTargetList(out hudTargets) && hudTargets.Count > 0)
            {
                TraceResolver("hardlock-targetlist-count", "Hard lock true via CombatHUD.targetList count=" + hudTargets.Count);
                return true;
            }

            object[] sources =
            {
                SceneSingleton<CombatHUD>.i,
                ownAircraft,
                ws
            };

            string[] boolNames =
            {
                "hasHardLock",
                "HasHardLock",
                "isHardLocked",
                "IsHardLocked",
                "hardLocked",
                "HardLocked",
                "radarHardLock",
                "RadarHardLock"
            };

            for (int i = 0; i < sources.Length; i++)
            {
                object source = sources[i];
                if (source == null)
                    continue;

                for (int j = 0; j < boolNames.Length; j++)
                {
                    bool state;
                    if (TryGetBoolFromMember(source, boolNames[j], out state) && state)
                    {
                        TraceResolver("hardlock-explicit-" + GetSourceLabel(source) + "-" + boolNames[j],
                            "Hard lock true via " + GetSourceLabel(source) + "." + boolNames[j]);
                        return true;
                    }
                }

                if (HasTrueLockFlagViaReflection(source))
                {
                    TraceResolver("hardlock-reflect-" + GetSourceLabel(source),
                        "Hard lock true via reflection sweep on " + GetSourceLabel(source));
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetActiveSelectedTarget(Aircraft ownAircraft, WeaponStation ws, out Unit selectedTarget)
        {
            selectedTarget = null;
            if (ownAircraft == null || ws == null || ws.WeaponInfo == null)
                return false;

            // Primary source from NO_Tactitools: CombatHUD.targetList.
            // Target list behaves as a LIFO stack in UI flow, so prefer last valid target.
            List<Unit> hudTargets;
            if (TryGetHudTargetList(out hudTargets) && hudTargets.Count > 0)
            {
                FactionHQ ownHqFromHud = ownAircraft.NetworkHQ;
                for (int i = hudTargets.Count - 1; i >= 0; i--)
                {
                    Unit candidate = hudTargets[i];
                    if (!IsValidTarget(candidate, ownAircraft, ownHqFromHud))
                        continue;

                    selectedTarget = candidate;
                    TraceResolver("target-targetlist-index-" + i,
                        "Selected target via CombatHUD.targetList[" + i + "] -> " + candidate.unitName);
                    return true;
                }
            }

            object[] sources =
            {
                SceneSingleton<CombatHUD>.i,
                ownAircraft,
                ws
            };

            string[] memberNames =
            {
                "lockedTarget",
                "LockedTarget",
                "hardLockedTarget",
                "HardLockedTarget",
                "lockTarget",
                "LockTarget",
                "targetUnit",
                "TargetUnit",
                "currentTarget",
                "CurrentTarget",
                "activeTarget",
                "ActiveTarget",
                "selectedTarget",
                "SelectedTarget",
                "radarTarget",
                "RadarTarget",
                "target",
                "Target",
                "GetLockedTarget",
                "GetHardLockedTarget",
                "GetLockTarget",
                "GetTargetUnit",
                "GetCurrentTarget",
                "GetSelectedTarget",
                "GetRadarTarget",
                "GetTarget"
            };

            FactionHQ ownHq = ownAircraft.NetworkHQ;

            for (int i = 0; i < sources.Length; i++)
            {
                object source = sources[i];
                if (source == null)
                    continue;

                for (int j = 0; j < memberNames.Length; j++)
                {
                    Unit candidate;
                    if (!TryGetUnitFromMember(source, memberNames[j], out candidate))
                        continue;

                    if (!IsValidTarget(candidate, ownAircraft, ownHq))
                        continue;

                    selectedTarget = candidate;
                    TraceResolver("target-explicit-" + GetSourceLabel(source) + "-" + memberNames[j],
                        "Selected target via " + GetSourceLabel(source) + "." + memberNames[j] + " -> " + candidate.unitName);
                    return true;
                }

                Unit reflectedTarget;
                if (TryGetTargetViaReflectionSweep(source, ownAircraft, ownHq, out reflectedTarget))
                {
                    selectedTarget = reflectedTarget;
                    return true;
                }
            }

            return false;
        }

        private static float GetTotalIRIntensity(Unit unit)
        {
            if (unit == null)
                return 0f;

            var sources = HarmonyLib.Traverse.Create(unit).Field("IRSources").GetValue<List<IRSource>>();
            if (sources == null)
                return 0f;

            float total = 0f;
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                if (source != null && !source.flare)
                    total += source.intensity;
            }

            return total;
        }

        private static float ComputeCueStrength(CueTargetInfo cue)
        {
            return SeekerCueMath.ComputeCueStrength(cue.angleDeg, cue.heat, cue.distance);
        }

        private static int CountActiveLaunchedIrMissiles(Aircraft ownAircraft)
        {
            if (ownAircraft == null)
                return 0;

            var ownId = ownAircraft.persistentID;
            Vector3 ownPos = ownAircraft.transform.position;
            int count = 0;

            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                var unit = UnitRegistry.allUnits[i];
                if (unit == null || unit.disabled)
                    continue;

                var missile = unit as Missile;
                if (missile == null || missile.owner == null)
                    continue;

                if (missile.owner.persistentID != ownId)
                    continue;

                if (missile.GetComponent<IRSeeker>() == null)
                    continue;

                bool engineOn = false;
                try
                {
                    engineOn = missile.EngineOn();
                }
                catch (Exception)
                {
                    engineOn = missile.speed > 50f;
                }

                float separation = Vector3.Distance(missile.transform.position, ownPos);
                bool clearlyInFlight = engineOn || missile.speed > 120f || separation > 250f;

                if (clearlyInFlight)
                    count++;
            }

            return count;
        }

        private static bool HasFlareInDetectionCone(Vector3 origin, Vector3 viewDir, float maxRange, float coneHalfAngle)
        {
            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                var unit = UnitRegistry.allUnits[i];
                if (unit == null || unit.disabled)
                    continue;

                var sources = HarmonyLib.Traverse.Create(unit).Field("IRSources").GetValue<List<IRSource>>();
                if (sources == null)
                    continue;

                bool hasFlareSource = false;
                for (int j = 0; j < sources.Count; j++)
                {
                    var src = sources[j];
                    if (src != null && src.flare)
                    {
                        hasFlareSource = true;
                        break;
                    }
                }

                if (!hasFlareSource)
                    continue;

                Vector3 toUnit = unit.transform.position - origin;
                float distance = toUnit.magnitude;
                if (distance < 5f || distance > maxRange)
                    continue;

                float angle = Vector3.Angle(viewDir, toUnit / Mathf.Max(0.001f, distance));
                if (angle > coneHalfAngle)
                    continue;

                if (Physics.Linecast(origin, unit.transform.position, 64))
                    continue;

                return true;
            }

            return false;
        }

        private static AudioClip BuildStandbyClip()
        {
            const int sampleRate = 44100;
            const float seconds = 1.0f;
            int sampleCount = Mathf.RoundToInt(sampleRate * seconds);
            var clip = AudioClip.Create("aim9x-standby-hum", sampleCount, 1, sampleRate, false);

            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float baseHum = Mathf.Sin(2f * Mathf.PI * 115f * t) * 0.55f;
                float harmonicA = Mathf.Sin(2f * Mathf.PI * 230f * t) * 0.2f;
                float harmonicB = Mathf.Sin(2f * Mathf.PI * 345f * t) * 0.1f;

                samples[i] = Mathf.Clamp(baseHum + harmonicA + harmonicB, -1f, 1f) * 0.58f;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        private static AudioClip BuildLockClip()
        {
            const int sampleRate = 44100;
            const float seconds = 1.0f;
            int sampleCount = Mathf.RoundToInt(sampleRate * seconds);
            var clip = AudioClip.Create("aim9x-lock-screech", sampleCount, 1, sampleRate, false);

            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float main = Mathf.Sin(2f * Mathf.PI * 1900f * t) * 0.58f;
                float side = Mathf.Sin(2f * Mathf.PI * 2500f * t) * 0.30f;

                samples[i] = Mathf.Clamp(main + side, -1f, 1f) * 0.78f;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        private static AudioClip BuildFlaredLockClip()
        {
            const int sampleRate = 44100;
            const float seconds = 1.0f;
            int sampleCount = Mathf.RoundToInt(sampleRate * seconds);
            var clip = AudioClip.Create("aim9x-lock-flared", sampleCount, 1, sampleRate, false);

            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float main = Mathf.Sin(2f * Mathf.PI * 1700f * t) * 0.48f;
                float side = Mathf.Sin(2f * Mathf.PI * 2900f * t) * 0.34f;
                samples[i] = Mathf.Clamp(main + side, -1f, 1f) * 0.78f;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        private System.Collections.IEnumerator LoadWavProfileAsync()
        {
            if (Plugin.Instance == null || !Plugin.EnableWavProfileAudio.Value)
                yield break;

            string pluginDir = Path.GetDirectoryName(Plugin.Instance.Info.Location);
            if (string.IsNullOrEmpty(pluginDir))
                yield break;

            string folder = Plugin.WavProfileFolder.Value;
            if (string.IsNullOrWhiteSpace(folder))
                folder = "SeekerNoises";

            string fullFolder = Path.Combine(pluginDir, folder);
            string standbyFile = Plugin.WavStandbyFile.Value;
            string lockFile = Plugin.WavLockFile.Value;
            string flaredFile = Plugin.WavFlaredLockFile.Value;

            AudioClip loadedStandby = null;
            AudioClip loadedLock = null;
            AudioClip loadedFlared = null;

            if (!string.IsNullOrWhiteSpace(standbyFile))
                yield return LoadWavClip(Path.Combine(fullFolder, standbyFile), clip => loadedStandby = clip);

            if (!string.IsNullOrWhiteSpace(lockFile))
                yield return LoadWavClip(Path.Combine(fullFolder, lockFile), clip => loadedLock = clip);

            if (!string.IsNullOrWhiteSpace(flaredFile))
                yield return LoadWavClip(Path.Combine(fullFolder, flaredFile), clip => loadedFlared = clip);

            bool applied = false;

            if (loadedStandby != null)
            {
                _wavStandbyClip = loadedStandby;
                if (_standbySource != null)
                    _standbySource.clip = _wavStandbyClip;
                applied = true;
            }

            if (loadedLock != null)
            {
                _wavLockClip = loadedLock;
                if (_lockSource != null)
                    _lockSource.clip = _wavLockClip;
                applied = true;
            }

            if (loadedFlared != null)
            {
                _wavFlaredLockClip = loadedFlared;
                applied = true;
            }

            if (applied)
            {
                Plugin.Log.LogInfo("[Audio] WAV seeker profile active.");
            }
            else if (!Plugin.FallbackToSyntheticAudio.Value)
            {
                if (_standbySource != null)
                    _standbySource.clip = null;
                if (_lockSource != null)
                    _lockSource.clip = null;

                Plugin.Log.LogWarning("[Audio] WAV profile requested, no clips loaded, synthetic fallback disabled.");
            }
            else
            {
                Plugin.Log.LogWarning("[Audio] WAV profile not loaded, using synthetic fallback.");
            }
        }

        private static System.Collections.IEnumerator LoadWavClip(string fullPath, Action<AudioClip> onLoaded)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                onLoaded?.Invoke(null);
                yield break;
            }

            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(fullPath).AbsoluteUri, AudioType.WAV))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogWarning("[Audio] Failed to load WAV: " + fullPath + " | " + req.error);
                    onLoaded?.Invoke(null);
                    yield break;
                }

                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip != null)
                    clip.name = Path.GetFileName(fullPath);

                onLoaded?.Invoke(clip);
            }
        }
    }
}
