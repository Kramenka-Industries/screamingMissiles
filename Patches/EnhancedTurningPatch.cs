using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    /// <summary>
    /// Enhances IR missile turning performance to AIM-9X levels.
    /// The AIM-9X uses thrust vector control (jet vanes) for 50+ G maneuverability.
    /// We increase the PID turn rate limit and torque for missiles with IR seekers.
    /// </summary>
    [HarmonyPatch]
    public static class EnhancedTurningPatch
    {
        /// <summary>
        /// Patch Missile.StartMissile (called via OnStartNetwork) to boost IR missile agility.
        /// At this point the PID is created with: new PID2D(PIDFactors, maxTurnRate, 3f)
        /// and torqueAxes = rb.inertiaTensor * torque.
        /// We patch after StartMissile to override these values for IR missiles.
        /// 
        /// QOL compatibility: The QOL mod's MissileTangiblePatch is also a postfix on
        /// Missile.StartMissile (cleans up clone names). It does not modify PID, torque,
        /// or seeker state, so both postfixes coexist without conflict regardless of
        /// execution order.
        /// </summary>
        [HarmonyPatch(typeof(Missile), "StartMissile")]
        [HarmonyPostfix]
        public static void Missile_StartMissile_Postfix(Missile __instance)
        {
            if (!Plugin.EnableEnhancedTurning.Value) return;

            // Check if this missile has an IR seeker
            var seeker = __instance.gameObject.GetComponent<IRSeeker>();
            if (seeker == null) return;

            // Get the original torque value (GetTorque still returns the correct serialized torque)
            float origTorque = __instance.GetTorque();

            // Apply enhanced values — use config value directly since GetMaxTurnRate()
            // now returns hardcoded 90f and no longer reflects the actual PID pLimit.
            // The new PID2D initializes with pLimit=1.0, so we always override to our configured value.
            float newTurnRate = Plugin.MissileMaxTurnRate_IR1.Value;
            if (__instance.name.Equals("AAM3")) {
                newTurnRate = Plugin.MissileMaxTurnRate_S2.Value;
            } else if (__instance.name.Equals("AAM1")) {
                newTurnRate = Plugin.MissileMaxTurnRate_MMR.Value;
            }

            float newTorque = origTorque * Plugin.MissileTorqueMultiplier.Value;

            // AAM3 -> IRM-S2
            // AAM1 -> MMR-S3
            // SAM_IR1 -> IRM-S1

            // SetTorque updates torqueAxes and calls pid.SetPLimit(maxTurnRate) when LocalSim is true
            __instance.SetTorque(newTorque, newTurnRate);

            Plugin.Log.LogDebug($"[TVC] {__instance.name} Enhanced IR missile turning: turnRate -> {newTurnRate}, torque {origTorque} -> {newTorque}");
        }
    }
}
