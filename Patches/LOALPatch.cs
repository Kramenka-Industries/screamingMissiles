using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using AIM9XMod.Services;
using System.Reflection;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Implements Lock-On After Launch (LOAL) for IR missiles.
    /// 
    /// In vanilla, if an IR missile loses lock or is launched without one, it drifts
    /// on its last known heading with accumulating error until self-destruct.
    /// 
    /// With LOAL, the seeker actively scans for IR-emitting targets within its search
    /// cone during flight. If it finds one with LOS and within the cone, it acquires
    /// lock and begins tracking — just like the AIM-9X Block II datalink capability.
    /// 
    /// Flare evasion memory: when a target successfully decoys the seeker with flares,
    /// the seeker records a relock threshold. By default, this is the aircraft's IR
    /// output at the moment of evasion — so maintaining or reducing throttle keeps you
    /// safe. With UsePeakIRThreshold enabled, the threshold is instead the highest IR
    /// the missile ever observed while tracking that unit — a stricter standard that
    /// requires the aircraft to exceed its historical peak to be reacquired.
    /// </summary>
    [HarmonyPatch]
    public static class LOALPatch
    {
        // Track which missiles are in LOAL search mode and their search start time
        private static readonly Dictionary<int, float> loalSearchStart = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> lastScanTime = new Dictionary<int, float>();

        // Peak observed IR intensity per unit, tracked while the seeker has active lock.
        // Only used when UsePeakIRThreshold is enabled.
        // Key: missile instance ID -> Dictionary of (unit PersistentID -> peak IR intensity)
        private static readonly Dictionary<int, Dictionary<PersistentID, float>> peakObservedIR
            = new Dictionary<int, Dictionary<PersistentID, float>>();

        // Units that successfully evaded each missile via flares.
        // Stores either the aircraft's IR at moment of evasion, or the peak observed IR,
        // depending on the UsePeakIRThreshold config setting.
        // Key: missile instance ID -> Dictionary of (evaded unit PersistentID -> IR threshold)
        private static readonly Dictionary<int, Dictionary<PersistentID, float>> flareEvadedUnits
            = new Dictionary<int, Dictionary<PersistentID, float>>();

        // Preferred LOAL target assignment per missile, seeded from CombatHUD.targetList
        // at launch to support ripple-shot distribution across multiple locked targets.
        private static readonly Dictionary<int, PersistentID> preferredTargetByMissileId
            = new Dictionary<int, PersistentID>();

        // Round-robin cursor per owner aircraft.
        private static readonly Dictionary<PersistentID, int> nextPreferredTargetIndexByOwner
            = new Dictionary<PersistentID, int>();

        private static readonly FieldInfo HudTargetListField = typeof(CombatHUD).GetField(
            "targetList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static bool LoalDebug => Plugin.ShowLoalTargetDebug != null && Plugin.ShowLoalTargetDebug.Value;

        private static void LogLoal(string msg)
        {
            if (LoalDebug)
                Plugin.Log.LogInfo("[LOAL-DBG] " + msg);
        }

        /// <summary>
        /// Get the total IR intensity of a unit by summing all non-flare IR sources.
        /// </summary>
        private static float GetTotalIRIntensity(Unit unit)
        {
            if (unit == null) return 0f;
            var sources = Traverse.Create(unit).Field("IRSources").GetValue<List<IRSource>>();
            if (sources == null) return 0f;

            float total = 0f;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] != null && !sources[i].flare)
                    total += sources[i].intensity;
            }
            return total;
        }

        private static void CleanupMissile(int id)
        {
            loalSearchStart.Remove(id);
            lastScanTime.Remove(id);
            peakObservedIR.Remove(id);
            flareEvadedUnits.Remove(id);
            preferredTargetByMissileId.Remove(id);
        }

        private static bool TryGetHudTargetListForOwner(Missile missile, out List<Unit> targetList)
        {
            targetList = null;
            if (missile == null || missile.owner == null || HudTargetListField == null)
            {
                LogLoal("TryGetHudTargetListForOwner: null missile/owner/field");
                return false;
            }

            var hud = SceneSingleton<CombatHUD>.i;
            if (hud == null || hud.aircraft == null)
            {
                LogLoal("TryGetHudTargetListForOwner: CombatHUD or aircraft is null");
                return false;
            }

            if (hud.aircraft.persistentID != missile.owner.persistentID)
            {
                LogLoal("TryGetHudTargetListForOwner: HUD aircraft != missile owner (AI missile)");
                return false;
            }

            try
            {
                var raw = HudTargetListField.GetValue(hud) as List<Unit>;
                if (raw == null || raw.Count == 0)
                {
                    LogLoal("TryGetHudTargetListForOwner: CombatHUD.targetList is null or empty");
                    return false;
                }

                if (LoalDebug)
                {
                    var names = new System.Text.StringBuilder();
                    for (int i = 0; i < raw.Count; i++)
                        names.Append(raw[i] != null ? raw[i].unitName : "null").Append(", ");
                    LogLoal($"TryGetHudTargetListForOwner: targetList has {raw.Count} entries: [{names}]");
                }

                targetList = raw;
                return true;
            }
            catch (Exception ex)
            {
                LogLoal($"TryGetHudTargetListForOwner: exception reading targetList: {ex.Message}");
                return false;
            }
        }

        private static bool IsValidEnemyTargetForMissile(Missile missile, Unit candidate)
        {
            if (missile == null || missile.owner == null || candidate == null || candidate.disabled)
                return false;

            if (candidate.persistentID == missile.owner.persistentID)
                return false;

            if (missile.NetworkHQ != null && candidate.NetworkHQ == missile.NetworkHQ)
                return false;

            return candidate.HasIRSignature();
        }

        private static bool TryGetAssignedPreferredTarget(Missile missile, out Unit target)
        {
            target = null;
            if (missile == null)
                return false;

            PersistentID pid;
            if (!preferredTargetByMissileId.TryGetValue(missile.GetInstanceID(), out pid))
                return false;

            Unit resolved;
            if (!UnitRegistry.TryGetUnit(new PersistentID?(pid), out resolved))
                return false;

            if (!IsValidEnemyTargetForMissile(missile, resolved))
                return false;

            target = resolved;
            return true;
        }

        private static bool TryAssignPreferredTarget(Missile missile, Unit candidateTarget, string sourceLabel)
        {
            if (!IsValidEnemyTargetForMissile(missile, candidateTarget))
            {
                LogLoal($"TryAssignPreferredTarget [{sourceLabel}]: {(candidateTarget != null ? candidateTarget.unitName : "null")} rejected — failed IsValidEnemyTargetForMissile");
                return false;
            }

            Vector3 ownerForward = missile.owner.transform.forward;
            Vector3 ownerPosition = missile.owner.transform.position;
            float offBoresightAngle = Plugin.GetOffBoresightAngle(missile.name);
            offBoresightAngle = Mathf.Max(1f, offBoresightAngle);

            Vector3 toTarget = candidateTarget.transform.position - ownerPosition;
            float angle = Vector3.Angle(ownerForward, toTarget);
            if (angle > offBoresightAngle)
            {
                LogLoal($"TryAssignPreferredTarget [{sourceLabel}]: {candidateTarget.unitName} rejected — angle {angle:F1}° > offBoresight {offBoresightAngle:F1}°");
                return false;
            }

            preferredTargetByMissileId[missile.GetInstanceID()] = candidateTarget.persistentID;
            Plugin.Log.LogDebug($"[LOAL] Assigned preferred target {candidateTarget.unitName} for missile {missile.GetInstanceID()} via {sourceLabel}.");
            LogLoal($"TryAssignPreferredTarget [{sourceLabel}]: assigned {candidateTarget.unitName} (angle {angle:F1}°)");
            return true;
        }

        private static bool AssignPreferredTargetFromHudList(Missile missile)
        {
            if (missile == null || missile.owner == null)
                return false;

            List<Unit> hudTargets;
            if (!TryGetHudTargetListForOwner(missile, out hudTargets))
                return false;

            var validTargets = new List<Unit>(hudTargets.Count);

            float offBoresightAngle = Plugin.GetOffBoresightAngle(missile.name);
            offBoresightAngle = Mathf.Max(1f, offBoresightAngle);

            Vector3 ownerForward = missile.owner.transform.forward;
            Vector3 ownerPosition = missile.owner.transform.position;

            for (int i = hudTargets.Count - 1; i >= 0; i--)
            {
                var candidate = hudTargets[i];
                if (!IsValidEnemyTargetForMissile(missile, candidate))
                    continue;

                Vector3 toTarget = candidate.transform.position - ownerPosition;
                if (Vector3.Angle(ownerForward, toTarget) > offBoresightAngle)
                    continue;

                validTargets.Add(candidate);
            }

            if (validTargets.Count == 0)
                return false;

            PersistentID ownerId = missile.owner.persistentID;
            int nextIndex;
            if (!nextPreferredTargetIndexByOwner.TryGetValue(ownerId, out nextIndex))
                nextIndex = 0;

            int selectedIndex = Mathf.Abs(nextIndex) % validTargets.Count;
            Unit selectedTarget = validTargets[selectedIndex];

            nextPreferredTargetIndexByOwner[ownerId] = selectedIndex + 1;

            bool assigned = TryAssignPreferredTarget(missile, selectedTarget, "HUD.targetList");
            if (assigned)
            {
                Plugin.Log.LogDebug($"[LOAL] HUD assignment slot {selectedIndex + 1}/{validTargets.Count} for owner {ownerId}.");
                return true;
            }

            return false;
        }

        private static bool AssignPreferredTargetFromHudSelectedTarget(Missile missile)
        {
            if (missile == null || missile.owner == null)
                return false;

            List<Unit> hudTargets;
            if (!TryGetHudTargetListForOwner(missile, out hudTargets))
            {
                LogLoal("AssignPreferredTargetFromHudSelectedTarget: no HUD target list available");
                return false;
            }

            // CombatHUD.targetList behaves as a LIFO stack, where the last valid
            // entry is the currently diamond-marked target in the UI.
            LogLoal($"AssignPreferredTargetFromHudSelectedTarget: trying {hudTargets.Count} HUD targets (LIFO order)");
            for (int i = hudTargets.Count - 1; i >= 0; i--)
            {
                Unit candidate = hudTargets[i];
                if (TryAssignPreferredTarget(missile, candidate, "HUD.targetList(selected)"))
                    return true;
            }

            LogLoal("AssignPreferredTargetFromHudSelectedTarget: no valid candidate found in HUD target list");
            return false;
        }

        private static bool AssignPreferredTargetFromPrelaunchCue(Missile missile)
        {
            if (missile == null || missile.owner == null)
                return false;

            var hud = SceneSingleton<CombatHUD>.i;
            if (hud == null || hud.aircraft == null || hud.aircraft.persistentID != missile.owner.persistentID)
            {
                LogLoal("AssignPreferredTargetFromPrelaunchCue: skipped (missile owner is not current HUD aircraft)");
                return false;
            }

            CueTargetInfo cue;
            if (!SeekerCueState.TryGetPrelaunchCue(out cue, 0.5f))
            {
                LogLoal("AssignPreferredTargetFromPrelaunchCue: no valid prelaunch cue within 0.5s");
                return false;
            }

            if (cue.target == null)
            {
                LogLoal("AssignPreferredTargetFromPrelaunchCue: prelaunch cue has null target");
                return false;
            }

            LogLoal($"AssignPreferredTargetFromPrelaunchCue: cue target is {cue.target.unitName} (angle {cue.angleDeg:F1}°, dist {cue.distance:F0}m, heat {cue.heat:F2})");
            return TryAssignPreferredTarget(missile, cue.target, "SeekerCueState.PrelaunchCue");
        }

        private static bool TryGetUnitFromMember(object instance, string memberName, out Unit unit)
        {
            unit = null;
            if (instance == null || string.IsNullOrWhiteSpace(memberName))
                return false;

            try
            {
                unit = Traverse.Create(instance).Field(memberName).GetValue<Unit>();
                if (unit != null)
                    return true;
            }
            catch (Exception)
            {
            }

            try
            {
                unit = Traverse.Create(instance).Property(memberName).GetValue<Unit>();
                if (unit != null)
                    return true;
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static bool TryGetActiveLockedTarget(Missile missile, out Unit lockedTarget)
        {
            lockedTarget = null;
            if (missile == null || missile.owner == null)
                return false;

            Unit preferredTarget;
            if (TryGetAssignedPreferredTarget(missile, out preferredTarget))
            {
                lockedTarget = preferredTarget;
                return true;
            }

            object[] lockSources =
            {
                SceneSingleton<CombatHUD>.i,
                missile.owner
            };

            string[] memberNames =
            {
                "lockedTarget",
                "lockTarget",
                "targetUnit",
                "currentTarget",
                "activeTarget",
                "target"
            };

            for (int i = 0; i < lockSources.Length; i++)
            {
                object source = lockSources[i];
                if (source == null)
                    continue;

                for (int j = 0; j < memberNames.Length; j++)
                {
                    Unit candidate;
                    if (!TryGetUnitFromMember(source, memberNames[j], out candidate))
                        continue;

                    if (candidate == null || candidate.disabled)
                        continue;

                    if (candidate.persistentID == missile.owner.persistentID)
                        continue;

                    if (missile.NetworkHQ != null && candidate.NetworkHQ == missile.NetworkHQ)
                        continue;

                    lockedTarget = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Patch IRSeeker.Initialize to register missiles for LOAL tracking.
        /// If the target is beyond the seeker cone (e.g. rear-hemisphere launch),
        /// force the missile into LOAL mode so it launches without lock and acquires
        /// after turning.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "Initialize")]
        [HarmonyPostfix]
        public static void IRSeeker_Initialize_Postfix(IRSeeker __instance)
        {
            // Note: 'missile' and 'targetUnit' are declared on the MissileSeeker base class,
            // not on IRSeeker itself. Traverse.Create(__instance).Field("fieldName") resolves
            // inherited fields by walking the type hierarchy, so these lookups work correctly
            // on IRSeeker instances despite the fields living on MissileSeeker.
            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null)
            {
                Plugin.Log.LogWarning("[LOAL] IRSeeker_Initialize_Postfix: Traverse failed to resolve 'missile' field — skipping LOAL registration.");
                return;
            }

            if (!Plugin.IsLoalEnabledForMissile(missile.name))
                return;

            int id = missile.GetInstanceID();
            loalSearchStart[id] = Time.timeSinceLevelLoad;
            lastScanTime[id] = 0f;
            peakObservedIR[id] = new Dictionary<PersistentID, float>();
            flareEvadedUnits[id] = new Dictionary<PersistentID, float>();

            if (!AssignPreferredTargetFromHudSelectedTarget(missile)
                && !AssignPreferredTargetFromHudList(missile))
            {
                AssignPreferredTargetFromPrelaunchCue(missile);
            }

            if (LoalDebug)
            {
                Unit assigned;
                string assignedName = TryGetAssignedPreferredTarget(missile, out assigned)
                    ? assigned.unitName
                    : "NONE";
                LogLoal($"Launch assignment result for missile {id}: preferred target = {assignedName}");
            }

            // Check if the target is outside the seeker cone at launch.
            // This handles rear-hemisphere shots: the missile fires forward,
            // enters LOAL, view-slaves toward the target, and locks on once
            // the target enters the seeker's 90° cone.
            var targetUnit = t.Field("targetUnit").GetValue<Unit>();
            var irTarget = t.Field("IRTarget").GetValue<IRSource>();
            if (targetUnit != null && irTarget != null && !irTarget.flare)
            {
                Vector3 toTarget = targetUnit.transform.position - missile.transform.position;
                float angle = Vector3.Angle(missile.transform.forward, toTarget);

                float offBoresightAngle = Plugin.GetOffBoresightAngle(missile.name);
                offBoresightAngle = Mathf.Max(1f, offBoresightAngle);

                if (angle > offBoresightAngle)
                {
                    // Target is outside seeker cone — force LOAL mode
                    t.Field("IRTarget").SetValue(null);
                    t.Field("targetUnit").SetValue(null);
                    t.Field("achievedLock").SetValue(false);

                    Plugin.Log.LogDebug(
                        $"[LOAL] Rear-hemisphere launch: target at {angle:F0}° off-bore, " +
                        $"exceeds {offBoresightAngle}° seeker cone. Entering LOAL.");
                }
            }
        }

        /// <summary>
        /// Patch IRSeeker.Seek to implement LOAL scanning when the seeker has no lock.
        /// Scans for targets within the search cone, respecting flare evasion memory.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "Seek")]
        [HarmonyPostfix]
        public static void IRSeeker_Seek_Postfix(IRSeeker __instance)
        {
            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null)
            {
                Plugin.Log.LogWarning("[LOAL] IRSeeker_Seek_Postfix: Traverse failed to resolve 'missile' field — skipping LOAL scan.");
                return;
            }
            if (!Plugin.IsLoalEnabledForMissile(missile.name))
                return;
            if (missile.disabled) return;

            int id = missile.GetInstanceID();
            var irTarget = t.Field("IRTarget").GetValue<IRSource>();
            var targetUnit = t.Field("targetUnit").GetValue<Unit>();

            // If we have active lock, update peak IR tracking if enabled, then return.
            if (irTarget != null && irTarget.transform != null && targetUnit != null)
            {
                if (Plugin.UsePeakIRThreshold.Value)
                {
                    Dictionary<PersistentID, float> peaks;
                    if (peakObservedIR.TryGetValue(id, out peaks))
                    {
                        float currentIR = GetTotalIRIntensity(targetUnit);
                        PersistentID uid = targetUnit.persistentID;
                        float existing;
                        if (!peaks.TryGetValue(uid, out existing) || currentIR > existing)
                            peaks[uid] = currentIR;
                    }
                }
                return;
            }

            // --- LOAL scanning when we have no lock ---
            bool guidance = t.Field("guidance").GetValue<bool>();
            if (!guidance) return;

            if (!loalSearchStart.ContainsKey(id)) return;

            float searchElapsed = Time.timeSinceLevelLoad - loalSearchStart[id];
            if (searchElapsed > Plugin.LOALSearchTime.Value) return;

            // Steer LOAL missiles toward the player's active locked target when present,
            // otherwise fall back to player view slaving.
            if (Plugin.EnableViewSlaving.Value && missile.owner != null)
            {
                try
                {
                    var combatHUD = SceneSingleton<CombatHUD>.i;
                    if (combatHUD != null && combatHUD.aircraft != null
                        && combatHUD.aircraft.persistentID == missile.owner.persistentID)
                    {
                        var cam = SceneSingleton<CameraStateManager>.i;
                        if (cam != null)
                        {
                            Vector3 steerDirection = cam.transform.forward;

                            Unit lockedTarget;
                            if (TryGetActiveLockedTarget(missile, out lockedTarget))
                            {
                                Vector3 toTarget = lockedTarget.transform.position - missile.transform.position;
                                if (toTarget.sqrMagnitude > 1f)
                                    steerDirection = toTarget.normalized;
                            }
                            else
                            {
                                Vector3 viewDir;
                                if (SeekerCueState.TryGetViewDirection(out viewDir))
                                    steerDirection = viewDir;
                            }

                            GlobalPosition steerAimpoint = missile.GlobalPosition() + steerDirection * 10000f;
                            missile.SetAimpoint(steerAimpoint, Vector3.zero);
                        }
                    }
                }
                catch (Exception) { /* SceneSingleton not available */ }
            }

            // Throttle scanning to every 0.25s
            if (!lastScanTime.ContainsKey(id)) lastScanTime[id] = 0f;
            if (Time.timeSinceLevelLoad - lastScanTime[id] < 0.25f) return;
            lastScanTime[id] = Time.timeSinceLevelLoad;

            // Get the flare evasion blacklist for this missile
            Dictionary<PersistentID, float> evadedLookup = null;
            flareEvadedUnits.TryGetValue(id, out evadedLookup);

            Unit bestTarget = null;
            IRSource bestSource = null;
            float bestScore = float.MaxValue;
            float bestAngle = 0f;
            float bestDistance = 0f;
            float bestHeat = 0f;

            float searchAngle = Plugin.LOALSearchAngle.Value;
            float maxRange = missile.GetWeaponInfo().targetRequirements.maxRange;
            Vector3 missilePos = missile.transform.position;
            Vector3 missileForward = missile.transform.forward;
            FactionHQ missileHQ = missile.NetworkHQ;

            // Prefer assigned target for this missile when valid and in-cone.
            Unit preferred;
            if (TryGetAssignedPreferredTarget(missile, out preferred))
            {
                Vector3 toPreferred = preferred.transform.position - missilePos;
                float preferredDist = toPreferred.magnitude;
                if (preferredDist <= maxRange && preferredDist >= 50f)
                {
                    float preferredAngle = Vector3.Angle(missileForward, toPreferred);
                    if (preferredAngle <= searchAngle && !Physics.Linecast(missilePos, preferred.transform.position, 64))
                    {
                        float preferredHeat = GetTotalIRIntensity(preferred);
                        if (preferredHeat > 0f)
                        {
                            bool relockBlocked = false;
                            if (evadedLookup != null)
                            {
                                float evasionIR;
                                if (evadedLookup.TryGetValue(preferred.persistentID, out evasionIR) && preferredHeat <= evasionIR)
                                    relockBlocked = true;
                            }

                            if (!relockBlocked)
                            {
                                IRSource preferredSource = preferred.GetIRSource();
                                if (preferredSource != null && !preferredSource.flare)
                                {
                                    bestTarget = preferred;
                                    bestSource = preferredSource;
                                    bestScore = -1000f;
                                    bestAngle = preferredAngle;
                                    bestDistance = preferredDist;
                                    bestHeat = preferredHeat;
                                    LogLoal($"Scan: preferred target {preferred.unitName} accepted (angle {preferredAngle:F1}°, dist {preferredDist:F0}m, heat {preferredHeat:F2}) — score -1000");
                                }
                                else
                                {
                                    LogLoal($"Scan: preferred target {preferred.unitName} REJECTED — IRSource null or is-flare (source={(preferredSource == null ? "null" : "flare")})");
                                }
                            }
                            else
                            {
                                LogLoal($"Scan: preferred target {preferred.unitName} REJECTED — relock blocked by flare evasion (heat {preferredHeat:F2} <= evasion threshold)");
                            }
                        }
                        else
                        {
                            LogLoal($"Scan: preferred target {preferred.unitName} REJECTED — zero IR heat");
                        }
                    }
                    else
                    {
                        if (preferredAngle > searchAngle)
                            LogLoal($"Scan: preferred target {preferred.unitName} REJECTED — angle {Vector3.Angle(missileForward, toPreferred):F1}° > searchAngle {searchAngle:F1}° (missile seeker cone)");
                        else
                            LogLoal($"Scan: preferred target {preferred.unitName} REJECTED — terrain LOS blocked");
                    }
                }
                else
                {
                    LogLoal($"Scan: preferred target {preferred.unitName} REJECTED — dist {preferredDist:F0}m out of range [50, {maxRange:F0}]");
                }
            }
            else
            {
                LogLoal("Scan: no preferred target assigned for this missile");
            }

            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                Unit unit = UnitRegistry.allUnits[i];
                if (unit == null || unit.disabled) continue;
                if (unit == (Unit)missile) continue;

                // Skip friendlies
                if (missileHQ != null && unit.NetworkHQ == missileHQ) continue;

                // Must have IR signature
                if (!unit.HasIRSignature()) continue;

                // Range check (do early to avoid expensive angle/LOS checks)
                Vector3 toTarget = unit.transform.position - missilePos;
                float dist = toTarget.magnitude;
                if (dist > maxRange || dist < 50f) continue;

                // Cone check — must be within seeker search angle
                float angle = Vector3.Angle(missileForward, toTarget);
                if (angle > searchAngle) continue;

                // --- Flare evasion memory check ---
                // Only allow relock if the aircraft's current IR output exceeds
                // what it was putting out when it successfully flared.
                float currentIR = GetTotalIRIntensity(unit);
                if (evadedLookup != null)
                {
                    float evasionIR;
                    if (evadedLookup.TryGetValue(unit.persistentID, out evasionIR))
                    {
                        if (currentIR <= evasionIR)
                            continue; // Aircraft hasn't increased throttle — skip
                    }
                }

                // Line of sight check (layer 64 = terrain)
                if (Physics.Linecast(missilePos, unit.transform.position, 64))
                    continue;

                // Score by angle and distance (prefer close, on-axis targets)
                float score = angle + dist * 0.001f;
                if (score < bestScore)
                {
                    IRSource source = unit.GetIRSource();
                    if (source != null && !source.flare)
                    {
                        bestTarget = unit;
                        bestSource = source;
                        bestScore = score;
                        bestAngle = angle;
                        bestDistance = dist;
                        bestHeat = currentIR;
                    }
                }
            }

            if (bestTarget != null && bestSource != null)
            {
                SeekerCueState.ReportLoalCandidate(missile, bestTarget, bestScore, bestAngle, bestDistance, bestHeat);

                // Acquire lock
                t.Field("IRTarget").SetValue(bestSource);
                t.Field("targetUnit").SetValue(bestTarget);
                t.Field("driftError").SetValue(Vector3.zero);
                t.Field("dazzleAmount").SetValue(0f);
                t.Field("achievedLock").SetValue(false);

                // Subscribe to flare events on the new target
                try
                {
                    var flareHandler = AccessTools.Method(typeof(IRSeeker), "IRSeeker_OnTargetFlare");
                    if (flareHandler != null)
                    {
                        var del = (Action<IRSource>)Delegate.CreateDelegate(
                            typeof(Action<IRSource>), __instance, flareHandler);
                        bestTarget.onAddIRSource += del;
                    }
                }
                catch (Exception) { /* Non-critical */ }

                missile.SetTarget(bestTarget);

                // Remove from LOAL search — we have lock now
                // Keep peakObservedIR and flareEvadedUnits for continued tracking
                loalSearchStart.Remove(id);
                lastScanTime.Remove(id);

                Plugin.Log.LogDebug(
                    $"[LOAL] Acquired lock on {bestTarget.unitName} at {bestScore:F1} score");

                if (LoalDebug)
                {
                    Unit pref;
                    bool hadPreferred = TryGetAssignedPreferredTarget(missile, out pref);
                    bool lockedPreferred = hadPreferred && pref != null && pref.persistentID == bestTarget.persistentID;
                    LogLoal($"Lock acquired: {bestTarget.unitName} (score {bestScore:F1}, angle {bestAngle:F1}°, dist {bestDistance:F0}m)" +
                        (lockedPreferred ? " — PREFERRED TARGET ✓" : (hadPreferred && pref != null ? $" — OVERRODE preferred target {pref.unitName}!" : " — no preferred target was set")));
                }
            }
            else
            {
                SeekerCueState.ReportLoalCandidate(missile, null, 0f, 0f, 0f, 0f);
            }
        }

        /// <summary>
        /// Snapshot state before IRSeeker_OnTargetFlare runs so we can detect
        /// whether the flare successfully decoyed the seeker.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "IRSeeker_OnTargetFlare")]
        [HarmonyPrefix]
        public static void IRSeeker_OnTargetFlare_Prefix(
            IRSeeker __instance,
            out FlareEvasionSnapshot __state)
        {
            __state = default;

            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null)
                return;

            if (!Plugin.IsLoalEnabledForMissile(missile.name))
                return;

            var targetUnit = t.Field("targetUnit").GetValue<Unit>();
            var irTarget = t.Field("IRTarget").GetValue<IRSource>();

            if (targetUnit != null && irTarget != null && !irTarget.flare)
            {
                __state = new FlareEvasionSnapshot
                {
                    valid = true,
                    unitId = targetUnit.persistentID,
                    originalIRTarget = irTarget
                };
            }
        }

        /// <summary>
        /// After the vanilla flare logic runs, check if lock transferred to the flare.
        /// If so, store the peak observed IR for that unit as the evasion threshold.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "IRSeeker_OnTargetFlare")]
        [HarmonyPostfix]
        public static void IRSeeker_OnTargetFlare_Postfix(
            IRSeeker __instance,
            ref FlareEvasionSnapshot __state)
        {
            if (!__state.valid) return;

            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null)
            {
                Plugin.Log.LogWarning("[LOAL] IRSeeker_OnTargetFlare_Postfix: Traverse failed to resolve 'missile' field — skipping flare evasion tracking.");
                return;
            }
            if (!Plugin.IsLoalEnabledForMissile(missile.name))
                return;

            var currentIRTarget = t.Field("IRTarget").GetValue<IRSource>();

            // If IRTarget changed, the flare won
            if (currentIRTarget != __state.originalIRTarget)
            {
                int missileId = missile.GetInstanceID();

                float thresholdIR = 0f;

                if (Plugin.UsePeakIRThreshold.Value)
                {
                    // Use the highest IR the missile ever observed while tracking this unit
                    Dictionary<PersistentID, float> peaks;
                    if (peakObservedIR.TryGetValue(missileId, out peaks))
                        peaks.TryGetValue(__state.unitId, out thresholdIR);
                }

                // Fall back to (or use) the aircraft's current IR at moment of evasion
                if (thresholdIR <= 0f)
                {
                    Unit evadedUnit;
                    if (UnitRegistry.TryGetUnit(new PersistentID?(__state.unitId), out evadedUnit))
                        thresholdIR = GetTotalIRIntensity(evadedUnit);
                }

                // Store as the relock threshold
                if (!flareEvadedUnits.ContainsKey(missileId))
                    flareEvadedUnits[missileId] = new Dictionary<PersistentID, float>();

                flareEvadedUnits[missileId][__state.unitId] = thresholdIR;

                // Re-enter LOAL search mode
                if (!loalSearchStart.ContainsKey(missileId))
                    loalSearchStart[missileId] = Time.timeSinceLevelLoad;

                string mode = Plugin.UsePeakIRThreshold.Value ? "peak observed" : "at evasion";
                Plugin.Log.LogDebug(
                    $"[LOAL] Flare evasion: unit {__state.unitId}, IR threshold ({mode}): {thresholdIR:F2}. " +
                    $"Relock requires aircraft IR > {thresholdIR:F2} while in seeker cone.");
            }
        }

        /// <summary>
        /// Prevent premature self-destruct during LOAL search.
        /// 
        /// The vanilla SlowChecks now self-destructs when targetOnLaunch == true
        /// and targetUnit == null. During LOAL, the mod clears targetUnit to null,
        /// which would trigger this condition. This prefix returns false (skipping
        /// vanilla logic) whenever the missile is in active LOAL search and is not
        /// in a terminal condition — regardless of engine state.
        /// 
        /// Terminal conditions (LosingGround, MissedTarget, speed below threshold)
        /// still allow self-destruct when the missile is no longer viable.
        /// </summary>
        [HarmonyPatch(typeof(IRSeeker), "SlowChecks")]
        [HarmonyPrefix]
        public static bool IRSeeker_SlowChecks_Prefix(IRSeeker __instance)
        {
            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null)
            {
                Plugin.Log.LogWarning("[LOAL] IRSeeker_SlowChecks_Prefix: Traverse failed to resolve 'missile' field — allowing vanilla SlowChecks.");
                return true;
            }
            if (!Plugin.IsLoalEnabledForMissile(missile.name))
                return true;
            if (missile.disabled) return true;

            int id = missile.GetInstanceID();
            if (!loalSearchStart.ContainsKey(id)) return true;

            float searchElapsed = Time.timeSinceLevelLoad - loalSearchStart[id];
            if (searchElapsed > Plugin.LOALSearchTime.Value)
            {
                CleanupMissile(id);
                return true;
            }

            // Engine still running — skip vanilla entirely, LOAL search is active.
            if (missile.EngineOn()) return false;

            // Engine off: check terminal conditions. If the missile is losing ground,
            // missed its target, or is too slow, it's no longer viable — allow
            // vanilla self-destruct.
            bool losingGround = missile.LosingGround();
            bool missedTarget = missile.MissedTarget();
            float selfDestructSpeed = t.Field("selfDestructAtSpeed").GetValue<float>();

            if (losingGround || missedTarget || missile.speed < selfDestructSpeed)
            {
                CleanupMissile(id);
                return true;
            }

            // Engine off but missile is still viable and LOAL search is active.
            // Return false to block vanilla SlowChecks, which would otherwise
            // self-destruct due to targetOnLaunch == true && targetUnit == null
            // (the mod clears targetUnit during LOAL search).
            return false;
        }

        /// <summary>
        /// Clean up tracking dictionaries when missiles are destroyed.
        /// </summary>
        [HarmonyPatch(typeof(Missile), "UnitDisabled")]
        [HarmonyPostfix]
        public static void Missile_UnitDisabled_Postfix(Missile __instance)
        {
            CleanupMissile(__instance.GetInstanceID());
            SeekerCueState.RemoveMissile(__instance);
        }
    }

    /// <summary>
    /// Snapshot of state before IRSeeker_OnTargetFlare runs.
    /// </summary>
    public struct FlareEvasionSnapshot
    {
        public bool valid;
        public PersistentID unitId;
        public IRSource originalIRTarget;
    }
}
