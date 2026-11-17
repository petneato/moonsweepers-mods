using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.SplitterProbe;

// Diagnostic only: records how capsules move between belts and machines so splitter behaviour can be worked out.
// Writes BepInEx\SplitterProbe.csv, stops by itself after RunMinutes. Changes nothing in the game.
[BepInPlugin("moonsweepers.splitterprobe", "Splitter Probe", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> RunMinutes;
    internal static StreamWriter W;
    internal static float StartAt = -1, LastFlush, LastTurns;
    internal static int Lines;
    internal static bool Stopped;
    internal static readonly HashSet<int> MachinesSeen = new();
    internal static readonly Dictionary<int, int> Turns = new(), Inputs = new();
    internal static readonly Dictionary<int, string> RouteState = new();

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", true, "Record belt/splitter activity to BepInEx/SplitterProbe.csv.");
        RunMinutes = Config.Bind("General", "RunMinutes", 15f, "Stop recording after this many minutes of play.");
        if (!Enabled.Value) { L.LogInfo("Splitter Probe disabled in config."); return; }
        var path = Path.Combine(Paths.BepInExRootPath, "SplitterProbe.csv");
        W = new StreamWriter(path, false) { AutoFlush = false };
        W.WriteLine("t,event,a,b,c,d,e,f");
        try { new Harmony("moonsweepers.splitterprobe").PatchAll(typeof(Patches)); L.LogInfo("Splitter Probe recording to " + path); }
        catch (Exception e) { L.LogError("Patching failed: " + e); }
    }

    internal static bool Active(out float t)
    {
        t = Time.realtimeSinceStartup;
        if (Stopped || W == null) return false;
        if (StartAt < 0) StartAt = t;
        if (t - StartAt > RunMinutes.Value * 60f || Lines > 400000) { Stop("time/line limit reached"); return false; }
        if (t - LastFlush > 1f) { LastFlush = t; W.Flush(); }
        return true;
    }

    internal static void Stop(string why)
    {
        if (Stopped) return; Stopped = true;
        try { W.WriteLine($"{Time.realtimeSinceStartup - StartAt:F2},stop,{why}"); W.Flush(); W.Close(); } catch { }
        L.LogInfo("Splitter Probe stopped: " + why + $" ({Lines} lines).");
    }

    internal static void Row(float t, string ev, params object[] f)
    {
        W.WriteLine($"{t - StartAt:F2},{ev},{string.Join(",", f)}"); Lines++;
    }

    internal static string Cargo(MoonWorld w, int id)
    {
        try
        {
            var c = w.canisters;
            if (c != null && c.ContainsKey(id)) return c.get_Item(id).cargo.ToString();
        }
        catch { }
        return "?";
    }

    internal static void Machine(float t, MoonWorld w, int m)
    {
        if (!MachinesSeen.Add(m)) return;
        try
        {
            var d = w.depots;
            if (d != null && d.ContainsKey(m)) { var s = d.get_Item(m); Row(t, "machine", m, s.kind, s.x.ToString("F1"), s.z.ToString("F1"), s.y.ToString("F1")); return; }
        }
        catch (Exception e) { Row(t, "machine-err", m, e.GetType().Name); return; }
        Row(t, "machine", m, "?");
    }

    internal static void TurnsTick(float t, MoonWorld w)
    {
        if (t - LastTurns < 0.25f) return; LastTurns = t;
        try
        {
            Diff(t, "turn", w.machineTurns, Turns);
            Diff(t, "lastInput", w.lastInput, Inputs);
        }
        catch (Exception e) { Row(t, "turns-err", e.GetType().Name); }
    }

    static void Diff(float t, string ev, Il2CppSystem.Collections.Generic.Dictionary<int, int> src, Dictionary<int, int> seen)
    {
        if (src == null) return;
        foreach (var kv in src)
        {
            if (!seen.TryGetValue(kv.Key, out var v) || v != kv.Value)
            {
                seen[kv.Key] = kv.Value; Row(t, ev, kv.Key, kv.Value);
            }
        }
    }

    internal static string Route(int r)
    {
        try { return $"{MoonWorld.RouteMachine(r)}:{MoonWorld.RouteInput(r)}>{MoonWorld.RouteOutput(r)}"; } catch { return r.ToString(); }
    }
}

[HarmonyPatch]
internal static class Patches
{
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.HandOff))]
    [HarmonyPostfix]
    static void HandOff(MoonWorld __instance, int belt, MoonConveyorPath path, int id, bool __result)
    {
        try
        {
            if (!Plugin.Active(out var t)) return;
            string where = "";
            if (path != null) { var s = path.start; var e = path.end; where = $"{s.x:F1};{s.z:F1}>{e.x:F1};{e.z:F1}"; }
            Plugin.Row(t, "handoff", belt, id, Plugin.Cargo(__instance, id), __result, where);
            Plugin.TurnsTick(t, __instance);
        }
        catch (Exception e) { Plugin.L.LogError("HandOff probe: " + e.Message); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.FeedProcessor))]
    [HarmonyPostfix]
    static void Feed(MoonWorld __instance, MoonDepotSnapshot machine, int input, int id, bool __result)
    {
        try
        {
            if (!Plugin.Active(out var t)) return;
            int m = machine != null ? machine.id : -1;
            Plugin.Machine(t, __instance, m);
            Plugin.Row(t, "feed", m, input, id, Plugin.Cargo(__instance, id), __result);
        }
        catch (Exception e) { Plugin.L.LogError("Feed probe: " + e.Message); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.RouteFull))]
    [HarmonyPostfix]
    static void RouteFull(MoonWorld __instance, int route, bool __result)
    {
        try
        {
            if (!Plugin.Active(out var t)) return;
            var key = "F" + __result;
            if (Plugin.RouteState.TryGetValue(route, out var prev) && prev.StartsWith("F") && prev == key) return;
            Plugin.RouteState[route] = key;
            Plugin.Machine(t, __instance, MoonWorld.RouteMachine(route));
            Plugin.Row(t, "routeFull", Plugin.Route(route), __result);
        }
        catch (Exception e) { Plugin.L.LogError("RouteFull probe: " + e.Message); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ForkClear))]
    [HarmonyPostfix]
    static void ForkClear(MoonWorld __instance, int route, float __result)
    {
        try
        {
            if (!Plugin.Active(out var t)) return;
            var key = "C" + Math.Round(__result, 1);
            if (Plugin.RouteState.TryGetValue(-route - 1, out var prev) && prev == key) return;
            Plugin.RouteState[-route - 1] = key;
            Plugin.Machine(t, __instance, MoonWorld.RouteMachine(route));
            Plugin.Row(t, "forkClear", Plugin.Route(route), __result.ToString("F2"));
        }
        catch (Exception e) { Plugin.L.LogError("ForkClear probe: " + e.Message); }
    }
}
