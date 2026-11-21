# Moon Sweepers mods

Unofficial BepInEx mods for Moon Sweepers (Steam, 1.0.6):
- splitters that don't stall
- faster J.O.O.N.A.S. cleaning robots, and small helper robots for them
- a fix that keeps the game's cleaning work from backing up when you run many robots

| Mod | Version | What it does | Status |
|---|---|---|---|
| [Smart Splitters](src/SmartSplitters) | 1.2.0 | Splitters skip a full output instead of stalling. Chained splitters split evenly (3-way, 4-way, 5-way…). Each splitter has a mode: Smart, Overflow, Sort dust/sand/stone or Vanilla | Tested |
| [Joonas Overdrive](src/JoonasOverdrive) | 0.2.1 | Upgrade in the Cleaning tab, 4 levels: J.O.O.N.A.S. cleans, drives and turns 25 / 50 / 100 / 150 % faster | Tested (0.2.1 only logs less) |
| [Baby J.O.O.N.A.S.](src/BabyJoonas) | 1.6.0 | Upgrade in the Cleaning tab, 3 tiers: 1–3 small helper robots per J.O.O.N.A.S. They clean the middle of its sector, take over lanes it hasn't reached yet, and clean up dirt it leaves behind | 1.5 tested for 18 h; cleaning up left-behind dirt is reworked in 1.6 and not yet played |
| [Cleaning Catch-Up](src/CleaningCatchUp) | 0.7.0 | Keeps the game's cleaning queue from backing up. J.O.O.N.A.S. strokes over ground that is already clean are not sent (about half of them late in a session). Cleaning also gets more time while the game runs in the background | 0.6 tested for 18 h; 0.7 changes when skipping switches off for a robot |
| [Dev Menu Unlock](src/DevMenuUnlock) | 1.1.1 | Shows the game's hidden Dev tab in the pause-menu settings (time of day, time speed, …) | Tested |

Optional diagnostics write CSV files for analysis:
- [Perf Logger](src/PerfLogger) 0.1.0
- [Robot Logger](src/RobotLogger) 0.2.1
- [Splitter Probe](src/SplitterProbe) 0.1.0 (source only)

## Requirements

