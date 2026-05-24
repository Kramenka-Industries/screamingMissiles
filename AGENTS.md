# Agent Notes

## Build prerequisites

- Before running `dotnet build`, add the BepInEx NuGet source:
  - `dotnet nuget add source https://nuget.bepinex.dev/v3/index.json --name bepinex`

## Debug: LOAL diamond-target priority

To trace why a missile ignores the diamond-marked target set `ShowLoalTargetDebug = true` in
`BepInEx/config/com.modder.aim9xmod.cfg` (under `[Debug]`) before launching a missile.

Log lines prefixed `[LOAL-DBG]` will appear in the BepInEx console and show:

- Contents of `CombatHUD.targetList` at launch time
- Which assignment path succeeded (HUD-selected, HUD-list, or prelaunch cue) and with what target
- The final preferred target assigned to the missile
- Per-check rejection reasons during the in-flight seeker scan (angle, range, LOS, IR heat, flare)
- Whether the eventual lock matched the preferred target or overrode it
