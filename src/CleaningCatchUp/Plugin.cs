using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.CleaningCatchUp;

// The game applies queued cleaning strokes (from J.O.O.N.A.S., the player and vehicles) a limited amount per frame,
// inside MoonWorld.ProcessToolStrokes. When the queue gets long the game slows J.O.O.N.A.S. down until it catches up.
//
// Background boost (0.5): robot strokes get at most min(cleaningWorkBudgetMs, ~20% of the frame) per frame. Focused at
// 150+ fps the 20% share is the limit and the budget doesn't matter; at the 30 fps background cap the 2 ms budget is
// the limit (about 12 strokes a frame, half of what the robots send), so the queue backs up and the game slows the
// robots. While the frame rate is capped and strokes are waiting, the budget is raised (the capped frame is mostly
// idle), and put back to the game's own value as soon as the cap is lifted or the queue is empty.
//
// Clean-ground skipping (0.4/0.5): about half of all J.O.O.N.A.S. strokes land on ground that is already clean. Each
// is checked against the game's own dust map at 19 points covering the whole rounded stroke; if all are clean the
// stroke is not queued and the robot gets its normal "stroke done" signal with 0 kg right after the game's next
// cleaning pass. Spot-checks (strokes sent anyway, the game's result recorded) and a sector-progress watch switch
// skipping off per robot if it would cost the robot more than a small share of its dust.
//
// Stroke cost meter (0.6): in the 0.5 session the game's cleaning share was used up in every frame, and each stroke cost
// ~0.5 ms against ~0.2 ms before. MoonWorld.CleanToolStroke is now timed per call and sorted by sender (J.O.O.N.A.S.,
// babies, other), and cleaning passes over 25 ms are logged with what was in them.
//
// Skip safety rework (0.7): in an 18-hour session the per-robot dust-share rule fired on freshly placed robots driving
// over ground other robots had already cleaned (0.3 kg "lost" of 5 kg collected = 6%), and once half the robots had
// stopped, skipping switched off for everyone - while the estimated loss over all robots was 0.2-0.7%. For the rest of
// the session 46% of all J.O.O.N.A.S. strokes went over clean ground. Now the dust-share rule needs real evidence
// (MinCollectedKg, 0.5 kg estimated) and only pauses that robot (10 minutes, doubling, at most 60); only the rules that
// point at a broken check (spot-checks finding dust, no sector finished) stop a robot for good or switch skipping off
// for all. While a robot isn't skipping, only every 10th of its strokes is checked (for the report).
[BepInPlugin("moonsweepers.cleaningcatchup", "Cleaning Catch-Up", Version)]
public class Plugin : BasePlugin
{
    public const string Version = "0.7.0";
    internal static ManualLogSource L;
    internal static Harmony H;
    internal static ConfigEntry<bool> MeasureStrokes;
    internal static ConfigEntry<bool> Enabled, ExtraPasses, RaiseBudget, BackgroundBoost;
    internal static ConfigEntry<float> MinFps, MaxExtraMs, MaxBudgetMs, BackgroundBudgetMs;
    internal static ConfigEntry<int> MinPending, MaxPassesPerFrame, SurveyEvery;
    internal static ConfigEntry<bool> SurveyParents, SkipClean;
    internal static ConfigEntry<float> SkipBelow, DustyKg, MaxDustyShare, MaxLostShare, GuardMinutes, PauseMinutes, MinKg;
    internal static ConfigEntry<int> SpotCheckEvery, WarmUpChecks;

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", true, "Watch the game's cleaning queue and log it once a minute.");
        RaiseBudget = Config.Bind("General", "RaiseBudget", false, "Also raise the cleaning budget while the game runs uncapped (measured not to help: there the game limits robot strokes to a share of the frame instead).");
        MaxBudgetMs = Config.Bind("Budget", "MaxBudgetMs", 6f, "Highest cleaning time budget per frame (milliseconds) for RaiseBudget. The game's own value is 2.");
        ExtraPasses = Config.Bind("General", "ExtraPassesV2", false, "Experimental: also run extra cleaning passes in spare frame time (single strokes can take up to ~10 ms, so this can cause hitches).");
        MinFps = Config.Bind("Budget", "MinFps", 90f, "Only use spare time while the game stays above this frame rate. While the game itself caps the frame rate lower (e.g. 30 fps in the background) that cap is used instead.");
        MaxExtraMs = Config.Bind("Budget", "MaxExtraMs", 6f, "Hard limit on the extra time spent on cleaning strokes per frame (milliseconds).");
        MinPending = Config.Bind("Budget", "MinPendingStrokes", 32, "Only help when at least this many strokes are waiting after the game's own pass.");
        MaxPassesPerFrame = Config.Bind("Budget", "MaxPassesPerFrame", 8, "At most this many extra passes per frame.");
        BackgroundBoost = Config.Bind("Background", "BoostWhileCapped", true, "While the game runs at a capped frame rate (30 fps in the background) and strokes are waiting, give its cleaning pass more time per frame; the capped frame is mostly idle. Back to the game's own value as soon as the cap is lifted or the queue is empty.");
        BackgroundBudgetMs = Config.Bind("Background", "BudgetMs", 6f, "Cleaning time per frame while capped (the game's own value is 2 ms; a 30 fps frame is 33 ms, and the game gives robot strokes at most about a fifth of it).");
        SurveyParents = Config.Bind("Survey", "MeasureParentStrokes", true, "Check the ground under J.O.O.N.A.S. strokes and log once a minute how many land on ground that is already clean.");
        SurveyEvery = Config.Bind("Survey", "CheckEveryNthWhileNotSkipping", 10, "For a J.O.O.N.A.S. that isn't skipping (paused, stopped or skipping off): check every Nth of its strokes for the report (1 = all; each check costs ~9 microseconds). While it skips, every stroke is checked.");
        SkipClean = Config.Bind("Skip", "SkipCleanParentStrokes", true, "Don't queue J.O.O.N.A.S. strokes over ground that is already clean (19 points over the whole rounded stroke clean in the game's own dust map); the robot is still told the stroke is done, with 0 kg. Spot-checks and a sector-progress watch switch it off per robot if anything looks wrong.");
        SkipBelow = Config.Bind("Skip", "CleanBelow", 0.004f, "A point counts as clean when the game's RemainingAt reads at most this (0 = spotless; the game stores dust in steps of 1/255 = 0.0039).");
        SpotCheckEvery = Config.Bind("Skip", "SpotCheckEvery", 10, "Send every Nth clean-looking stroke anyway, as a spot-check of what the game collects there.");
        WarmUpChecks = Config.Bind("Skip", "FirstSpotChecks", 20, "Each J.O.O.N.A.S. sends its first this-many clean-looking strokes as spot-checks before it skips any.");
        DustyKg = Config.Bind("Skip", "FoundDustKg", 0.02f, "A spot-check counts as 'found dust' when the game collected more than this many kg (same line as Baby J.O.O.N.A.S.; a J.O.O.N.A.S. stroke over dirty ground collects roughly 0.05 kg).");
        MaxDustyShare = Config.Bind("Skip", "MaxDustyShare", 0.2f, "A J.O.O.N.A.S. stops skipping for the session when more than this share of its recent spot-checks (at least 20) found dust: the clean-ground check is wrong for it.");
        MaxLostShare = Config.Bind("Skip", "MaxLostDustShare", 0.02f, "A J.O.O.N.A.S. pauses skipping (see PauseMinutes) when the dust its skipped strokes would have picked up (estimated from its spot-checks) is more than this share of the dust it collects.");
        MinKg = Config.Bind("Skip", "MinCollectedKg", 20f, "The MaxLostDustShare rule only judges a J.O.O.N.A.S. once it has collected this much dust recently, and only when its skipped strokes would have picked up at least 0.5 kg (a robot placed on ground that others already cleaned reads high shares from very little dust).");
        PauseMinutes = Config.Bind("Skip", "PauseMinutes", 10f, "How long a J.O.O.N.A.S. sends every stroke after the MaxLostDustShare rule fired; doubles each time for that robot, up to 60 minutes. Afterwards it starts again with fresh numbers.");
        GuardMinutes = Config.Bind("Skip", "NoSectorMinutes", 8f, "A J.O.O.N.A.S. stops skipping for the session when it cleans this many minutes without finishing a sector (normal: about 1 minute per sector, longest seen 7.4).");
        MeasureStrokes = Config.Bind("Survey", "MeasureStrokeCost", true, "Time every stroke inside the game's cleaning (MoonWorld.CleanToolStroke) and log once a minute what J.O.O.N.A.S., babies and other tools cost per stroke; also log cleaning passes over 25 ms.");
        if (!Enabled.Value) { L.LogInfo("Cleaning Catch-Up disabled in config."); return; }
        var h = new Harmony("moonsweepers.cleaningcatchup"); H = h;
        h.PatchAll(typeof(Hooks));
        if (SurveyParents.Value || SkipClean.Value) h.PatchAll(typeof(ParentStrokes));
        if (MeasureStrokes.Value)
        {
            try { h.PatchAll(typeof(StrokeCost)); }
            catch (Exception e) { L.LogWarning("Stroke cost meter could not be attached: " + e.Message); }
        }
        ClassInjector.RegisterTypeInIl2Cpp<CatchUpBehaviour>();
        AddComponent<CatchUpBehaviour>();
        L.LogInfo($"Cleaning Catch-Up {Version} loaded. Clean-ground skipping {(SkipClean.Value ? $"on (19 points <= {SkipBelow.Value}, spot-check every {SpotCheckEvery.Value}th, found dust > {DustyKg.Value} kg; a robot whose skipping would cost more than {MaxLostShare.Value:P0} of its dust pauses for {PauseMinutes.Value:0} min, judged after {MinKg.Value:0} kg)" : "off")}; background boost {(BackgroundBoost.Value ? $"on (up to {BackgroundBudgetMs.Value} ms)" : "off")}; budget raising while uncapped {(RaiseBudget.Value ? "on" : "off")}; extra passes {(ExtraPasses.Value ? "on" : "off")}.");
    }
}

