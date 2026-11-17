using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Injection;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.JoonasOverdrive;

// Joonas Overdrive: a purchasable upgrade (Cleaning branch of the upgrade terminal) that makes J.O.O.N.A.S.
// robots drive, clean and turn faster. The owned level is stored by the game itself under the node id
// (read back with MoonWorld.TreeLevel), the same way other modded upgrades persist.
[BepInPlugin("moonsweepers.joonasoverdrive", "Joonas Overdrive", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<string> Bonuses, Prices, Title, Summary, RobotName;
    internal static ConfigEntry<bool> Acceleration;

    public override void Load()
    {
        L = Log;
        Bonuses = Config.Bind("Upgrade", "SpeedBonuses", "25, 50, 100, 150", "Speed bonus for J.O.O.N.A.S. at each level, in percent (one entry per level).");
        Prices = Config.Bind("Upgrade", "Prices", "20000, 40000, 65000, 100000", "Price of each level ($). Missing entries are extrapolated.");
        Title = Config.Bind("Upgrade", "Title", "Joonas Overdrive", "Name of the upgrade in the upgrade terminal.");
        Summary = Config.Bind("Upgrade", "Summary", "J.O.O.N.A.S. drives, cleans and turns faster.", "Description in the upgrade terminal.");
        RobotName = Config.Bind("Advanced", "RobotName", "J.O.O.N.A.S.", "Robot docks with this display name get the bonus.");
        Acceleration = Config.Bind("Advanced", "ScaleAcceleration", true, "Also scale acceleration, so the robot reaches its faster speeds on short passes.");
        ClassInjector.RegisterTypeInIl2Cpp<OverdriveBehaviour>();
        AddComponent<OverdriveBehaviour>();
        L.LogInfo("Joonas Overdrive loaded.");
    }

    internal static List<float> Floats(string s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1f)
            .Where(v => v >= 0f).ToList();
}

public class OverdriveBehaviour : MonoBehaviour
{
    public OverdriveBehaviour(IntPtr ptr) : base(ptr) { }

    public const string NodeId = "mod.joonas.speed";

    struct Stock { public float Clean, Travel, Turn, Accel; }

    bool started, failed;
    float next;
    int level = -1;
    List<float> bonuses = new();
    MoonWorld world;
    readonly Dictionary<IntPtr, Stock> stock = new();   // per robot: values before any bonus, so it never compounds
    Stock sample; bool haveSample;

    void Update()
    {
        if (failed) return;
        try
        {
            if (!started)
            {
                started = true;
                bonuses = Plugin.Floats(Plugin.Bonuses.Value).Where(b => b > 0f).ToList();
                if (bonuses.Count == 0) { Plugin.L.LogWarning("SpeedBonuses is empty; upgrade not added."); failed = true; return; }
                if (!AddNode()) { failed = true; return; }
            }
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            Track();
        }
        catch (Exception e)
        {
            failed = true;
            Plugin.L.LogError("Joonas Overdrive stopped (no effect until next start). If this is a MissingMethodException, delete BepInEx\\interop and restart. " + e);
        }
    }

    float Factor(int lv) => 1f + (lv > 0 ? bonuses[Math.Min(lv, bonuses.Count) - 1] : 0f) / 100f;

    void Track()
    {
        if (world == null || world.WasCollected || !world.isActiveAndEnabled)
        {
            world = UnityEngine.Object.FindFirstObjectByType<MoonWorld>();
            stock.Clear(); level = -1;
            if (world == null) return;
        }
        int lv = Math.Clamp(world.TreeLevel("", NodeId), 0, bonuses.Count);
        bool changed = lv != level;
        level = lv;
        float f = Factor(lv);

        var docks = MoonRobotDock.all;
        if (docks == null) return;
        int applied = 0;
        for (int i = 0; i < docks.Count; i++)
        {
            try
            {
                var d = docks.get_Item(i);
                if (d == null || !d.isActiveAndEnabled || d.displayName != Plugin.RobotName.Value) continue;
                var r = d.Robot;
                if (r == null) continue;
                if (!stock.TryGetValue(r.Pointer, out var s))
                {
                    s = new Stock { Clean = r.cleaningSpeed, Travel = r.travelSpeed, Turn = r.turnRate, Accel = r.acceleration };
                    // A robot object that already carries our bonus (seen before a world reload) falls back to the stock sample.
                    if (haveSample && f > 1f && Math.Abs(s.Clean - sample.Clean * f) < 1e-3f) s = sample;
                    stock[r.Pointer] = s;
                    if (!haveSample) { sample = s; haveSample = true; }
                    changed = true;
                }
                float clean = s.Clean * f, travel = s.Travel * f, turn = s.Turn * f, accel = Plugin.Acceleration.Value ? s.Accel * f : s.Accel;
                if (Math.Abs(r.cleaningSpeed - clean) > 1e-4f) r.cleaningSpeed = clean;
                if (Math.Abs(r.travelSpeed - travel) > 1e-4f) r.travelSpeed = travel;
                if (Math.Abs(r.turnRate - turn) > 1e-4f) r.turnRate = turn;
                if (Math.Abs(r.acceleration - accel) > 1e-4f) r.acceleration = accel;
                applied++;
            }
            catch (Exception e) { Plugin.L.LogWarning("Could not update a robot: " + e.Message); }
        }
        if (changed && applied > 0 && haveSample)
            Plugin.L.LogInfo($"{Plugin.Title.Value} level {lv} (x{f:0.##}) on {applied} J.O.O.N.A.S.: cleaning {sample.Clean * f:0.##} m/s, travel {sample.Travel * f:0.##} m/s, turn {sample.Turn * f:0.#} deg/s, accel {(Plugin.Acceleration.Value ? sample.Accel * f : sample.Accel):0.##}.");
    }

    bool AddNode()
    {
        IL2CPP.il2cpp_runtime_class_init(Il2CppClassPointerStore.GetNativeClassPointer(typeof(MoonUpgradeTree)));
        if (MoonUpgradeTree.Find(NodeId) != null) return true;
        var nodes = MoonUpgradeTree.Nodes;
        if (nodes == null) { Plugin.L.LogError("Upgrade tree not available; upgrade not added."); return false; }

        var branch = MoonUpgradeTree.Branch.Cleaning;
        var inBranch = nodes.Where(n => n != null && n.branch == branch).ToList();
        if (inBranch.Count == 0) { branch = MoonUpgradeTree.Branch.Economy; inBranch = nodes.Where(n => n != null && n.branch == branch).ToList(); }
        if (inBranch.Count == 0) { Plugin.L.LogError("No Cleaning or Economy branch in the upgrade tree (game update?); upgrade not added."); return false; }
        Plugin.L.LogInfo($"{branch} upgrades: " + string.Join(", ", inBranch.Select(n => $"{n.id} '{MoonLoc.T(n.title)}' x{n.maxLevel} [{(n.prices == null ? "" : string.Join(", ", n.prices))}]")));

        // Unlock like the cheapest entry-level node of the branch, and sit just outside it on the tree.
        var anchor = inBranch.OrderBy(n => n.requires?.Length ?? 0).ThenBy(n => n.prices != null && n.prices.Length > 0 ? n.prices[0] : 0).First();
        var cfg = Plugin.Floats(Plugin.Prices.Value);
        var prices = new Il2CppStructArray<int>(bonuses.Count);
        for (int i = 0; i < bonuses.Count; i++)
        {
            float p = i < cfg.Count ? cfg[i] : (cfg.Count > 0 ? cfg[cfg.Count - 1] * (1f + 0.6f * (i - cfg.Count + 1)) : 800f * (1f + 1.5f * i));
            prices[i] = Math.Max(10, (int)Math.Round(p / 50.0) * 50);
        }
        var node = new MoonUpgradeTree.Node
        {
            id = NodeId,
            title = Plugin.Title.Value,
            summary = Plugin.Summary.Value,
            branch = branch,
            glyph = MoonUpgradeTree.Glyph.Turbo,
            maxLevel = bonuses.Count,
            prices = prices,
            requires = anchor.requires,
            requiresAny = anchor.requiresAny,
            tool = null,
            position = anchor.position * 1.2f,
            capstone = false,
        };
        node.effect = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<int, string>>(new Func<int, string>(Effect));
        var grown = new Il2CppReferenceArray<MoonUpgradeTree.Node>(nodes.Length + 1);
        for (int i = 0; i < nodes.Length; i++) grown[i] = nodes[i];
        grown[nodes.Length] = node;
        MoonUpgradeTree.Nodes = grown;
        MoonUpgradeTree.byId.Add(NodeId, node);
        Plugin.L.LogInfo($"Added '{node.title}' to {branch}: {bonuses.Count} levels, prices [{string.Join(", ", prices)}], unlocked like {anchor.id}.");
        return true;
    }

    string Effect(int lv)
    {
        lv = Math.Clamp(lv, 0, bonuses.Count);
        if (lv == 0) return "Stock J.O.O.N.A.S. speed";
        string t = "+" + bonuses[lv - 1].ToString("0.#", CultureInfo.InvariantCulture) + "% J.O.O.N.A.S. speed";
        if (haveSample)
            t += $" (cleaning {(sample.Clean * Factor(lv)).ToString("0.#", CultureInfo.InvariantCulture)} m/s, travel {(sample.Travel * Factor(lv)).ToString("0.#", CultureInfo.InvariantCulture)} m/s)";
        return t;
    }
}
