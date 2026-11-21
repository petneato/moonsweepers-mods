using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.BabyJoonas;

// Baby J.O.O.N.A.S. (see DESIGN.md for how it started). Babies come from an upgrade in the Cleaning tab.
//
//  Stage 0  ProbeStrokes = true : logs the arguments J.O.O.N.A.S. passes to MoonWorld.QueueToolStroke
//                                (player id, width, depth, dose, edge, credit, storage) - needed for stage 1b.
//  Stage 1a Enabled = true      : small visual copies follow each J.O.O.N.A.S. and drive the inner rings of
//                                the sector it is cleaning. Visual only, no cleaning, nothing saved.
//  Stage 1b Clean = true        : babies also clean, copying the parent's last observed stroke settings and
//                                crediting the dust to the parent's storage, so the parent launches it.
//                                The parent then skips inner-ring lanes that are already clean (assumed:
//                                NextLane uses LaneDirty) - no game AI is changed.
//  1.5: babies with nothing to do clean dirt the parent can't reach. Strokes are longer with a smaller soft edge: in
//       the 1.4 session baby strokes (2 m wide + 0.6 m edges, 1.5 m long) were the bulk of the game's cleaning work.
//  1.6: that dirt is now taken from the sector the parent just finished. J.O.O.N.A.S. maps one sector at a time and
//       replaces the map in the same frame it finishes, so 1.5 (which scanned the current map) found almost nothing
//       (10 kg in 18 hours, while the robots left ~10 dirty points per sector). Babies parked behind a parent that
//       isn't moving are no longer updated every frame. On by default (babies still need the upgrade).
[BepInPlugin("moonsweepers.babyjoonas", "Baby J.O.O.N.A.S.", Version)]
public class Plugin : BasePlugin
{
    public const string Version = "1.6.0";
    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Enabled, Clean, ProbeStrokes, CreditParent, TrimParent, PadStrokes, StealLanes;
    internal static ConfigEntry<int> BabiesPerRobot;
    internal static ConfigEntry<bool> RequireUpgrade;
    internal static ConfigEntry<string> Prices, Title, Summary;
    internal static ConfigEntry<float> Scale, Speed, Width, InnerShare, MaxBacklog;
    internal static ConfigEntry<int> MaxPending;
    internal static ConfigEntry<bool> SkipClean;
    internal static ConfigEntry<float> CleanBelow;
    internal static ConfigEntry<bool> SkipStretches, TrustRemainingAt, FarUpdate;
    internal static ConfigEntry<float> TransitSpeed, Edge, StrokeLength;
    internal static ConfigEntry<bool> OutOfReach;

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", true, "Babies: small helpers that follow each J.O.O.N.A.S. and drive the inner rings of its sector (they come from the 'Baby J.O.O.N.A.S.' upgrade, see [Upgrade]).");
        Clean = Config.Bind("General", "Clean", true, "Babies also clean, with the parent's own stroke settings; the dust goes to the parent, which fires it at catchers as usual. Off = visual only.");
        ProbeStrokes = Config.Bind("Diagnostics", "ProbeStrokes", false, "Stage 0: log the stroke arguments J.O.O.N.A.S. uses (Harmony hook on MoonWorld.QueueToolStroke, main thread only).");
        MaxPending = Config.Bind("Babies", "MaxGamePendingStrokes", 1500, "Babies hold back while the game's whole cleaning queue has more than this many strokes waiting (the game drops strokes above 4096).");
        MaxBacklog = Config.Bind("Babies", "MaxParentBacklogSeconds", 0f, "Babies hold back their strokes while their parent has more than this many seconds of cleaning strokes waiting. 0 = automatic: just below the point where the game starts slowing the parent down.");
        SkipClean = Config.Bind("Babies", "SkipCleanGround", true, "Don't send strokes over ground that is already clean (checked with the game's own dust map). Every 10th such stroke is still sent as a spot-check; if the check turns out wrong, skipping switches itself off.");
        CleanBelow = Config.Bind("Babies", "CleanGroundShare", 0.02f, "Ground counts as clean when the chosen dust reading is at most this share of a typical dirty reading (chosen automatically at the start of each session).");
        TrimParent = Config.Bind("General", "TrimParent", true, "Remove the inner lanes the babies take over from the parent's plan, so the parent only cleans the outer ring.");
        StealLanes = Config.Bind("General", "StealLanes", true, "When a baby has finished the middle, it takes over outer-ring lanes the parent hasn't reached yet, so more babies make the whole sector faster.");
        PadStrokes = Config.Bind("General", "PadStrokes", false, "Experimental: babies clean with scaled-down copies of the parent's scrub pads (round strokes) instead of one soft-edged strip. About 6x more strokes for the game to process, and babies then drive at the pads' pace.");
        CreditParent = Config.Bind("General", "CreditParent", true, "Give the dust babies clean to their parent J.O.O.N.A.S. (it then fires it at catchers as usual).");
        BabiesPerRobot = Config.Bind("Babies", "PerRobot", 2, "Babies per J.O.O.N.A.S. when RequireUpgrade = false.");
        RequireUpgrade = Config.Bind("Upgrade", "RequireUpgrade", true, "Babies come from the 'Baby J.O.O.N.A.S.' upgrade (Cleaning tab): tier 1/2/3 = 1/2/3 babies per J.O.O.N.A.S. Set false to use PerRobot instead.");
        Prices = Config.Bind("Upgrade", "Prices", "30000, 60000, 100000", "Price of each tier ($).");
        Title = Config.Bind("Upgrade", "Title", "Baby J.O.O.N.A.S.", "Name in the upgrade terminal.");
        Summary = Config.Bind("Upgrade", "Summary", "Each J.O.O.N.A.S. gets small helpers that clean the middle of its sector while it does the outer ring.", "Description in the upgrade terminal.");
        Scale = Config.Bind("Babies", "Scale", 0.35f, "Size relative to the parent model.");
        Speed = Config.Bind("Babies", "Speed", 1.5f, "Driving speed in m/s.");
        Width = Config.Bind("Babies", "CleaningWidth", 2.0f, "Cleaning width in metres (only used when Clean = true).");
        InnerShare = Config.Bind("Babies", "InnerRadiusShare", 0.7f, "Lanes whose middle is closer to the sector centre than this share of the outermost lane's radius count as inner rings.");
        SkipStretches = Config.Bind("Babies", "SkipCleanStretches", true, "Babies don't drive the parts of their planned path that are already clean (the parent or another baby got there first); they head straight for the next dirty part.");
        TransitSpeed = Config.Bind("Babies", "TransitSpeedFactor", 2f, "Speed multiplier while a baby drives to its next job or follows its parent (not cleaning).");
        TrustRemainingAt = Config.Bind("Babies", "TrustRemainingAt", true, "Use the game's RemainingAt reading for clean ground from the first minute (the automatic check picked it in every session so far); the check still runs and can overrule it.");
        FarUpdate = Config.Bind("Performance", "FarBabiesUpdateLess", true, "Babies far from the camera are moved every 3rd frame (over 80 m) or every 6th frame (over 200 m). Their cleaning is unchanged; only the motion updates less often.");
        Edge = Config.Bind("Performance", "StrokeEdgeWidth", 0.2f, "Soft edge on each side of a baby stroke, in metres (was 30% of the width = 0.6 m). The game processes every bit of ground under the stroke including the edges, so a smaller edge makes each stroke cheaper.");
        StrokeLength = Config.Bind("Performance", "StrokeLength", 2.4f, "Metres of path per baby stroke (was about 1.5 m). Fewer, longer strokes are cheaper for the game's cleaning queue; the cleaned strip is the same.");
        OutOfReach = Config.Bind("Babies", "CleanOutOfReachDirt", true, "Babies with nothing to do clean the dirt their parent left in sectors it finished (dirt beyond its brush reach, e.g. next to structures, or dirt it gave up on after 5 passes). Babies have no collision, so they may drive through structures to get there.");

        if (!Enabled.Value && !ProbeStrokes.Value) { L.LogInfo("Baby J.O.O.N.A.S. loaded, switched off in config (General.Enabled)."); return; }
        bool probe = false;
        try
        {
            if (ProbeStrokes.Value || Clean.Value) { new Harmony("moonsweepers.babyjoonas").PatchAll(typeof(StrokeProbe)); probe = true; }
        }
        catch (Exception e) { L.LogError("Stroke probe could not be attached (babies still start, cleaning stays off): " + e.Message); }
        if (Enabled.Value && Clean.Value && OutOfReach.Value)
        {
            try { new Harmony("moonsweepers.babyjoonas.leftovers").PatchAll(typeof(LeftoverHook)); }
            catch (Exception e) { L.LogWarning("Finished-sector hook could not be attached (babies do their normal work): " + e.Message); }
        }
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<BabyDriver>(); AddComponent<BabyDriver>();   // also tracks the main thread for the probe
            L.LogInfo($"Baby J.O.O.N.A.S. {Version}: babies {(Enabled.Value ? "on" : "off")}, cleaning {(Clean.Value && probe ? "on" : "off")}, stroke probe {(probe ? "on" : "off")}.");
            L.LogInfo($"Baby strokes: {Width.Value} m wide, {Math.Clamp(Edge.Value, 0f, Width.Value * 0.3f)} m soft edges, {StrokeLength.Value} m long; dirt left in finished sectors {(OutOfReach.Value ? "on" : "off")}.");
        }
        catch (Exception e) { L.LogError("Baby J.O.O.N.A.S. failed to start: " + e); }
    }
}

// The purchasable upgrade (same technique as Joonas Overdrive / Machine Overclock): a node appended to the game's
// upgrade tree; the game stores the bought tier under the node id, read back with MoonWorld.TreeLevel.
internal static class Upgrade
{
    public const string NodeId = "mod.joonas.babies";
    internal static int MaxLevel = 3;

