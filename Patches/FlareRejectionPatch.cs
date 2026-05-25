using HarmonyLib;
using UnityEngine;

namespace AIM9XMod.Patches
{
    [HarmonyPatch]
    public static class FlareRejectionPatch
    {
        [HarmonyPatch(typeof(IRSeeker), "Initialize")]
        [HarmonyPostfix]
        public static void IRSeeker_Initialize_Postfix(IRSeeker __instance)
        {
            var t = Traverse.Create(__instance);
            var missile = t.Field("missile").GetValue<Missile>();
            if (missile == null)
            {
                return;
            }

            // patch flare rejection factor
            float flareRejection = Plugin.FlareRejection_IR1.Value;
            if (missile.name.Equals("AAM3")) {
                flareRejection = Plugin.FlareRejection_S2.Value;
            } else if (missile.name.Equals("AAM1")) {
                flareRejection = Plugin.FlareRejection_MMR.Value;
            }
            Plugin.Log.LogInfo($"[LOAL] Flare rejection{t.Field("flareRejection").GetValue<float>()}->{flareRejection}");
            t.Field("flareRejection").SetValue(flareRejection);
        }
    }
}