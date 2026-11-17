using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.BabyJoonas;

// Baby J.O.O.N.A.S. - STAGE 1 PROTOTYPE (see DESIGN.md). Everything is OFF by default.
//
//  Stage 0  ProbeStrokes = true : logs the arguments J.O.O.N.A.S. passes to MoonWorld.QueueToolStroke
//                                (player id, width, depth, dose, edge, credit, storage) - needed for stage 1b.
//  Stage 1a Enabled = true      : small visual copies follow each J.O.O.N.A.S. and drive the inner rings of
//                                the sector it is cleaning. Visual only, no cleaning, nothing saved.
//  Stage 1b Clean = true        : babies also clean, copying the parent's last observed stroke settings and
//                                crediting the dust to the parent's storage, so the parent launches it.
//                                The parent then skips inner-ring lanes that are already clean (assumed:
//                                NextLane uses LaneDirty) - no game AI is changed.
[BepInPlugin("moonsweepers.babyjoonas", "Baby J.O.O.N.A.S. (prototype)", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Enabled, Clean, ProbeStrokes;
    internal static ConfigEntry<int> BabiesPerRobot;
    internal static ConfigEntry<float> Scale, Speed, Width, InnerShare;

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", false, "Stage 1a: spawn visual babies that drive the inner rings of their parent's sector.");
        Clean = Config.Bind("General", "Clean", false, "Stage 1b: babies also clean (needs ProbeStrokes data from the parent first; see DESIGN.md).");
        ProbeStrokes = Config.Bind("Diagnostics", "ProbeStrokes", false, "Stage 0: log the stroke arguments J.O.O.N.A.S. uses (Harmony hook on MoonWorld.QueueToolStroke, main thread only).");
        BabiesPerRobot = Config.Bind("Babies", "PerRobot", 2, "Babies per J.O.O.N.A.S.");
        Scale = Config.Bind("Babies", "Scale", 0.35f, "Size relative to the parent model.");
        Speed = Config.Bind("Babies", "Speed", 1.5f, "Driving speed in m/s.");
        Width = Config.Bind("Babies", "CleaningWidth", 2.0f, "Cleaning width in metres (only used when Clean = true).");
        InnerShare = Config.Bind("Babies", "InnerRadiusShare", 0.7f, "Lanes whose middle is closer to the sector centre than this share of the outermost lane's radius count as inner rings.");

        if (!Enabled.Value && !ProbeStrokes.Value) { L.LogInfo("Baby J.O.O.N.A.S. prototype loaded, all stages off (see config)."); return; }
        try
        {
            if (ProbeStrokes.Value || Clean.Value) new Harmony("moonsweepers.babyjoonas").PatchAll(typeof(StrokeProbe));
            if (Enabled.Value) { ClassInjector.RegisterTypeInIl2Cpp<BabyDriver>(); AddComponent<BabyDriver>(); }
            L.LogInfo($"Baby J.O.O.N.A.S. prototype: babies {(Enabled.Value ? "on" : "off")}, cleaning {(Clean.Value ? "on" : "off")}, stroke probe {(ProbeStrokes.Value || Clean.Value ? "on" : "off")}.");
        }
        catch (Exception e) { L.LogError("Baby J.O.O.N.A.S. failed to start: " + e); }
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
            var key = $"{player}|{width:0.##}|{depth:0.##}|{dose:0.##}|{edgeWidth:0.##}|{credit}|{inventoryId}|{completed != null}|{Prefix(vehicleStorageId)}";
            if (logged.Count < 200 && logged.Add(key))
                Plugin.L.LogInfo($"[stroke] player={player} width={width:0.###} depth={depth:0.###} dose={dose:0.###} edge={edgeWidth:0.###} callback={completed != null} inventory='{inventoryId}' storage='{vehicleStorageId}' credit='{credit}'");
        }
        catch { }
    }

    static string Prefix(string s) { int i = s.IndexOf('/'); return i > 0 ? s.Substring(0, i) : s; }
}

internal sealed class Baby
{
    public GameObject Go;
    public Vector3 Pos;
    public readonly Queue<(Vector3 a, Vector3 b)> Work = new();
    public (Vector3 a, Vector3 b)? Current;
    public bool OnLane;          // false = driving to the lane start
    public Vector3 LastStroke; public float StrokeTimer;
}

internal sealed class Family
{
    public int DockId; public MoonRobotVacuum Parent; public int Cell = int.MinValue;
    public readonly List<Baby> Babies = new();
}

public class BabyDriver : MonoBehaviour
{
    public BabyDriver(IntPtr ptr) : base(ptr) { }

    readonly Dictionary<int, Family> families = new();
    float nextScan;
    const float StrokeInterval = 0.4f;
    readonly Stopwatch budget = new();

    void Update()
    {
        StrokeProbe.MainThread = Environment.CurrentManagedThreadId;
        float dt = Math.Min(Time.deltaTime, 0.1f), now = Time.unscaledTime;
        try
        {
            if (now >= nextScan) { nextScan = now + 1f; Scan(); }
            budget.Restart();
            foreach (var f in families.Values)
            {
                if (f.Parent == null || f.Parent.Pointer == IntPtr.Zero) continue;
                Plan(f);
                foreach (var b in f.Babies) Step(f, b, dt);
                if (budget.ElapsedMilliseconds > 4) break;   // never take more than ~4 ms a frame
            }
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
                if (f.Parent == null || f.Parent.Pointer != r.Pointer) { Clear(f); f.Parent = r; }
                while (f.Babies.Count < Plugin.BabiesPerRobot.Value) f.Babies.Add(Spawn(r, f.Babies.Count));
            }
        foreach (var id in new List<int>(families.Keys))
            if (!seen.Contains(id)) { Clear(families[id]); families.Remove(id); }
    }

