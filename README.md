# AIM-9X IR Missile Overhaul — Nuclear Option BepInEx Mod

Adds AIM-9X-style behavior to IR-seeking missiles in Nuclear Option:

- **90° seeker off-boresight + 180° launch gate** — fire well outside the vanilla arc; launches beyond the seeker cone leave the rail in LOAL
- **Enhanced turning** — higher PID turn rate and torque for much tighter IR missile maneuvering
- **Lock-On After Launch (LOAL) for MMR-S3** — MMR-S3 missiles can search after launch, reacquire after losing lock, and keep flare-evasion memory
- **View-slaved seeker cueing** — pre-launch growl/overlay follows either the selected target or the pilot view direction
- **Seeker overlay** — center-screen rings plus target diamonds show cue quality, launch-assignable targets, and flare-contaminated wobble
- **Ripple-shot target assignment** — multi-target HUD target lists are distributed across successive launches when possible

## Building

1. Add the bepinex nuget source
   ```
   dotnet nuget add source https://nuget.bepinex.dev/v3/index.json --name bepinex
   ```

2. Build:
   ```
   dotnet build AIM9XMod/AIM9XMod.csproj -c Release
   ```

3. Copy `AIM9XMod/bin/Release/net472/AIM9XMod.dll` and the `SeekerNoises` directory to `BepInEx/plugins/`

## Unit Tests

Run unit tests for extracted seeker math logic:

```
dotnet test AIM9XMod.Tests/AIM9XMod.Tests.csproj -c Release
```

## Installation

1. Build the mod.
2. Copy `AIM9XMod.dll` into `BepInEx/plugins/`.
3. Copy the `SeekerNoises` directory next to the DLL, for example `BepInEx/plugins/SeekerNoises/`.
4. Start the game once to generate `BepInEx/config/com.modder.aim9xmod.cfg`.

## Configuration

After first run, edit `BepInEx/config/com.modder.aim9xmod.cfg` or use an in-game config editor.

| Section | Setting | Default | Description |
|---------|---------|---------|-------------|
| Features | EnableHighOffBoresight | true | Enables widened IR launch geometry. |
| Features | EnableEnhancedTurning | true | Enables AIM-9X-style turn performance. |
| Features | UsePeakIRThreshold | false | Uses the highest observed target IR, instead of IR at flare evasion time, for reacquisition blocking. |
| Features | EnableViewSlaving | true | Steers player-fired LOAL missiles toward the current view direction while scanning. |
| Features | EnableSeekerGrowl | true | Enables the seeker audio and overlay system. |
| IRM-S1 | OffBoresightAngle | 50 | IRM-S1 seeker cone half-angle in degrees. |
| IRM-S1 | FiringGateAngle | 70 | IRM-S1 maximum launch angle in degrees. |
| IRM-S1 | MaxTurnRate | 6 | IRM-S1 PID turn-rate limit. |
| IRM-S2 | OffBoresightAngle | 70 | IRM-S2 seeker cone half-angle in degrees. |
| IRM-S2 | FiringGateAngle | 100 | IRM-S2 maximum launch angle in degrees. |
| IRM-S2 | MaxTurnRate | 9 | IRM-S2 PID turn-rate limit. |
| MMR-S3 | OffBoresightAngle | 90 | MMR-S3 seeker cone half-angle in degrees. |
| MMR-S3 | FiringGateAngle | 180 | MMR-S3 maximum launch angle in degrees. |
| MMR-S3 | MaxTurnRate | 12 | MMR-S3 PID turn-rate limit. |
| MMR-S3 | EnableLOAL | true | Enables lock-on-after-launch and reacquisition behavior for MMR-S3 only. |
| Turning | TorqueMultiplier | 3 | Multiplier applied to IR missile torque. |
| LOAL | SearchAngle | 90 | Missile seeker search cone half-angle during LOAL. |
| LOAL | SearchTime | 8 | Maximum LOAL search time before the missile goes ballistic. |
| Cueing | PrelaunchCueAngle | 12 | Center-screen cueing cone half-angle for pre-launch candidate selection. |
| Audio | GrowlVolume | 0.1 | Master seeker audio volume. |
| Audio | EnableWavProfileAudio | true | Loads seeker audio from WAV files next to the plugin DLL. |
| Audio | WavProfileFolder | SeekerNoises | Folder containing seeker WAV clips. |
| Audio | WavStandbyFile | Aim9Caged.wav | Standby / caged seeker clip. |
| Audio | WavLockFile | Aim9UnCaged.wav | Lock / uncaged seeker clip. |
| Audio | WavFlaredLockFile | Aim9UncagedFlared.wav | Lock clip used when flares are inside the detection cone. |
| Audio | FallbackToSyntheticAudio | true | Falls back to generated tones if no WAV clips load. |
| Audio | LaunchMuteSeconds | 0.5 | Temporarily mutes growl after detecting a player IR launch. |
| Debug | ShowDetectionPercentDebug | false | Shows seeker percentage text in the HUD and emits seeker-state debug logs. |
| Debug | ShowLoalTargetDebug | false | Logs verbose LOAL target assignment and scan decisions. |
| Debug | ShowOffBoresightAngleDebug | false | Draws current viewed off-boresight angle vs selected missile off-boresight angle in the HUD. |
| Overlay | WobbleMaxOffset | 14 | Maximum pixel wobble of the target diamond at low detection strength. |
| Overlay | WobbleSpeed | 1.3 | Speed multiplier for overlay diamond wobble. |

## WAV Profile Audio

- WAV files are loaded relative to the installed plugin DLL, for example:
  - `BepInEx/plugins/SeekerNoises/Aim9Caged.wav`
  - `BepInEx/plugins/SeekerNoises/Aim9UnCaged.wav`
  - `BepInEx/plugins/SeekerNoises/Aim9UncagedFlared.wav`
- Blend logic:
  - `0% - 30%` detection: pure caged tone
  - `30% - 60%` detection: linear blend between caged and lock tones
  - `60% - 100%` detection: pure lock tone
- If flares are inside the detection cone, the mod uses the flared lock clip for the uncaged side of the blend.
- If no WAV clips load:
  - with `FallbackToSyntheticAudio = true`, generated tones are used
  - with `FallbackToSyntheticAudio = false`, seeker audio stays silent

## Behavior Notes

- Selected targets inside the aircraft forward cone get pinned pre-launch seeker feedback instead of center-screen scanning.
- If a hard-locked target is outside the seeker cone, pre-launch feedback is suppressed and the shot remains LOAL-oriented.
- Player-fired LOAL missiles can steer toward the current camera view while they search.
- During LOAL, MMR-S3 missiles prefer their assigned target first, then fall back to the best in-cone IR source.
- Successful flare breaks create a relock threshold so the missile will not reacquire until the target's IR output rises above that threshold.
- The overlay can draw multiple target diamonds for launch-assignable HUD targets, and low-confidence or flare-contaminated cues wobble.

## Debugging

- `ShowDetectionPercentDebug = true` adds seeker strength text to the overlay and periodic `[SeekerDebug]` / `[SeekerState]` logs.
- `ShowLoalTargetDebug = true` adds `[LOAL-DBG]` logs covering launch assignment, candidate rejection reasons, and preferred-target overrides.
- `ShowOffBoresightAngleDebug = true` draws a HUD debug readout and gauge for current viewed off-boresight angle vs selected missile off-boresight angle.
