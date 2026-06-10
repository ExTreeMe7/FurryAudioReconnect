using HarmonyLib;
using System.Reflection;
using FurryAudioReconnect;

// ReSharper disable once CheckNamespace
[HarmonyPatch]
public static class AudioManagerInitializePatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            AccessTools.TypeByName("Robust.Client.Audio.AudioManager"),
            "InitializePostWindowing");
    }

    private static void Postfix(object __instance)
    {
        AudioReconnectController.CaptureAudioManager(__instance);
    }
}