- Moon Sweepers 1.0.6 (Steam)
- [BepInEx 6.0.0-be.791](https://builds.bepinex.dev/projects/bepinex_be) (Unity.IL2CPP, win-x64)
- The MoonSweepers BepInEx patcher ([Nexus mod #9](https://www.nexusmods.com/moonsweepers/mods/9)), which makes BepInEx work with this game's IL2CPP version

Start the game once with BepInEx installed, so it can generate `BepInEx\interop`.

## Install

1. Download this repository: **Code → Download ZIP**.
2. From the ZIP, copy `dist\BepInEx` into the game folder (`…\steamapps\common\Moon Sweepers`) and merge it with the `BepInEx` folder that is already there.
   - Each mod is its own folder under `BepInEx\plugins`. Leave out any you don't want.
   - The diagnostics are in `dist\diagnostics\BepInEx` and are only needed for troubleshooting.
3. Start the game. Each mod creates its settings file on first start: `BepInEx\config\moonsweepers.<mod>.cfg`. Every setting is described in the file.

To uninstall a mod, delete its folder from `BepInEx\plugins`. The mods don't edit your save files.

The two upgrades (Joonas Overdrive, Baby J.O.O.N.A.S.) work like the game's own upgrades: the game saves the level you bought under the upgrade's id. If you remove the mod, the upgrade stops doing anything, and the money spent on it is not refunded.

## The mods in short

### Smart Splitters

- Every splitter starts in **Smart** mode. If one output is full, the capsule goes to the other output instead of waiting.
- Splitters connected output → input act as one N-way splitter, with an equal share for each final output:
  - 2 splitters = 3-way
  - 3 splitters = 4-way
  - 4 splitters = 5-way
- Look at a splitter and press **F6** to change its mode. The modes cycle Smart → Overflow → Sort dust → Sort sand → Sort stone → Vanilla.
- **F7** swaps the main side (for the Overflow and Sort modes). A label at the top of the screen shows the current mode.
- The mod steers the game's own splitter turn counter just before each hand-off, so no game logic is replaced. A start-up self-check falls back to vanilla behaviour if it can't confirm that steering works.
- Settings: `moonsweepers.smartsplitters.cfg` (keys, default mode). Modes you set per splitter are stored in `BepInEx\config\SmartSplitters\settings.json`.

### Joonas Overdrive

- Buy it in the upgrade terminal, on the **Cleaning** tab. It unlocks like the first cleaning upgrade.
- Levels cost $20k / 40k / 65k / 100k and add +25 / 50 / 100 / 150 % to cleaning speed, travel speed, turning and acceleration.
- It also speeds up the J.O.O.N.A.S. scrub motion, which is what actually sets its cleaning pace.
- Bonuses and prices can be changed in `moonsweepers.joonasoverdrive.cfg`.

### Baby J.O.O.N.A.S.

- Buy **Baby J.O.O.N.A.S.** in the upgrade terminal, on the **Cleaning** tab. Tiers cost $30k / 60k / 100k and give 1 / 2 / 3 babies per J.O.O.N.A.S.
- Babies are 35 % scale copies of their parent. Their dust goes to the parent, which fires it at catchers as usual.
- **What they clean:**
  - When the parent starts a sector, it keeps the outer ring and the babies sweep the disc inside it in arcs. The inner lanes are removed from the parent's plan.
  - Idle babies take over the parent's last lanes.
  - Idle babies also clean the dirt the parent left in sectors it has finished: next to structures, under conveyors, or spots it gave up on.
- Babies skip ground that is already clean. They hold back while the game's cleaning queue is busy, so they never slow their parent down.
- They have no collision, so they can drive through structures. They aren't saved; they are recreated when the world loads.
- Settings: `moonsweepers.babyjoonas.cfg` (prices, size, speed, cleaning width, stroke shape, which jobs they take).
- Turn the whole mod off with `General.Enabled = false`, or make babies visual-only with `General.Clean = false`.
- Co-op is untested. Babies stay off when you join someone else's game.

### Cleaning Catch-Up

The game applies the robots' cleaning strokes from a queue, and gives them a limited share of each frame (about a fifth). With many robots that share is used up in every frame:
- strokes pile up
- the game slows the robots down
- frame rate suffers

This mod helps in three ways:
- **Clean-ground skipping.** Each J.O.O.N.A.S. stroke is checked against the game's own dust map at 19 points. If they are all clean, the stroke isn't queued, and the robot is told it finished with 0 kg, exactly as the game would report it.
  - In long sessions 40–50 % of J.O.O.N.A.S. strokes went over clean ground.
  - Spot-checks measure the dust this costs: 0.2–0.7 % of the total.
  - A robot whose skipping would cost it more than 2 % of its dust pauses skipping for a while.
  - If the check looks wrong for a robot, skipping stops for that robot for the session.
- **Background boost.** When the game runs in the background (capped at 30 fps), the game's 2 ms cleaning budget is the limit. The mod raises it while strokes are waiting, so robots keep their pace when you alt-tab.
- **A once-a-minute report** in `BepInEx\LogOutput.log`: queue length, strokes skipped and the dust estimate, and what each kind of stroke costs.

Settings: `moonsweepers.cleaningcatchup.cfg`.

### Dev Menu Unlock

Pause menu → Settings → **Dev**. This is the game's own developer page (time of day, time speed and more), hidden in release builds.

## Diagnostics and tools

- **Perf Logger:** once a second, writes frame timing, frame cap/focus/menu state and world load to `BepInEx\PerfLog.csv`. Stops after 4 hours. Summarise it with `tools/analysis/perf_summary.py`.
- **Robot Logger:** once a second, writes every cleaning robot's state to `BepInEx\RobotLog.csv`. Stops after 30 minutes, and the file gets large with many robots. Summarise it with `tools/analysis/robot_summary.py` and `robot_sectors.py`.
- **Splitter Probe:** logs belt hand-offs and splitter decisions. There is no DLL in `dist`: it crashed once while a splitter was being placed.
- **Save tools:** `tools/save/fix_belts.py` removes given capsules from a `session.moon` save (used to clear wrong cargo stuck on belts). `tools/save/analyze_belts.py` lists splitters, mergers and belt contents. Close the game and back up the save first.

What we learned about the game's internals: [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md).

## Build from source

You need the .NET SDK (6 or newer) and a game install where BepInEx has run once.

```
dotnet build src/SmartSplitters -c Release
```

The output is `src/<Mod>/bin/Release/net6.0/MoonSweepers.<Mod>.dll`. Each project references the game in the default Steam folder; to use another folder, add `-p:GameDir="D:\Games\Moon Sweepers"`. No game files or generated interop assemblies are in this repository.

## Compatibility

Tested in single-player on 1.0.6. Co-op is untested. A game update may change the internals these mods rely on. The mods catch and log their own errors, and where something they need is missing they leave the game's own behaviour in place.

## AI disclosure

The code in this repository was written with an AI assistant (Claude) and tested in-game by the repository owner.

## License

MIT, see [LICENSE](LICENSE). Moon Sweepers is © DragDrop Games. These are unofficial fan mods.