[HarmonyPatch]
internal static class Hooks
{
    internal static bool Ours;
    internal static int MainThread = -1;
    // the game's own calls, accumulated per minute
    internal static int GameCalls, GameCallsComplete, GameProcessed, GameMaxProcessed, OffThread;
    internal static double GameMs, GameMaxMs;
    internal static float LastRobotMs = float.NaN;
    internal static int LastFramePending = -1, LastFrameProcessed, LastFrameCalls, FrameOfLastCall = -1;
    // split by whether the frame rate was capped (background) at the time
    internal static int CappedCalls, CappedProcessed; internal static double CappedMs;
    internal static bool CappedNow;
    static int logged;
    static readonly Stopwatch sw = new();
    static int before;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ProcessToolStrokes))]
    [HarmonyPrefix]
    static void Before(MoonWorld __instance, bool complete, float robotMs)
    {
        if (Ours) return;
        try
        {
            if (MainThread >= 0 && Environment.CurrentManagedThreadId != MainThread) { OffThread++; return; }
            before = __instance.PendingToolStrokes;
            if (logged < 3) { logged++; Plugin.L.LogInfo($"[game call] ProcessToolStrokes(complete={complete}, robotMs={robotMs}) with {before} strokes waiting."); }
            LastRobotMs = robotMs;
            if (complete) GameCallsComplete++;
            StrokeCost.InPass = 0; StrokeCost.PassTicks = StrokeCost.PassMaxTicks = 0;
            sw.Restart();
        }
        catch { }
    }

    internal static long OwedTicks;
    internal static int SlowPasses;
    internal static readonly List<(double ms, string what)> Slowest = new();

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ProcessToolStrokes))]
    [HarmonyPostfix]
    static void After(MoonWorld __instance)
    {
        if (MainThread >= 0 && Environment.CurrentManagedThreadId != MainThread) return;
        double ms = sw.Elapsed.TotalMilliseconds;   // the game's own pass, before we pay out skipped strokes (0.5 counted both)
        // skipped strokes report "done" at the point where the game reports its own
        long o0 = Stopwatch.GetTimestamp();
        try { ParentStrokes.PayOwed(); } catch { }
        OwedTicks += Stopwatch.GetTimestamp() - o0;
        if (Ours) return;
        try
        {
            int after = __instance.PendingToolStrokes, done = Math.Max(0, before - after);
            if (ms >= 25.0)
            {
                SlowPasses++;
                string what = $"{ms:0.0} ms with {done} strokes done ({StrokeCost.InPass} timed: {StrokeCost.Ms(StrokeCost.PassTicks):0.0} ms inside them, slowest {StrokeCost.Ms(StrokeCost.PassMaxTicks):0.0} ms from {StrokeCost.Names[StrokeCost.PassMaxKind]}), {after} waiting";
                Slowest.Add((ms, what)); Slowest.Sort((x, y) => y.ms.CompareTo(x.ms)); if (Slowest.Count > 4) Slowest.RemoveAt(4);
            }
            GameCalls++; GameProcessed += done; GameMs += ms;
            if (CappedNow) { CappedCalls++; CappedProcessed += done; CappedMs += ms; }
            if (done > GameMaxProcessed) GameMaxProcessed = done;
            if (ms > GameMaxMs) GameMaxMs = ms;
            int frame = Time.frameCount;
            if (frame != FrameOfLastCall) { FrameOfLastCall = frame; LastFrameProcessed = 0; LastFrameCalls = 0; }
            LastFrameProcessed += done; LastFrameCalls++;
            LastFramePending = after;
        }
        catch { }
    }
}

// What each kind of stroke costs inside the game's cleaning (0.6). MoonWorld.CleanToolStroke is timed per call and sorted
// by sender; MoonWorld.ApplyCleaningCore (the per-piece workhorse) is counted inside it, and timed on its own when it runs
// outside it (in case the queue doesn't go through CleanToolStroke). Kinds: 0 = J.O.O.N.A.S. (robot storage, narrow
// hard-edged strokes), 1 = babies (robot storage, wide or soft-edged), 2 = everything else (player, vehicles).
[HarmonyPatch]
internal static class StrokeCost
{
    internal static readonly string[] Names = { "J.O.O.N.A.S.", "babies", "other" };
    internal static readonly int[] Count = new int[3], Pieces = new int[3], LooseCount = new int[3];
    internal static readonly long[] Ticks = new long[3], MaxTicks = new long[3], LooseTicks = new long[3];
    internal static readonly double[] Area = new double[3], Len = new double[3];
    internal static int InPass, PassMaxKind; internal static long PassTicks, PassMaxTicks;
    internal static bool CoreOff;
    static long t0, c0; static int kind, looseKind; static bool inTool, inLoose;
    static int rateCalls; static float rateStart = -1f;