    internal static void Add()
    {
        try
        {
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(Il2CppInterop.Runtime.Il2CppClassPointerStore.GetNativeClassPointer(typeof(MoonUpgradeTree)));
            if (MoonUpgradeTree.Find(NodeId) != null) return;
            var nodes = MoonUpgradeTree.Nodes;
            if (nodes == null) { Plugin.L.LogError("Upgrade tree not available; Baby J.O.O.N.A.S. upgrade not added."); return; }
            MoonUpgradeTree.Node anchor = null;
            foreach (var n in nodes)
            {
                if (n == null || n.branch != MoonUpgradeTree.Branch.Cleaning) continue;
                int req = n.requires?.Length ?? 0, price = n.prices != null && n.prices.Length > 0 ? n.prices[0] : 0;
                if (anchor == null || req < (anchor.requires?.Length ?? 0) || (req == (anchor.requires?.Length ?? 0) && price < anchor.prices[0])) anchor = n;
            }
            if (anchor == null) { Plugin.L.LogError("No Cleaning branch in the upgrade tree; Baby J.O.O.N.A.S. upgrade not added."); return; }
            var cfg = new List<int>();
            foreach (var part in (Plugin.Prices.Value ?? "").Split(','))
                if (int.TryParse(part.Trim(), out var v) && v > 0) cfg.Add(v);
            if (cfg.Count == 0) cfg.AddRange(new[] { 30000, 60000, 100000 });
            MaxLevel = cfg.Count;
            var prices = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(cfg.Count);
            for (int i = 0; i < cfg.Count; i++) prices[i] = cfg[i];
            var node = new MoonUpgradeTree.Node
            {
                id = NodeId, title = Plugin.Title.Value, summary = Plugin.Summary.Value,
                branch = MoonUpgradeTree.Branch.Cleaning, glyph = MoonUpgradeTree.Glyph.Suction,
                maxLevel = cfg.Count, prices = prices, requires = anchor.requires, requiresAny = anchor.requiresAny,
                tool = null, position = anchor.position * 1.45f, capstone = false,
            };
            node.effect = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<int, string>>(new Func<int, string>(lv =>
                lv <= 0 ? "No helpers yet" : $"{lv} baby J.O.O.N.A.S. per J.O.O.N.A.S."));
            var grown = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MoonUpgradeTree.Node>(nodes.Length + 1);
            for (int i = 0; i < nodes.Length; i++) grown[i] = nodes[i];
            grown[nodes.Length] = node;
            MoonUpgradeTree.Nodes = grown;
            MoonUpgradeTree.byId.Add(NodeId, node);
            Plugin.L.LogInfo($"Added '{node.title}' to Cleaning: {cfg.Count} tiers, prices [{string.Join(", ", cfg)}], unlocked like {anchor.id}.");
        }
        catch (Exception e) { Plugin.L.LogError("Could not add the Baby J.O.O.N.A.S. upgrade: " + e.Message); }
    }

    internal static int Level(MoonWorld w)
    {
        try { return w == null ? 0 : Math.Clamp(w.TreeLevel("", NodeId), 0, MaxLevel); } catch { return 0; }
    }
}

// What the parent last passed to QueueToolStroke, keyed by its storage id.
internal sealed class StrokeArgs { public int Player; public float Width, Depth, Dose, Edge; public string Credit, Inventory; public bool HasCallback; }

[HarmonyPatch]
internal static class StrokeProbe
{
    internal static int MainThread = -1;
    internal static readonly Dictionary<string, StrokeArgs> ByStorage = new();
    static readonly HashSet<string> logged = new();
    internal static bool Ours;   // set while a baby issues its own stroke

    // Only primitive arguments and strings are touched; no game objects are dereferenced.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.QueueToolStroke))]
    [HarmonyPrefix]
    static void Before(int player, float width, float depth, float dose, float edgeWidth,
                       Il2CppSystem.Action<int, double> completed, string inventoryId, string vehicleStorageId, string credit)
    {
        try
        {
            if (Ours || Environment.CurrentManagedThreadId != MainThread || string.IsNullOrEmpty(vehicleStorageId)) return;
            ByStorage[vehicleStorageId] = new StrokeArgs { Player = player, Width = width, Depth = depth, Dose = dose, Edge = edgeWidth, Credit = credit, Inventory = inventoryId, HasCallback = completed != null };
            if (!Plugin.ProbeStrokes.Value) return;
            var key = $"{player}|{width:0.##}|{depth:0.##}|{dose:0.##}|{edgeWidth:0.##}|{credit}|{inventoryId}|{completed != null}|{StoragePrefix(vehicleStorageId)}";
            if (logged.Count < 200 && logged.Add(key))
                Plugin.L.LogInfo($"[stroke] player={player} width={width:0.###} depth={depth:0.###} dose={dose:0.###} edge={edgeWidth:0.###} callback={completed != null} inventory='{inventoryId}' storage='{vehicleStorageId}' credit='{credit}'");
        }
        catch { }
    }

    static string StoragePrefix(string s) { int i = s.IndexOf('/'); return i > 0 ? s.Substring(0, i) : s; }
}

// Dirt left in finished sectors: read the robot's sector map just before MapFor replaces it (see BabyDriver.Harvest).
[HarmonyPatch]
internal static class LeftoverHook
{
    static int errors;

    [HarmonyPatch(typeof(MoonRobotVacuum), nameof(MoonRobotVacuum.MapFor))]
    [HarmonyPrefix]
    static void Before(MoonRobotVacuum __instance, int c)
    {
        if (Environment.CurrentManagedThreadId != StrokeProbe.MainThread || __instance == null) return;
        try { BabyDriver.Harvest(__instance, c); }
        catch (Exception e) { if (errors++ < 3) Plugin.L.LogWarning("Reading a finished sector failed (babies keep doing their normal work): " + e.Message); }
    }
}

internal sealed class Baby
{
    public GameObject Go;
    public Vector3 Pos;
    public readonly Queue<(Vector3 a, Vector3 b)> Work = new();
    public (Vector3 a, Vector3 b)? Current;
    public bool OnLane;          // false = driving to the lane start
    public Vector3 LastStroke; public float StrokeTimer;
    public int StrokeId;                                   // stable "tool" id for this baby's strokes
    public Il2CppSystem.Action<int, double> Done;          // stroke-completed callback (created once)
    public Il2CppSystem.Action<int, double> AuditDone;     // callback for spot-check strokes over "clean" ground
    public readonly List<(Renderer src, Renderer dst)> Skin = new();   // parent renderer -> our copy (material refresh)
    public float SegmentTime;                              // seconds spent on the current segment (stuck watchdog)
    public Vector3 Normal = Vector3.up; public float NormalTimer;   // ground slope under the baby (refreshed 5x a second)
    public float Scrub;                                    // scrub-motion clock for the pad strokes
    public float GroundTimer; public Vector3 GroundPoint; public bool HaveGround;
    public readonly List<Vector3> Held = new();            // path points not yet cleaned while the parent was backed up
    public Vector3[] PadLast;                              // where each pad was at the last stroke
    public bool LastClean;                                 // was the last stroke skipped as clean ground?
    public int SpotWork;                                   // queued work items that are out-of-reach spot jobs
    public bool OnSpot;                                    // the current work item is a spot job
    public float NextSpot;                                 // don't look for a spot every frame
    public Il2CppSystem.Action<int, double> SpotDone;      // callback for spot-job strokes (kg counted separately)
    public bool Parked; public float ParkTime; public Vector3 ParkGoal;   // settled behind a parent that isn't moving
}

internal sealed class Family
{
    public int DockId; public MoonRobotVacuum Parent; public int Cell = int.MinValue; public float SeenAt = -1, NextSkin, Lag; public string PlanKey; public int Trimmed, LastTrimCell = int.MinValue; public bool CanSteal;
    public float NextPlan, NextSteal, NextBoost, Boost = 1f;                 // plan check 4x a second, parent speed once a second
    public int TagFrame = -1; public Vector3 TagP, TagF, TagR;               // parent position/axes, read once per frame
    public readonly List<Baby> Babies = new();
    public bool PlanWaiting;                                                 // parent started a new plan; babies finish up and join it
    // Dirt left in sectors the parent finished (read from its sector map in MapFor, see BabyDriver.Harvest).
    public int WorkCell = int.MinValue, MapForCell = int.MinValue, HarvestDone = int.MinValue, HarvestLeft;
    public readonly List<(Vector3 pos, float at)> Leftovers = new();
}

public class BabyDriver : MonoBehaviour
{
    public BabyDriver(IntPtr ptr) : base(ptr) { }

    readonly Dictionary<int, Family> families = new();
    float nextScan;
    static float StrokeInterval => Plugin.PadStrokes.Value ? 0.2f : 0.6f;
    internal static double KgThisMinute, KgTotal; internal static int StrokesThisMinute, EmptyThisMinute;
    float nextReport = 60f;
    int cursor;
    bool nodeAdded; int lastLevel = -1;
    static int frames; static double totalMs, maxMs;
    readonly Stopwatch budget = new();
    readonly List<Family> familyList = new();               // rebuilt in Scan (once a second), not every frame
    Camera cam; Vector3 camPos; int frameNo;
    internal static int StretchesSkipped, CleanLanesPassed, stretchChecksThisFrame;
    internal static readonly double[] TimeUse = new double[5];   // baby-seconds: cleaning dirt, over clean ground, driving, idle, waiting
    static bool trustChecked;

