# Moon Sweepers internals (1.0.6) — what we found

Notes from instrumenting Moon Sweepers 1.0.6 (Unity 6000.6, IL2CPP, metadata v108) with BepInEx. Method bodies are
native, so behaviour was inferred from member names, logging probes and save files. Treat it as observations, not docs.

## Modding environment

- BepInEx 6.0.0-be.791 Unity.IL2CPP plus the MoonSweepers patcher (Nexus mod #9), which generates interop assemblies
  for metadata v108.
- **Input:** the game uses the Unity Input System. Read keys with `UnityEngine.InputSystem.Keyboard.current`; the legacy
  `Input`/BepInEx `UnityInput` path never sees key presses.
- **UI:** IMGUI (`OnGUI`) labels were not visible in testing. A uGUI canvas with TextMeshPro works.
- **Do not write "static tuning values" such as `MoonRobotVacuum.LaneSpacing`.** They are compile-time constants exposed
  as static fields without storage; the interop setter crashed the game instantly (access violation in
  GameAssembly.dll) and the value read back unchanged. Instance fields (e.g. `cleaningSpeed`) and real statics
  (e.g. `MoonUpgradeTree.Nodes`) are fine.
- Harmony hooks on hot paths should check they run on the main thread and should not read objects that are being
  built or removed (an early probe crashed while a splitter was being placed).

## Conveyors and splitters

- Cargo: `MoonCargo` = Dust 0, Sand 1, Stone 2.
- Machines are `MoonDepotSnapshot` (`id`, `kind`, `x/y/z`). Splitter = kind 11 (input port 0, outputs 1 and 2),
  merger = kind 12 (inputs 0 and 1, output 2).
- Routes: `route = machine * 16 + input * 4 + output` (`MoonWorld.MachineRoute`). A machine's internal lanes are keyed
  `-route` in `MoonWorld.riders.lanes`; belts are keyed by their conveyor id.
- `MoonWorld.HandOff(belt, path, id)` moves the capsule at the end of a belt/lane on. For a splitter's input belt that is
  where the lane is chosen from `MoonWorld.machineTurns[machine]`, which flips after every capsule.
- **Stock splitter = strict alternation that waits:** if the output whose turn it is is full (`RouteFull`), the splitter
  stalls even if the other side is free. Measured: equal counts per output (e.g. 154/156, 343/348).
- Machines (stone press) sort capsules into slots by cargo type, not by input port, so a wrong capsule slows a press but
  does not jam it permanently.
- Production numbers (wiki, confirmed where the game logs them): pressure chamber 5 kg / 12 s, stone press 1 dust +
  1 sand → 2 kg block / 6 s, belts 1.1 m/s with 0.75 m spacing, sources emit 1 capsule / 0.35 s.

## Cleaning robots (J.O.O.N.A.S.)

- `MoonRobotDock.all` → `dock.Robot` (`MoonRobotVacuum`). J.O.O.N.A.S.: cleaning width 5.6 m, cleaning speed 2 m/s,
  travel 3 m/s, turn 120°/s, 60 kg capacity, fires 20 kg capsules at catchers while working.
- States: Docked, Unloading, Waiting, Leaving, Travel, Aligning, Cleaning, Checking, Returning, Turning, Entering,
  Launching.
- A sector (~18 × 14 m) is cleaned as concentric rings, outer ring first: ~13 lane segments in 3 rows
  (outer ~60 % of cleaning time, middle ~29 %, centre ~3 %).
- Measured with 18 robots over 9.4 min: 70 % cleaning, 21 % travel, 8 % aligning, ~0 % idle/stuck; ~86 s per sector,
  ~98 % of dirty cells cleaned, 11–22 kg/min per robot. Average speed while cleaning ~1.3 m/s (short passes, turning).
- Robots clean through `MoonWorld.QueueToolStroke(...)`; see `src/BabyJoonas/DESIGN.md`.

## Save files

- `%USERPROFILE%\AppData\LocalLow\DragDropGames\MoonSweepers\MoonSessions\slot-N\<hash>\session.moon`:
  `1PCM` + uint32 raw length + byte 64 + sha256(raw) hex + gzip(raw). `chunk_*.moon` files are terrain.
- Raw data is written BinaryWriter-style: canister list (`int nextId, int count`, 67-byte records), cargo-kind map
  (`int count`, `{int canisterId, byte kind}`, absent = dust), structures (42-byte records), belts (58 bytes + byte
  waypoint count + 24 bytes per waypoint + 1 byte).
- Riders are not stored per belt; a capsule's belt is found by its position.
- The game autosaves; close it before editing a save.
