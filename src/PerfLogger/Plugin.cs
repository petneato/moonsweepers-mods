using System;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.PerfLogger;

// Read-only diagnostic: once a second writes frame timing, frame caps/focus/menu state and world load to
// BepInEx\PerfLog.csv. No game code is hooked or changed. Stops by itself after RunMinutes.
[BepInPlugin("moonsweepers.perflogger", "Perf Logger", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<float> RunMinutes;

    public override void Load()
    {
        L = Log;
        var enabled = Config.Bind("General", "Enabled", true, "Record performance data to BepInEx/PerfLog.csv.");
        RunMinutes = Config.Bind("General", "RunMinutes", 240f, "Stop recording after this many minutes.");
        if (!enabled.Value) { L.LogInfo("Perf Logger disabled in config."); return; }
        ClassInjector.RegisterTypeInIl2Cpp<PerfLoggerBehaviour>();
        AddComponent<PerfLoggerBehaviour>();
        L.LogInfo("Perf Logger loaded (read-only).");
    }
}

public class PerfLoggerBehaviour : MonoBehaviour
{
    public PerfLoggerBehaviour(IntPtr ptr) : base(ptr) { }

    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    StreamWriter w;
    bool done;
    float started = -1, windowStart = -1, lastFlush, nextLookup, nextSettings;
    int frames, over33, over50, rows;
    double sumMs, worstMs;
    int gc0, gc1, gc2;
    string lastSettings;
    MoonWorld world;
    MoonPauseMenu menu;

    void Update()
    {
        if (done) return;
        try
        {
            float now = Time.realtimeSinceStartup;
            double ms = Time.unscaledDeltaTime * 1000.0;
            if (windowStart < 0) { windowStart = now; Open(now); return; }
            frames++; sumMs += ms; if (ms > worstMs) worstMs = ms;
            if (ms > 33.4) over33++;
            if (ms > 50) over50++;
            if (now - windowStart < 1f) return;

            float t = now - started;
            if (t > Plugin.RunMinutes.Value * 60f) { Finish("time limit reached"); return; }
            if (now >= nextLookup) { nextLookup = now + 5f; Lookup(); }
            if (now >= nextSettings) { nextSettings = now + 10f; Settings(t); }
            Row(t, now - windowStart);
            frames = 0; sumMs = 0; worstMs = 0; over33 = over50 = 0; windowStart = now;
            if (now - lastFlush > 5f) { lastFlush = now; w.Flush(); }
        }
        catch (Exception e) { Plugin.L.LogError("Perf Logger: " + e.Message); }
    }

    void Open(float now)
    {
        var path = Path.Combine(Paths.BepInExRootPath, "PerfLog.csv");
        w = new StreamWriter(path, false);
        w.WriteLine("t,kind,fps,avgMs,worstMs,over33,over50,targetFps,vSync,focused,runInBackground,timeScale,screenW,screenH,menuOpen,menuPage,capsules,ridersOnBelts,conveyors,depots,robots,pendingStrokes,strokeCount,gc0,gc1,gc2,managedMB,unityAllocMB,monoUsedMB");
        started = now; lastFlush = now;
        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
        Plugin.L.LogInfo("Perf Logger recording to " + path);
    }

    void Lookup()
    {
        try { if (world == null || world.WasCollected || !world.isActiveAndEnabled) world = UnityEngine.Object.FindFirstObjectByType<MoonWorld>(); } catch { world = null; }
        try { if (menu == null || menu.WasCollected) menu = UnityEngine.Object.FindFirstObjectByType<MoonPauseMenu>(); } catch { menu = null; }
    }

    void Row(float t, float window)
    {
        int fps = (int)Math.Round(frames / Math.Max(0.001f, window));
        string target = "", vsync = "", focused = "", bg = "", ts = "", sw = "", sh = "";
        try { target = Application.targetFrameRate.ToString(C); } catch { }
        try { vsync = QualitySettings.vSyncCount.ToString(C); } catch { }
        try { focused = Application.isFocused ? "1" : "0"; } catch { }
        try { bg = Application.runInBackground ? "1" : "0"; } catch { }
        try { ts = Time.timeScale.ToString("0.##", C); } catch { }
        try { sw = Screen.width.ToString(C); sh = Screen.height.ToString(C); } catch { }

        string menuOpen = "", menuPage = "";
        try { if (menu != null) { menuOpen = menu.IsOpen ? "1" : "0"; menuPage = menu.CurrentPage.ToString(); } } catch { }

        string caps = "", riders = "", conv = "", dep = "", robots = "", pend = "", strokes = "";
        try
        {
            if (world != null)
            {
                try { caps = world.canisters?.Count.ToString(C) ?? ""; } catch { }
                try { riders = world.riders?.all?.Count.ToString(C) ?? ""; } catch { }
                try { conv = world.conveyors?.Count.ToString(C) ?? ""; } catch { }
                try { dep = world.depots?.Count.ToString(C) ?? ""; } catch { }
                try { pend = world.PendingToolStrokes.ToString(C); } catch { }
                try { strokes = world.strokeCount.ToString(C); } catch { }
            }
            try { robots = MoonRobotDock.all?.Count.ToString(C) ?? ""; } catch { }
        }
        catch { }

        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        int d0 = g0 - gc0, d1 = g1 - gc1, d2 = g2 - gc2; gc0 = g0; gc1 = g1; gc2 = g2;
        string managed = (GC.GetTotalMemory(false) / 1048576.0).ToString("0.#", C), alloc = "", mono = "";
        try { alloc = (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0).ToString("0", C); } catch { }
        try { mono = (UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576.0).ToString("0", C); } catch { }

        w.WriteLine(string.Join(",", F(t), "perf", fps, F(sumMs / Math.Max(1, frames)), F(worstMs), over33, over50,
            target, vsync, focused, bg, ts, sw, sh, menuOpen, menuPage, caps, riders, conv, dep, robots, pend, strokes,
            d0, d1, d2, managed, alloc, mono));
        rows++;
    }

    // Game and Unity graphics settings; written at start and again whenever they change (checked every 10 s).
    void Settings(float t)
    {
        string s = "";
        try { s += $"quality={QualitySettings.names[QualitySettings.GetQualityLevel()]} "; } catch { }
        try { s += $"shadows={QualitySettings.shadows} shadowDistance={QualitySettings.shadowDistance:0} "; } catch { }
        try
        {
            var u = MoonUserSettings.Current;
            if (u != null)
                s += $"frameLimit={u.frameLimit} vSync={u.vSync} backgroundLimit={u.backgroundLimit} renderScale={u.renderScale:0.##} upscaler={u.upscaler} " +
                     $"frameGeneration={u.frameGeneration} graphicsPreset={u.graphicsPreset} shadowQuality={u.shadowQuality} antiAliasing={u.antiAliasing} " +
                     $"terrainDetail={u.terrainDetail} objectDetail={u.objectDetail} windowMode={u.windowMode} showFps={u.showFps}";
        }
        catch (Exception e) { s += "userSettings-error=" + e.GetType().Name; }
        if (s == lastSettings) return;
        lastSettings = s;
        w.WriteLine($"{F(t)},settings,\"{s.Trim().Replace("\"", "'")}\"");
    }

    void Finish(string why)
    {
        done = true;
        try { w?.WriteLine($",stop,\"{why}\""); w?.Flush(); w?.Close(); } catch { }
        Plugin.L.LogInfo($"Perf Logger stopped: {why} ({rows} rows).");
    }

    void OnApplicationQuit() { if (!done) Finish("game closed"); }

    static string F(double v) => v.ToString("0.##", C);
}
