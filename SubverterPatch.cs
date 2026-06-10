using HarmonyLib;

// ReSharper disable once CheckNamespace
public static class SubverterPatch
{
    public static string Name = "Furry Audio Reconnect";
    public static string Description = "OpenAL audio reconnect";
    public static Harmony Harm = new("com.furryaudioreconnect.patch");

    static SubverterPatch()
    {
        MarseyLogger.Info("SubverterPatch metadata touched.");
    }
}
