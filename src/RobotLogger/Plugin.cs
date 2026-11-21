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

namespace MoonSweepers.RobotLogger;

// Read-only diagnostic: once a second, records what every cleaning robot (J.O.O.N.A.S., Mk I, Mk II) is doing.
// No game code is hooked or changed. Output: BepInEx\RobotLog.csv. Stops by itself after RunMinutes.
[BepInPlugin("moonsweepers.robotlogger", "Robot Logger", "0.2.1")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<float> RunMinutes, Interval;

    public override void Load()
    {
        L = Log;
        var enabled = Config.Bind("General", "Enabled", true, "Record robot activity to BepInEx/RobotLog.csv.");
        RunMinutes = Config.Bind("General", "RunMinutes", 30f, "Stop recording after this many minutes in a world.");
        Interval = Config.Bind("General", "IntervalSeconds", 1f, "Seconds between samples.");
        if (!enabled.Value) { L.LogInfo("Robot Logger disabled in config."); return; }
        ClassInjector.RegisterTypeInIl2Cpp<RobotLoggerBehaviour>();
        AddComponent<RobotLoggerBehaviour>();
        L.LogInfo("Robot Logger loaded (read-only).");
    }
}

public class RobotLoggerBehaviour : MonoBehaviour
{
    public RobotLoggerBehaviour(IntPtr ptr) : base(ptr) { }

    StreamWriter w;
    float next, started = -1, lastFlush;
    bool done, paramsWritten;
    int rows;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    float fps = 60f;

    void Update()
    {
        if (done) return;
        float udt = Time.unscaledDeltaTime;
        if (udt > 0) fps = fps * 0.95f + (1f / udt) * 0.05f;
        float now = Time.unscaledTime;
        if (now < next) return;
        next = now + Math.Max(0.2f, Plugin.Interval.Value);
        try
        {
            var docks = MoonRobotDock.all;
            if (docks == null || docks.Count == 0) return;
            if (w == null) Open(now);
            if (now - started > Plugin.RunMinutes.Value * 60f) { Finish("time limit reached"); return; }
            float t = now - started;
            MoonRobotVacuum first = null;
            for (int i = 0; i < docks.Count && first == null; i++) { try { first = docks.get_Item(i)?.Robot; } catch { } }
            World(t, first);
            for (int i = 0; i < docks.Count; i++)
            {
                MoonRobotDock d = null;
                try
                {
                    d = docks.get_Item(i);
                    if (d == null || !d.isActiveAndEnabled) continue;
                    var r = d.Robot;
                    if (r == null) { Row(t, d, null); continue; }
                    if (!paramsWritten) Params(t, d, r);
                    Row(t, d, r);
                }
                catch (Exception e) { w.WriteLine($"{F(t)},error,{(d != null ? d.DockId : -1)},{Q(e.GetType().Name + ": " + e.Message)}"); }
            }
            paramsWritten = true;
            if (now - lastFlush > 5f) { lastFlush = now; w.Flush(); }
        }
        catch (Exception e) { Plugin.L.LogError("Robot Logger: " + e.Message); }
    }

    void Open(float now)
    {
        var path = Path.Combine(Paths.BepInExRootPath, "RobotLog.csv");
        w = new StreamWriter(path, false);
        w.WriteLine("t,kind,dock,name,state,stateTime,status,x,y,z,speed,carriedKg,capacityKg,dockStoredKg,dockFull,hexesDone,leftInDoneHexes,recoveries,cell,mapCell,round,laneIndex,rows,lanes,hexDirtyStart,hexDirtyNow,finishing,stuckInHex,waitingAs,waitReason,dockStatus,beltStatus,strokesWaitingS,strokesBehind");
        started = now;
        Plugin.L.LogInfo("Robot Logger recording to " + path);
    }