    static bool Main => Hooks.MainThread < 0 || Environment.CurrentManagedThreadId == Hooks.MainThread;
    internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CleanToolStroke))]
    [HarmonyPrefix]
    static void ToolBefore(Vector3 from, Vector3 to, float width, float edgeWidth, string vehicleStorageId)
    {
        if (inTool || !Main) return;
        try
        {
            bool robot = vehicleStorageId != null && vehicleStorageId.StartsWith("robot/", StringComparison.Ordinal);
            kind = !robot ? 2 : (width > 1.4f || edgeWidth > 0.12f) ? 1 : 0;
            float r = width * 0.5f + edgeWidth, len = (to - from).magnitude;
            Area[kind] += 2.0 * r * len + Math.PI * r * r; Len[kind] += len;
            inTool = true; t0 = Stopwatch.GetTimestamp();
        }
        catch { inTool = false; }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CleanToolStroke))]
    [HarmonyPostfix]
    static void ToolAfter()
    {
        if (!inTool || !Main) return;
        long dt = Stopwatch.GetTimestamp() - t0; inTool = false;
        Count[kind]++; Ticks[kind] += dt; if (dt > MaxTicks[kind]) MaxTicks[kind] = dt;
        InPass++; PassTicks += dt; if (dt > PassMaxTicks) { PassMaxTicks = dt; PassMaxKind = kind; }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ApplyCleaningCore))]
    [HarmonyPrefix]
    static void CoreBefore(float pad)
    {
        if (CoreOff || !Main) return;
        rateCalls++;
        if (inTool) { Pieces[kind]++; return; }
        if (inLoose) return;
        looseKind = pad > 0.7f ? 1 : 0; inLoose = true; c0 = Stopwatch.GetTimestamp();
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ApplyCleaningCore))]
    [HarmonyPostfix]
    static void CoreAfter()
    {
        if (!inLoose || !Main) return;
        long dt = Stopwatch.GetTimestamp() - c0; inLoose = false;
        LooseCount[looseKind]++; LooseTicks[looseKind] += dt;
        InPass++; PassTicks += dt; if (dt > PassMaxTicks) { PassMaxTicks = dt; PassMaxKind = looseKind; }
    }

    // If ApplyCleaningCore runs thousands of times a second, the hook's own overhead would distort what it measures:
    // remove that hook (CleanToolStroke stays timed).
    internal static void CheckRate()
    {
        if (CoreOff || Plugin.H == null || !Plugin.MeasureStrokes.Value) return;
        float now = Time.realtimeSinceStartup;
        if (rateStart < 0f) { rateStart = now; rateCalls = 0; return; }
        if (now - rateStart < 5f) return;
        float rate = rateCalls / (now - rateStart);
        rateStart = now; rateCalls = 0;
        if (rate <= 3000f) return;
        CoreOff = true;
        try { Plugin.H.Unpatch(AccessTools.Method(typeof(MoonWorld), nameof(MoonWorld.ApplyCleaningCore)), HarmonyPatchType.All, Plugin.H.Id); }
        catch (Exception e) { Plugin.L.LogWarning("Could not remove the ApplyCleaningCore hook (it now returns at once): " + e.Message); }
        Plugin.L.LogInfo($"Stroke cost: ApplyCleaningCore runs {rate:0} times a second; that hook was removed (CleanToolStroke is still timed).");
    }

    internal static void Report()
    {
        if (!Plugin.MeasureStrokes.Value) return;
        var sb = new StringBuilder("Stroke cost last minute (time inside the game's cleaning): ");
        long all = Ticks[0] + Ticks[1] + Ticks[2];
        for (int k = 0; k < 3; k++)
        {
            if (k > 0) sb.Append("; ");
            sb.Append(Names[k]).Append(' ');
            if (Count[k] == 0) { sb.Append("none"); continue; }
            sb.Append($"{Count[k]} strokes, {Ms(Ticks[k]) / Count[k]:0.000} ms each (max {Ms(MaxTicks[k]):0.0}), {Len[k] / Count[k]:0.0} m long, {Area[k] / Count[k]:0.0} m2 under each");
            if (!CoreOff) sb.Append($", {(double)Pieces[k] / Count[k]:0.0} pieces each");
            sb.Append($", {Ms(Ticks[k]):0} ms in all ({100.0 * Ticks[k] / Math.Max(1, all):0}%)");
        }
        if (LooseCount[0] + LooseCount[1] > 0) sb.Append($". Cleaning pieces outside CleanToolStroke: narrow {LooseCount[0]} ({Ms(LooseTicks[0]):0} ms), wide {LooseCount[1]} ({Ms(LooseTicks[1]):0} ms)");
        if (all == 0 && LooseCount[0] + LooseCount[1] == 0) sb.Append(". Nothing measured: the game's queue may not use these methods");
        sb.Append($". Paying out skipped strokes took {Ms(Hooks.OwedTicks):0} ms. Cleaning passes over 25 ms: {Hooks.SlowPasses}");
        for (int i = 0; i < Hooks.Slowest.Count; i++) sb.Append(i == 0 ? "; slowest: " : " | ").Append(Hooks.Slowest[i].what);
        sb.Append('.');
        Plugin.L.LogInfo(sb.ToString());
        Array.Clear(Count, 0, 3); Array.Clear(Pieces, 0, 3); Array.Clear(LooseCount, 0, 3);
        Array.Clear(Ticks, 0, 3); Array.Clear(MaxTicks, 0, 3); Array.Clear(LooseTicks, 0, 3);
        Array.Clear(Area, 0, 3); Array.Clear(Len, 0, 3);
        Hooks.OwedTicks = 0; Hooks.SlowPasses = 0; Hooks.Slowest.Clear();
    }
}

// J.O.O.N.A.S. strokes over ground that is already clean.
// Every parent stroke is checked against the game's own dust map (RemainingAt) at 19 points covering the whole rounded
// stroke (both end caps, both edges, the middle). If all are clean the stroke would pick up nothing, so it is not
// queued; the robot's own "stroke done" callback is called with (0 changed, 0 kg) right after the game's next cleaning
// pass, exactly what the game reports for a stroke over clean ground.
// Safety, per robot:
//  - spot-checks: its first 20 clean-looking strokes, and every 10th after that, are sent anyway under a stroke id of
//    their own (so the game never joins them with other strokes) and the game's result is recorded;
//  - it stops skipping for the session if more than 20% of its recent spot-checks found dust (> 0.02 kg), or if it
//    cleans 8 minutes without finishing a sector (both mean the check is wrong for it);
//  - it pauses skipping (10 min, doubling, max 60) if the dust its skipped strokes would have picked up (spot-check
//    average x strokes skipped) is more than 2% of the dust it collects - judged only after 20 kg and 0.5 kg;
//  - if more than half of the robots present stopped for good, skipping stops for all. Babies, players, vehicles:
//    never touched.
[HarmonyPatch]
internal static class ParentStrokes
{
    internal sealed class Bot
    {
        public string Id; public MoonRobotVacuum R; public int StrokePlayer;
        public bool Off; public int WarmLeft, Tick, WinDone, WinDusty;
        public float PauseUntil; public int Pauses;      // dust-share pauses (0.7): skipping resumes at PauseUntil
        public int Hexes = int.MinValue, SkippedSinceSector; public float FieldSeconds;
        public int Strokes, Skipped;                     // this minute
        // dust-share window (halved every 2 minutes, so it follows recent behaviour)
        public double KgSeen = -1, WinCollected, WinAuditKg; public int WinSkipped, WinAudits; public float WinAge;
    }

    // One per robot callback (the robot reuses one per brush): the wrapper used for its spot-checks.
    sealed class Spot
    {
        public Bot Bot; public int Slot; public Il2CppSystem.Action<int, double> Orig, Wrapper;
        public readonly Queue<bool> NearDirt = new();   // per spot-check in flight: was there dirt just outside the stroke?
    }