    void Update()
    {
        StrokeProbe.MainThread = Environment.CurrentManagedThreadId;
        float dt = Math.Min(Time.deltaTime, 0.1f), now = Time.unscaledTime;
        try
        {
            if (!nodeAdded) { nodeAdded = true; if (Plugin.Enabled.Value && Plugin.RequireUpgrade.Value) Upgrade.Add(); }
            if (!Plugin.Enabled.Value) return;
            if (!trustChecked) { trustChecked = true; TrustDefault(); }
            PumpProbes();
            if (now >= nextScan) { nextScan = now + 1f; Scan(); }
            if (now >= nextReport) { nextReport = now + 60f; Report(); }
            budget.Restart();
            var list = familyList;
            frames++; frameNo++; stretchChecksThisFrame = 0;
            bool lod = Plugin.FarUpdate.Value && cam != null;
            if (lod) { try { camPos = cam.transform.position; } catch { lod = false; cam = null; } }
            int count = list.Count, done = 0;
            for (; done < count; done++)
            {
                var f = list[(cursor + done) % count];
                if (f.Parent == null || f.Parent.Pointer == IntPtr.Zero) continue;
                if (lod && f.Babies.Count > 0)
                {
                    // far from the camera: move every 3rd / 6th frame; the skipped time is caught up through Lag
                    float d2 = (f.Babies[0].Pos - camPos).sqrMagnitude;
                    int every = d2 > 200f * 200f ? 6 : d2 > 80f * 80f ? 3 : 1;
                    if (every > 1 && (frameNo + f.DockId) % every != 0) { f.Lag = Math.Min(0.5f, f.Lag + dt); continue; }
                }
                if (now >= f.NextPlan) { f.NextPlan = now + 0.25f; Plan(f); }
                if (now >= f.NextBoost) { f.NextBoost = now + 1f; try { f.Boost = Math.Clamp(f.Parent.cleaningSpeed / 2f, 1f, 4f); } catch { f.Boost = 1f; } }
                foreach (var b in f.Babies) Step(f, b, f.Lag + dt);
                f.Lag = 0;
                if (budget.ElapsedMilliseconds > 4) { done++; break; }   // never take more than ~4 ms a frame
            }
            // Families skipped this frame catch up with the time they missed next time.
            for (int k = done; k < count; k++) list[(cursor + k) % count].Lag = Math.Min(0.5f, list[(cursor + k) % count].Lag + dt);
            if (count > 0) cursor = (cursor + done) % count;
            double ms = budget.Elapsed.TotalMilliseconds; totalMs += ms; if (ms > maxMs) maxMs = ms;
        }
        catch (Exception e) { Plugin.L.LogError("Baby J.O.O.N.A.S. update: " + e.Message); }
    }

    // Find J.O.O.N.A.S. robots; create or drop families.
    void Scan()
    {
        var docks = MoonRobotDock.all;
        var seen = new HashSet<int>();
        bool guest = false;
        try { guest = MoonCrew.Joined && !MoonSteamLobby.IsHost; } catch { }   // co-op untested: host / single-player only
        if (docks != null && !guest)
            for (int i = 0; i < docks.Count; i++)
            {
                var d = docks.get_Item(i);
                if (d == null || !d.isActiveAndEnabled) continue;
                var r = d.Robot;
                if (r == null || r.launcher == null) continue;        // only J.O.O.N.A.S. carries a launcher
                seen.Add(d.DockId);
                if (!families.TryGetValue(d.DockId, out var f)) families[d.DockId] = f = new Family { DockId = d.DockId };
                float now = Time.unscaledTime;
                if (f.Parent == null || f.Parent.Pointer != r.Pointer) { Clear(f); f.Parent = r; f.SeenAt = now; f.HarvestDone = f.MapForCell = f.WorkCell = int.MinValue; }
                int want = Plugin.RequireUpgrade.Value ? Upgrade.Level(r.world) : Plugin.BabiesPerRobot.Value;
                if (want != lastLevel && Plugin.RequireUpgrade.Value) { lastLevel = want; Plugin.L.LogInfo($"Baby J.O.O.N.A.S. tier {want}: {want} babies per J.O.O.N.A.S."); }
                if (f.Babies.Count != want)
                {
                    while (f.Babies.Count > want) { var last = f.Babies[f.Babies.Count - 1]; if (last.Go != null) UnityEngine.Object.Destroy(last.Go); f.Babies.RemoveAt(f.Babies.Count - 1); }
                    while (f.Babies.Count < want) f.Babies.Add(Spawn(r, f.Babies.Count));
                    foreach (var bb in f.Babies) { bb.Work.Clear(); bb.Current = null; bb.Held.Clear(); bb.SpotWork = 0; bb.OnSpot = false; }
                    f.PlanKey = null;                       // re-split the inner area between the new number of babies
                }
                // A freshly built J.O.O.N.A.S. wears the white build material for a moment: re-copy materials every
                // second for its first 20 s, then every 15 s.
                if (now >= f.NextSkin) { f.NextSkin = now + (now - f.SeenAt < 20f ? 1f : 15f); foreach (var b in f.Babies) RefreshSkin(b); }
            }
        foreach (var id in new List<int>(families.Keys))
            if (!seen.Contains(id)) { Clear(families[id]); families.Remove(id); }
        familyList.Clear(); familyList.AddRange(families.Values);
        ByParent.Clear(); foreach (var fam in familyList) if (fam.Parent != null) ByParent[fam.Parent.Pointer] = fam;
        try { if (cam == null || !cam.isActiveAndEnabled) cam = Camera.main; } catch { cam = null; }
    }

    static void Clear(Family f)
    {
        foreach (var b in f.Babies) if (b.Go != null) UnityEngine.Object.Destroy(b.Go);
        f.Babies.Clear(); f.Cell = int.MinValue; f.Leftovers.Clear();
    }

    // A baby is a static copy of the parent's meshes: no colliders, no scripts, not saved.
    static bool loggedParts;

    static Baby Spawn(MoonRobotVacuum parent, int index)
    {
        var root = new GameObject($"BabyJoonas_{index}");
        var pt = parent.transform;
        var b = new Baby { Go = root };
        int meshes = 0, skinned = 0, lines = 0, other = 0;
        foreach (var rend in parent.GetComponentsInChildren<Renderer>())
        {
            try
            {
                if (rend == null || !rend.enabled) continue;
                var t = rend.transform;
                var mr = rend.TryCast<MeshRenderer>();
                var smr = mr == null ? rend.TryCast<SkinnedMeshRenderer>() : null;
                var lr = mr == null && smr == null ? rend.TryCast<LineRenderer>() : null;
                Mesh mesh = null;
                if (mr != null) { var mf = t.GetComponent<MeshFilter>(); mesh = mf != null ? mf.sharedMesh : null; if (mesh != null) meshes++; }
                else if (smr != null) { mesh = new Mesh(); smr.BakeMesh(mesh); skinned++; }
                if (mesh != null)
                {
                    var part = new GameObject(t.name);
                    part.transform.SetParent(root.transform, false);
                    part.transform.localPosition = pt.InverseTransformPoint(t.position);
                    part.transform.localRotation = Quaternion.Inverse(pt.rotation) * t.rotation;
                    var ls = t.lossyScale; var ps = pt.lossyScale;
                    part.transform.localScale = smr != null ? new Vector3(1f / ps.x, 1f / ps.y, 1f / ps.z)
                                                            : new Vector3(ls.x / ps.x, ls.y / ps.y, ls.z / ps.z);
                    part.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var dst = part.AddComponent<MeshRenderer>();
                    dst.sharedMaterials = rend.sharedMaterials;
                    b.Skin.Add((rend, dst));
                }
                else if (lr != null && lr.positionCount > 1)
                {
                    var part = new GameObject(t.name);
                    part.transform.SetParent(root.transform, false);
                    var copy = part.AddComponent<LineRenderer>();
                    copy.useWorldSpace = false;
                    copy.positionCount = lr.positionCount;
                    for (int i = 0; i < lr.positionCount; i++)
                    {
                        var wp = lr.useWorldSpace ? lr.GetPosition(i) : t.TransformPoint(lr.GetPosition(i));
                        copy.SetPosition(i, pt.InverseTransformPoint(wp));
                    }
                    copy.widthMultiplier = lr.widthMultiplier * Plugin.Scale.Value;
                    copy.widthCurve = lr.widthCurve;
                    copy.numCapVertices = lr.numCapVertices; copy.numCornerVertices = lr.numCornerVertices;
                    copy.sharedMaterials = lr.sharedMaterials;
                    copy.startColor = lr.startColor; copy.endColor = lr.endColor;
                    b.Skin.Add((rend, copy));
                    lines++;
                }
                else other++;
            }
            catch (Exception e) { other++; if (!loggedParts) Plugin.L.LogWarning("Baby part copy: " + e.Message); }
        }
        if (!loggedParts)
        {
            loggedParts = true;
            var names = new List<string>(); foreach (var (src, _) in b.Skin) names.Add(src.name);
            Plugin.L.LogInfo($"Baby model copied from parent: {meshes} meshes, {skinned} skinned, {lines} lines, {other} other renderers skipped. Parts: {string.Join(", ", names)}");
        }
        root.transform.localScale = Vector3.one * Plugin.Scale.Value;
        var side = (index % 2 == 0 ? 1f : -1f) * (2f + index);
        b.Pos = pt.position - pt.forward * 4f + pt.right * side;
        b.StrokeId = 0x5B000000 | ((parent.GetInstanceID() & 0xFFFF) << 4) | (index & 0xF);
        b.LastStroke = b.Pos;
        root.transform.position = b.Pos;
        return b;
    }

    // Keep the babies' materials in step with the parent (fixes copies taken while it still had build materials).
    static void RefreshSkin(Baby b)
    {
        foreach (var (src, dst) in b.Skin)
        {
            try { if (src != null && dst != null) dst.sharedMaterials = src.sharedMaterials; } catch { }
        }
    }

