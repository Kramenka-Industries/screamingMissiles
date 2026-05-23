# AIM-9X IR Missile Overhaul — Nuclear Option BepInEx Mod

Adds AIM-9X-class capabilities to all IR-seeking missiles in Nuclear Option:

- **90° High Off-Boresight (HOBS)** — Launch IR missiles at targets up to 90° off your nose
- **Enhanced Turning** — Thrust-vector-control-level agility (increased torque and PID turn rate)
- **Lock-On After Launch (LOAL)** — Missiles actively search for IR targets if launched without lock or after losing lock
- **Seeker View Slaving + Growl Cueing** — Pre-launch center-screen seeker cueing with growl feedback, and post-launch LOAL candidate/lock feedback
- **Seeker Overlay** — A cockpit HUD ring around center-screen showing current seeker confidence and likely target name

## Building

1. Copy the following DLLs from your game install into a `lib/` folder at the workspace root:
   - `NuclearOption_Data/Managed/Assembly-CSharp.dll`
   - `NuclearOption_Data/Managed/UnityEngine.AudioModule.dll`
   - `NuclearOption_Data/Managed/UnityEngine.CoreModule.dll`
   - `NuclearOption_Data/Managed/UnityEngine.IMGUIModule.dll`
   - `NuclearOption_Data/Managed/UnityEngine.PhysicsModule.dll`
   - `NuclearOption_Data/Managed/UniTask.dll`

2. Build:
   ```
   dotnet build AIM9XMod/AIM9XMod.csproj -c Release
   ```

3. Copy `AIM9XMod/bin/Release/net472/AIM9XMod.dll` to `BepInEx/plugins/`

## Configuration

After first run, edit `BepInEx/config/com.modder.aim9xmod.cfg`:

| Setting | Default | Description |
|---------|---------|-------------|
| EnableHighOffBoresight | true | 90° off-boresight launch |
| EnableEnhancedTurning | true | AIM-9X turning performance |
| EnableLOAL | true | Lock-on after launch |
| EnableViewSlaving | true | Slaves LOAL missile steering to player view direction |
| EnableSeekerGrowl | true | Enables growl tone + seeker cue overlay |
| OffBoresightAngle | 90 | Max launch angle (degrees) |
| MaxTurnRate | 12 | PID turn rate limit (vanilla ~3) |
| TorqueMultiplier | 3 | Torque multiplier for IR missiles |
| LOALSearchAngle | 90 | Seeker search cone for LOAL |
| LOALSearchTime | 8 | Seconds to search before going ballistic |
| PrelaunchCueAngle | 12 | Center-screen cone used to pick pre-launch seeker candidate |
| GrowlVolume | 0.6 | Growl master volume (0-1) |
| EnableWavProfileAudio | true | Load seeker audio from WAV files |
| WavProfileFolder | SeekerNoises | Folder next to mod DLL containing WAV clips |
| WavStandbyFile | Aim9Caged.wav | Standby seeker hum WAV |
| WavLockFile | Aim9UnCaged.wav | Lock screech WAV |
| WavFlaredLockFile | Aim9UncagedFlared.wav | Flare-contaminated lock WAV |
| FallbackToSyntheticAudio | true | Use generated tones if WAV files fail/missing |
| LaunchMuteSeconds | 0.5 | Growl mute duration immediately after IR launch |
| ShowDetectionPercentDebug | false | Show seeker detection percentage in HUD (debug) |

## WAV Profile Audio

- Place WAV files in a folder next to [BetterIR/AIM9XMod/bin/Release/net472/AIM9XMod.dll](BetterIR/AIM9XMod/bin/Release/net472/AIM9XMod.dll), for example:
   - `BepInEx/plugins/SeekerNoises/Aim9Caged.wav`
   - `BepInEx/plugins/SeekerNoises/Aim9UnCaged.wav`
   - `BepInEx/plugins/SeekerNoises/Aim9UncagedFlared.wav`
- Audio logic:
   - Uses thresholded blending by detection percentage:
      - `0% - 30%`: pure `Aim9Caged.wav`
      - `30% - 60%`: linear blend between caged and uncaged
      - `60% - 100%`: pure uncaged side
   - Uses `Aim9UncagedFlared.wav` as the uncaged side of the blend when flares are inside the detection cone.
   - Plays no seeker audio while a launched player IR missile is in flight.
- If loading fails and fallback is enabled, the mod automatically reverts to synthetic tones.

## Behavior Notes

- Before launch, the mod evaluates targets around center-screen and provides a growl cue for the best candidate.
- On launch without lock (LOAL), the missile steers along player view direction while scanning.
- During LOAL scan, the best candidate is reported back to the cue system so tone/overlay indicate likely lock outcome.
- Once lock is acquired, the overlay and growl confidence peak briefly, then naturally decay if no fresh cue is produced.