    const int AuditBit = 0x00400000;
    internal static readonly Dictionary<string, MoonRobotVacuum> Robots = new();
    static readonly Dictionary<string, Bot> bots = new();
    static readonly Dictionary<IntPtr, Spot> spots = new();
    static readonly List<Il2CppSystem.Action<int, double>> owed = new(), payBuf = new();
    static readonly Stopwatch sw = new();
    internal static bool SkipOn = true;
    internal static float Now;                          // Time.unscaledTime, set once a frame by CatchUpBehaviour
    static bool inPay;
    static int tick, errors, payErrors, auditLogged, geomLogged, stopLogged, pausesThisMinute, stopsThisMinute;
    // per minute
    static int parent, baby, other, noRobot, sampled, clean, busy, busyClean, calm, calmClean, skipped, auditsSent, auditsDone, auditsDusty, paid, changed0;
    static int smallDust, smallDustNear, zeroNear, zeroCount;
    static double sampleMs, auditKg, collectedKg;
    static readonly int[] kgHist = new int[6];
    static readonly int[] slotStrokes = new int[9], slotSkipped = new int[9];

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.QueueToolStroke))]
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]   // after other mods' hooks, so they still see every stroke
    static bool Before(MoonWorld __instance, ref int player, Vector3 from, Vector3 to, Vector3 fromRight, Vector3 toRight, float width, float edgeWidth,
                       ref Il2CppSystem.Action<int, double> completed, string vehicleStorageId, ref bool __result)
    {
        try
        {
            if (Hooks.MainThread < 0 || Environment.CurrentManagedThreadId != Hooks.MainThread) return true;
            if (string.IsNullOrEmpty(vehicleStorageId) || !vehicleStorageId.StartsWith("robot/", StringComparison.Ordinal)) { other++; return true; }
            if ((player & unchecked((int)0xFF000000)) == 0x5B000000) { baby++; return true; }   // Baby J.O.O.N.A.S. checks its own
            parent++;
            var b = BotFor(vehicleStorageId);
            if (b == null) { noRobot++; return true; }
            b.Strokes++;
            int slot = Slot(b, player); slotStrokes[slot]++;
            bool skipping = Plugin.SkipClean.Value && SkipOn && !b.Off && Now >= b.PauseUntil;
            if (!skipping && (!Plugin.SurveyParents.Value || ++tick % Math.Max(1, Plugin.SurveyEvery.Value) != 0)) return true;

            sw.Restart();
            var g = new Geometry(b.R, from, to, fromRight, toRight, width, edgeWidth);
            bool isClean = g.Valid && AllClean(__instance, b.R, in g);
            sampleMs += sw.Elapsed.TotalMilliseconds; sampled++;
            if (geomLogged < 1 && g.Valid) { geomLogged++; Plugin.L.LogInfo($"[skip] stroke shape: {g.Length:0.00} m long, width {width:0.00} m, edge {edgeWidth:0.00} m -> checked radius {g.Radius:0.00} m around the stroke."); }
            if (__instance.PendingToolStrokes >= 500) { busy++; if (isClean) busyClean++; } else { calm++; if (isClean) calmClean++; }
            if (!isClean) return true;
            clean++;
            if (!skipping) return true;

            if (b.WarmLeft > 0 || ++b.Tick % Math.Max(2, Plugin.SpotCheckEvery.Value) == 0)
            {
                // spot-check: send it anyway, under its own stroke id, and record what the game collects
                if (b.WarmLeft > 0) b.WarmLeft--;
                if (completed != null)
                {
                    var s = SpotFor(completed, b, slot);
                    if (s != null)
                    {
                        bool near = false; try { near = DirtNear(__instance, b.R, in g); } catch { }
                        if (s.NearDirt.Count > 16) s.NearDirt.Clear();
                        s.NearDirt.Enqueue(near);
                        completed = s.Wrapper; player ^= AuditBit; auditsSent++;
                    }
                }
                return true;
            }
            if (completed != null) owed.Add(completed);
            skipped++; b.Skipped++; b.SkippedSinceSector++; b.WinSkipped++; slotSkipped[slot]++;
            __result = true;
            return false;
        }
        catch (Exception e) { if (errors++ < 3) Plugin.L.LogWarning("Stroke check: " + e.Message); return true; }
    }

    // A stroke as the game cleans it: a strip from 'from' to 'to' with rounded ends (radius = half width + soft edge).
    readonly struct Geometry
    {
        public readonly bool Valid; public readonly Vector3 From, To, Dir, SideF, SideT, SideM; public readonly float Length, Radius;
        public Geometry(MoonRobotVacuum r, Vector3 from, Vector3 to, Vector3 fromRight, Vector3 toRight, float width, float edge)
        {
            From = from; To = to; Length = (to - from).magnitude;
            Radius = Math.Max(0.05f, width * 0.5f + Math.Max(0f, edge));
            Vector3 sf = Side(fromRight), st = Side(toRight);
            if (sf == Vector3.zero) sf = st; else if (st == Vector3.zero) st = sf;
            Vector3 sm = Side(sf + st);
            Vector3 dir = Length > 1e-3f ? (to - from) / Length : Vector3.zero;
            if (dir == Vector3.zero && sm != Vector3.zero)
            {
                // a stroke with no length (a dab): use the up direction to get "forward" from "right"
                Vector3 up = Vector3.up; try { up = r.Up(from); } catch { }
                dir = Side(Vector3.Cross(sm, up));
            }
            SideF = sf; SideT = st; SideM = sm; Dir = dir;
            Valid = sm != Vector3.zero && dir != Vector3.zero;   // no side direction: the edges can't be checked, so send it
        }
    }

    static Vector3 Side(Vector3 v) => v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.zero;

    static bool Clean(MoonWorld w, MoonRobotVacuum r, Vector3 p, float lim)
    {
        float v = w.RemainingAt(r.Up(p));
        return v >= 0f && v <= lim;   // NaN or negative = unknown = dirty
    }

    // 19 points over the rounded strip, likeliest-dirty first so most strokes over dirt stop after one or two:
    // middle, front end, front cap, back end, back cap; both edges at the middle and the ends; the caps' diagonals;
    // and the gaps between centre line and edges.
    static bool AllClean(MoonWorld w, MoonRobotVacuum r, in Geometry g)
    {
        float lim = Plugin.SkipBelow.Value, R = g.Radius, e = R * 0.92f, c = R * 0.65f, L = g.Length;
        Vector3 d = g.Dir, f = g.From, t = g.To, m = (f + t) * 0.5f;
        if (!Clean(w, r, m, lim) || !Clean(w, r, t, lim) || !Clean(w, r, t + d * e, lim) || !Clean(w, r, f, lim) || !Clean(w, r, f - d * e, lim)) return false;
        if (!Clean(w, r, m + g.SideM * e, lim) || !Clean(w, r, m - g.SideM * e, lim)) return false;
        if (!Clean(w, r, t + g.SideT * e, lim) || !Clean(w, r, t - g.SideT * e, lim) || !Clean(w, r, f + g.SideF * e, lim) || !Clean(w, r, f - g.SideF * e, lim)) return false;
        if (!Clean(w, r, t + (d + g.SideT) * c, lim) || !Clean(w, r, t + (d - g.SideT) * c, lim)) return false;
        if (!Clean(w, r, f + (-d + g.SideF) * c, lim) || !Clean(w, r, f + (-d - g.SideF) * c, lim)) return false;
        Vector3 q1 = f + d * (L * 0.25f), q3 = f + d * (L * 0.75f);
        return Clean(w, r, q1 + g.SideM * (R * 0.45f), lim) && Clean(w, r, q1 - g.SideM * (R * 0.45f), lim)
            && Clean(w, r, q3 + g.SideM * (R * 0.45f), lim) && Clean(w, r, q3 - g.SideM * (R * 0.45f), lim);
    }

    // Spot-check diagnostics: is there dirt just outside the stroke (a ring at 1.6x its radius)?
    static bool DirtNear(MoonWorld w, MoonRobotVacuum r, in Geometry g)
    {
        float lim = Plugin.SkipBelow.Value, R = g.Radius * 1.6f, c = R * 0.7f;
        Vector3 d = g.Dir, f = g.From, t = g.To, m = (f + t) * 0.5f;
        return !Clean(w, r, t + d * R, lim) || !Clean(w, r, f - d * R, lim) || !Clean(w, r, m + g.SideM * R, lim) || !Clean(w, r, m - g.SideM * R, lim)
            || !Clean(w, r, t + (d + g.SideT) * c, lim) || !Clean(w, r, t + (d - g.SideT) * c, lim) || !Clean(w, r, f + (-d + g.SideF) * c, lim) || !Clean(w, r, f + (-d - g.SideF) * c, lim);
    }

    // Brush slot for the report: J.O.O.N.A.S. pads use the robot's stroke id plus multiples of 0x01010101.
    static int Slot(Bot b, int player)
    {
        int d = unchecked(player - b.StrokePlayer);
        if (d == 0) return 0;
        if (d % 0x01010101 == 0) { int k = d / 0x01010101; if (k >= 1 && k <= 7) return k; }
        return 8;
    }

    static Bot BotFor(string id)
    {
        if (bots.TryGetValue(id, out var b)) return b.R != null ? b : null;
        if (!Robots.TryGetValue(id, out var r) || r == null) return null;
        b = new Bot { Id = id, R = r, WarmLeft = Math.Max(0, Plugin.WarmUpChecks.Value) };
        try { b.StrokePlayer = r.strokePlayer; } catch { }
        bots[id] = b;
        return b;
    }

    static Spot SpotFor(Il2CppSystem.Action<int, double> orig, Bot b, int slot)
    {
        IntPtr key = orig.Pointer;
        if (key == IntPtr.Zero) return null;
        if (spots.TryGetValue(key, out var s)) return s;
        if (spots.Count >= 4096) spots.Clear();
        s = new Spot { Bot = b, Slot = slot, Orig = orig };
        var me = s;
        s.Wrapper = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int, double>>(new Action<int, double>((changed, kg) =>
        {
            try { AuditResult(me, changed, kg); } catch { }
            try { me.Orig.Invoke(changed, kg); }
            catch (Exception e) { if (payErrors++ < 3) Plugin.L.LogWarning("Spot-check hand-back failed: " + e.Message); }
        }));
        spots[key] = s;
        return s;
    }

    static void AuditResult(Spot s, int changed, double kg)
    {
        var b = s.Bot;
        bool near = s.NearDirt.Count > 0 && s.NearDirt.Dequeue();
        auditsDone++; if (kg > 0) auditKg += kg;
        kgHist[kg <= 0 ? 0 : kg <= 0.001 ? 1 : kg <= 0.005 ? 2 : kg <= 0.02 ? 3 : kg <= 0.1 ? 4 : 5]++;
        if (changed == 0) changed0++;
        if (kg > 0.005) { smallDust++; if (near) smallDustNear++; }
        else if (kg <= 0) { zeroCount++; if (near) zeroNear++; }
        bool dusty = kg > Plugin.DustyKg.Value;
        if (dusty) auditsDusty++;
        if (auditLogged < 8) { auditLogged++; Plugin.L.LogInfo($"[spot-check] {b.Id} brush {s.Slot}: changed {changed}, {kg:0.#####} kg{(near ? ", dirt just outside the stroke" : "")}."); }
        b.WinDone++; if (dusty) b.WinDusty++;
        b.WinAudits++; if (kg > 0) b.WinAuditKg += kg;
        if (!b.Off && b.WinDone >= 20 && b.WinDusty > b.WinDone * Plugin.MaxDustyShare.Value)
            TurnOff(b, $"{b.WinDusty} of its last {b.WinDone} spot-checks found more than {Plugin.DustyKg.Value} kg");
        if (b.WinDone >= 40) { b.WinDone /= 2; b.WinDusty /= 2; }
    }

    static string Where(Bot b)
    {
        try { if (b.R != null && !b.R.WasCollected) return $" (sector {b.R.HexNumber}, {b.R.HexesDone} done, {b.R.state})"; } catch { }
        return "";
    }

    // The check is wrong for this robot: it sends every stroke for the rest of the session.
    static void TurnOff(Bot b, string why)
    {
        if (b.Off) return;
        b.Off = true; stopsThisMinute++;
        if (stopLogged++ < 20) Plugin.L.LogWarning($"{b.Id} stops skipping clean ground: {why}{Where(b)}. It sends every stroke from now on.");
        int off = 0, present = 0;
        foreach (var x in bots.Values) { if (x.R == null) continue; present++; if (x.Off) off++; }   // robots still in the world
        if (SkipOn && present >= 4 && off * 2 > present)
        {
            SkipOn = false;
            Plugin.L.LogWarning($"Clean-ground skipping switched off for all J.O.O.N.A.S. ({off} of {present} had to stop).");
        }
    }

    // Skipping cost this robot a bit more dust than allowed: it sends every stroke for a while, then starts again with
    // fresh numbers (pauses double for the same robot, up to an hour). Never switches skipping off for anyone else.
    static void Pause(Bot b, string why)
    {
        b.Pauses++; pausesThisMinute++;
        float minutes = Math.Min(60f, Math.Max(1f, Plugin.PauseMinutes.Value) * (1 << Math.Min(3, b.Pauses - 1)));
        b.PauseUntil = Now + minutes * 60f;
        b.WinCollected = b.WinAuditKg = 0; b.WinSkipped = b.WinAudits = 0; b.WinAge = 0f; b.WinDone = b.WinDusty = 0;
        b.WarmLeft = Math.Max(0, Plugin.WarmUpChecks.Value / 2);
        if (stopLogged++ < 20) Plugin.L.LogInfo($"{b.Id} pauses skipping clean ground for {minutes:0} minutes: {why}{Where(b)}.");
    }

    // Owed "done" signals for skipped strokes: (0 changed, 0 kg), what the game reports for a stroke over clean ground.
    internal static void PayOwed()
    {
        if (owed.Count == 0 || inPay) return;
        inPay = true;
        try
        {
            payBuf.AddRange(owed); owed.Clear();
            for (int i = 0; i < payBuf.Count; i++)
            {
                try { payBuf[i].Invoke(0, 0.0); paid++; }
                catch (Exception e)
                {
                    if (++payErrors <= 3) Plugin.L.LogWarning("Done-signal for a skipped stroke failed: " + e.Message);
                    if (payErrors == 50 && SkipOn) { SkipOn = false; Plugin.L.LogWarning("Done-signals for skipped strokes keep failing; clean-ground skipping switched off."); }
                }
            }
        }
        finally { payBuf.Clear(); inPay = false; }
    }

    internal static void ResetWorld() { owed.Clear(); spots.Clear(); bots.Clear(); Robots.Clear(); }

    // Every 5 s: robot lookup by storage id, dust collected, the sector-progress watch and the dust-share rule.
    internal static void Refresh(float gameDt)
    {
        try
        {
            var all = MoonRobotDock.all;
            if (all == null) return;
            Robots.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                var d = all.get_Item(i); if (d == null) continue;
                var r = d.Robot; if (r == null) continue;
                var id = r.storageId; if (!string.IsNullOrEmpty(id)) Robots[id] = r;
            }
            foreach (var b in bots.Values)
            {
                if (Robots.TryGetValue(b.Id, out var r)) { b.R = r; try { b.StrokePlayer = r.strokePlayer; } catch { } }
                else b.R = null;
            }
        }
        catch (Exception e) { if (errors++ < 3) Plugin.L.LogWarning("Robot lookup: " + e.Message); }
        if (gameDt > 0f) Guard(gameDt);
    }

    static void Guard(float dt)
    {
        foreach (var b in bots.Values)
        {
            if (b.R == null) continue;
            int hexes; MoonRobotVacuum.State st; double kg;
            try { if (b.R.WasCollected) continue; hexes = b.R.HexesDone; st = b.R.state; kg = b.R.DustKilograms; } catch { continue; }
            // dust collected: rises while it cleans, drops when it fires a capsule (those drops are ignored)
            if (b.KgSeen >= 0 && kg > b.KgSeen) { b.WinCollected += kg - b.KgSeen; collectedKg += kg - b.KgSeen; }
            b.KgSeen = kg;
            if (!Plugin.SkipClean.Value) continue;
            if (hexes != b.Hexes) { b.Hexes = hexes; b.FieldSeconds = 0f; b.SkippedSinceSector = 0; }
            else
            {
                if (st == MoonRobotVacuum.State.Aligning || st == MoonRobotVacuum.State.Cleaning || st == MoonRobotVacuum.State.Checking || st == MoonRobotVacuum.State.Turning)
                    b.FieldSeconds += dt;
                if (!b.Off && SkipOn && b.SkippedSinceSector > 0 && b.FieldSeconds > Plugin.GuardMinutes.Value * 60f)
                    TurnOff(b, $"no sector finished in {b.FieldSeconds / 60f:0.0} minutes of cleaning ({b.SkippedSinceSector} strokes skipped meanwhile)");
            }
            // dust-share rule: what the skipped strokes would have picked up vs what the robot collected (needs real
            // evidence; a fresh robot on ground others already cleaned collects almost nothing either way)
            if (!b.Off && SkipOn && Now >= b.PauseUntil && b.WinAudits >= 30 && b.WinCollected >= Math.Max(2f, Plugin.MinKg.Value))
            {
                double lost = b.WinSkipped * (b.WinAuditKg / b.WinAudits), share = lost / (b.WinCollected + lost);
                if (lost >= 0.5 && share > Plugin.MaxLostShare.Value)
                    Pause(b, $"its skipped strokes would have picked up about {share:P1} of its dust ({lost:0.0} kg of {b.WinCollected + lost:0} kg)");
            }
            b.WinAge += dt;
            if (b.WinAge >= 120f) { b.WinAge = 0f; b.WinCollected *= 0.5; b.WinAuditKg *= 0.5; b.WinSkipped /= 2; b.WinAudits /= 2; }
        }
    }

    static string Pc(int a, int b) => b <= 0 ? "-" : $"{100.0 * a / b:0}%";

    internal static void Report()
    {
        if (!Plugin.SurveyParents.Value && !Plugin.SkipClean.Value) return;
        var counts = new List<int>();
        int offNow = 0, pausedNow = 0, present = 0;
        foreach (var b in bots.Values)
        {
            if (b.Strokes > 0) counts.Add(b.Strokes);
            if (b.R == null) continue;
            present++; if (b.Off) offNow++; else if (Now < b.PauseUntil) pausedNow++;
        }
        counts.Sort();
        int median = counts.Count > 0 ? counts[counts.Count / 2] : 0;
        Plugin.L.LogInfo($"J.O.O.N.A.S. strokes last minute: {parent} from {counts.Count} robots (median {median / 60.0:0.0} per robot per second), baby strokes {baby}, other {other}, robot not found {noRobot}. " +
            $"Checked {sampled} ({sampleMs:0.0} ms): already clean {clean} ({Pc(clean, sampled)}); while the queue was long (500+) {Pc(busyClean, busy)} of {busy}, while short {Pc(calmClean, calm)} of {calm}. J.O.O.N.A.S. collected {collectedKg:0} kg.");
        if (Plugin.SkipClean.Value)
        {
            double avg = auditsDone > 0 ? auditKg / auditsDone : 0, lost = skipped * avg;
            Plugin.L.LogInfo($"Clean-ground skipping ({(SkipOn ? "on" : "OFF")}): {skipped} strokes skipped ({Pc(skipped, parent)} of all J.O.O.N.A.S. strokes), done-signals sent {paid}{(payErrors > 0 ? $" ({payErrors} failed so far)" : "")}; " +
                $"dust they would have picked up, estimated from spot-checks: {lost:0.0} kg ({(collectedKg + lost > 0 ? lost / (collectedKg + lost) : 0):P1} of the total). " +
                $"Spot-checks: {auditsSent} sent, {auditsDone} came back, {auditsDusty} found more than {Plugin.DustyKg.Value} kg; average {avg * 1000:0.00} g. " +
                $"Collected per spot-check: 0 kg x{kgHist[0]}, <=0.001 x{kgHist[1]}, <=0.005 x{kgHist[2]}, <=0.02 x{kgHist[3]}, <=0.1 x{kgHist[4]}, more x{kgHist[5]}; 'changed' 0 in {Pc(changed0, auditsDone)}. " +
                $"Dirt just outside the stroke: {Pc(smallDustNear, smallDust)} of spot-checks that found more than 5 g, {Pc(zeroNear, zeroCount)} of those that found none. " +
                $"Of {present} J.O.O.N.A.S.: {pausedNow} paused skipping ({pausesThisMinute} pauses started this minute), {offNow} stopped for the session ({stopsThisMinute} this minute).");
            var sb = new StringBuilder("Skipped by brush (0 = main, 8 = other):");
            for (int i = 0; i < slotStrokes.Length; i++) if (slotStrokes[i] > 0) sb.Append($" {i}: {Pc(slotSkipped[i], slotStrokes[i])} of {slotStrokes[i]};");
            Plugin.L.LogInfo(sb.ToString());
        }
        parent = baby = other = noRobot = sampled = clean = busy = busyClean = calm = calmClean = skipped = auditsSent = auditsDone = auditsDusty = paid = changed0 = 0;
        smallDust = smallDustNear = zeroNear = zeroCount = 0; pausesThisMinute = stopsThisMinute = 0;
        sampleMs = auditKg = collectedKg = 0;
        Array.Clear(kgHist, 0, kgHist.Length); Array.Clear(slotStrokes, 0, slotStrokes.Length); Array.Clear(slotSkipped, 0, slotSkipped.Length);
        foreach (var b in bots.Values) { b.Strokes = 0; b.Skipped = 0; }
    }
}

