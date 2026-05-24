using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using AIM9XMod.Services;

namespace AIM9XMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.modder.aim9xmod";
        public const string PluginName = "AIM-9X IR Missile Overhaul";
        public const string PluginVersion = "0.7.0";

        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;
        internal static Plugin Instance;

        // Config entries
        public static ConfigEntry<float> OffBoresightAngle;
        public static ConfigEntry<float> FiringGateAngle;
        public static ConfigEntry<float> MissileMaxTurnRate;
        public static ConfigEntry<float> MissileTorqueMultiplier;
        public static ConfigEntry<float> LOALSearchAngle;
        public static ConfigEntry<float> LOALSearchTime;
        public static ConfigEntry<bool> EnableLOAL;
        public static ConfigEntry<bool> EnableHighOffBoresight;
        public static ConfigEntry<bool> EnableEnhancedTurning;
        public static ConfigEntry<bool> UsePeakIRThreshold;
        public static ConfigEntry<bool> EnableViewSlaving;
        public static ConfigEntry<bool> EnableSeekerGrowl;
        public static ConfigEntry<float> GrowlVolume;
        public static ConfigEntry<bool> EnableWavProfileAudio;
        public static ConfigEntry<string> WavProfileFolder;
        public static ConfigEntry<string> WavStandbyFile;
        public static ConfigEntry<string> WavLockFile;
        public static ConfigEntry<string> WavFlaredLockFile;
        public static ConfigEntry<bool> FallbackToSyntheticAudio;
        public static ConfigEntry<float> LaunchMuteSeconds;
        public static ConfigEntry<bool> ShowDetectionPercentDebug;
        public static ConfigEntry<bool> ShowLoalTargetDebug;
        public static ConfigEntry<float> PrelaunchCueAngle;
        public static ConfigEntry<float> WobbleMaxOffset;
        public static ConfigEntry<float> WobbleSpeed;

        private SeekerCueService _seekerCueService;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Config
            EnableHighOffBoresight = Config.Bind("Features", "EnableHighOffBoresight", true,
                "Enable 90° off-boresight launch capability for IR missiles");
            EnableEnhancedTurning = Config.Bind("Features", "EnableEnhancedTurning", true,
                "Enable AIM-9X-class turning performance for IR missiles");
            EnableLOAL = Config.Bind("Features", "EnableLOAL", true,
                "Enable Lock-On After Launch for IR missiles");
            UsePeakIRThreshold = Config.Bind("Features", "UsePeakIRThreshold", false,
                "When true, flare evasion threshold uses the highest IR the missile ever observed while tracking. " +
                "When false (default), uses the aircraft's IR output at the moment of flare evasion.");
            EnableViewSlaving = Config.Bind("Features", "EnableViewSlaving", true,
                "When true, IR missiles without a lock steer toward the player's view direction (center of view marker) " +
                "while scanning for targets. Only affects player-launched missiles.");
            EnableSeekerGrowl = Config.Bind("Features", "EnableSeekerGrowl", true,
                "Enable pre-launch seeker cueing overlay and growl tone feedback.");

            OffBoresightAngle = Config.Bind("Boresight", "OffBoresightAngle", 90f,
                "Maximum off-boresight seeker angle in degrees (AIM-9X: 90°). " +
                "The firing gate allows 180° but launches beyond this angle go out in LOAL mode.");
            FiringGateAngle = Config.Bind("Boresight", "FiringGateAngle", 180f,
                "Maximum angle at which IR missiles can be launched. Targets beyond OffBoresightAngle " +
                "but within this angle launch the missile in LOAL (no-lock) mode.");

            MissileMaxTurnRate = Config.Bind("Turning", "MaxTurnRate", 12f,
                "Maximum turn rate for IR missile PID (vanilla default ~3). Higher = tighter tracking.");
            MissileTorqueMultiplier = Config.Bind("Turning", "TorqueMultiplier", 3f,
                "Multiplier applied to IR missile torque for enhanced maneuverability");

            LOALSearchAngle = Config.Bind("LOAL", "SearchAngle", 90f,
                "Seeker search cone half-angle (degrees) when acquiring target after launch");
            LOALSearchTime = Config.Bind("LOAL", "SearchTime", 8f,
                "Maximum time (seconds) the seeker will search for a target after launch before going ballistic");

            PrelaunchCueAngle = Config.Bind("Cueing", "PrelaunchCueAngle", 12f,
                "Half-angle (degrees) around center-screen used for pre-launch seeker cue candidate selection.");

            GrowlVolume = Config.Bind("Audio", "GrowlVolume", 0.1f,
                "Master growl volume (0-1)");
            EnableWavProfileAudio = Config.Bind("Audio", "EnableWavProfileAudio", true,
                "Use WAV files for seeker standby and lock tones.");
            WavProfileFolder = Config.Bind("Audio", "WavProfileFolder", "SeekerNoises",
                "Folder next to the plugin DLL that contains seeker WAV files.");
            WavStandbyFile = Config.Bind("Audio", "WavStandbyFile", "Aim9Caged.wav",
                "Standby seeker hum WAV filename.");
            WavLockFile = Config.Bind("Audio", "WavLockFile", "Aim9UnCaged.wav",
                "Lock seeker screech WAV filename.");
            WavFlaredLockFile = Config.Bind("Audio", "WavFlaredLockFile", "Aim9UncagedFlared.wav",
                "Flare-contaminated lock WAV filename.");
            FallbackToSyntheticAudio = Config.Bind("Audio", "FallbackToSyntheticAudio", true,
                "Use generated synthetic tones if WAV files are unavailable.");
            LaunchMuteSeconds = Config.Bind("Audio", "LaunchMuteSeconds", 0.5f,
                "How long to mute seeker growl after detecting an IR missile launch (seconds).");
            ShowDetectionPercentDebug = Config.Bind("Debug", "ShowDetectionPercentDebug", false,
                "Show seeker detection percentage text in HUD (debug output).");
            ShowLoalTargetDebug = Config.Bind("Debug", "ShowLoalTargetDebug", false,
                "Log verbose LOAL target-assignment and scan details to BepInEx console. " +
                "Enable when debugging diamond-target priority issues.");

            WobbleMaxOffset = Config.Bind("Overlay", "WobbleMaxOffset", 14f,
                "Maximum pixel displacement of the diamond target indicator when detection is at 0%%. " +
                "Scales smoothly to zero at 60%% detection. Set to 0 to disable wobble.");
            WobbleSpeed = Config.Bind("Overlay", "WobbleSpeed", 1.3f,
                "Speed multiplier for the diamond indicator wobble oscillation. " +
                "Higher values produce faster, more erratic movement.");

            HarmonyInstance = new Harmony(PluginGUID);
            HarmonyInstance.PatchAll();

            _seekerCueService = gameObject.AddComponent<SeekerCueService>();

            Log.LogInfo($"AIM-9X IR Missile Overhaul v{PluginVersion} loaded!");
        }

        private void OnDestroy()
        {
            if (_seekerCueService != null)
            {
                Destroy(_seekerCueService);
                _seekerCueService = null;
            }

            HarmonyInstance?.UnpatchSelf();
            Instance = null;
        }
    }
}
