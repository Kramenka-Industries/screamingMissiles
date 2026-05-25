using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AIM9XMod.Patches
{
    [HarmonyPatch]
    public static class EnhancedMotorPatch
    {
        private static readonly FieldInfo MotorsField =
            AccessTools.Field(typeof(Missile), "motors");

        private static readonly Type MotorType =
            AccessTools.Inner(typeof(Missile), "Motor");

        private static readonly AccessTools.FieldRef<object, float> ThrustRef =
            AccessTools.FieldRefAccess<float>(MotorType, "thrust");

        private static readonly AccessTools.FieldRef<object, float> BurnTimeRef =
            AccessTools.FieldRefAccess<float>(MotorType, "burnTime");

        private static readonly AccessTools.FieldRef<object, float> FuelMassRef =
            AccessTools.FieldRefAccess<float>(MotorType, "fuelMass");

        [HarmonyPatch(typeof(Missile), "StartMissile")]
        [HarmonyPostfix]
        public static void Missile_StartMissile_Postfix(Missile __instance)
        {
            if (!Plugin.EnableEnhancedTurning.Value) return;

            // Check if this missile has an IR seeker
            var seeker = __instance.gameObject.GetComponent<IRSeeker>();
            if (seeker == null) return;

            if (!(MotorsField.GetValue(__instance) is Array motors) || motors.Length == 0)
                return;

            object motor = motors.GetValue(0);
            if (motor == null)
                return;

            float newThrust = Plugin.Thrust_IR1.Value;
            float newBurn = Plugin.Burn_IR1.Value;
            float newFuel = Plugin.Fuel_IR1.Value;
            if (__instance.name.Equals("AAM3")) {
                newThrust = Plugin.Thrust_S2.Value;
                newBurn = Plugin.Burn_S2.Value;
                newFuel = Plugin.Fuel_S2.Value;
            } else if (__instance.name.Equals("AAM1")) {
                newThrust = Plugin.Thrust_MMR.Value;
                newBurn = Plugin.Burn_MMR.Value;
                newFuel = Plugin.Fuel_MMR.Value;
            }

            float oThrust = ThrustRef(motor);
            float oBurn = BurnTimeRef(motor);
            float oFuel = FuelMassRef(motor);
            Plugin.Log?.LogInfo(
                    $"{__instance.name} motor Thrust {oThrust}->{newThrust}, BurnTime {oBurn}->{newBurn}, FuelMass {oFuel}->{newFuel}");

            ThrustRef(motor) = newThrust;
            BurnTimeRef(motor) = newBurn;
            FuelMassRef(motor) = newFuel;
        }
    }
}
