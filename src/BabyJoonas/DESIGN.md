# Baby J.O.O.N.A.S. — design

> **Status (1.6.0).** This is the design note written before the first build, kept for reference. Some details below
> are out of date: "off by default", "not installed", and the files section. What shipped:
> - Babies come from an upgrade with tiers 1–3.
> - They sweep the sector's inner disc in arcs, rather than taking the game's inner lanes.
> - Once baby cleaning is confirmed, the parent's inner lanes are removed from its plan, never its current pass.
> - Idle babies take over the parent's last remaining lanes, and later the dirt it leaves in finished sectors (1.6).
> - Babies skip ground that is already clean, using the game's `RemainingAt`.
> - They hold back while the cleaning queue is busy.
>
> Measured costs and the queue limits are in `docs/GAME-INTERNALS.md`.

Upgrade idea: every J.O.O.N.A.S. gets small helper robots ("babies") that clean the inner rings of the
sector their parent is working on, while the parent cleans the outer ring. Babies stay with their parent.

## Verdict

**Feasible, without replacing any game AI.** The key facts:

1. **A sector is already planned as concentric rings, outer ring first.** From `RobotLog.csv`
   (89 sectors, 18 robots): lane indices 1–6 run at ~8 m from the sector centre (outer ring, ~60 % of
   cleaning time), 7–12 at ~4 m (middle ring, ~29 %), 13 at ~1 m (centre, ~3 %). 13 lanes / 3 rows is the
   normal plan; 37 of 89 sectors needed a second round. So "babies take the inner rings" maps 1:1 onto
   the game's own plan.
2. **The plan is readable.** `MoonRobotVacuum.lanes` is a `List<Lane>`; `Lane` = `{ Vector3 a, Vector3 b,
   int row }` (a straight segment). `cell`, `laneIndex`, `rows`, `round` are instance fields.
3. **Cleaning is a public world API.** Robots clean through `MoonWorld.QueueToolStroke(player, from, to,
   fromRight, toRight, width, depth, dose, edgeWidth, push, completed, inventoryId, vehicleStorageId,
   credit)`; the robot's `padCleaned` callbacks are exactly that method's `Action<int,double> completed`
   type, and `storageId` is the robot's storage string. A baby can call the same method with
   `vehicleStorageId = parent.storageId`, so the dust lands in the parent's storage and the parent fires it
   at catchers as usual. (Narrower alternatives exist — `CleanStroke(from,to,width,dose)` and
   `CleanAt(point,width,dose)` — but they return only a changed-cell count, no kilograms/credit.)
4. **The parent's workload shrinks without touching its AI** — *assumption to verify*: `NextLane()` uses
   `LaneDirty(lane)` to skip lanes that are already clean. The parent does lanes 1–6 first (~40 s); if the
   babies clean the inner lanes in that time, the parent finds 7–13 clean and moves on.

**Not touched:** the static tuning values (`LaneSpacing`, `MergeGap`, `MaxRounds`, `StrokeInterval`,
`PadStroke*`, `RevisitSeconds`, `HexIdleSeconds`, `StuckLimit`). They are compile-time constants; writing
`LaneSpacing` through the interop setter crashed the game (access violation in GameAssembly.dll) and the
value did not change. Anything that needs different ring spacing/count must edit the planned `lanes` list
(e.g. a postfix on `PlanRings(int hex)`, which returns an int) or instance fields — never those statics.

## Approach

| Piece | How |
|---|---|
| Find parents | Every second, `MoonRobotDock.all` → `dock.Robot`; J.O.O.N.A.S. = robot with a `launcher` (`MoonJoonasRig`). |
| Babies | Pure visuals: a copy of the parent's `MeshFilter`/`MeshRenderer`s (shared mesh + materials), scaled ~0.35. No colliders, no game scripts, no dock, not saved, recreated on load. (Instantiating `robotPrefab` would run a real `MoonRobotVacuum` with its own AI — avoided.) |
| Work split | When the parent's `cell` changes and its `lanes` list is filled, lanes whose midpoint is within 70 % of the outermost lane's radius go to the babies (round-robin). The parent keeps its full list. |
| Movement | Mod-driven in `Update` (main thread): drive to lane start, then along the lane at ~1.5 m/s; ground and up from the parent's own `GroundAt(p)` / `Up(p)`. Idle babies tag along behind the parent. |
| Cleaning (stage 1b) | Every 0.4 s, `QueueToolStroke` over the strip just driven, width ~2 m, copying `player`, `depth`, `dose`, `edgeWidth`, `credit`, `inventoryId` from the parent's last observed stroke, `vehicleStorageId = parent.storageId`, no callback. |
| Dust credit | Through the parent's storage → parent's `DustKilograms` → normal capsule launches. |
| Parent time saved | Inner rings + their lane changes ≈ 35–40 % of sector time; expected sector ~86 s → ~55 s if the skip assumption holds. |

