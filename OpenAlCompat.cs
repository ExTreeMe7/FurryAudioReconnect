using System.Reflection;

namespace FurryAudioReconnect;

internal static class OpenAlCompat
{
    private const string OpenAlAssemblyName = "OpenTK.Audio.OpenAL";

    private static readonly object Sync = new();
    private static bool _initialized;
    private static bool _available;
    private static string _unavailableReason = "OpenAL compatibility adapter has not initialized.";

    private static Type? _alType;
    private static Type? _alcType;
    private static Type? _alDeviceType;
    private static Type? _alContextType;
    private static Type? _alErrorType;
    private static Type? _alListenerfType;
    private static Type? _alcGetIntegerType;
    private static Type? _alcGetStringType;

    private static object? _alDeviceNull;
    private static object? _alContextNull;
    private static object? _alNoError;
    private static object? _alListenerGain;
    private static object? _alcDefaultAllDevicesSpecifier;
    private static object? _alcDefaultDeviceSpecifier;

    private static MethodInfo? _getCurrentContext;
    private static MethodInfo? _getContextsDevice;
    private static MethodInfo? _getInteger;
    private static MethodInfo? _getString;
    private static MethodInfo? _listener;
    private static MethodInfo? _getListener;
    private static MethodInfo? _getError;
    private static MethodInfo? _genSource;
    private static MethodInfo? _isSource;
    private static MethodInfo? _deleteSource;

    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    public static string UnavailableReason
    {
        get
        {
            EnsureInitialized();
            return _unavailableReason;
        }
    }

    public static object? GetCurrentContext()
    {
        EnsureAvailable();
        return _getCurrentContext!.Invoke(null, null);
    }

    public static object? GetContextsDevice(object? context)
    {
        EnsureAvailable();
        if (context == null)
            return null;

        return _getContextsDevice!.Invoke(null, new[] { context });
    }

    public static bool IsNullContext(object? context)
    {
        EnsureInitialized();
        return IsNullHandle(context, _alContextNull);
    }

    public static bool IsNullDevice(object? device)
    {
        EnsureInitialized();
        return IsNullHandle(device, _alDeviceNull);
    }

    public static int GetInteger(object? device, int parameter)
    {
        EnsureAvailable();
        if (device == null)
            return -1;

        var enumValue = Enum.ToObject(_alcGetIntegerType!, parameter);
        return _getInteger!.Invoke(null, new[] { device, enumValue }) is int value ? value : -1;
    }

    public static string? GetDefaultAllDevicesSpecifier()
    {
        EnsureInitialized();
        return _available && _alcDefaultAllDevicesSpecifier != null
            ? GetString(_alDeviceNull, _alcDefaultAllDevicesSpecifier)
            : null;
    }

    public static string? GetDefaultDeviceSpecifier()
    {
        EnsureInitialized();
        return _available && _alcDefaultDeviceSpecifier != null
            ? GetString(_alDeviceNull, _alcDefaultDeviceSpecifier)
            : null;
    }

    public static void ListenerGain(float gain)
    {
        EnsureAvailable();
        _listener!.Invoke(null, new[] { _alListenerGain!, gain });
    }

    public static float GetListenerGain()
    {
        EnsureAvailable();
        var args = new object?[] { _alListenerGain!, 0f };
        _getListener!.Invoke(null, args);
        return args[1] is float gain ? gain : 1f;
    }

    public static object? GetError()
    {
        EnsureInitialized();
        if (!_available)
            return _alNoError;

        return _getError!.Invoke(null, null);
    }

    public static bool IsNoError(object? error)
    {
        EnsureInitialized();
        return error == null || _alNoError == null || error.Equals(_alNoError);
    }

    public static string FormatError(object? error)
    {
        return error?.ToString() ?? "NoError";
    }

    public static int GenSource()
    {
        EnsureAvailable();
        return _genSource!.Invoke(null, null) is int source ? source : 0;
    }

    public static bool IsSource(int source)
    {
        EnsureAvailable();
        return _isSource!.Invoke(null, new object[] { source }) is true;
    }

    public static void DeleteSource(int source)
    {
        EnsureAvailable();
        _deleteSource!.Invoke(null, new object[] { source });
    }