    // When the parent starts a new sector: its outer ring covers the rim; the babies cover the disc inside it.
    // Each baby gets its own wedge and sweeps it in arcs, outside in, alternating direction: full coverage,
    // no overlap between babies, no following each other.
    static void Plan(Family f)
    {
        var p = f.Parent;
        if (f.Babies.Count == 0) return;                   // no babies (upgrade not bought): leave the parent alone
        try { f.WorkCell = p.mapCell; } catch { }          // the sector its map is for (Harvest checks the map is still that one)
        var lanes = p.lanes;
        if (lanes == null || lanes.Count == 0) { f.PlanWaiting = false; return; }
        // Re-plan when the parent's lane plan itself changes (not when it merely picks its next sector).
        var l0 = lanes.get_Item(0);
        string key = $"{p.cell}|{lanes.Count}|{l0.a.x:0.0},{l0.a.z:0.0}";
        if (key == f.PlanKey) { f.PlanWaiting = false; return; }
        // Babies still busy with the previous sector: check again shortly (Plan runs 4x a second) and join this sector
        // as soon as they are free; parts the parent has already cleaned by then are skipped quickly.
        f.PlanWaiting = true;                              // no new spot jobs until the babies have joined the new plan
        foreach (var bb in f.Babies) if (bb.Current != null || bb.Work.Count > 0) return;
        f.PlanWaiting = false;
        f.PlanKey = key;
        f.Cell = p.cell;
        f.CanSteal = false;
        Vector3 centre = Vector3.zero; int n = 0;
        for (int i = 0; i < lanes.Count; i++) { var l = lanes.get_Item(i); centre += l.a + l.b; n += 2; }
        centre /= n;
        float outer = 0;
        for (int i = 0; i < lanes.Count; i++) { var l = lanes.get_Item(i); outer = Math.Max(outer, ((l.a + l.b) * 0.5f - centre).magnitude); }
        float parentHalf = 2.8f; try { parentHalf = p.cleaningWidth * 0.5f; } catch { }
        float R = Math.Max(1.5f, outer - parentHalf + 0.5f);
        // Only hand inner lanes over once baby strokes are proven to clean, and never twice for the same sector
        // (if the parent comes back for a second round, it finishes the sector itself - no loop).
        if (Plugin.TrimParent.Value && Plugin.Clean.Value && BabyDriver.Mode == 1 && BabyDriver.CompletedTotal > 20 && p.cell != f.LastTrimCell)
        {
            f.LastTrimCell = p.cell;
            TrimParent(f, p, lanes, centre, outer);
            f.CanSteal = Plugin.StealLanes.Value;
        }
        // Arcs 0.8 x width apart (1.4 used 0.72 for its wide soft edges); arc pieces as long as one stroke.
        float w = Plugin.Width.Value, step = w * 0.8f, chord = Math.Max(1.5f, Plugin.StrokeLength.Value);
        var up = Vector3.up; try { up = p.Up(centre); } catch { }
        var e1 = Vector3.ProjectOnPlane(p.transform.forward, up).normalized;
        if (e1.sqrMagnitude < 1e-4f) e1 = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
        var e2 = Vector3.Cross(up, e1);
        int nb = f.Babies.Count; if (nb == 0) return;
        for (int i = 0; i < nb; i++)
        {
            var b = f.Babies[i]; b.Work.Clear(); b.Current = null;
            float a0 = (float)(2 * Math.PI * i / nb), a1 = (float)(2 * Math.PI * (i + 1) / nb);
            var pts = new List<Vector3>();
            bool forward = true;
            for (float r = R - w * 0.5f; r > w * 0.3f; r -= step)
            {
                int segs = Math.Max(2, (int)Math.Ceiling(r * (a1 - a0) / chord));
                for (int k = 0; k <= segs; k++)
                {
                    float t = forward ? (float)k / segs : 1f - (float)k / segs;
                    float a = a0 + (a1 - a0) * t;
                    pts.Add(centre + (e1 * (float)Math.Cos(a) + e2 * (float)Math.Sin(a)) * r);
                }
                forward = !forward;
            }
            float am = (a0 + a1) * 0.5f;
            pts.Add(centre + (e1 * (float)Math.Cos(am) + e2 * (float)Math.Sin(am)) * 0.3f);
            for (int k = 1; k < pts.Count; k++) b.Work.Enqueue((pts[k - 1], pts[k]));
            b.SpotWork = 0; b.OnSpot = false;
        }
    }