    static void Clear(Family f)
    {
        foreach (var b in f.Babies) if (b.Go != null) UnityEngine.Object.Destroy(b.Go);
        f.Babies.Clear(); f.Cell = int.MinValue;
    }

    // A baby is a static copy of the parent's meshes: no colliders, no scripts, not saved.
    static Baby Spawn(MoonRobotVacuum parent, int index)
    {
        var root = new GameObject($"BabyJoonas_{index}");
        var pt = parent.transform;
        foreach (var mf in parent.GetComponentsInChildren<MeshFilter>())
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mf.sharedMesh == null || mr == null) continue;
            var part = new GameObject(mf.name);
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition = pt.InverseTransformPoint(mf.transform.position);
            part.transform.localRotation = Quaternion.Inverse(pt.rotation) * mf.transform.rotation;
            var ls = mf.transform.lossyScale; var ps = pt.lossyScale;
            part.transform.localScale = new Vector3(ls.x / ps.x, ls.y / ps.y, ls.z / ps.z);
            part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            part.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
        }
        root.transform.localScale = Vector3.one * Plugin.Scale.Value;
        var side = (index % 2 == 0 ? 1f : -1f) * (2f + index);
        var b = new Baby { Go = root, Pos = pt.position - pt.forward * 4f + pt.right * side };
        b.LastStroke = b.Pos;
        root.transform.position = b.Pos;
        return b;
    }

    // When the parent starts a new sector, hand the inner-ring lanes of its plan to the babies.
    static void Plan(Family f)
    {
        var p = f.Parent;
        int cell = p.cell;
        if (cell == f.Cell) return;
        var lanes = p.lanes;
        if (lanes == null || lanes.Count == 0) return;
        f.Cell = cell;
        var list = new List<(Vector3 a, Vector3 b)>();
        Vector3 centre = Vector3.zero;
        for (int i = 0; i < lanes.Count; i++) { var l = lanes.get_Item(i); list.Add((l.a, l.b)); centre += (l.a + l.b) * 0.5f; }
        centre /= list.Count;
        float outer = 0; foreach (var l in list) outer = Math.Max(outer, ((l.a + l.b) * 0.5f - centre).magnitude);
        foreach (var b in f.Babies) { b.Work.Clear(); b.Current = null; }
        int k = 0;
        foreach (var l in list)
            if (((l.a + l.b) * 0.5f - centre).magnitude < outer * Plugin.InnerShare.Value)
                f.Babies[k++ % f.Babies.Count].Work.Enqueue(l);
    }

    void Step(Family f, Baby b, float dt)
    {
        var p = f.Parent;
        if (b.Current == null && b.Work.Count > 0) { b.Current = b.Work.Dequeue(); b.OnLane = false; }
        Vector3 goal;
        if (b.Current is { } lane) goal = b.OnLane ? lane.b : lane.a;
        else goal = p.transform.position - p.transform.forward * 5f;    // nothing to do: tag along behind the parent

        Vector3 to = goal - b.Pos; float dist = to.magnitude, step = Plugin.Speed.Value * dt;
        if (dist <= step + 0.05f)
        {
            b.Pos = goal;
            if (b.Current != null) { if (!b.OnLane) { b.OnLane = true; b.LastStroke = b.Pos; } else { Stroke(p, b, true); b.Current = null; } }
        }
        else b.Pos += to / dist * step;

        Vector3 ground = b.Pos;
        try { ground = p.GroundAt(b.Pos); } catch { }
        Vector3 up = Vector3.up;
        try { up = p.Up(ground); } catch { }
        b.Pos = ground;
        var fwd = Vector3.ProjectOnPlane(dist > 0.01f ? to : b.Go.transform.forward, up);
        if (fwd.sqrMagnitude > 1e-4f) b.Go.transform.rotation = Quaternion.LookRotation(fwd, up);
        b.Go.transform.position = ground;

        if (b.OnLane) { b.StrokeTimer += dt; if (b.StrokeTimer >= StrokeInterval) Stroke(p, b, false); }
    }

    // Stage 1b: clean the strip from the last stroke point to here, crediting the parent's storage.
    static void Stroke(MoonRobotVacuum p, Baby b, bool final)
    {
        b.StrokeTimer = 0;
        var from = b.LastStroke; var to = b.Pos; b.LastStroke = to;
        if (!Plugin.Clean.Value || (to - from).sqrMagnitude < 0.01f) return;
        var storage = p.storageId;
        if (string.IsNullOrEmpty(storage) || !StrokeProbe.ByStorage.TryGetValue(storage, out var a)) return;  // wait until the parent has been seen cleaning
        var w = p.world; if (w == null) return;
        Vector3 up = p.Up(to), right = Vector3.Cross(up, (to - from).normalized).normalized;
        StrokeProbe.Ours = true;
        try
        {
            w.QueueToolStroke(a.Player, from, to, right, right, Plugin.Width.Value, a.Depth, a.Dose, Math.Min(a.Edge, Plugin.Width.Value * 0.25f),
                              Vector3.zero, null, a.Inventory, storage, a.Credit);
        }
        finally { StrokeProbe.Ours = false; }
    }

    void OnDestroy() { foreach (var f in families.Values) Clear(f); }
}
