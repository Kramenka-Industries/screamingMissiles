# ScreamingMissiles — Nuclear Option BepInEx Mod

![ScreamingMissiles hero](https://github.com/user-attachments/assets/98ee4a19-6573-4bfd-b3c9-90f720be4e1f)

ScreamingMissiles overhauls IR missile behavior in Nuclear Option with wider launch envelopes, LOAL search/reacquire logic, stronger turn authority, seeker cue overlays, and configurable seeker audio.

- **High off-boresight + wide launch gates** — launch well outside vanilla arcs; shots outside seeker cone can still leave the rail in LOAL.
- **Enhanced turning + motor tuning** — tune PID turn rate, torque multiplier, thrust, burn time, and fuel mass per missile family.
- **MMR-S3 LOAL + reacquisition memory** — post-launch search with configurable timing/angles and flare-evasion relock behavior.
- **View-slaved seeker cueing** — pre-launch growl/overlay follows selected target or pilot view direction.
- **Seeker overlay + ripple assignment** — center-screen rings and target diamonds visualize cue quality and launch assignment.

## Building

1. Add the bepinex nuget source
   ```
   dotnet nuget add source https://nuget.bepinex.dev/v3/index.json --name bepinex
   ```

2. Build:
   ```
   dotnet build AIM9XMod.csproj -c Release
   ```

3. Copy `bin/Release/net472/ScreamingMissiles.dll` and the `SeekerNoises` directory to `BepInEx/plugins/`

## Unit Tests

Run unit tests for extracted seeker math logic:

```
dotnet test AIM9XMod.Tests/AIM9XMod.Tests.csproj -c Release
```

## Installation

1. Build the mod.
2. Copy `ScreamingMissiles.dll` into `BepInEx/plugins/`.
3. Copy the `SeekerNoises` directory next to the DLL, for example `BepInEx/plugins/SeekerNoises/`.
4. Start the game once to generate `BepInEx/config/com.modder.aim9xmod.cfg`.

## Configuration

After first run, edit `BepInEx/config/com.modder.aim9xmod.cfg` or use an in-game config editor.

| Section | Setting | Default | Description |
|---------|---------|---------|-------------|
| Features | EnableHighOffBoresight | true | Enables widened IR launch geometry and high off-boresight behavior. |
| Features | EnableEnhancedTurning | true | Enables higher-turn-performance IR missile handling. |
| Features | UsePeakIRThreshold | false | Uses the highest observed target IR for reacquisition blocking instead of the IR value at flare evasion time. |
| Features | EnableViewSlaving | true | Steers player-fired no-lock missiles toward current view direction while scanning. |
| Features | EnableSeekerGrowl | true | Enables pre-launch seeker cue overlay and growl feedback. |
| IRM-S1 | OffBoresightAngle | 50 | IRM-S1 seeker cone half-angle in degrees. |
| IRM-S1 | FiringGateAngle | 70 | IRM-S1 maximum launch angle in degrees. |
| IRM-S1 | MaxTurnRate | 6 | IRM-S1 PID turn-rate limit. |
| IRM-S1 | Flare rejection factor | 1.75 | Flare rejection multiplier for IRM-S1 lock logic. |
| IRM-S1 | Motor thrust | 2750 | IRM-S1 motor thrust in newtons. |
| IRM-S1 | Motor burn time | 2.0 | IRM-S1 motor burn duration in seconds. |
| IRM-S1 | Fuel mass | 4.0 | IRM-S1 fuel mass in kilograms. |
| IRM-S2 | OffBoresightAngle | 70 | IRM-S2 seeker cone half-angle in degrees. |
| IRM-S2 | FiringGateAngle | 100 | IRM-S2 maximum launch angle in degrees. |
| IRM-S2 | MaxTurnRate | 9 | IRM-S2 PID turn-rate limit. |
| IRM-S2 | Flare rejection factor | 2.0 | Flare rejection multiplier for IRM-S2 lock logic. |
| IRM-S2 | Motor thrust | 18000 | IRM-S2 motor thrust in newtons. |
| IRM-S2 | Motor burn time | 2.0 | IRM-S2 motor burn duration in seconds. |
| IRM-S2 | Fuel mass | 25.0 | IRM-S2 fuel mass in kilograms. |
| MMR-S3 | OffBoresightAngle | 90 | MMR-S3 seeker cone half-angle in degrees. |
| MMR-S3 | FiringGateAngle | 180 | MMR-S3 maximum launch angle in degrees. |
| MMR-S3 | MaxTurnRate | 12 | MMR-S3 PID turn-rate limit. |
| MMR-S3 | EnableLOAL | true | Enables lock-on-after-launch and reacquisition behavior for MMR-S3 only. |
| MMR-S3 | Flare rejection factor | 3.0 | Flare rejection multiplier for MMR-S3 lock logic. |
| MMR-S3 | Motor thrust | 40000 | MMR-S3 motor thrust in newtons. |
| MMR-S3 | Motor burn time | 2.2 | MMR-S3 motor burn duration in seconds. |
| MMR-S3 | Fuel mass | 40.0 | MMR-S3 fuel mass in kilograms. |
| Turning | TorqueMultiplier | 3 | Multiplier applied to IR missile torque. |
| LOAL | SearchAngle | 90 | Missile seeker search cone half-angle during LOAL. |
| LOAL | SearchTime | 8 | Maximum LOAL search time before missile goes ballistic. |
| Cueing | PrelaunchCueAngle | 12 | Center-screen cueing cone half-angle for pre-launch candidate selection. |
| Audio | GrowlVolume | 0.1 | Master seeker audio volume. |
| Audio | EnableWavProfileAudio | true | Loads seeker audio from WAV files next to plugin DLL. |
| Audio | WavProfileFolder | SeekerNoises | Folder containing seeker WAV clips. |
| Audio | WavStandbyFile | Aim9Caged.wav | Standby/caged seeker clip filename. |
| Audio | WavLockFile | Aim9UnCaged.wav | Lock/uncaged seeker clip filename. |
| Audio | WavFlaredLockFile | Aim9UncagedFlared.wav | Lock clip used when flares are inside detection cone. |
| Audio | FallbackToSyntheticAudio | true | Falls back to generated tones if no WAV clips load. |
| Audio | LaunchMuteSeconds | 0.5 | Temporarily mutes growl after player IR launch detection. |
| Debug | ShowDetectionPercentDebug | false | Shows seeker percentage text in HUD and emits seeker debug logs. |
| Debug | ShowLoalTargetDebug | false | Logs verbose LOAL target assignment and scan decisions. |
| Debug | ShowOffBoresightAngleDebug | false | Draws viewed off-boresight angle vs selected missile limits in HUD. |
| Overlay | WobbleMaxOffset | 14 | Maximum pixel wobble of target diamonds at low detection strength. |
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
