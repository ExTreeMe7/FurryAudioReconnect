using System;

// ReSharper disable once CheckNamespace
/// <summary>
/// Delegate fields for the Marsey compat bridge.
/// The loader binds these at load time via reflection.
/// </summary>
public static class CompatBridge
{
    // Assembly/type/method resolvers
    public static Func<string, System.Reflection.Assembly?>? FindAssembly;
    public static Func<string, string, Type?>? FindType;
    public static Func<string, System.Collections.Generic.IReadOnlyList<string>, Type?>? FindTypeByCandidates;
    public static Func<string, string, string, System.Reflection.MethodInfo?>? FindMethod;
    public static Func<string, string, string, System.Reflection.FieldInfo?>? FindField;
    public static Func<Version?>? GetEngineVersion;
    public static Func<int, int, bool>? IsEngineAtLeast;

    // OpenAL compatibility
    public static Func<bool>? OpenAlIsAvailable;
    public static Func<string>? OpenAlUnavailableReason;
    public static Func<object?>? OpenAlGetCurrentContext;
    public static Func<object?, bool>? OpenAlIsNullContext;
    public static Func<object?, object?>? OpenAlGetContextsDevice;
    public static Func<object?, bool>? OpenAlIsNullDevice;
    public static Action<float>? OpenAlListenerGain;
    public static Func<float>? OpenAlGetListenerGain;
    public static Func<object?>? OpenAlGetError;
    public static Func<object?, bool>? OpenAlIsNoError;
    public static Func<object?, string>? OpenAlFormatError;
    public static Func<int>? OpenAlGenSource;
    public static Func<int, bool>? OpenAlIsSource;
    public static Action<int>? OpenAlDeleteSource;
    public static Func<object?, int, int>? OpenAlGetInteger;
    public static Func<string?>? OpenAlGetDefaultAllDevicesSpecifier;
    public static Func<string?>? OpenAlGetDefaultDeviceSpecifier;
}
