using System.Reflection;

// ReSharper disable once CheckNamespace
public static class MarseyEntry
{
    public static void Entry()
    {
        try
        {
            MarseyLogger.Info("Furry Audio Reconnect entry started.");
            if (!TryGetAssembly("Content.Client"))
            {
                MarseyLogger.Warn("Content.Client was not found before Entry timeout.");
                return;
            }

            SubverterPatch.Harm.PatchAll(Assembly.GetExecutingAssembly());
            MarseyLogger.Info("Harmony PatchAll completed.");
            MarseyLogger.Info("Runtime initialization deferred to GameShared.PostInit.");
        }
        catch (Exception e)
        {
            MarseyLogger.Fatal($"Entry failed: {e}");
        }
    }

    private static bool TryGetAssembly(string assembly)
    {
        for (var loops = 0; loops < 50; loops++)
        {
            if (FindAssembly(assembly) != null)
                return true;

            Thread.Sleep(200);
        }

        return false;
    }

    private static Assembly? FindAssembly(string assemblyName)
    {
        var asmList = AppDomain.CurrentDomain.GetAssemblies();
        return asmList.FirstOrDefault(asm => asm.FullName?.Contains(assemblyName) == true);
    }
}
