using System;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepers.DevMenuUnlock;

// The release build hides the settings screen's Dev tab (time of day, time speed, money, dust...).
// The pause menu still has the code that fills it (MoonPauseMenu.BuildDev); we hand it back to the screen.
[BepInPlugin("moonsweepers.devmenuunlock", "Dev Menu Unlock", "1.1.1")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Enabled;

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", true, "Show the developer page in the pause menu settings.");
        var h = new Harmony("moonsweepers.devmenuunlock");
        try { h.PatchAll(typeof(Patches)); L.LogInfo("Dev Menu Unlock 1.1.1 loaded (hooks: settings constructor + Build)."); }
        catch (Exception e) { L.LogError("Patching failed: " + e); }
    }

    internal static string ClassName(Il2CppObjectBase o)
    {
        if (o == null) return "null";
        try { return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(IL2CPP.il2cpp_object_get_class(o.Pointer))); }
        catch { return "?"; }
    }

    static bool told;
    internal static void Supplied(string where)
    {
        if (!told) { told = true; L.LogInfo($"Dev page added to the pause menu settings ({where})."); }
        else L.LogDebug($"  -> supplied Dev callback {where}.");
    }

    internal static Il2CppSystem.Action<MoonSettingsScreen> MakeCallback(MoonPauseMenu menu) =>
        (Il2CppSystem.Action<MoonSettingsScreen>)new Action<MoonSettingsScreen>(s =>
        {
            try { menu.BuildDev(s); L.LogDebug("Dev page built."); }
            catch (Exception e) { L.LogError("BuildDev failed: " + e); }
        });
}

[HarmonyPatch]
internal static class Patches
{
    [HarmonyPatch(typeof(MoonSettingsScreen), MethodType.Constructor,
        new[] { typeof(MoonSimpleKit), typeof(MoonSettingsScreen.IHost), typeof(Il2CppSystem.Action<MoonSettingsScreen>) })]
    [HarmonyPrefix]
    private static void CtorPrefix(MoonSettingsScreen.IHost host, ref Il2CppSystem.Action<MoonSettingsScreen> buildDev)
    {
        try
        {
            Plugin.L.LogDebug($"Settings screen created: host={Plugin.ClassName(host)}, buildDev={(buildDev == null ? "null" : "set")}");
            if (!Plugin.Enabled.Value || buildDev != null || host == null) return;
            var menu = host.TryCast<MoonPauseMenu>();
            if (menu != null) { buildDev = Plugin.MakeCallback(menu); Plugin.Supplied("in the settings constructor"); }
        }
        catch (Exception e) { Plugin.L.LogError("Ctor prefix failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonSettingsScreen), nameof(MoonSettingsScreen.Build))]
    [HarmonyPrefix]
    private static void BuildPrefix(MoonSettingsScreen __instance)
    {
        try
        {
            var host = __instance.host;
            Plugin.L.LogDebug($"Settings Build: host={Plugin.ClassName(host)}, buildDev={(__instance.buildDev == null ? "null" : "set")}");
            if (!Plugin.Enabled.Value || __instance.buildDev != null || host == null) return;
            var menu = host.TryCast<MoonPauseMenu>();
            if (menu != null) { __instance.buildDev = Plugin.MakeCallback(menu); Plugin.Supplied("before Build"); }
        }
        catch (Exception e) { Plugin.L.LogError("Build prefix failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonSettingsScreen), nameof(MoonSettingsScreen.Build))]
    [HarmonyPostfix]
    private static void BuildPostfix(MoonSettingsScreen __instance)
    {
        try
        {
            var order = __instance.order; var tabs = "";
            if (order != null) for (int i = 0; i < order.Count; i++) tabs += order.get_Item(i) + " ";
            Plugin.L.LogDebug($"  tabs after Build: {tabs}");
        }
        catch (Exception e) { Plugin.L.LogError("Build postfix failed: " + e); }
    }
}