    // The parent keeps its outer ring; lanes inside it (which the babies now cover) are removed from its plan, but only
    // lanes after the one it is working on, so its current pass is never touched.
    static void TrimParent(Family f, MoonRobotVacuum p, Il2CppSystem.Collections.Generic.List<MoonRobotVacuum.Lane> lanes, Vector3 centre, float outer)
    {
        try
        {
            int current = Math.Max(0, p.laneIndex);
            int removed = 0;
            for (int i = lanes.Count - 1; i > current; i--)
            {
                var l = lanes.get_Item(i);
                float r = ((l.a + l.b) * 0.5f - centre).magnitude;
                if (r < outer * Plugin.InnerShare.Value) { lanes.RemoveAt(i); removed++; }
            }
            if (removed > 0)
            {
                f.Trimmed += removed;
                if (!trimLogged) { trimLogged = true; Plugin.L.LogInfo($"Parent plan trimmed: {removed} inner lanes handed to the babies (parent was on lane {current} of {lanes.Count + removed})."); }
                f.PlanKey = $"{p.cell}|{lanes.Count}|{lanes.get_Item(0).a.x:0.0},{lanes.get_Item(0).a.z:0.0}";
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Could not trim parent plan: " + e.Message); }
    }
    static bool trimLogged, stealLogged;
    internal static int StolenThisMinute;

    // Work-stealing: take the parent's LAST remaining lane (the one it would reach latest), never its current or next
    // one, remove it from the parent's plan, and cover the parent's full cleaning width with narrow back-and-forth passes.
    static void TrySteal(Family f, Baby b)
    {
        var p = f.Parent;
        try
        {
            if (p.state != MoonRobotVacuum.State.Cleaning && p.state != MoonRobotVacuum.State.Aligning) return;
            var lanes = p.lanes;
            if (lanes == null) return;
            int last = lanes.Count - 1, current = Math.Max(0, p.laneIndex);
            if (last <= current + 1) { f.CanSteal = false; return; }      // nothing left that the parent isn't about to do
            // Take the parent's last lane that is still dirty (the game's own LaneDirty); clean ones would only be
            // driven for nothing (the parent passes over them too).
            int pick = -1;
            for (int i = last; i > current + 1; i--)
            {
                bool dirty = true; try { dirty = p.LaneDirty(lanes.get_Item(i)); } catch { }
                if (dirty) { pick = i; break; }
                CleanLanesPassed++;
            }
            if (pick < 0) { f.NextSteal = Time.unscaledTime + 3f; return; }
            var lane = lanes.get_Item(pick);
            lanes.RemoveAt(pick);
            var l0 = lanes.get_Item(0);
            f.PlanKey = $"{p.cell}|{lanes.Count}|{l0.a.x:0.0},{l0.a.z:0.0}";   // our own change: don't re-plan for it

            Vector3 a = lane.a, e = lane.b;
            var up = Vector3.up; try { up = p.Up((a + e) * 0.5f); } catch { }
            var dir = Vector3.ProjectOnPlane(e - a, up).normalized; var side = Vector3.Cross(up, dir);
            float W = 5.6f; try { W = p.cleaningWidth; } catch { }
            float w = Plugin.Width.Value, half = Math.Max(0f, (W - w) * 0.5f);
            int passes = Math.Max(1, (int)Math.Ceiling(W / (w * 0.8f)));
            // start from the end nearer to the baby
            if ((e - b.Pos).sqrMagnitude < (a - b.Pos).sqrMagnitude) { var t = a; a = e; e = t; side = -side; }
            Vector3 prev = b.Pos; bool first = true;
            for (int k = 0; k < passes; k++)
            {
                float off = passes == 1 ? 0f : -half + 2f * half * k / (passes - 1);
                Vector3 s0 = (k % 2 == 0 ? a : e) + side * off, s1 = (k % 2 == 0 ? e : a) + side * off;
                if (!first) b.Work.Enqueue((prev, s0));          // short sideways step between passes (cleans too)
                b.Work.Enqueue((s0, s1));
                prev = s1; first = false;
            }
            StolenThisMinute++;
            if (!stealLogged) { stealLogged = true; Plugin.L.LogInfo($"Idle baby took over a parent lane ({(e - a).magnitude:0.0} m, {passes} passes); parent now has {lanes.Count} lanes, on lane {current}."); }
        }
        catch (Exception ex) { f.CanSteal = false; Plugin.L.LogWarning("Lane take-over failed: " + ex.Message); }
    }

    [HideFromIl2Cpp]
    void Step(Family f, Baby b, float dt)
    {
        var p = f.Parent;
        if (b.Current == null && b.Work.Count == 0 && f.CanSteal && Time.unscaledTime >= f.NextSteal) TrySteal(f, b);
        if (b.Current == null && b.Work.Count == 0) TrySpot(f, b);       // nothing planned: dirt the parent can't reach
        if (b.Current == null && b.Work.Count > 0)
        {
            // Planned stretches that are already clean (the parent or another baby got there first) are dropped: the
            // baby drives straight to the next dirty one instead of crawling along at cleaning speed for nothing.
            while (b.Work.Count > 0 && StretchClean(p, b.Work.Peek().a, b.Work.Peek().b))
            {
                b.Work.Dequeue();
                if (b.SpotWork > 0) { b.SpotWork--; SpotsAlreadyClean++; }   // the parent's map was behind: already clean
                else StretchesSkipped++;
            }
            if (b.Work.Count > 0)
            {
                var l = b.Work.Dequeue();
                b.OnSpot = b.SpotWork > 0;
                if (b.OnSpot) { b.SpotWork--; SpotJobsDriven++; }
                // segments chain end-to-start; never reverse them
                b.Current = l; b.OnLane = false;
            }
            else b.OnSpot = false;
        }
        Vector3 goal;
        if (b.Current is { } lane) goal = b.OnLane ? lane.b : lane.a;
        else
        {
            if (f.TagFrame != frameNo) { f.TagFrame = frameNo; var pt = p.transform; f.TagP = pt.position; f.TagF = pt.forward; f.TagR = pt.right; }
            int idx = f.Babies.IndexOf(b);
            float side = (idx % 2 == 0 ? 1f : -1f) * (2.5f + 1.5f * (idx / 2));
            goal = f.TagP - f.TagF * 6f + f.TagR * side;   // tag along, each in its own spot
        }
        // Parked behind a parent that isn't moving (docked, waiting): nothing to update until it moves or work comes in.
        bool idle = b.Current == null && b.Held.Count == 0;
        if (idle && b.Parked && (goal - b.ParkGoal).sqrMagnitude < 0.04f) { TimeUse[3] += dt; return; }

        float boost = f.Boost;
        float speed = Plugin.Speed.Value * boost;
        if (!b.OnLane) speed *= Math.Max(1f, Plugin.TransitSpeed.Value);   // not cleaning: drive faster
        if (Plugin.PadStrokes.Value && b.OnLane)
        {
            try { var m = p.launcher?.motion; if (m != null && m.Seconds > 0) speed = Math.Max(0.2f, m.Speed(b.Scrub % m.Seconds) * Plugin.Scale.Value); } catch { }
        }
        Vector3 upHere = Vector3.up; try { upHere = p.Up(b.Pos); } catch { }
        // Distances along the ground only: planned points can sit a little above or below the real surface.
        // The parent's cleaning backlog is so long that our held path is nearly full: wait here instead of driving on
        // (driving on would mean dropping path = blank spots). Resumes as soon as the game catches up.
        bool busy = b.Held.Count > 0 && Busy(p);
        if (b.Held.Count > 0 && !busy) FlushHeld(p, b);              // queue free again: catch up right away
        if (b.Held.Count >= 18 && busy) { speed = 0f; waitedFrames++; }
        TimeUse[b.Held.Count >= 18 && busy ? 4 : b.Current == null ? 3 : !b.OnLane ? 2 : b.LastClean ? 1 : 0] += dt;
        if (b.OnSpot && b.Current != null) SpotTime += dt;
        Vector3 to = Vector3.ProjectOnPlane(goal - b.Pos, upHere); float dist = to.magnitude, step = speed * dt;
        if (b.Current != null && b.OnLane) b.SegmentTime += dt;   // driving to a stretch (can be long now) isn't "stuck"
        if (speed <= 0f) b.SegmentTime = Math.Max(0f, b.SegmentTime - dt);   // waiting doesn't count as stuck
        bool stuck = b.Current != null && speed > 0f && b.SegmentTime > 3f + 3f * (b.Current.Value.b - b.Current.Value.a).magnitude / Math.Max(0.1f, Plugin.Speed.Value);
        if (dist <= step + 0.05f || stuck)
        {
            b.Pos += to;
            if (b.Current != null)
            {
                if (!b.OnLane && !stuck) { b.OnLane = true; b.LastStroke = b.Pos; b.SegmentTime = 0; }
                else { if (b.OnLane) Stroke(p, b, true); b.Current = null; b.OnLane = false; b.SegmentTime = 0; }
            }
        }
        else b.Pos += to / dist * step;

        Vector3 ground = b.Pos;
        b.GroundTimer -= dt;
        if (b.GroundTimer <= 0f || !b.HaveGround)
        {
            b.GroundTimer = 0.05f;
            try { ground = p.GroundAt(b.Pos); b.GroundPoint = ground; b.HaveGround = true; } catch { }
        }
        else ground = b.Pos - b.Normal * Vector3.Dot(b.Pos - b.GroundPoint, b.Normal);   // stay on the last measured slope
        Vector3 radial = upHere;
        b.Pos = ground;
        // Lean with the terrain: sample the ground in front/behind/left/right of the baby (every 0.2 s).
        var heading = Vector3.ProjectOnPlane(dist > 0.01f ? to : b.Go.transform.forward, radial);
        if (heading.sqrMagnitude < 1e-4f) heading = Vector3.ProjectOnPlane(p.transform.forward, radial);
        b.NormalTimer -= dt;
        if (b.NormalTimer <= 0f && heading.sqrMagnitude > 1e-4f)
        {
            b.NormalTimer = 0.2f;
            try
            {
                float h = Math.Max(0.4f, 2.2f * Plugin.Scale.Value);          // about half the baby's length
                var f0 = heading.normalized; var r0 = Vector3.Cross(radial, f0);
                Vector3 pf = p.GroundAt(ground + f0 * h), pb = p.GroundAt(ground - f0 * h);
                Vector3 pr = p.GroundAt(ground + r0 * h), pl = p.GroundAt(ground - r0 * h);
                var n = Vector3.Cross(pf - pb, pr - pl);
                if (n.sqrMagnitude > 1e-6f) { n.Normalize(); if (Vector3.Dot(n, radial) < 0) n = -n; if (Vector3.Dot(n, radial) > 0.4f) b.Normal = n; else b.Normal = radial; }
            }
            catch { b.Normal = radial; }
        }
        var up = Vector3.Slerp(b.Go.transform.up, b.Normal, 1f - (float)Math.Exp(-10f * dt));
        var fwd = Vector3.ProjectOnPlane(heading, up);
        if (fwd.sqrMagnitude > 1e-4f)
            b.Go.transform.rotation = Quaternion.Slerp(b.Go.transform.rotation, Quaternion.LookRotation(fwd, up), 1f - (float)Math.Exp(-8f * dt));
        b.Go.transform.position = ground;
        if (idle && dist <= 0.05f) { b.ParkTime += dt; if (b.ParkTime >= 1f && !b.Parked) { b.Parked = true; b.ParkGoal = goal; } }
        else { b.ParkTime = 0f; b.Parked = false; }

        if (b.OnLane)
        {
            b.Scrub += dt; b.StrokeTimer += dt;
            if (Plugin.PadStrokes.Value) { if (b.StrokeTimer >= StrokeInterval) Stroke(p, b, false); }
            else if (b.Current is { } cur)
            {
                // One stroke per StrokeLength metres of path (1.4: every 0.6 s, so ~1.5 m pieces); the last bit of a stretch
                // goes with its final stroke. While the queue is backed up, path points are held at the same spacing.
                float len = Math.Max(1f, Plugin.StrokeLength.Value);
                Vector3 last = b.Held.Count > 0 ? b.Held[b.Held.Count - 1] : b.LastStroke;
                if (((b.Pos - last).sqrMagnitude >= len * len && (cur.b - b.Pos).sqrMagnitude >= 0.36f) || b.StrokeTimer >= 2.5f)
                    Stroke(p, b, false);
            }
        }
    }

    // ---------- dirt left in finished sectors ----------
    // J.O.O.N.A.S. maps one sector at a time (MoonRobotVacuum.map: ~1000 ground points 1 m apart, Kind.Dusty and
    // dirty[] per point). When it finishes a sector, the points still dirty are what the game adds to LeftInDoneHexes:
    // dirt beyond its brush reach (next to structures, under conveyors) or given up after 5 passes - about 10 points per
    // sector. In the same frame MapFor(next cell) replaces the map, so the finished sector's map is read in a prefix on
    // MapFor (LeftoverHook) and its dirty points are kept; idle babies sweep them (they have no collision).
    internal static readonly Dictionary<IntPtr, Family> ByParent = new();
    internal static int SpotJobs, SpotJobsDriven, SpotsAlreadyClean, SpotStrokes, SpotEmpty, SpotPoints;
    internal static int SectorsSeen, SectorsMissed, PointsKept, PointsCounted, PointsExpired, PointsDropped;
    internal static double SpotKg, SpotKgTotal, SpotTime, HarvestMs;
    static float spotPausedUntil;
    static bool spotLogged, harvestLogged;
    static readonly Stopwatch harvestWatch = new();
    const int MaxLeftovers = 400;                     // per J.O.O.N.A.S.; oldest dropped first
    const float LeftoverSeconds = 1800f, MaxSpotDistance = 250f;

    static bool SpotsOn => Plugin.OutOfReach.Value && Plugin.Clean.Value && Mode == 1 && Time.unscaledTime >= spotPausedUntil;

    // Called from the MapFor prefix (main thread): the robot is about to map cell c.
    internal static unsafe void Harvest(MoonRobotVacuum r, int c)
    {
        if (!ByParent.TryGetValue(r.Pointer, out var f) || f.Babies.Count == 0) return;
        if (c == f.MapForCell) return;                    // the same cell asked for again: nothing is replaced
        f.MapForCell = c;
        int done = r.HexesDone, left = r.LeftInDoneHexes;
        if (f.HarvestDone == int.MinValue || done < f.HarvestDone) { f.HarvestDone = done; f.HarvestLeft = left; return; }   // first look, or a reload
        if (done == f.HarvestDone) return;                // no sector finished since the last map: the robot is only looking around
        int counted = Math.Max(0, left - f.HarvestLeft);
        f.HarvestDone = done; f.HarvestLeft = left;
        SectorsSeen++; PointsCounted += counted;
        if (!SpotsOn) return;
        harvestWatch.Restart();
        try
        {
            var map = r.map;
            int cell = r.mapCell;
            // the map must still be the one of the sector it was working on (Plan reads mapCell 4x a second)
            if (map == null || !map.Ready || cell != f.WorkCell || cell == c) { SectorsMissed++; return; }
            var kind = map.kind; var dirty = map.dirty;
            if (kind == null || dirty == null) { SectorsMissed++; return; }
            int n = Math.Min(kind.Length, dirty.Length), added = 0;
            int head = 4 * IntPtr.Size;                   // IL2CPP array data starts after the header (object header, bounds, length)
            byte* kindP = (byte*)(kind.Pointer + head), dirtyP = (byte*)(dirty.Pointer + head);
            float now = Time.unscaledTime;
            for (int k = 0; k < n; k++)
            {
                if (kindP[k] != (byte)MoonRobotDustMap.Kind.Dusty || dirtyP[k] == 0) continue;
                if (f.Leftovers.Count >= MaxLeftovers) { f.Leftovers.RemoveAt(0); PointsDropped++; }
                f.Leftovers.Add((map.Ground(k), now));
                added++;
            }
            PointsKept += added;
            if (!harvestLogged && (added > 0 || counted > 0))
            {
                harvestLogged = true;
                Plugin.L.LogInfo($"Dirt left in a finished sector (robot dock {f.DockId}, sector {cell}, next {c}): {added} dirty map points kept for its babies (the game counted {counted} left).");
            }
        }
        finally { HarvestMs += harvestWatch.Elapsed.TotalMilliseconds; }
    }

    // An idle baby takes the nearest kept point and sweeps one stroke through it, along its way in; every kept point under
    // that stroke is taken off the list. Points are kept at most 30 minutes (someone else may have cleaned them).
    static void TrySpot(Family f, Baby b)
    {
        if (!SpotsOn || f.PlanWaiting || f.Leftovers.Count == 0) return;
        float now = Time.unscaledTime;
        if (now < b.NextSpot) return;
        b.NextSpot = now + 0.5f;
        var p = f.Parent;
        if (Busy(p)) return;                       // the cleaning queue is backed up: its stroke would only be held
        var L = f.Leftovers;
        int keep = 0;
        for (int i = 0; i < L.Count; i++) { if (now - L[i].at <= LeftoverSeconds) L[keep++] = L[i]; else PointsExpired++; }
        if (keep < L.Count) L.RemoveRange(keep, L.Count - keep);
        int best = -1; float bd = MaxSpotDistance * MaxSpotDistance;
        for (int i = 0; i < L.Count; i++) { float d = (L[i].pos - b.Pos).sqrMagnitude; if (d < bd) { bd = d; best = i; } }
        if (best < 0) return;
        Vector3 pos = L[best].pos;
        Vector3 up = Vector3.up; try { up = p.Up(pos); } catch { }
        var dir = Vector3.ProjectOnPlane(pos - b.Pos, up);
        if (dir.sqrMagnitude < 0.01f) dir = Vector3.ProjectOnPlane(p.transform.forward, up);
        dir.Normalize();
        float half = Math.Max(0.75f, Plugin.StrokeLength.Value * 0.5f);
        Vector3 a = pos - dir * half, e = pos + dir * half, seg = e - a;
        float r = Plugin.Width.Value * 0.5f + 0.1f, segLen2 = Math.Max(1e-4f, seg.sqrMagnitude);
        int covered = 0; keep = 0;
        for (int i = 0; i < L.Count; i++)
        {
            Vector3 q = L[i].pos;
            float t = Math.Clamp(Vector3.Dot(q - a, seg) / segLen2, 0f, 1f);
            if (i == best || Vector3.ProjectOnPlane(q - (a + seg * t), up).sqrMagnitude <= r * r) { covered++; continue; }   // this stroke covers it
            L[keep++] = L[i];
        }
        L.RemoveRange(keep, L.Count - keep);
        b.Work.Enqueue((a, e)); b.SpotWork = 1;
        SpotJobs++; SpotPoints += covered;
        if (!spotLogged) { spotLogged = true; Plugin.L.LogInfo($"First job on dirt left in a finished sector: a baby drives {Math.Sqrt(bd):0} m ({covered} points under one stroke; {L.Count} more kept for this J.O.O.N.A.S.)."); }
    }

    static Il2CppSystem.Action<int, double> SpotCallback() =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int, double>>(new Action<int, double>((changed, kg) =>
        {
            try
            {
                StrokesThisMinute++; CompletedTotal++; SpotStrokes++;
                if (kg <= 0) { EmptyThisMinute++; SpotEmpty++; return; }
                KgThisMinute += kg; KgTotal += kg; SpotKg += kg; SpotKgTotal += kg;
            }
            catch { }
        }));

    // Stage 1b: clean the strip from the last stroke point to here. Settings copied from the parent's own pad strokes
    // (probe: width 0.92, depth 0.92, dose 1, edge 0.05, callback). No inventory/storage id is passed, so the game
    // can't credit the dust anywhere else; the callback credits it to the parent with its own Cleaned(changed, kg).
    // Mode 0: no inventory/storage, credit via callback -> parent.Cleaned. Mode 1: exactly the parent's own arguments
    // (inventory + storage), credit left to the game. This game version rejects mode 0 (no storage id), so we start in
    // mode 1; the switch below stays as a fallback.
    internal static int Mode = 1; static int attempts, noArgs, queued, rejected, detailed, deferred; static double completedAtSwitch;
    static readonly StrokeArgs Fallback = new StrokeArgs { Width = 0.92f, Depth = 0.92f, Dose = 1f, Edge = 0.05f, Inventory = "local" };

    static void Stroke(MoonRobotVacuum p, Baby b, bool final)
    {
        b.StrokeTimer = 0;
        if (!Plugin.Clean.Value) { b.LastStroke = b.Pos; return; }
        var from = b.LastStroke; var to = b.Pos;
        if ((to - from).sqrMagnitude < 0.01f) return;
        var storage = p.storageId;
        var w0 = p.world;
        bool backedUp = false;
        // Babies only use spare cleaning capacity: hold back when the parent's own strokes are backed up OR when the
        // game's whole cleaning queue is busy (it processes a limited amount per frame; parents get priority).
        try { backedUp = w0 != null && storage != null && (w0.RobotStrokesWaitingSeconds(storage) > BacklogLimit || w0.PendingToolStrokes > Plugin.MaxPending.Value); } catch { }
        if (backedUp)
        {
            // Keep the exact path (not one straight line that would cut across the arcs) and clean it later.
            deferred++;
            if (b.Held.Count == 0 || (b.Held[b.Held.Count - 1] - to).sqrMagnitude > 0.04f) b.Held.Add(to);
            if (b.Held.Count > 24) { b.Held.RemoveAt(0); droppedHeld++; }
            return;
        }
        if (b.Held.Count > 0)
        {
            // Backlog cleared: clean the held path as a few strokes along its corners, then carry on.
            var pts = new List<Vector3> { from }; pts.AddRange(b.Held); b.Held.Clear();
            if ((pts[pts.Count - 1] - to).sqrMagnitude > 0.01f) pts.Add(to);
            for (int k = 1; k < pts.Count; k++) { StripStroke(p, b, pts[k - 1], pts[k], storage); }
            b.LastStroke = to;
            return;
        }
        b.LastStroke = to;
        attempts++;
        if (string.IsNullOrEmpty(storage) || !StrokeProbe.ByStorage.TryGetValue(storage, out var a))
        {
            noArgs++;
            a = null; foreach (var v in StrokeProbe.ByStorage.Values) { a = v; break; }   // all J.O.O.N.A.S. use the same settings
            a ??= Fallback;
        }
        var w = p.world; if (w == null) return;
        if (b.Done == null)
        {
            var parent = p;
            b.Done = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int, double>>(new Action<int, double>((changed, kg) =>
            {
                try
                {
                    StrokesThisMinute++; CompletedTotal++;
                    if (kg <= 0) { EmptyThisMinute++; return; }
                    KgThisMinute += kg; KgTotal += kg;
                    // In mode 1 the game credits the storage itself; only credit by hand in mode 0.
                    if (Mode == 0 && Plugin.CreditParent.Value && parent != null && parent.Pointer != IntPtr.Zero) parent.Cleaned(changed, kg);
                }
                catch (Exception e) { Plugin.L.LogWarning("Baby stroke callback: " + e.Message); }
            }));
        }
        Vector3 up = p.Up(to), right = Vector3.Cross(up, (to - from).normalized).normalized;
        float edge = EdgeWidth;                          // soft, rounded edges instead of a hard rectangle
        if (Mode == 1 && Plugin.PadStrokes.Value && PadStroke(p, b, w, a, storage)) return;
        bool audit = false;
        if (Mode == 1 && SkipAsClean(p, b, w, from, to, right, out audit)) { b.LastClean = true; return; }
        b.LastClean = false;
        if (audit) b.AuditDone ??= AuditCallback(p);
        if (b.OnSpot) b.SpotDone ??= SpotCallback();
        bool ok;
        StrokeProbe.Ours = true;
        try
        {
            ok = Mode == 0
                ? w.QueueToolStroke(b.StrokeId, from, to, right, right, Plugin.Width.Value, a.Depth, a.Dose, edge, Vector3.zero, b.Done, null, null, null)
                : w.QueueToolStroke(audit ? b.StrokeId + 0x40000 : b.StrokeId, from, to, right, right, Plugin.Width.Value, a.Depth, a.Dose, edge, Vector3.zero, audit ? b.AuditDone : b.OnSpot ? b.SpotDone : b.Done, a.Inventory, storage, a.Credit);
        }
        finally { StrokeProbe.Ours = false; }
        if (ok) queued++; else rejected++;
        if (detailed < 6)
        {
            detailed++;
            Plugin.L.LogInfo($"[baby stroke] mode {Mode}: queued={ok} storage='{storage}' argsFromParent={(a != Fallback && StrokeProbe.ByStorage.ContainsKey(storage ?? ""))} len={(to - from).magnitude:0.##} m width={Plugin.Width.Value} depth={a.Depth} dose={a.Dose} edge={edge}");
        }
        // Switch to the parent's exact arguments if mode 0 is refused, or accepted but never completes.
        if (Mode == 0 && ((rejected >= 20 && queued == 0) || (queued >= 40 && CompletedTotal == 0)))
        {
            Mode = 1; detailed = 0;
            Plugin.L.LogWarning($"Baby strokes without a storage id don't clean ({queued} queued, {rejected} rejected, {CompletedTotal} completed). Switching to the parent's own stroke arguments.");
            queued = rejected = 0; completedAtSwitch = CompletedTotal;
        }
    }

    internal static double CompletedTotal;
    static int droppedHeld; internal static int waitedFrames;
    static float EdgeWidth => Math.Clamp(Plugin.Edge.Value, 0f, Plugin.Width.Value * 0.3f);

    // Parent backlog (seconds) above which babies hold back. Automatic: 80% of the game's own limit, past which it
    // slows the J.O.O.N.A.S. down (MoonRobotVacuum.StrokesBehindSeconds). Baby strokes count towards the parent's
    // backlog (same storage), so this keeps babies from ever being the reason a parent slows down.
    static float backlogLimit = -1f;
    internal static float BacklogLimit
    {
        get
        {
            if (Plugin.MaxBacklog.Value > 0f) return Plugin.MaxBacklog.Value;
            if (backlogLimit < 0f)
            {
                float game = 1.5f; bool hold = true;
                try { game = MoonRobotVacuum.StrokesBehindSeconds; hold = MoonRobotVacuum.HoldForStrokesBehind; } catch { }
                if (!(game > 0.1f && game < 60f)) game = 1.5f;
                backlogLimit = game * 0.8f;
                Plugin.L.LogInfo($"Babies hold back when their parent is {backlogLimit:0.00} s behind (the game slows a J.O.O.N.A.S. at {game:0.00} s{(hold ? "" : ", but that is switched off in this build")}).");
            }
            return backlogLimit;
        }
    }

    // ---------- skipping ground that is already clean ----------
    static bool skipOn = true; static int skipChecks;
    internal static int Skipped, Audits, AuditsDone, AuditsDusty, CleanChecks;
    static readonly int[] dustHist = new int[5];   // max reading per stroke vs the limit: 0, <=limit, <=10x, <=100x, more
    static double sampleMs;
    static readonly Stopwatch sampleWatch = new();

    // Which of the game's dust readings says "clean" on cleaned ground? Calibrated each session: a reading at a stroke's
    // middle when the stroke is sent ("before") and again 8 s later once the queue has applied it ("after"). The one
    // that reads ~0 after but not before is used. (DustAt alone turned out to read >= 1 nearly everywhere.)
    static readonly string[] CandNames = { "DustAt", "RemainingAt", "RawRemainingAt", "SharedRemaining", "StrokeRemaining", "CompletedRemaining" };
    static float Cand(MoonWorld w, int i, Vector3 d) => i switch
    {
        0 => w.DustAt(d), 1 => w.RemainingAt(d), 2 => w.RawRemainingAt(d), 3 => w.SharedRemaining(d), 4 => w.StrokeRemaining(d), _ => w.CompletedRemaining(d),
    };
    static readonly bool[] candBroken = new bool[6];
    static int chosen = -1, rounds, probeTick; static float chosenLimit; static bool confirming;

    // RemainingAt (1 = untouched, 0 = clean) was picked by the automatic check in every session so far: use it from the
    // start, and keep checking it on strokes that are sent (before -> 8 s after) until it is confirmed or overruled.
    static void TrustDefault()
    {
        if (!Plugin.TrustRemainingAt.Value || !Plugin.SkipClean.Value || chosen >= 0) return;
        chosen = 1; chosenLimit = Plugin.CleanBelow.Value; confirming = true;
        Plugin.L.LogInfo($"Babies skip ground where RemainingAt <= {chosenLimit} from the start (picked in every session so far); still checking it this session.");
    }
    sealed class Probe { public float T; public Vector3 Dir; public float[] Before; public MoonWorld W; }
    static readonly Queue<Probe> probes = new();
    static readonly List<float>[] before = { new(), new(), new(), new(), new(), new() }, after = { new(), new(), new(), new(), new(), new() };

    static float[] SampleAll(MoonWorld w, Vector3 dir)
    {
        var v = new float[CandNames.Length];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = float.NaN;
            if (candBroken[i]) continue;
            try { v[i] = Cand(w, i, dir); }
            catch (Exception e) { candBroken[i] = true; Plugin.L.LogInfo($"Dust reading {CandNames[i]} not usable ({e.GetType().Name}: {e.Message.Split('\n')[0].Trim()})."); }
        }
        return v;
    }

    internal static void PumpProbes()
    {
        if ((chosen >= 0 && !confirming) || !skipOn || probes.Count == 0) return;
        float now = Time.realtimeSinceStartup;
        while (probes.Count > 0 && probes.Peek().T + 8f <= now)
        {
            var pr = probes.Dequeue();
            try
            {
                if (pr.W == null || pr.W.WasCollected || pr.W.PendingToolStrokes > 200) continue;   // stroke may not be applied yet
                var a = SampleAll(pr.W, pr.Dir);
                for (int i = 0; i < a.Length; i++)
                    if (!float.IsNaN(pr.Before[i]) && !float.IsNaN(a[i]) && !float.IsInfinity(a[i]) && !float.IsInfinity(pr.Before[i])) { before[i].Add(pr.Before[i]); after[i].Add(a[i]); }
            }
            catch { }
        }
        int n = 0; foreach (var l in before) n = Math.Max(n, l.Count);
        if (n >= 80) Choose();
    }

    static float Pct(List<float> l, float q) { if (l.Count == 0) return float.NaN; var c = new List<float>(l); c.Sort(); return c[(int)(q * (c.Count - 1))]; }

    static void Choose()
    {
        rounds++;
        var sb = new System.Text.StringBuilder($"Dust readings, before -> 8 s after a baby stroke (round {rounds}):");
        int best = -1; float bestScore = 0f, bestLim = 0f;
        for (int i = 0; i < CandNames.Length; i++)
        {
            int n = before[i].Count;
            if (candBroken[i] || n < 40) { sb.Append($" {CandNames[i]}: {(candBroken[i] ? "not usable" : "too few")};"); continue; }
            float p90 = Pct(before[i], 0.9f);
            if (!(p90 > 0f)) { sb.Append($" {CandNames[i]}: always 0;"); continue; }
            float lim = Plugin.CleanBelow.Value * p90;
            int bc = 0, ac = 0; for (int k = 0; k < n; k++) { if (before[i][k] <= lim) bc++; if (after[i][k] <= lim) ac++; }
            float bClean = (float)bc / n, aClean = (float)ac / n, score = aClean - bClean;
            sb.Append($" {CandNames[i]}: before median {Pct(before[i], 0.5f):0.####} (90% {p90:0.####}), after median {Pct(after[i], 0.5f):0.####}; clean before {100 * bClean:0}%, after {100 * aClean:0}%;");
            if (aClean >= 0.7f && score >= 0.15f && score > bestScore) { best = i; bestScore = score; bestLim = lim; }
        }
        Plugin.L.LogInfo(sb.ToString());
        if (best >= 0)
        {
            bool same = confirming && best == chosen;
            chosen = best; chosenLimit = bestLim; probes.Clear(); confirming = false;
            Plugin.L.LogInfo(same ? $"Clean-ground reading confirmed: {CandNames[chosen]} <= {chosenLimit:0.#####}." : $"Babies now skip ground where {CandNames[chosen]} <= {chosenLimit:0.#####} (counts as already clean).");
        }
        else if (rounds >= 3) { skipOn = false; confirming = false; probes.Clear(); Plugin.L.LogWarning("None of the game's dust readings tells clean from dirty ground reliably; babies send every stroke."); }
        foreach (var l in before) l.Clear();
        foreach (var l in after) l.Clear();
    }

    // Highest chosen dust reading along and across the strip (7 samples).
    static float MaxDust(MoonRobotVacuum p, MoonWorld w, Vector3 from, Vector3 to, Vector3 right)
    {
        float half = Plugin.Width.Value * 0.4f, max = 0f;
        Vector3 mid = (from + to) * 0.5f;
        Span<Vector3> pts = stackalloc Vector3[7] { from, mid, to, mid + right * half, mid - right * half, to + right * half, to - right * half };
        foreach (var q in pts)
        {
            float d = Cand(w, chosen, p.Up(q));
            if (d > max) max = d;
        }
        return max;
    }

    // Is a planned stretch already clean? 7 readings along and across it. At most 24 checks a frame for all babies.
    static bool StretchClean(MoonRobotVacuum p, Vector3 a, Vector3 b)
    {
        if (!Plugin.SkipStretches.Value || !Plugin.SkipClean.Value || !skipOn || chosen < 0 || Mode != 1) return false;
        if (stretchChecksThisFrame >= 24) return false;
        stretchChecksThisFrame++;
        var w = p.world; if (w == null) return false;
        sampleWatch.Restart();
        try
        {
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-4f) return true;   // nothing to drive
            Vector3 mid = (a + b) * 0.5f, up = p.Up(mid), right = Vector3.Cross(up, d.normalized).normalized;
            float half = Plugin.Width.Value * 0.4f, lim = chosenLimit;
            Span<Vector3> pts = stackalloc Vector3[7] { mid, a + d * 0.25f, a + d * 0.75f, b, a, mid + right * half, mid - right * half };
            foreach (var q in pts) { float v = Cand(w, chosen, p.Up(q)); if (!(v <= lim)) return false; }
            return true;
        }
        catch { return false; }
        finally { sampleMs += sampleWatch.Elapsed.TotalMilliseconds; }
    }

