using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.SmartSplitters;

// Smart Splitters
// ---------------
// The stock splitter alternates strictly between its two outputs and waits when the output whose turn it is
// is full. This mod picks the output for every capsule as it enters a splitter:
//   Smart     - skip a full output; splitters chained output->input share capsules evenly over all final outputs
//               (2 chained splitters = 3-way, 3 = 4-way, 4 = 5-way ...)
//   Overflow  - always the main output, the other one only when the main one is backed up
//   Sort X    - cargo X to the main output, everything else to the other output
//   Vanilla   - the game's own behaviour
// It steers by setting the splitter's own turn counter just before the game moves the capsule in, so no game
// logic is replaced. Splitters stay normal splitters in the save; removing the mod restores vanilla behaviour.
[BepInPlugin("moonsweepers.smartsplitters", "Smart Splitters", "1.2.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<UnityEngine.InputSystem.Key> ModeKey, SideKey;
    internal static ConfigEntry<Mode> DefaultMode;
    internal static ConfigEntry<int> SplitterKind;
    internal static ConfigEntry<bool> ShowLabel;

    public override void Load()
    {
        L = Log;
        ModeKey = Config.Bind("Keys", "CycleMode", UnityEngine.InputSystem.Key.F6, "Look at a splitter and press to cycle its mode.");
        SideKey = Config.Bind("Keys", "SwapMainSide", UnityEngine.InputSystem.Key.F7, "Look at a splitter and press to swap which side is the main output (Overflow / Sort modes).");
        DefaultMode = Config.Bind("General", "DefaultMode", Mode.Smart, "Mode for splitters you haven't set yourself.");
        ShowLabel = Config.Bind("General", "ShowLabel", true, "Show a label with the mode when you look at a splitter.");
        SplitterKind = Config.Bind("Advanced", "SplitterKind", 11, "Structure kind number of the splitter (11 in Moon Sweepers 1.0.6).");
        Core.LoadSettings();
        try
        {
            new Harmony("moonsweepers.smartsplitters").PatchAll(typeof(Hooks));
            ClassInjector.RegisterTypeInIl2Cpp<SplitterBehaviour>();
            AddComponent<SplitterBehaviour>();
            L.LogInfo($"Smart Splitters loaded. Look at a splitter: {ModeKey.Value} = mode, {SideKey.Value} = swap main side. Default mode: {DefaultMode.Value}.");
        }
        catch (Exception e) { L.LogError("Smart Splitters failed to start: " + e); }
    }
}

public enum Mode { Smart, Overflow, SortDust, SortSand, SortStone, Vanilla }

internal sealed class Splitter
{
    public int Id;
    public Vector3 Pos, Entrance;
    public readonly Vector3[] Out = new Vector3[3];
    public readonly int[] Lane = new int[3], Route = new int[3], OutBelt = new int[3], Child = { -1, -1, -1 }, Leaves = { 0, 1, 1 };
    public int InputBelt, LeftIndex = 1, Parent = -1;
    public bool Partial;                             // known machine, but no lanes yet (nothing has passed since loading)
    public readonly double[] Credit = new double[3];
    public string Key => $"{Id}@{Pos.x:F1},{Pos.z:F1}";
    public int Total => Leaves[1] + Leaves[2];
}

internal sealed class Setting { public Mode Mode { get; set; } public bool Swap { get; set; } }

internal static class Core
{
    internal static MoonWorld World;
    internal static IntPtr WorldPtr;
    internal static int MainThread = -1;
    internal static bool Ready;                      // topology valid and settled
    internal static Dictionary<int, Splitter> Splitters = new();
    internal static Dictionary<int, Splitter> ByInputBelt = new();

    // Pending decision between HandOff prefix and postfix (main thread only).
    internal static Splitter PendingSplitter; internal static int PendingId, PendingOut, PendingT0; internal static bool PendingForced;

    // Self-check.
    // Each splitter learns on its own which way its turn counter works: we set it for the output we want, look where
    // the capsule really went, and flip that splitter's mapping after two misses in a row (only counted when the
    // wanted lane had room, so the game had no reason to divert). Globally, Phase 1 verifies that overriding the
    // game's own choice works at least 85% of the time; Phase 2 = confirmed (still monitored); Phase 3 = off.
    internal static int Phase = 1;
    internal static bool SteeringOff => Phase == 3;
    internal static readonly Dictionary<int, (bool next, int miss, int uses)> Mapping = new();
    static int forcedGood, forcedBad, winGood, winBad;

    internal static bool Next(int id) => !Mapping.TryGetValue(id, out var m) || m.next;

    internal static int Predict(int id, int t0) => t0 == 1 || t0 == 2 ? (Next(id) ? t0 : 3 - t0) : 0;

    // Returns true if this sample counts (splitter past its warm-up).
    internal static bool Observe(int id, bool ok)
    {
        Mapping.TryGetValue(id, out var m);
        if (!Mapping.ContainsKey(id)) m = (true, 0, 0);
        m.uses++;
        if (ok) m.miss = 0;
        else if (++m.miss >= 2) { m.next = !m.next; m.miss = 0; }
        Mapping[id] = m;
        return m.uses > 4;
    }

    internal static int MissesThisMinute, MissWantedBusy, MissOtherBusy, MissWarmup; static int missLogged;
    internal static void Miss(Splitter s, int wanted, int actual, int t0, bool forced, bool wantedBusy, bool otherBusy, int uses)
    {
        MissesThisMinute++; if (wantedBusy) MissWantedBusy++; if (otherBusy) MissOtherBusy++; if (uses <= 4) MissWarmup++;
        if (missLogged < 15)
        {
            missLogged++;
            Plugin.L.LogInfo($"[miss] splitter #{s.Id} ({Phase switch { 1 => "self-check", 2 => "confirmed", _ => "off" }}): wanted {(wanted == 1 ? "left" : "right")}, went {(actual == 1 ? "left" : "right")}; turn before {t0}, mapping {(Next(s.Id) ? "next" : "flipped")}, use #{uses}, forced {forced}; after hand-off wanted lane {(wantedBusy ? "busy" : "free")}, other lane {(otherBusy ? "busy" : "free")}.");
        }
    }

    internal static void Verify(bool ok)
    {
        if (Phase == 1)
        {
            if (ok) forcedGood++; else forcedBad++;
            if (forcedGood + forcedBad < 40) return;
            // If overriding didn't work at all, forced capsules would follow the game's own choice (~0% right); random
            // would be 50%. 65% or better means control works and the misses are the game diverting (timing).
            if (forcedGood >= 26) { Phase = 2; Plugin.L.LogInfo($"Splitter control confirmed: {forcedGood}/{forcedGood + forcedBad} capsules sent against the game's choice went where intended."); }
            else { Phase = 3; Plugin.L.LogWarning($"Splitter control failed ({forcedGood} right / {forcedBad} wrong when overriding the game). Splitters stay vanilla this session."); }
            return;
        }
        if (Phase != 2) return;
        if (ok) winGood++; else winBad++;
        if (winGood + winBad < 200) return;
        if (winBad > 80) { Phase = 3; Plugin.L.LogWarning($"Splitter control degraded ({winBad} of 200 recent capsules went the wrong way). Splitters stay vanilla for the rest of this session."); }
        winGood = winBad = 0;
    }

    static Dictionary<string, Setting> settings = new();
    static string SettingsPath => Path.Combine(Paths.ConfigPath, "SmartSplitters", "settings.json");

    internal static Setting SettingOf(Splitter s) =>
        settings.TryGetValue(s.Key, out var v) ? v : new Setting { Mode = Plugin.DefaultMode.Value };

    internal static void Set(Splitter s, Setting v) { settings[s.Key] = v; SaveSettings(); }

    internal static void LoadSettings()
    {
        try { if (File.Exists(SettingsPath)) settings = JsonSerializer.Deserialize<Dictionary<string, Setting>>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception e) { Plugin.L.LogWarning("Could not read splitter settings, starting fresh: " + e.Message); settings = new(); }
    }

    static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception e) { Plugin.L.LogWarning("Could not save splitter settings: " + e.Message); }
    }

    internal static int MainIndex(Splitter s, Setting v) => v.Swap ? 3 - s.LeftIndex : s.LeftIndex;

    static bool Full(MoonWorld w, Splitter s, int k) => w.RouteFull(s.Route[k]);

    internal static int Cargo(MoonWorld w, int id)
    {
        var c = w.canisters;
        if (c == null || !c.ContainsKey(id)) return -1;
        return (int)c.get_Item(id).cargo;
    }

    // Choose output 1 or 2 for capsule `id` entering splitter `s`; 0 = leave it to the game.
    internal static int Decide(MoonWorld w, Splitter s, int id, Setting v)
    {
        int main = MainIndex(s, v), other = 3 - main;
        switch (v.Mode)
        {
            case Mode.Smart:
            {
                bool f1 = Full(w, s, 1), f2 = Full(w, s, 2);
                if (f1 && f2) return 0;
                if (f1) return 2;
                if (f2) return 1;
                double a = s.Credit[1] + s.Leaves[1], b = s.Credit[2] + s.Leaves[2];
                return a >= b ? 1 : 2;
            }
            case Mode.Overflow:
                if (!Full(w, s, main)) return main;
                if (!Full(w, s, other)) return other;
                return 0;
            case Mode.SortDust: case Mode.SortSand: case Mode.SortStone:
            {
                int want = v.Mode == Mode.SortDust ? 0 : v.Mode == Mode.SortSand ? 1 : 2;
                int c = Cargo(w, id);
                if (c < 0) return 0;
                return c == want ? main : other;
            }
        }
        return 0;
    }

    // Smooth weighted round robin: commit the output that actually received the capsule.
    internal static void Commit(Splitter s, int k)
    {
        double t = s.Total;
        s.Credit[1] += s.Leaves[1]; s.Credit[2] += s.Leaves[2]; s.Credit[k] -= t;
        for (int i = 1; i <= 2; i++) s.Credit[i] = Math.Clamp(s.Credit[i], -2 * t, 2 * t);
    }

    // ---------------- topology ----------------
    struct BeltEnds { public int Key; public Vector3 Start, End; }

    // Builds a fresh layout and swaps it in, so refreshing never interrupts steering.
    internal static bool Rebuild(MoonWorld w)
    {
        var depots = w.depots; var riders = w.riders;
        if (depots == null || riders == null || riders.lanes == null) return false;
        var old = Splitters;
        var Splitters_ = new Dictionary<int, Splitter>(); var byIn = new Dictionary<int, Splitter>();
        var lanes = riders.lanes;
        int kind = Plugin.SplitterKind.Value;

        var belts = new List<BeltEnds>();
        foreach (var kv in lanes)
        {
            if (kv.Key <= 0) continue;
            var q = kv.Value; var p = q?.path; if (p == null) continue;
            belts.Add(new BeltEnds { Key = kv.Key, Start = p.start, End = p.end });
        }

        foreach (var kv in depots)
        {
            var d = kv.Value; if (d == null || d.kind != kind) continue;
            var s = new Splitter { Id = d.id, Pos = new Vector3(d.x, d.y, d.z) };
            if (old.TryGetValue(s.Id, out var prev)) Array.Copy(prev.Credit, s.Credit, 3);
            bool ok = true;
            for (int k = 1; k <= 2; k++)
            {
                int r = MoonWorld.MachineRoute(s.Id, 0, k);
                int key = lanes.ContainsKey(-r) ? -r : lanes.ContainsKey(r) ? r : 0;
                var path = key != 0 ? lanes.get_Item(key)?.path : null;
                if (path == null) { ok = false; break; }
                s.Route[k] = r; s.Lane[k] = key; s.Out[k] = path.end;
                if (k == 1) s.Entrance = path.start;
            }
            Splitters_[s.Id] = s;
            if (!ok) { s.Partial = true; continue; }
            s.InputBelt = Nearest(belts, s.Entrance, true);
            for (int k = 1; k <= 2; k++) s.OutBelt[k] = Nearest(belts, s.Out[k], false);
            Vector3 f = (s.Out[1] + s.Out[2]) * 0.5f - s.Entrance, v1 = s.Out[1] - s.Entrance;
            s.LeftIndex = f.x * v1.z - f.z * v1.x > 0 ? 1 : 2;
            if (s.InputBelt != 0) byIn[s.InputBelt] = s;
        }

        // Chains: follow each output over plain belt joints until the next machine; a splitter there is a child.
        foreach (var s in Splitters_.Values)
            for (int k = 1; k <= 2 && !s.Partial; k++)
            {
                int b = s.OutBelt[k];
                for (int hop = 0; b != 0 && hop < 40; hop++)
                {
                    var end = Ends(belts, b).End;
                    Splitter child = null;
                    foreach (var t in Splitters_.Values) if (t != s && !t.Partial && (t.Entrance - end).magnitude < 0.75f) { child = t; break; }
                    if (child != null) { s.Child[k] = child.Id; if (child.Parent < 0) child.Parent = s.Id; break; }
                    int next = 0;
                    foreach (var e in belts) if (e.Key != b && (e.Start - end).magnitude < 0.3f) { next = e.Key; break; }
                    b = next;
                }
            }
        Splitters = Splitters_;
        foreach (var s in Splitters_.Values) if (!s.Partial) Count(s, 0);
        ByInputBelt = byIn;

        int partial = 0; foreach (var s in Splitters_.Values) if (s.Partial) partial++;
        var sb = new StringBuilder($"Found {Splitters_.Count} splitters ({partial} idle since loading)");
        foreach (var s in Splitters_.Values)
            if (s.Parent < 0 && (s.Child[1] >= 0 || s.Child[2] >= 0)) sb.Append($"; #{s.Id} heads a {s.Total}-way chain");
        var text = sb.ToString();
        if (text != lastLayout) { lastLayout = text; Plugin.L.LogInfo(text); }
        return true;
    }

    static string lastLayout;
    internal static readonly Dictionary<int, int[]> Sent = new();

    internal static void Note(Splitter s, int k)
    {
        if (!Sent.TryGetValue(s.Id, out var c)) Sent[s.Id] = c = new int[3];
        c[k]++;
    }

    internal static void Report()
    {
        if (Sent.Count == 0) return;
        if (MissesThisMinute > 0) Plugin.L.LogInfo($"Last minute, {MissesThisMinute} capsules went the other way than chosen ({MissWantedBusy} with the chosen lane busy right after, {MissOtherBusy} with the other lane busy, {MissWarmup} during a splitter's first uses).");
        MissesThisMinute = MissWantedBusy = MissOtherBusy = MissWarmup = 0;
        var sb = new StringBuilder("Last minute, capsules per splitter (left/right):");
        foreach (var kv in Sent)
            if (Splitters.TryGetValue(kv.Key, out var s))
            {
                int l = kv.Value[s.LeftIndex], r = kv.Value[3 - s.LeftIndex];
                sb.Append($" #{s.Id} {SettingOf(s).Mode} {l}/{r};");
            }
        Plugin.L.LogInfo(sb.ToString());
        Sent.Clear();
    }

    static int Count(Splitter s, int depth)
    {
        for (int k = 1; k <= 2; k++)
            s.Leaves[k] = s.Child[k] >= 0 && depth < 8 && Splitters.TryGetValue(s.Child[k], out var c) ? Count(c, depth + 1) : 1;
        return s.Leaves[1] + s.Leaves[2];
    }

    static int Nearest(List<BeltEnds> belts, Vector3 p, bool byEnd)
    {
        int best = 0; float bd = 0.75f;
        foreach (var e in belts) { float d = ((byEnd ? e.End : e.Start) - p).magnitude; if (d < bd) { bd = d; best = e.Key; } }
        return best;
    }

    static BeltEnds Ends(List<BeltEnds> belts, int key) { foreach (var e in belts) if (e.Key == key) return e; return default; }

    internal static string Describe(Splitter s)
    {
        var v = SettingOf(s);
        string side = MainIndex(s, v) == s.LeftIndex ? "left" : "right";
        string text = v.Mode switch
        {
            Mode.Smart when s.Parent >= 0 && Splitters.TryGetValue(s.Parent, out var p) && SettingOf(p).Mode == Mode.Smart
                => $"Smart · part of the chain headed by splitter #{Root(s).Id}",
            Mode.Smart when s.Total > 2 => $"Smart · {s.Total}-way chain, {100.0 / s.Total:F0}% to each output, skips full ones",
            Mode.Smart => "Smart · 50/50, skips a full output",
            Mode.Overflow => $"Overflow · everything {side}, the other side only when {side} is backed up",
            Mode.SortDust => $"Sort · dust {side}, everything else the other way",
            Mode.SortSand => $"Sort · sand {side}, everything else the other way",
            Mode.SortStone => $"Sort · stone {side}, everything else the other way",
            _ => "Vanilla · strict alternation (game default)",
        };
        if (SteeringOff) text += "  [inactive: control check failed, see log]";
        else if (!Ready) text += "  [waiting for building to settle]";
        else if (s.Partial) text += "  [idle: applies once capsules come through]";
        else if (Phase < 2) text += "  [self-check running]";
        return $"Splitter #{s.Id} · {text}";
    }

    static Splitter Root(Splitter s)
    {
        for (int i = 0; i < 10 && s.Parent >= 0 && Splitters.TryGetValue(s.Parent, out var p); i++) s = p;
        return s;
    }
}

