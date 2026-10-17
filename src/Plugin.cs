using System;
using System.Security;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;

// private 멤버 접근 시 MethodAccessException 방지 (필수 관례)
#pragma warning disable CS0618
[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace RoomPreparerFix;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    public const string PLUGIN_GUID = "levick.roompreparerfix";
    public const string PLUGIN_NAME = "Room Loader Thread Fix";
    public const string PLUGIN_VERSION = "1.0.0";

    internal static new ManualLogSource Logger;
    private static bool isInit;

    private void OnEnable()
    {
        Logger = base.Logger;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        if (isInit) PreparerFix.Remove();
        isInit = false;
    }

    private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        if (isInit) return;
        try
        {
            PreparerFix.Apply();
            isInit = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private void Update()
    {
        if (!isInit) return;
        try
        {
            PreparerFix.Tick();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }
}