    // true = skip this stroke. Every 10th clean-looking stroke is still sent (spot-check, scored by AuditDone).
    static bool SkipAsClean(MoonRobotVacuum p, Baby b, MoonWorld w, Vector3 from, Vector3 to, Vector3 right, out bool audit)
    {
        audit = false;
        if (!Plugin.SkipClean.Value || !skipOn) return false;
        if (chosen < 0)
        {
            // still calibrating: sample every 3rd stroke, skip nothing
            if (++probeTick % 3 == 0 && probes.Count < 300)
            {
                sampleWatch.Restart();
                try { var dir = p.Up((from + to) * 0.5f); probes.Enqueue(new Probe { T = Time.realtimeSinceStartup, Dir = dir, Before = SampleAll(w, dir), W = w }); }
                catch { }
                finally { sampleMs += sampleWatch.Elapsed.TotalMilliseconds; }
            }
            return false;
        }
        float m;
        sampleWatch.Restart();
        try { m = MaxDust(p, w, from, to, right); }
        catch (Exception e) { skipOn = false; Plugin.L.LogWarning("Clean-ground check failed, babies send every stroke: " + e.Message); return false; }
        finally { sampleMs += sampleWatch.Elapsed.TotalMilliseconds; }
        CleanChecks++;
        float lim = chosenLimit;
        dustHist[m <= 0f ? 0 : m <= lim ? 1 : m <= lim * 10f ? 2 : m <= lim * 100f ? 3 : 4]++;
        if (m > lim)
        {
            // confirming the trusted reading: sample some strokes that are sent (dirty before, should read clean after)
            if (confirming && ++probeTick % 3 == 0 && probes.Count < 300)
            {
                try { var dir = p.Up((from + to) * 0.5f); probes.Enqueue(new Probe { T = Time.realtimeSinceStartup, Dir = dir, Before = SampleAll(w, dir), W = w }); } catch { }
            }
            return false;
        }
        if (++skipChecks % 10 == 0) { audit = true; Audits++; return false; }
        Skipped++;
        return true;
    }

