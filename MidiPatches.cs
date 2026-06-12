using System.Reflection;
using HarmonyLib;

// ReSharper disable once CheckNamespace
[HarmonyPatch]
public static class MidiRendererRenderDisposedGuardPatch
{
    private static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Robust.Client.Audio.Midi.MidiRenderer");
        if (type == null)
            return null;

        return AccessTools.Method(
            type,
            "Render",
            [typeof(int)]);
    }

    private static Exception? Finalizer(object __instance, Exception? __exception)
    {
        if (__exception == null)
            return null;

        if (!MidiDisposedSourceGuard.IsDisposedBaseAudioSource(__exception))
            return __exception;

        MidiDisposedSourceGuard.MarkRendererDisposed(__instance);
        return null;
    }
}

// ReSharper disable once CheckNamespace
[HarmonyPatch]
public static class MidiManagerThreadDisposedGuardPatch
{
    private static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Robust.Client.Audio.Midi.MidiManager");
        if (type == null)
            return null;

        return AccessTools.Method(
            type,
            "ThreadUpdate");
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        return MidiDisposedSourceGuard.IsDisposedBaseAudioSource(__exception)
            ? null
            : __exception;
    }
}

internal static class MidiDisposedSourceGuard
{
    public static bool IsDisposedBaseAudioSource(Exception? exception)
    {
        while (exception != null)
        {
            if (exception is ObjectDisposedException disposed &&
                string.Equals(disposed.ObjectName, "BaseAudioSource", StringComparison.Ordinal))
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    public static void MarkRendererDisposed(object renderer)
    {
        try
        {
            AccessTools
                .Field(renderer.GetType(), "<Disposed>k__BackingField")
                ?.SetValue(renderer, true);
        }
        catch
        {
            // Best-effort guard. The thread-level finalizer still prevents a crash.
        }
    }
}