## Staged plan

- **Stage 0 — stroke probe (built, off by default: `ProbeStrokes`).** Harmony prefix on
  `MoonWorld.QueueToolStroke`, main thread only, reads only ints/floats/strings. Logs the distinct argument
  sets the robots use. Answers: which `player` id, `depth`, `dose`, `edgeWidth`, `credit` a robot stroke uses.
- **Stage 1a — visual babies (built, off by default: `Enabled`).** Babies spawn, follow, and drive the inner
  lanes. No game state changes. Checks: mesh copy looks right, ground following on the curved moon, lane
  split matches the rings, frame time.
- **Stage 1b — cleaning babies (built, off by default: `Clean`).** Only after stage 0 shows sane values.
  Verify with `RobotLogger`: parent sector time, `hexDirtyNow` at the end, dust/min per family, whether the
  parent skips the cleaned inner lanes (laneIndex jumps from 6 to next sector).
- **Stage 2 — make it an upgrade.** Purchasable tree node (like Machine Overclock) controlling baby count
  (e.g. 1 → 2 → 3); babies only exist when bought. Remember the purchase in a mod-side file per world.
- **Stage 3 — polish.** If the parent does *not* skip clean lanes: postfix on `PlanRings` that removes the
  inner lanes from the parent's list while babies own them (re-plan rounds keep the full list so nothing is
  left dirty). Simple animation (wheel/brush spin), obstacle check against `MoonRobotVacuum.Blocked`-style
  queries, babies return into the parent when it docks/launches.

## Open questions (need in-game logging)

1. Stroke arguments robots use (stage 0): `player` id for robots, `credit`, `depth`/`dose`, callback
   required for crediting or not (if dust only reaches storage via the callback, stage 1b must pass a
   callback that calls `parent.Cleaned(changed, kg)` instead).
2. Does `NextLane()` skip lanes where `LaneDirty` is false? (Watch `laneIndex` and time per sector with
   stage 1b.)
3. Are `GroundAt(p)` / `Up(p)` safe to call for points the robot isn't at (they may raycast against
   structures or use the robot's own state)?
4. Does `QueueToolStroke` with a robot storage id from a second "source" upset the robot's own cleaning
   budget (`Strips`, queue throttling) or the region/bounty accounting?
5. Co-op: are tool strokes broadcast to guests? (`MoonCrew.Joined`, `MoonSteamLobby.IsHost` /
   `MoonCrewLimits.Hosting` exist.) Until known, babies should only run in single-player or on the host.

## Risks

| Risk | Mitigation |
|---|---|
| Crash from touching game objects mid-change (seen with the splitter probe) | No hooks in the AI; per-frame work only on the main thread; parent/robot pointers re-checked; stroke probe reads primitives only. |
| Writing compile-time "static" fields crashes (seen with `LaneSpacing`) | Design never writes statics; only reads instance data and calls public methods. |
| Wrong stroke arguments (wrong player/credit, double payout) | Stage 1b copies the parent's own observed arguments and stays off until stage 0 data is reviewed. |
| Performance: 18 parents × 2 babies = 36 extra strokes every 0.4 s (+~50 % stroke load); mesh copies | 4 ms per-frame budget in the driver; stroke interval and baby count configurable; meshes are shared, not duplicated. |
| Babies clip through buildings | They only drive lanes the game itself planned for the parent (already obstacle-aware); no colliders so they can't push anything. |
| Traffic: parent's `Traffic`/`Separate`/`GatherMovers` don't see babies | Fine for visuals; babies keep to inner rings while the parent is on the outer ring. |
| Saves / removal | Nothing is saved; removing the mod removes the babies. Parent plans are never modified (until optional stage 3). |
| Co-op | Run only when not joined as a guest; untested otherwise. |
| Game updates | Field/method names may change; everything is wrapped so a failure logs and disables babies. |

## Files

- `Plugin.cs`, `BabyJoonas.csproj` — stage 0/1a/1b prototype, all off by default
  (`BepInEx\config\moonsweepers.babyjoonas.cfg`: `Enabled`, `Clean`, `ProbeStrokes`, baby count/scale/speed/width).
  Builds with `dotnet build -c Release` (`-p:GameDir=...` to override). **Not installed into the game.**
