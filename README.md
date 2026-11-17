# moonswprsConveyorLogicEnhance

Enhances the conveyor logic for conveyor chains in Moon Sweepers.

**Smart Splitters** is a BepInEx mod that changes how splitters pick an output for each capsule:

- **No more stalls.** The stock splitter alternates strictly between its two outputs and *waits* when the output whose turn it is is backed up, so one full belt stops the whole splitter. In Smart mode a full output is skipped.
- **3-way, 4-way, 5-way… splitters.** Connect splitters output → input and the chain is treated as one splitter with an even share for every final output: 2 splitters = 3-way, 3 = 4-way, 4 = 5-way. The stock game would give 50/25/25 % for a 3-way chain; with the mod it is 33/33/33 %.
- **Per-splitter modes** (look at a splitter, press a key):

| Mode | Behaviour |
|---|---|
| Smart (default) | Even share over all final outputs of a chain, skips full outputs |
| Overflow | Everything to the main side; the other side only when the main side is backed up |
| Sort dust / sand / stone | That cargo to the main side, everything else to the other side |
| Vanilla | The game's own strict alternation |

Splitters stay ordinary splitters in your save. Removing the mod returns them to vanilla behaviour; nothing in the save depends on it.

## Requirements

- Moon Sweepers 1.0.6 (Steam, IL2CPP, Unity 6000.6)
- [BepInEx 6.0.0-be.791](https://builds.bepinex.dev/projects/bepinex_be) (Unity.IL2CPP, win-x64)
- The MoonSweepers BepInEx patcher ([Nexus mod #9](https://www.nexusmods.com/moonsweepers/mods/9)), which makes BepInEx work with the game's metadata version 108

## Install

Copy `MoonSweepers.SmartSplitters.dll` into `<game>\BepInEx\plugins\MoonSweepers.SmartSplitters\` and start the game.

## Use

| Key | Action |
|---|---|
| F6 | Look at (or stand next to) a splitter: cycle its mode |
| F7 | Swap which side is the main output (Overflow / Sort) |

A label at the top of the screen shows the mode of the splitter you are looking at. Modes are remembered per splitter in `BepInEx\config\SmartSplitters\settings.json`. Keys, the default mode and the label can be changed in `BepInEx\config\moonsweepers.smartsplitters.cfg`.

The log (`BepInEx\LogOutput.log`) shows the detected chains and, once a minute, how many capsules each splitter sent left and right.

## How it works

Findings from instrumenting the game (see the comments in `Plugin.cs`):

- A splitter is machine kind 11 with input port 0 and output ports 1 and 2. Connections are encoded as `route = machine * 16 + input * 4 + output`; the splitter's internal lanes use `-route` as their belt key.
- When a capsule leaves the splitter's input belt (`MoonWorld.HandOff`), the game picks a lane from `MoonWorld.machineTurns[machine]` and flips the counter. If that lane is full (`MoonWorld.RouteFull`) the splitter waits.
- The mod sets `machineTurns` just before that hand-off, so the game's own code moves the capsule — no game logic is replaced. Chains are found by following belts from a splitter's output lanes to the next splitter's entrance, and a smooth weighted round-robin keeps each final output's share equal.
- On start it first watches unmodified splitters to learn how the counter maps to lanes, then verifies that capsules sent against the game's own choice really go there. If that check fails, the mod leaves all splitters on vanilla behaviour for the session.
- Steering only runs on the main thread and pauses while machines or belts are being built or removed.

## Build

Requires the .NET SDK (6 or newer) and a game install with BepInEx already run once (so `BepInEx\interop` exists).

```
dotnet build src/SmartSplitters -c Release
```

If the game is not in the default Steam folder: `dotnet build src/SmartSplitters -c Release -p:GameDir="D:\Games\Moon Sweepers"`.

No game files or generated interop assemblies are included in this repository; they are referenced from your own install at build time.

## Status

Tested in single-player on 1.0.6: splitter steering (Smart mode, chains up to 6-way), mode keys and the on-screen label. Co-op is untested (the host's splitters decide; guests probably need the mod too for consistent results).

## License

MIT, see [LICENSE](LICENSE). Moon Sweepers is © DragDrop Games; this is an unofficial fan mod.