    private static string? GetString(object? device, object parameter)
    {
        try
        {
            var value = _getString?.Invoke(null, new[] { device!, parameter }) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNullHandle(object? handle, object? nullValue)
    {
        if (handle == null)
            return true;

        return nullValue != null && handle.Equals(nullValue);
    }

    private static void EnsureAvailable()
    {
        EnsureInitialized();
        if (!_available)
            throw new InvalidOperationException(_unavailableReason);
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (Sync)
        {
            if (_initialized)
                return;

            try
            {
                Initialize();
            }
            catch (Exception e)
            {
                _available = false;
                _unavailableReason = $"OpenAL compatibility adapter failed: {e.GetType().Name}: {e.Message}";
            }
            finally
            {
                _initialized = true;
            }
        }
    }

    private static void Initialize()
    {
        var assembly = FindOpenAlAssembly();
        if (assembly == null)
        {
            _unavailableReason = "OpenTK.Audio.OpenAL assembly was not found in the current engine.";
            return;
        }

        _alType = assembly.GetType("OpenTK.Audio.OpenAL.AL");
        _alcType = assembly.GetType("OpenTK.Audio.OpenAL.ALC");
        _alDeviceType = assembly.GetType("OpenTK.Audio.OpenAL.ALDevice");
        _alContextType = assembly.GetType("OpenTK.Audio.OpenAL.ALContext");
        _alErrorType = assembly.GetType("OpenTK.Audio.OpenAL.ALError");
        _alListenerfType = assembly.GetType("OpenTK.Audio.OpenAL.ALListenerf");
        _alcGetIntegerType = assembly.GetType("OpenTK.Audio.OpenAL.AlcGetInteger");
        _alcGetStringType = assembly.GetType("OpenTK.Audio.OpenAL.AlcGetString");

        if (_alType == null || _alcType == null || _alDeviceType == null || _alContextType == null
            || _alErrorType == null || _alListenerfType == null || _alcGetIntegerType == null
            || _alcGetStringType == null)
        {
            _unavailableReason = $"OpenTK.Audio.OpenAL types are incomplete in {assembly.FullName}.";
            return;
        }

        _alDeviceNull = GetStaticField(_alDeviceType, "Null");
        _alContextNull = GetStaticField(_alContextType, "Null");
        _alNoError = Enum.Parse(_alErrorType, "NoError");
        _alListenerGain = Enum.Parse(_alListenerfType, "Gain");
        _alcDefaultAllDevicesSpecifier = TryParseEnum(_alcGetStringType, "DefaultAllDevicesSpecifier");
        _alcDefaultDeviceSpecifier = TryParseEnum(_alcGetStringType, "DefaultDeviceSpecifier");

        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        _getCurrentContext = _alcType.GetMethod("GetCurrentContext", flags, Type.EmptyTypes);
        _getContextsDevice = _alcType.GetMethod("GetContextsDevice", flags, new[] { _alContextType });
        _getInteger = _alcType.GetMethod("GetInteger", flags, new[] { _alDeviceType, _alcGetIntegerType });
        _getString = _alcType.GetMethod("GetString", flags, new[] { _alDeviceType, _alcGetStringType });

        _listener = _alType.GetMethod("Listener", flags, new[] { _alListenerfType, typeof(float) });
        _getListener = _alType.GetMethod("GetListener", flags, new[] { _alListenerfType, typeof(float).MakeByRefType() });
        _getError = _alType.GetMethod("GetError", flags, Type.EmptyTypes);
        _genSource = _alType.GetMethod("GenSource", flags, Type.EmptyTypes);
        _isSource = _alType.GetMethod("IsSource", flags, new[] { typeof(int) });
        _deleteSource = _alType.GetMethod("DeleteSource", flags, new[] { typeof(int) });

        if (_alDeviceNull == null || _alContextNull == null || _alcDefaultDeviceSpecifier == null
            || _getCurrentContext == null || _getContextsDevice == null || _getInteger == null || _getString == null
            || _listener == null || _getListener == null || _getError == null || _genSource == null
            || _isSource == null || _deleteSource == null)
        {
            _unavailableReason = $"OpenTK.Audio.OpenAL API surface is incompatible in {assembly.FullName}.";
            return;
        }

        _available = true;
        _unavailableReason = string.Empty;
    }

    private static Assembly? FindOpenAlAssembly()
    {
        var loaded = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(asm => string.Equals(asm.GetName().Name, OpenAlAssemblyName, StringComparison.Ordinal));
        if (loaded != null)
            return loaded;

        try
        {
            return Assembly.Load(new AssemblyName(OpenAlAssemblyName));
        }
        catch
        {
            return null;
        }
    }

    private static object? GetStaticField(Type type, string name)
    {
        return type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
    }

    private static object? TryParseEnum(Type enumType, string name)
    {
        try
        {
            return Enum.Parse(enumType, name);
        }
        catch
        {
            return null;
        }
    }
}
