using System.Reflection;
using System.Text;

// ReSharper disable once CheckNamespace
public static class MarseyLogger
{
    private enum LogType
    {
        INFO,
        WARN,
        FATL,
        DEBG
    }

    public delegate void Forward(AssemblyName asm, string message);

    public static Forward? logDelegate;

    private static void LogFatal(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LogType.FATL}] {message}";
        WriteFileLog(line);
    }

    private static void WriteFileLog(string line)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "FurryAudioReconnect.log");
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Best-effort diagnostic log.
        }
    }

    public static void Info(string message)
    {
    }

    public static void Warn(string message)
    {
    }

    public static void Fatal(string message) => LogFatal(message);

    public static void Debug(string message)
    {
    }
}