public class CatchUpBehaviour : MonoBehaviour
{
    public CatchUpBehaviour(IntPtr ptr) : base(ptr) { }

    MoonWorld world; IntPtr worldPtr;
    MoonSettings settings; float stock = -1f, gameBudget = -1f; IntPtr settingsPtr;
    double sumGameBudget, sumBoostBudget; int raisedFrames, cappedFrames, boostFrames; float maxGameBudget;
    float nextLookup, nextReport = 60f, dtAvg = 16f, budget, lastGameTime = -1f;
    double lastMetresCleaned = -1, lastMetresJumped = -1;
    bool reportedSetup, stopped, boostLogged;
    int hitches;
    readonly Stopwatch sw = new();
    // our extra work, accumulated per minute
    int frames, helpedFrames, passes, extraProcessed, maxPending, sumPendingN;
    double extraMs, extraMaxMs, sumPending, sumBudget, sumDt;

    void Update()
    {
        if (Hooks.MainThread < 0) Hooks.MainThread = Environment.CurrentManagedThreadId;
        ParentStrokes.Now = Time.unscaledTime;
        float now = Time.realtimeSinceStartup;
        if (now >= nextLookup)
        {
            nextLookup = now + 5f;
            try { if (world == null || world.WasCollected || !world.isActiveAndEnabled) world = UnityEngine.Object.FindFirstObjectByType<MoonWorld>(); } catch { world = null; }
            IntPtr wp = IntPtr.Zero; try { if (world != null) wp = world.Pointer; } catch { }
            if (wp != worldPtr) { if (worldPtr != IntPtr.Zero) ParentStrokes.ResetWorld(); worldPtr = wp; lastMetresCleaned = lastMetresJumped = -1; }
            if (world != null && !reportedSetup) { reportedSetup = true; ReportSetup(); }
            BindSettings();
            float gt = Time.time, gdt = lastGameTime < 0f ? 0f : Math.Max(0f, gt - lastGameTime); lastGameTime = gt;
            if (Plugin.SurveyParents.Value || Plugin.SkipClean.Value) ParentStrokes.Refresh(gdt);
        }
    }