    void Params(float t, MoonRobotDock d, MoonRobotVacuum r)
    {
        w.WriteLine($"{F(t)},params,{d.DockId},{Q(d.displayName)},rings={d.rings} roams={d.Roams} cleaningSpeed={F(r.cleaningSpeed)} travelSpeed={F(r.travelSpeed)} cleaningWidth={F(r.cleaningWidth)} laneOverrun={F(r.laneOverrun)} laneEdgeRun={F(r.laneEdgeRun)} capacityKg={F(r.capacityKilograms)} turnRate={F(r.turnRate)}");
        try
        {
            w.WriteLine($"{F(t)},static,,,MaxRobotStrokesWaiting={MoonWorld.MaxRobotStrokesWaiting} MaxStrokesAFrame={MoonWorld.MaxStrokesAFrame} MaxQueuedStrokes={MoonWorld.MaxQueuedStrokes}");
            w.WriteLine($"{F(t)},static,,,LaneSpacing={F(MoonRobotVacuum.LaneSpacing)} MergeGap={F(MoonRobotVacuum.MergeGap)} MaxRounds={MoonRobotVacuum.MaxRounds} RevisitSeconds={F(MoonRobotVacuum.RevisitSeconds)} HexIdleSeconds={F(MoonRobotVacuum.HexIdleSeconds)} StuckLimit={MoonRobotVacuum.StuckLimit} StrokeInterval={F(MoonRobotVacuum.StrokeInterval)}");
        }
        catch (Exception e) { w.WriteLine($"{F(t)},static-error,,,{Q(e.Message)}"); }
    }

    void Row(float t, MoonRobotDock d, MoonRobotVacuum r)
    {
        if (r == null)
        {
            w.WriteLine($"{F(t)},robot,{d.DockId},{Q(d.displayName)},none,,,,,,,,,{F(d.StoredKilograms)},{d.Full},,,,,,,,,,,,,,,,{Q(d.Status)},{Q(d.BeltStatus)}");
            rows++; return;
        }
        var p = r.transform.position;
        int lanes = 0; try { lanes = r.lanes != null ? r.lanes.Count : 0; } catch { }
        w.WriteLine(string.Join(",",
            F(t), "robot", d.DockId, Q(d.displayName), r.state, F(r.stateTime), Q(r.StatusText),
            F(p.x), F(p.y), F(p.z), F(r.speed), F(r.DustKilograms), F(r.capacityKilograms), F(d.StoredKilograms), d.Full,
            r.HexesDone, r.LeftInDoneHexes, r.Recoveries, r.cell, r.mapCell, r.round, r.laneIndex, r.rows, lanes,
            r.hexDirtyStart, r.hexDirtyNow, r.finishing, r.stuckInHex, r.waitingAs, Q(r.waitReason), Q(d.Status), Q(d.BeltStatus),
            F(Waiting(r)), Behind(r)));
        rows++;
    }

    // Cleaning backlog: robots pause while too many of their strokes wait to be processed (read-only calls).
    static float Waiting(MoonRobotVacuum r) { try { var w = r.world; return w != null && r.storageId != null ? w.RobotStrokesWaitingSeconds(r.storageId) : -1f; } catch { return -1f; } }
    static string Behind(MoonRobotVacuum r) { try { return r.StrokesBehind() ? "True" : "False"; } catch { return ""; } }

    void World(float t, MoonRobotVacuum anyRobot)
    {
        try
        {
            var mw = anyRobot?.world; if (mw == null) return;
            w.WriteLine($"{F(t)},world,,,fps={F(fps)} pendingToolStrokes={mw.PendingToolStrokes} strokeCount={mw.strokeCount}");
        }
        catch (Exception e) { w.WriteLine($"{F(t)},world-error,,,{Q(e.Message)}"); }
    }

    void Finish(string why)
    {
        done = true;
        try { w?.WriteLine($",stop,,{Q(why)}"); w?.Flush(); w?.Close(); } catch { }
        Plugin.L.LogInfo($"Robot Logger stopped: {why} ({rows} rows).");
    }

    void OnApplicationQuit() { if (!done) Finish("game closed"); }

    static string F(float v) => v.ToString("0.###", C);
    static string F(double v) => v.ToString("0.###", C);
    static string Q(string s) => s == null ? "" : "\"" + s.Replace("\"", "'").Replace("\n", " ") + "\"";
}
