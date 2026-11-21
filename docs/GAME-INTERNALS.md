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
- Reading those constants through the interop getters is fine (they return the compiled value).
- Il2CppInterop warns at start-up about injected `MonoBehaviour` methods whose parameter or return types it can't
  map. Mark such methods `[HideFromIl2Cpp]`.
- IL2CPP arrays (`Il2CppStructArray<T>`) can be read directly. The element data starts `4 * IntPtr.Size` bytes after
  `.Pointer` (object header, bounds, length). This is much cheaper than the indexer when scanning thousands of entries.

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
- Each J.O.O.N.A.S. cleans with 6 scrub pads. Each pad stroke is 0.92 m wide with 0.05 m soft edges and about 1 m long.
  Stroke ids are `strokePlayer + k × 0x01010101` (k = 1–6).
- **Sector maps.** `MoonRobotVacuum.map` (`MoonRobotDustMap`) covers only the sector being worked on: ~960 ground points,
  1 m apart.
  - Per point it holds `kind[]` (`Kind.Dusty` = ground that holds dust), `dirty[]` (above `DirtyThreshold` 0.03),
    `inReach[]` vs `reachStamp`, and `passes[]`.
  - The robot leaves a point alone after `PassesBeforeIgnoring` = 5 passes, for `IgnoreSeconds` = 240.
  - When the sector is finished, `HexesDone` and `LeftInDoneHexes` go up, and `MapFor(nextCell)` replaces the map in
    the same frame.
  - The dirty points left in the map at that moment match the increase in `LeftInDoneHexes`: about 10 per sector. They
    are dirt out of reach (next to structures) or given up on.
  - So reading the map later only ever shows the current sector. Baby J.O.O.N.A.S. 1.6 reads the finished map in a
    prefix on `MapFor`.

## Cleaning queue (why many robots make the game slow)

Measured with Cleaning Catch-Up's per-minute reports. The main session was 18 hours with 55, later ~110 J.O.O.N.A.S.

- **Queueing.** `MoonWorld.QueueToolStroke` only queues a stroke. `MoonWorld.ProcessToolStrokes(complete, robotMs)`
  applies queued strokes once a frame, through `MoonWorld.CleanToolStroke`.
- **Budget.** Robot strokes get at most min(`settings.cleaningWorkBudgetMs` = 2 ms, roughly a fifth of the frame)
  per frame.
  - Focused at 130–180 fps, the fifth of the frame is the limit.
  - At the 30 fps background cap, the 2 ms budget is the limit.
  - `MoonWorld.MaxRobotStrokesWaiting` is 2048.
- **Throttling.** When a robot's strokes have waited `StrokesBehindSeconds` (2.5 s), the game holds that robot until
  the queue catches up.
  - With 55 robots, the queue averaged 1,700 of 2,048 for hours.
  - Cleaning used ~20 % of wall time in every minute, and robots were held in 35–55 % of samples.
- **Cost is proportional to the area under the stroke,** soft edges included: about 0.09 ms per m² inside
  `CleanToolStroke`.
  - A J.O.O.N.A.S. pad stroke (~1.9 m²) costs 0.17–0.23 ms.
  - A 2 m-wide baby stroke (2.4 m long, ~11 m² with its rounded ends) is applied as ~2.5 narrower pieces of
    0.42–0.6 ms each, about 1–1.5 ms in all.
  - Dust collected per millisecond was about the same for both.
- **Clean ground.** 43–48 % of J.O.O.N.A.S. strokes landed on ground that was already clean while sectors were still
  being worked. Once an area was done it was 90–100 %.
  - The game processes these like any other stroke.
  - Not queueing them is the largest saving available to a mod.
  - Spot-checks put the dust that skipping costs at 0.2–0.7 % of the total.
- **Single slow strokes.** Occasionally one stroke takes 25–60 ms (the slowest seen: 44.5 ms for one J.O.O.N.A.S. pad
  stroke). The larger hitches (150–400 ms) are not in the cleaning code.

## Save files

- `%USERPROFILE%\AppData\LocalLow\DragDropGames\MoonSweepers\MoonSessions\slot-N\<hash>\session.moon`:
  `1PCM` + uint32 raw length + byte 64 + sha256(raw) hex + gzip(raw). `chunk_*.moon` files are terrain.
- Raw data is written BinaryWriter-style: canister list (`int nextId, int count`, 67-byte records), cargo-kind map
  (`int count`, `{int canisterId, byte kind}`, absent = dust), structures (42-byte records), belts (58 bytes + byte
  waypoint count + 24 bytes per waypoint + 1 byte).
- Riders are not stored per belt; a capsule's belt is found by its position.
- The game autosaves; close it before editing a save.