    static void ScoreAudits()
    {
        if (!skipOn || AuditsDone < 20) return;
        if (AuditsDusty > AuditsDone / 5)
        {
            skipOn = false;
            Plugin.L.LogWarning($"Clean-ground check was wrong too often ({AuditsDusty} of {AuditsDone} spot-checks still found dust); babies now send every stroke.");
        }
    }

    internal static Il2CppSystem.Action<int, double> AuditCallback(MoonRobotVacuum parent) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int, double>>(new Action<int, double>((changed, kg) =>
        {
            try
            {
                AuditsDone++; StrokesThisMinute++; CompletedTotal++;
                if (kg > 0.02) AuditsDusty++;
                if (kg <= 0) { EmptyThisMinute++; return; }
                KgThisMinute += kg; KgTotal += kg;
                if (Mode == 0 && Plugin.CreditParent.Value && parent != null && parent.Pointer != IntPtr.Zero) parent.Cleaned(changed, kg);
            }
            catch { }
        }));

    internal static bool Busy(MoonRobotVacuum p)
    {
        try
        {
            var w = p.world; var storage = p.storageId;
            return w != null && storage != null && (w.RobotStrokesWaitingSeconds(storage) > BacklogLimit || w.PendingToolStrokes > Plugin.MaxPending.Value);
        }
        catch { return false; }
    }

