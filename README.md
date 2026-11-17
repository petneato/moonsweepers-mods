# Moon Sweepers mods

Unofficial BepInEx mods and tools for Moon Sweepers (Steam, 1.0.6). Started as conveyor-logic enhancements, now also
covers the J.O.O.N.A.S. cleaning robots.

| Mod | What it does | Status |
|---|---|---|
| [Smart Splitters](src/SmartSplitters) | Splitters skip full outputs instead of stalling; chained splitters split evenly (3/4/5-way…); per-splitter modes Smart / Overflow / Sort / Vanilla (F6/F7) | Tested in single-player |
| [Joonas Overdrive](src/JoonasOverdrive) | Upgrade in the Cleaning tree: 4 levels, +25/50/100/150 % J.O.O.N.A.S. cleaning, travel and turn speed | In testing |
| [Baby Joonas](src/BabyJoonas) | Small helper robots that clean the inner rings of their parent's sector ([design](src/BabyJoonas/DESIGN.md)) | Prototype, off by default |
| [Dev Menu Unlock](src/DevMenuUnlock) | Shows the game's hidden Dev tab in the pause menu settings (time of day, time speed, …) | Tested |
| [Robot Logger](src/RobotLogger) | Read-only: once a second logs every cleaning robot's state to `BepInEx/RobotLog.csv` | Diagnostic |
| [Splitter Probe](src/SplitterProbe) | Logs belt hand-offs and splitter decisions to a CSV | Diagnostic; crashed once while a splitter was placed — don't build with it running |

Tools:

- `tools/save/fix_belts.py` — removes given capsules from a `session.moon` save (used to clear wrong cargo stuck on
  belts). Close the game and back up the save first.
- `tools/save/analyze_belts.py` — lists splitters/mergers, belt contents and mixed-cargo belts from a save.
- `tools/analysis/robot_summary.py`, `robot_sectors.py` — summarise `RobotLog.csv`.

What we learned about the game's internals: [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md).

## Requirements

- Moon Sweepers 1.0.6 (Steam)
- [BepInEx 6.0.0-be.791](https://builds.bepinex.dev/projects/bepinex_be) (Unity.IL2CPP, win-x64)
- The MoonSweepers BepInEx patcher ([Nexus mod #9](https://www.nexusmods.com/moonsweepers/mods/9))

## Install

Build (below) or download a release, then copy each mod's DLL to `<game>\BepInEx\plugins\<ModName>\`.
Removing a mod's folder uninstalls it. None of the mods change save files; owned levels of the Joonas Overdrive upgrade
are stored by the game under the upgrade id and are lost (money spent) if the mod is removed.

## Smart Splitters in short

- Every splitter starts in **Smart** mode: a full output is skipped, and splitters connected output → input form one
  N-way splitter (2 = 3-way, 3 = 4-way, 4 = 5-way) with an equal share per final output.
- Look at a splitter: **F6** cycles Smart → Overflow → Sort dust → Sort sand → Sort stone → Vanilla, **F7** swaps the main
  side. A label at the top of the screen shows the mode.
- It sets the game's own splitter turn counter just before the hand-off; a start-up self-check falls back to vanilla
  if control can't be verified. Settings: `BepInEx\config\moonsweepers.smartsplitters.cfg`,
  modes per splitter: `BepInEx\config\SmartSplitters\settings.json`.

## Build

Requires the .NET SDK (6 or newer) and a game install where BepInEx has run once (so `BepInEx\interop` exists).

```
dotnet build src/SmartSplitters -c Release
```

Each project references the game from the default Steam folder; override with
`-p:GameDir="D:\Games\Moon Sweepers"`. No game files or generated interop assemblies are in this repository.

## Status and compatibility

Single-player on 1.0.6. Co-op is untested. A game update may change the internals these mods rely on.

## AI disclosure

The code in this repository was written with an AI assistant (Claude) and tested in-game by the repository owner.

## License

MIT, see [LICENSE](LICENSE). Moon Sweepers is © DragDrop Games; these are unofficial fan mods.