[HarmonyPatch]
internal static class Hooks
{
    // MoonWorld.HandOff(belt, path, id): the capsule at the end of `belt` moves on. For a splitter's input belt this
    // is where the game picks the splitter lane using machineTurns, so we set the turn just before it reads it.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.HandOff))]
    [HarmonyPrefix]
    static bool Before(MoonWorld __instance, int belt, int id, ref bool __result)
    {
        try
        {
            Core.PendingSplitter = null;
            if (belt <= 0 || !Core.Ready || Core.SteeringOff) return true;
            if (Environment.CurrentManagedThreadId != Core.MainThread) return true;
            if (__instance.Pointer != Core.WorldPtr) return true;
            if (!Core.ByInputBelt.TryGetValue(belt, out var s)) return true;
            var turns = __instance.machineTurns;
            if (turns == null) return true;
            var v = Core.SettingOf(s);
            if (v.Mode == Mode.Vanilla) return true;
            int d = Core.Decide(__instance, s, id, v);
            if (d == 0) return true;
            bool sort = v.Mode == Mode.SortDust || v.Mode == Mode.SortSand || v.Mode == Mode.SortStone;
            if (sort && __instance.RouteFull(s.Route[d])) { __result = false; return false; }   // wait rather than risk the wrong side
            int t0 = turns.ContainsKey(s.Id) ? turns.get_Item(s.Id) : 0;
            int pred = Core.Predict(s.Id, t0);
            // While verifying, deliberately go against the game's own choice when that costs nothing (Smart, both sides free).
            if (Core.Phase == 1 && v.Mode == Mode.Smart && pred != 0 && d == pred && !__instance.RouteFull(s.Route[3 - d])) d = 3 - d;
            turns.set_Item(s.Id, Core.Next(s.Id) ? d : 3 - d);
            Core.PendingSplitter = s; Core.PendingId = id; Core.PendingOut = d; Core.PendingT0 = t0; Core.PendingForced = pred != 0 && d != pred;
        }
        catch (Exception e) { Core.PendingSplitter = null; Plugin.L.LogError("Splitter decision failed: " + e.Message); }
        return true;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.HandOff))]
    [HarmonyPostfix]
    static void After(MoonWorld __instance, int id, bool __result)
    {
        var s = Core.PendingSplitter;
        if (s == null || Core.PendingId != id) return;
        Core.PendingSplitter = null;
        if (!__result) return;
        try
        {
            var all = __instance.riders?.all;
            if (all == null || !all.ContainsKey(id)) return;
            int lane = all.get_Item(id).belt;
            int actual = lane == s.Lane[1] ? 1 : lane == s.Lane[2] ? 2 : 0;
            if (actual == 0) return;
            bool ok = actual == Core.PendingOut;
            if (!ok)
            {
                bool wb = false, ob = false; int uses = 0;
                try { wb = __instance.RouteFull(s.Route[Core.PendingOut]); ob = __instance.RouteFull(s.Route[3 - Core.PendingOut]); } catch { }
                if (Core.Mapping.TryGetValue(s.Id, out var mm)) uses = mm.uses + 1;
                Core.Miss(s, Core.PendingOut, actual, Core.PendingT0, Core.PendingForced, wb, ob, uses);
            }
            bool counts = Core.Observe(s.Id, ok);
            Core.Commit(s, actual);
            Core.Note(s, actual);
            if (counts && (Core.Phase == 2 || Core.PendingForced)) Core.Verify(ok);
        }
        catch (Exception e) { Plugin.L.LogError("Splitter bookkeeping failed: " + e.Message); }
    }
}