    void LateUpdate()
    {
        try
        {
            ParentStrokes.PayOwed();   // in case the game's cleaning pass did not run this frame
            StrokeCost.CheckRate();
            float dtMs = Time.unscaledDeltaTime * 1000f;
            if (dtMs > 0f && dtMs < 1000f) dtAvg += (dtMs - dtAvg) * 0.08f;
            float now = Time.realtimeSinceStartup;
            if (now >= nextReport) { nextReport = now + 60f; Report(); }
            if (world == null || world.WasCollected) return;
            frames++; sumDt += dtMs;
            int pending = world.PendingToolStrokes;
            sumPending += pending; sumPendingN++; if (pending > maxPending) maxPending = pending;

            // Frame-time target: MinFps, or the game's own (lower) frame cap, e.g. 30 fps in the background, where most
            // of each frame is idle waiting.
            // slowAt: frames slower than this = back off; roomBelow: frames faster than this = room to grow.
            float slowAt = 1000f / Math.Max(10f, Plugin.MinFps.Value), roomBelow = slowAt * 0.92f;
            int cap = Application.targetFrameRate;
            bool capped = cap > 0 && cap <= 60;
            Hooks.CappedNow = capped;
            if (capped) cappedFrames++;
            if (cap > 0 && cap < Plugin.MinFps.Value) { float f = 1000f / cap; slowAt = f * 1.06f; roomBelow = f * 1.03f; }   // capped frames sit at the cap while there is idle time

            bool need = pending >= Plugin.MinPending.Value;
            ControlBudget(need, slowAt, roomBelow, capped);
            if (!need || !Plugin.ExtraPasses.Value || stopped) { budget = Math.Max(0f, budget - 0.2f); sumBudget += budget; return; }
            // AIMD: grow slowly while under the target, back off quickly when over it.
            if (dtAvg < roomBelow) budget = Math.Min(Plugin.MaxExtraMs.Value, budget + 0.05f);
            else if (dtAvg > slowAt) budget = Math.Max(0f, budget * 0.75f - 0.05f);
            sumBudget += budget;
            if (budget < 0.25f) return;

            sw.Restart();
            int n = 0, startPending = pending;
            while (n < Plugin.MaxPassesPerFrame.Value)
            {
                float left = budget - (float)sw.Elapsed.TotalMilliseconds;
                if (left < 0.1f || pending <= 0) break;
                double t0 = sw.Elapsed.TotalMilliseconds;
                Hooks.Ours = true;
                try { world.ProcessToolStrokes(false, left); }
                finally { Hooks.Ours = false; }
                double one = sw.Elapsed.TotalMilliseconds - t0;
                n++;
                int p2 = world.PendingToolStrokes;
                if (one > budget * 3f + 12f)
                {
                    hitches++;
                    Plugin.L.LogWarning($"One extra pass took {one:0.0} ms (budget {budget:0.0} ms); halving the budget.");
                    budget *= 0.5f;
                    if (hitches >= 3) { stopped = true; Plugin.L.LogWarning("Extra passes keep overrunning; turned off for this session (the queue is still watched)."); }
                    break;
                }
                if (p2 >= pending) { pending = p2; break; }   // nothing more it can do this frame
                pending = p2;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (n > 0) { helpedFrames++; passes += n; extraProcessed += Math.Max(0, startPending - pending); extraMs += ms; if (ms > extraMaxMs) extraMaxMs = ms; }
        }
        catch (Exception e) { Hooks.Ours = false; Plugin.L.LogError("Cleaning Catch-Up: " + e.Message); stopped = true; }
    }

    // Find the settings object the world uses and remember the game's own budget (restored when the queue is empty).
    void BindSettings()
    {
        try
        {
            if (world == null || world.WasCollected) return;
            var st = world.settings;
            if (st == null) return;
            if (st.Pointer != settingsPtr)
            {
                Restore();
                settings = st; settingsPtr = st.Pointer;
                stock = st.cleaningWorkBudgetMs; gameBudget = stock;
                if (!(stock > 0f && stock < 50f)) { Plugin.L.LogWarning($"Unexpected cleaning budget {stock} ms; not changing it."); settings = null; return; }
                if (Plugin.RaiseBudget.Value || Plugin.BackgroundBoost.Value)
                    Plugin.L.LogInfo($"Game cleaning budget is {stock} ms per frame; raised up to {(Plugin.BackgroundBoost.Value ? $"{Plugin.BackgroundBudgetMs.Value} ms while the frame rate is capped" : "")}{(Plugin.BackgroundBoost.Value && Plugin.RaiseBudget.Value ? " and " : "")}{(Plugin.RaiseBudget.Value ? $"{Plugin.MaxBudgetMs.Value} ms while uncapped" : "")}, only while strokes are waiting.");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Settings lookup: " + e.Message); settings = null; }
    }

    void ControlBudget(bool need, float slowAt, float roomBelow, bool capped)
    {
        if (settings == null || stock <= 0f) return;
        bool boost = capped && Plugin.BackgroundBoost.Value;
        bool allowed = boost || Plugin.RaiseBudget.Value;
        float b = gameBudget;
        if (!allowed) b = stock;                                   // uncapped (focused) and no raising there: the game's own value
        else
        {
            float max = Math.Max(stock, boost ? Plugin.BackgroundBudgetMs.Value : Plugin.MaxBudgetMs.Value);
            if (!need) b = Math.Max(stock, b - (boost ? 0.3f : 0.1f));                         // queue empty: back to the game's value
            else if (dtAvg > slowAt) b = Math.Max(stock, stock + (b - stock) * 0.7f - 0.05f);   // frames too slow: back off fast
            else if (dtAvg < roomBelow) b = Math.Min(max, b + (boost ? 0.15f : 0.02f));         // room: grow (fast while capped)
            b = Math.Min(b, max);
        }
        if (Math.Abs(b - gameBudget) > 0.001f)
        {
            gameBudget = b;
            try { settings.cleaningWorkBudgetMs = b; } catch (Exception e) { Plugin.L.LogWarning("Could not set the cleaning budget: " + e.Message); settings = null; return; }
            if (boost && b > stock + 0.5f && !boostLogged) { boostLogged = true; Plugin.L.LogInfo($"Background boost: frame rate capped at {Application.targetFrameRate} fps with {world.PendingToolStrokes} strokes waiting; cleaning budget raised above the game's {stock} ms."); }
        }
        sumGameBudget += gameBudget; if (gameBudget > stock + 0.01f) raisedFrames++; if (gameBudget > maxGameBudget) maxGameBudget = gameBudget;
        if (boost && need) { boostFrames++; sumBoostBudget += gameBudget; }
    }

    void Restore()
    {
        try { if (settings != null && !settings.WasCollected && stock > 0f) settings.cleaningWorkBudgetMs = stock; } catch { }
        gameBudget = stock;
    }

    void OnDestroy() => Restore();

    static string Kind(Type t, string name)
    {
        try
        {
            var f = t.GetField("NativeFieldInfoPtr_" + name, BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) return "?";
            int fl = IL2CPP.il2cpp_field_get_flags((IntPtr)f.GetValue(null));
            return (fl & 0x40) != 0 ? "const" : (fl & 0x20) != 0 ? "readonly" : "static";
        }
        catch { return "?"; }
    }

    void ReportSetup()
    {
        string s = "";
        void Add(string label, Func<object> get, Type t, string field)
        {
            try { s += $"{label}={get()} ({Kind(t, field)}); "; } catch (Exception e) { s += $"{label}=error {e.Message}; "; }
        }
        Add("MaxStrokesAFrame", () => MoonWorld.MaxStrokesAFrame, typeof(MoonWorld), "MaxStrokesAFrame");
        Add("MaxQueuedStrokes", () => MoonWorld.MaxQueuedStrokes, typeof(MoonWorld), "MaxQueuedStrokes");
        Add("MaxRobotStrokesWaiting", () => MoonWorld.MaxRobotStrokesWaiting, typeof(MoonWorld), "MaxRobotStrokesWaiting");
        Add("RobotCleaningShare", () => MoonWorld.RobotCleaningShare, typeof(MoonWorld), "RobotCleaningShare");
        Add("SaveRobotStrokesMs", () => MoonWorld.SaveRobotStrokesMs, typeof(MoonWorld), "SaveRobotStrokesMs");
        Add("RobotJoinsStraightOnly", () => MoonWorld.RobotJoinsStraightOnly, typeof(MoonWorld), "RobotJoinsStraightOnly");
        Add("StrokesBehindSeconds", () => MoonRobotVacuum.StrokesBehindSeconds, typeof(MoonRobotVacuum), "StrokesBehindSeconds");
        Add("StrokesHoldSeconds", () => MoonRobotVacuum.StrokesHoldSeconds, typeof(MoonRobotVacuum), "StrokesHoldSeconds");
        Add("HoldForStrokesBehind", () => MoonRobotVacuum.HoldForStrokesBehind, typeof(MoonRobotVacuum), "HoldForStrokesBehind");
        Add("StrokeInterval", () => MoonRobotVacuum.StrokeInterval, typeof(MoonRobotVacuum), "StrokeInterval");
        Add("LaneSpacing", () => MoonRobotVacuum.LaneSpacing, typeof(MoonRobotVacuum), "LaneSpacing");
        Add("StuckLimit", () => MoonRobotVacuum.StuckLimit, typeof(MoonRobotVacuum), "StuckLimit");
        Add("HexIdleSeconds", () => MoonRobotVacuum.HexIdleSeconds, typeof(MoonRobotVacuum), "HexIdleSeconds");
        Add("DustMap.DirtyThreshold", () => MoonRobotDustMap.DirtyThreshold, typeof(MoonRobotDustMap), "DirtyThreshold");
        Add("DustMap.PassesBeforeIgnoring", () => MoonRobotDustMap.PassesBeforeIgnoring, typeof(MoonRobotDustMap), "PassesBeforeIgnoring");
        Add("DustMap.IgnoreSeconds", () => MoonRobotDustMap.IgnoreSeconds, typeof(MoonRobotDustMap), "IgnoreSeconds");
        try { var st = world.settings; if (st != null) s += $"settings.cleaningWorkBudgetMs={st.cleaningWorkBudgetMs}; settings.dustVisualBudgetMs={st.dustVisualBudgetMs}; "; } catch (Exception e) { s += "settings: " + e.Message; }
        Plugin.L.LogInfo("Game cleaning setup: " + s);
    }

    void Report()
    {
        try
        {
            int calls = Math.Max(1, Hooks.GameCalls), f = Math.Max(1, frames);
            string times = ""; try { if (world != null) { var v = world.LastCleaningTimesMs; times = $" Game cleaning times last frame (ms): {v.x:0.00}/{v.y:0.00}/{v.z:0.00}/{v.w:0.00}, queued {world.LastQueuedCleaningMs:0.00}."; } } catch { }
            string metres = "";
            try
            {
                if (world != null && !world.WasCollected)
                {
                    double c = world.RobotStrokeMetresCleaned, j = world.RobotStrokeMetresJumped;
                    if (lastMetresCleaned >= 0) metres = $" Robot stroke metres: cleaned {c - lastMetresCleaned:0}, jumped {j - lastMetresJumped:0}.";
                    lastMetresCleaned = c; lastMetresJumped = j;
                }
            }
            catch { }
            string cappedPart = cappedFrames > 0
                ? $" Capped frame rate on {100.0 * cappedFrames / f:0}% of frames: {Hooks.CappedProcessed} strokes in {Hooks.CappedCalls} passes ({(double)Hooks.CappedProcessed / Math.Max(1, Hooks.CappedCalls):0.0} per pass, {Hooks.CappedMs / Math.Max(1, Hooks.CappedCalls):0.00} ms per pass); boost on {boostFrames} frames, average budget then {sumBoostBudget / Math.Max(1, boostFrames):0.0} ms."
                : "";
            string budgetPart = Plugin.RaiseBudget.Value ? $" Budget: {sumGameBudget / f:0.00} ms average (game's own {stock}), raised on {100.0 * raisedFrames / f:0}% of frames, highest {maxGameBudget:0.0} ms." : "";
            string extraPart = Plugin.ExtraPasses.Value ? $" Extra: {helpedFrames} frames helped ({100.0 * helpedFrames / f:0}%), {passes} passes, {extraProcessed} strokes, {extraMs / Math.Max(1, helpedFrames):0.00} ms per helped frame (max {extraMaxMs:0.0}), average budget {sumBudget / f:0.00} ms." : "";
            Plugin.L.LogInfo($"Last minute: {frames} frames at {1000.0 * frames / Math.Max(1, sumDt):0} fps; queue average {sumPending / Math.Max(1, sumPendingN):0}, max {maxPending}. " +
                $"Game: {Hooks.GameCalls} passes ({Hooks.GameCallsComplete} complete, {Hooks.OffThread} off-thread), {Hooks.GameProcessed} strokes, {(double)Hooks.GameProcessed / calls:0.0} per pass (max {Hooks.GameMaxProcessed}), {Hooks.GameMs / calls:0.00} ms per pass (max {Hooks.GameMaxMs:0.0}), robotMs {Hooks.LastRobotMs}." +
                cappedPart + budgetPart + extraPart + times + metres);
        }
        catch (Exception e) { Plugin.L.LogWarning("Report: " + e.Message); }
        try { ParentStrokes.Report(); } catch (Exception e) { Plugin.L.LogWarning("Stroke report: " + e.Message); }
        try { StrokeCost.Report(); } catch (Exception e) { Plugin.L.LogWarning("Stroke cost report: " + e.Message); }
        Hooks.GameCalls = Hooks.GameCallsComplete = Hooks.GameProcessed = Hooks.GameMaxProcessed = Hooks.OffThread = 0; Hooks.GameMs = Hooks.GameMaxMs = 0;
        Hooks.CappedCalls = Hooks.CappedProcessed = 0; Hooks.CappedMs = 0;
        sumGameBudget = sumBoostBudget = 0; raisedFrames = cappedFrames = boostFrames = 0; maxGameBudget = 0;
        frames = helpedFrames = passes = extraProcessed = maxPending = sumPendingN = 0; extraMs = extraMaxMs = sumPending = sumBudget = sumDt = 0;
    }
}