    // Clean the path held back while the queue was busy (piece by piece along its corners).
    internal static void FlushHeld(MoonRobotVacuum p, Baby b)
    {
        if (b.Held.Count == 0) return;
        var storage = p.storageId;
        var pts = new List<Vector3> { b.LastStroke }; pts.AddRange(b.Held); b.Held.Clear();
        for (int k = 1; k < pts.Count; k++) StripStroke(p, b, pts[k - 1], pts[k], storage);
        b.LastStroke = pts[pts.Count - 1];
    }

    static void StripStroke(MoonRobotVacuum p, Baby b, Vector3 from, Vector3 to, string storage)
    {
        if ((to - from).sqrMagnitude < 0.01f) return;
        var w = p.world; if (w == null) return;
        StrokeArgs a = null;
        if (storage == null || !StrokeProbe.ByStorage.TryGetValue(storage, out a)) { foreach (var v in StrokeProbe.ByStorage.Values) { a = v; break; } }
        a ??= Fallback;
        var up = b.Normal; var right = Vector3.Cross(up, (to - from).normalized).normalized;
        float edge = EdgeWidth;
        bool audit = false;
        if (Mode == 1 && SkipAsClean(p, b, w, from, to, right, out audit)) { b.LastClean = true; return; }
        b.LastClean = false;
        if (audit) b.AuditDone ??= AuditCallback(p);
        if (b.OnSpot) b.SpotDone ??= SpotCallback();
        StrokeProbe.Ours = true;
        try
        {
            bool ok = Mode == 0
                ? w.QueueToolStroke(b.StrokeId, from, to, right, right, Plugin.Width.Value, a.Depth, a.Dose, edge, Vector3.zero, b.Done, null, null, null)
                : w.QueueToolStroke(audit ? b.StrokeId + 0x40000 : b.StrokeId, from, to, right, right, Plugin.Width.Value, a.Depth, a.Dose, edge, Vector3.zero, audit ? b.AuditDone : b.OnSpot ? b.SpotDone : b.Done, a.Inventory, storage, a.Credit);
            attempts++; if (ok) queued++; else rejected++;
        }
        finally { StrokeProbe.Ours = false; }
    }

    // Like the parent: every pad of the J.O.O.N.A.S. scrub motion sweeps its own small round path, scaled to baby size.
    static bool PadStroke(MoonRobotVacuum p, Baby b, MoonWorld w, StrokeArgs a, string storage)
    {
        MoonJoonasMotion m = null;
        try { m = p.launcher?.motion; } catch { }
        if (m == null) return false;
        int pads = m.padCount; float cycle = m.Seconds;
        if (pads <= 0 || cycle <= 0) return false;
        var t = b.Go.transform; float sc = Plugin.Scale.Value;
        if (b.PadLast == null || b.PadLast.Length != pads)
        {
            b.PadLast = new Vector3[pads];
            for (int i = 0; i < pads; i++) b.PadLast[i] = t.TransformPoint(m.Pad(i, b.Scrub % cycle));
            return true;
        }
        float time = b.Scrub % cycle;
        float width = a.Width * sc, depth = a.Depth * sc, edge = Math.Min(a.Edge, width * 0.25f);
        StrokeProbe.Ours = true;
        try
        {
            for (int i = 0; i < pads; i++)
            {
                var to = t.TransformPoint(m.Pad(i, time)); var from = b.PadLast[i]; b.PadLast[i] = to;
                if ((to - from).sqrMagnitude < 1e-4f) continue;
                var up = b.Normal; var right = Vector3.Cross(up, (to - from).normalized).normalized;
                bool ok = w.QueueToolStroke(b.StrokeId + i * 0x10000, from, to, right, right, width, depth, a.Dose, edge, Vector3.zero, b.Done, a.Inventory, storage, a.Credit);
                if (ok) queued++; else rejected++;
                padStrokes++;
            }
        }
        finally { StrokeProbe.Ours = false; }
        return true;
    }
    static int padStrokes;

    void Report()
    {
        if (!Plugin.Clean.Value) return;
        Plugin.L.LogInfo($"Babies last minute: {KgThisMinute:0.#} kg cleaned in {StrokesThisMinute} completed strokes ({EmptyThisMinute} found no dust); {KgTotal:0} kg this session. Stroke mode {Mode}; attempts {attempts}, queued {queued}, rejected {rejected}, held back for parent backlog {deferred} (dropped {droppedHeld}, babies waited {waitedFrames} baby-frames), pad strokes {padStrokes}, without parent args {noArgs}.");
        Plugin.L.LogInfo($"Babies cost: {totalMs / Math.Max(1, frames):0.00} ms per frame on average, worst frame {maxMs:0.0} ms, over {frames} frames.");
        Plugin.L.LogInfo($"Babies took over {StolenThisMinute} parent lanes last minute (passed over {CleanLanesPassed} lanes that were already clean); skipped {StretchesSkipped} planned stretches that were already clean.");
        double tu = 0; foreach (var x in TimeUse) tu += x;
        if (tu > 0) Plugin.L.LogInfo($"Baby time last minute: cleaning dirty ground {100 * TimeUse[0] / tu:0}%, over clean ground {100 * TimeUse[1] / tu:0}%, driving to the next job {100 * TimeUse[2] / tu:0}%, following the parent with nothing to do {100 * TimeUse[3] / tu:0}%, waiting for the cleaning queue {100 * TimeUse[4] / tu:0}%.");
        if (Plugin.OutOfReach.Value)
        {
            int kept = 0, fams = 0;
            foreach (var fam in families.Values) if (fam.Leftovers.Count > 0) { kept += fam.Leftovers.Count; fams++; }
            string paused = Time.unscaledTime < spotPausedUntil ? " PAUSED for a few minutes." : "";
            Plugin.L.LogInfo($"Dirt left in finished sectors: last minute {SectorsSeen} sectors finished ({SectorsMissed} not readable in time), {PointsKept} dirty points kept (the game counted {PointsCounted}), {PointsExpired} expired, {PointsDropped} dropped; {kept} points waiting for {fams} of {families.Count} J.O.O.N.A.S. Babies started {SpotJobs} jobs over {SpotPoints} points ({SpotJobsDriven} driven, {SpotsAlreadyClean} already clean on arrival); {SpotStrokes} strokes collected {SpotKg:0.#} kg ({SpotEmpty} found none), {SpotKgTotal:0} kg this session; {(tu > 0 ? 100 * SpotTime / tu : 0):0}% of baby time; {HarvestMs:0.0} ms.{paused}");
            // Spot strokes that keep finding nothing: the targets aren't cleanable (e.g. under structures) - pause.
            if (SpotStrokes >= 40 && SpotEmpty > SpotStrokes * 0.8)
            {
                spotPausedUntil = Time.unscaledTime + 300f;
                Plugin.L.LogWarning($"Jobs on dirt left in finished sectors paused for 5 minutes: {SpotEmpty} of {SpotStrokes} of their strokes found no dust.");
            }
            SpotJobs = SpotJobsDriven = SpotsAlreadyClean = SpotStrokes = SpotEmpty = SpotPoints = 0; SpotKg = 0; SpotTime = 0; HarvestMs = 0;
            SectorsSeen = SectorsMissed = PointsKept = PointsCounted = PointsExpired = PointsDropped = 0;
        }
        Array.Clear(TimeUse, 0, TimeUse.Length); CleanLanesPassed = StretchesSkipped = 0;
        ScoreAudits();
        if (Plugin.SkipClean.Value)
            Plugin.L.LogInfo(chosen < 0 && skipOn
                ? $"Clean-ground check: calibrating ({probes.Count} readings waiting, {sampleMs:0.0} ms spent)."
                : $"Clean-ground check ({(skipOn ? "on, " + CandNames[Math.Max(0, chosen)] + (confirming ? ", being confirmed" : "") : "OFF")}): {CleanChecks} strokes checked in {sampleMs:0.0} ms, {Skipped} skipped as clean; spot-checks {AuditsDone}/{Audits} done, {AuditsDusty} found dust. Highest reading per stroke: 0 x{dustHist[0]}, <=limit x{dustHist[1]}, <=10x x{dustHist[2]}, <=100x x{dustHist[3]}, more x{dustHist[4]}.");
        CleanChecks = Skipped = 0; sampleMs = 0; Array.Clear(dustHist, 0, dustHist.Length);
        KgThisMinute = 0; StrokesThisMinute = EmptyThisMinute = 0; frames = 0; totalMs = maxMs = 0; StolenThisMinute = 0;
    }

    void OnDestroy() { foreach (var f in families.Values) Clear(f); }
}