public class SplitterBehaviour : MonoBehaviour
{
    public SplitterBehaviour(IntPtr ptr) : base(ptr) { }

    float nextScan, dirtySince = -1, toastUntil, nextAim, nextReport = 60f;
    int signature;
    string toast;
    Splitter aimed;

    void Update()
    {
        Core.MainThread = Environment.CurrentManagedThreadId;
        float now = Time.unscaledTime;
        try
        {
            if (now >= nextScan) { nextScan = now + 0.5f; Scan(now); }
            if (now >= nextReport) { nextReport = now + 60f; Core.Report(); }
            if (now >= nextAim) { nextAim = now + 0.1f; aimed = Aimed(); }
            // The game uses Unity's Input System; the legacy Input API never sees key presses here.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.get_Item(Plugin.ModeKey.Value).wasPressedThisFrame) Cycle(now);
                if (kb.get_Item(Plugin.SideKey.Value).wasPressedThisFrame) Swap(now);
            }
            UpdateHud();
        }
        catch (Exception e) { Plugin.L.LogError("Smart Splitters update: " + e.Message); }
    }

    void Scan(float now)
    {
        var w = Core.World;
        if (w == null || w.Pointer == IntPtr.Zero || !w.isActiveAndEnabled) w = UnityEngine.Object.FindFirstObjectByType<MoonWorld>();
        if (w == null) { Core.Ready = false; Core.World = null; Core.WorldPtr = IntPtr.Zero; return; }
        if (w.Pointer != Core.WorldPtr) { Core.World = w; Core.WorldPtr = w.Pointer; Core.Ready = false; dirtySince = now; signature = 0; }
        var lanes = w.riders?.lanes; var depots = w.depots; var conveyors = w.conveyors;
        if (lanes == null || depots == null || conveyors == null) { Core.Ready = false; return; }
        // Lanes come and go as belts fill and empty, so only machines and belts count as "something was built".
        int sig = depots.Count * 100003 + conveyors.Count;
        if (sig != signature) { signature = sig; dirtySince = now; Core.Ready = false; return; }
        // Something was built or removed: wait until it has been quiet for 1.5 s before trusting the layout again.
        if (!Core.Ready && dirtySince >= 0 && now - dirtySince > 1.5f) { if (Core.Rebuild(w)) Core.Ready = true; dirtySince = -1; nextRefresh = now + 5f; }
        // Splitters only get lanes once capsules pass through after loading: keep picking them up.
        else if (Core.Ready && now >= nextRefresh) { nextRefresh = now + 5f; Core.Rebuild(w); }
    }

    float nextRefresh;

    static Camera Cam()
    {
        var cam = Camera.main;
        if (cam != null && cam.isActiveAndEnabled) return cam;
        cam = Camera.current;
        if (cam != null) return cam;
        foreach (var c in Camera.allCameras) if (c != null && c.isActiveAndEnabled) return c;
        return null;
    }

    // Saved machine positions may be in the world object's local space rather than Unity world space; try both.
    static Vector3 WorldPos(Splitter s, bool local) =>
        local && Core.World != null ? Core.World.transform.TransformPoint(s.Pos) : s.Pos;

    Splitter Aimed() => Aimed(out _);

    Splitter Aimed(out string why)
    {
        why = null;
        var cam = Cam(); if (cam == null) { why = "no camera"; return null; }
        if (Core.Splitters.Count == 0) { why = "no splitters known yet"; return null; }
        Vector3 o = cam.transform.position, f = cam.transform.forward;
        Splitter best = null, near = null; float bd = 2.5f, nd = 6f, closest = float.MaxValue;
        foreach (bool local in new[] { false, true })
            foreach (var s in Core.Splitters.Values)
            {
                Vector3 d = WorldPos(s, local) - o; float t = Vector3.Dot(d, f);
                closest = Math.Min(closest, d.magnitude);
                if (d.magnitude < nd) { nd = d.magnitude; near = s; }      // fallback: standing right next to one
                if (t < 0.3f || t > 12f) continue;
                float perp = (d - f * t).magnitude;
                if (perp < bd) { bd = perp; best = s; }
            }
        if (best == null && near == null) why = $"camera '{cam.name}' at {o}, nearest splitter {closest:F1} m away";
        return best ?? near;
    }

    void Miss(string what, float now)
    {
        Aimed(out var why);
        Toast($"Look at a splitter (or stand next to one) to {what}.", now);
        if (why != null) Plugin.L.LogInfo("  (aim check: " + why + ")");
    }

    void Cycle(float now)
    {
        if (aimed == null) { Miss("change its mode", now); return; }
        var v = Core.SettingOf(aimed);
        v.Mode = (Mode)(((int)v.Mode + 1) % 6);
        Core.Set(aimed, v);
        Toast(Core.Describe(aimed), now);
    }

    void Swap(float now)
    {
        if (aimed == null) { Miss("swap its main side", now); return; }
        var v = Core.SettingOf(aimed);
        v.Swap = !v.Swap;
        Core.Set(aimed, v);
        Toast(Core.Describe(aimed), now);
    }

    void Toast(string text, float now) { toast = text; toastUntil = now + 4f; Plugin.L.LogInfo(text); }

    bool guiSeen, guiWarned, aimLogged, hudFailed;

    // ---- on-screen label, drawn with the game's own UI system (uGUI + TextMeshPro) ----
    GameObject hud, panel;
    TMPro.TextMeshProUGUI label;
    string shown;

    void UpdateHud()
    {
        string text = Time.unscaledTime < toastUntil ? toast
            : (Plugin.ShowLabel.Value && aimed != null && Core.World != null
                ? Core.Describe(aimed) + $"\n<size=75%>{Plugin.ModeKey.Value}: change mode    {Plugin.SideKey.Value}: swap main side</size>" : null);
        if (string.IsNullOrEmpty(text)) { if (panel != null && panel.activeSelf) panel.SetActive(false); return; }
        if (!EnsureHud()) return;
        if (text != shown) { label.text = text; shown = text; }
        if (!panel.activeSelf) panel.SetActive(true);
        if (!guiSeen) { guiSeen = true; Plugin.L.LogInfo("On-screen label created."); }
        if (aimed != null && !aimLogged) { aimLogged = true; Plugin.L.LogInfo($"Looking at splitter #{aimed.Id} - label shown."); }
    }

    bool EnsureHud()
    {
        if (label != null && panel != null) return true;
        if (hudFailed) return false;
        try
        {
            hud = new GameObject("SmartSplittersHUD");
            UnityEngine.Object.DontDestroyOnLoad(hud);
            var canvas = hud.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = hud.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            panel = new GameObject("Panel");
            panel.transform.SetParent(hud.transform, false);
            var bg = panel.AddComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0f, 0f, 0f, 0.6f);
            bg.raycastTarget = false;
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 1f);
            prt.pivot = new Vector2(0.5f, 1f);
            prt.anchoredPosition = new Vector2(0f, -110f);
            prt.sizeDelta = new Vector2(1150f, 78f);

            var tgo = new GameObject("Text");
            tgo.transform.SetParent(panel.transform, false);
            label = tgo.AddComponent<TMPro.TextMeshProUGUI>();
            var font = FindFont();
            if (font != null) label.font = font;
            label.fontSize = 24;
            label.color = Color.white;
            label.alignment = TMPro.TextAlignmentOptions.Center;
            label.raycastTarget = false;
            var trt = tgo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(16f, 6f); trt.offsetMax = new Vector2(-16f, -6f);
            panel.SetActive(false);
            Plugin.L.LogInfo($"On-screen label set up (font: {(font != null ? font.name : "TextMeshPro default")}).");
            return true;
        }
        catch (Exception e)
        {
            hudFailed = true;
            Plugin.L.LogError("Could not create the on-screen label: " + e);
            return false;
        }
    }

    static TMPro.TMP_FontAsset FindFont()
    {
        try
        {
            var any = UnityEngine.Object.FindFirstObjectByType<TMPro.TMP_Text>();
            if (any != null && any.font != null) return any.font;
        }
        catch { }
        try
        {
            var all = Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
            if (all != null && all.Length > 0) return all[0];
        }
        catch { }
        return TMPro.TMP_Settings.defaultFontAsset;
    }
}
