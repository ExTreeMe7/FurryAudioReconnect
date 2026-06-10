using Content.Shared.CCVar;
using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Audio.OpenAL;
using Robust.Client.Audio;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared;
using Robust.Shared.Asynchronous;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Effects;
using Robust.Shared.Audio.Sources;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using ThreadingTimer = System.Threading.Timer;

namespace FurryAudioReconnect;

public static class AudioReconnectController
{
    private const float PlaybackEndSafetySeconds = 0.01f;
    private const float UiClickGain = 0.25f;
    private const float UiHoverGain = 0.05f;
    private const int ConsecutiveProbeErrorsBeforeReconnect = 1;

    // ALC_EXT_disconnect: ALC_CONNECTED.
    private const int AlcConnected = 0x313;

    private static readonly object Gate = new();
    private static readonly TimeSpan WatchdogInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(2);

    private static object? _audioManager;
    private static ITaskManager? _tasks;
    private static IResourceCache? _resourceCache;
    private static IEntityManager? _entityManager;
    private static IGameTiming? _timing;
    private static IConfigurationManager? _configManager;
    private static IUserInterfaceManager? _uiManager;
    private static ThreadingTimer? _watchdog;

    private static bool _watchdogEnabled = true;
    private static bool _reconnectQueued;
    private static bool _reconnectRunning;
    private static int _consecutiveProbeErrors;
    private static DateTime _lastTickUtc = DateTime.MinValue;
    private static DateTime _lastReconnectUtc = DateTime.MinValue;
    private static string? _lastDefaultDeviceSpecifier;
    private static string? _lastWindowsDefaultRenderEndpointId;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            LogStatus("Initialize skipped, already initialized");
            return;
        }

        _initialized = true;
        MarseyLogger.Info("Initialize called.");
        ResolveServices();
        TryCaptureAudioManager();
        CaptureDefaultDeviceSpecifier();
        StartWatchdog();
        LogStatus("Initialize finished");
    }

    public static void Tick()
    {
        if ((DateTime.UtcNow - _lastTickUtc).TotalSeconds < 1)
            return;

        _lastTickUtc = DateTime.UtcNow;

        ResolveServices();
        TryCaptureAudioManager();
        StartWatchdog();
    }

    public static void CaptureAudioManager(object instance)
    {
        _audioManager = instance;
        MarseyLogger.Debug($"Captured audio manager: {instance.GetType().FullName}");
    }

    public static void QueueReconnect(string reason)
    {
        ResolveServices();

        lock (Gate)
        {
            if (_reconnectQueued || _reconnectRunning)
                return;

            if (DateTime.UtcNow - _lastReconnectUtc < ReconnectCooldown)
                return;

            _reconnectQueued = true;
        }

        if (_tasks == null)
        {
            MarseyLogger.Warn($"Cannot reconnect audio, ITaskManager is unavailable. Reason: {reason}");
            lock (Gate)
            {
                _reconnectQueued = false;
            }
            return;
        }

        MarseyLogger.Warn($"Queueing audio reconnect: {reason}");
        _tasks.RunOnMainThread(() => ReconnectNow(reason));
    }

    private static void ResolveServices()
    {
        try
        {
            if (_tasks == null && IoCManager.Resolve<ITaskManager>() is { } tasks)
            {
                _tasks = tasks;
                MarseyLogger.Debug("Resolved ITaskManager.");
            }
        }
        catch
        {
            // IoC may not be ready during very early loading.
        }

        try
        {
            if (_resourceCache == null && IoCManager.Resolve<IResourceCache>() is { } resourceCache)
            {
                _resourceCache = resourceCache;
                MarseyLogger.Debug("Resolved IResourceCache.");
            }
        }
        catch
        {
            // Resource reload is best-effort.
        }

        try
        {
            if (_entityManager == null && IoCManager.Resolve<IEntityManager>() is { } entityManager)
            {
                _entityManager = entityManager;
                MarseyLogger.Debug("Resolved IEntityManager.");
            }
        }
        catch
        {
            // Entity restoration is best-effort.
        }

        try
        {
            if (_timing == null && IoCManager.Resolve<IGameTiming>() is { } timing)
            {
                _timing = timing;
                MarseyLogger.Debug("Resolved IGameTiming.");
            }
        }
        catch
        {
            // Playback offset restoration is best-effort.
        }

        try
        {
            if (_configManager == null && IoCManager.Resolve<IConfigurationManager>() is { } configManager)
            {
                _configManager = configManager;
                MarseyLogger.Debug("Resolved IConfigurationManager.");
            }
        }
        catch
        {
            // UI sound restoration is best-effort.
        }

        try
        {
            if (_uiManager == null && IoCManager.Resolve<IUserInterfaceManager>() is { } uiManager)
            {
                _uiManager = uiManager;
                MarseyLogger.Debug("Resolved IUserInterfaceManager.");
            }
        }
        catch
        {
            // UI sound restoration is best-effort.
        }
    }

    private static void StartWatchdog()
    {
        if (!_watchdogEnabled || _watchdog != null)
            return;

        _watchdog = new ThreadingTimer(_ =>
        {
            if (!_watchdogEnabled || _tasks == null)
                return;

            try
            {
                _tasks.RunOnMainThread(ProbeOnMainThread);
            }
            catch
            {
                // Game may be closing or IoC may no longer be available.
            }
        }, null, WatchdogInitialDelay, WatchdogPeriod);
    }

    private static void ProbeOnMainThread()
    {
        if (_reconnectQueued || _reconnectRunning)
            return;

        if (!TryCaptureAudioManager())
            return;

        if (!IsOpenAlHealthy(out var details))
        {
            _consecutiveProbeErrors++;
            if (_consecutiveProbeErrors >= ConsecutiveProbeErrorsBeforeReconnect)
                QueueReconnect(details);

            return;
        }

        _consecutiveProbeErrors = 0;
    }

    private static bool IsOpenAlHealthy(out string details)
    {
        DrainOpenAlErrors();

        var context = ALC.GetCurrentContext();
        if (context == ALContext.Null)
        {
            details = "OpenAL current context is null";
            return false;
        }

        var device = ALC.GetContextsDevice(context);
        if (device == ALDevice.Null)
        {
            details = "OpenAL current device is null";
            return false;
        }

        var connected = TryGetAlcConnected(device);
        if (connected == 0)
        {
            details = "OpenAL device reports ALC_CONNECTED=0";
            return false;
        }

        if (HasDefaultDeviceChanged(out var defaultChangeDetails))
        {
            details = defaultChangeDetails;
            return false;
        }

        try
        {
            AL.Listener(ALListenerf.Gain, GetCurrentMasterGain());
        }
        catch (Exception e)
        {
            details = $"OpenAL listener probe threw {e.GetType().Name}: {e.Message}";
            return false;
        }

        var listenerError = AL.GetError();
        if (listenerError != ALError.NoError)
        {
            details = $"OpenAL listener probe error={listenerError}";
            DrainOpenAlErrors();
            return false;
        }

        if (!TryProbeSource(out var sourceDetails))
        {
            details = $"OpenAL source probe failed: {sourceDetails}";
            return false;
        }

        details = "OpenAL probe ok";
        return true;
    }

    private static bool TryProbeSource(out string details)
    {
        var source = 0;

        try
        {
            DrainOpenAlErrors();
            source = AL.GenSource();
            var error = AL.GetError();
            var isSource = source != 0 && AL.IsSource(source);
            details = $"source={source}, isSource={isSource}, error={error}";
            return isSource && error == ALError.NoError;
        }
        catch (Exception e)
        {
            details = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
        finally
        {
            if (source != 0)
            {
                try
                {
                    AL.DeleteSource(source);
                }
                catch
                {
                    // Diagnostic probe only.
                }
            }

            DrainOpenAlErrors();
        }
    }

    private static float GetCurrentMasterGain()
    {
        try
        {
            AL.GetListener(ALListenerf.Gain, out var gain);
            if (gain >= 0f && !float.IsNaN(gain))
                return gain;
        }
        catch
        {
            // Fall through to a harmless default.
        }

        return 1f;
    }

    private static int TryGetAlcConnected(ALDevice device)
    {
        if (device == ALDevice.Null)
            return -1;

        try
        {
            return ALC.GetInteger(device, (AlcGetInteger)AlcConnected);
        }
        catch
        {
            return -1;
        }
    }

    private static bool HasDefaultDeviceChanged(out string details)
    {
        details = "Default audio device unchanged";

        if (!ShouldFollowSystemDefaultDevice())
        {
            CaptureDefaultDeviceSpecifier();
            return false;
        }

        var windowsDefault = GetWindowsDefaultRenderEndpointKey();
        if (!string.IsNullOrWhiteSpace(windowsDefault))
        {
            if (string.IsNullOrWhiteSpace(_lastWindowsDefaultRenderEndpointId))
            {
                _lastWindowsDefaultRenderEndpointId = windowsDefault;
                return false;
            }

            if (!string.Equals(_lastWindowsDefaultRenderEndpointId, windowsDefault, StringComparison.Ordinal))
            {
                details = "Windows default render endpoint changed.";
                _lastWindowsDefaultRenderEndpointId = windowsDefault;
                return true;
            }
        }

        var currentDefault = GetDefaultDeviceSpecifier();
        if (string.IsNullOrWhiteSpace(currentDefault))
            return false;

        if (string.IsNullOrWhiteSpace(_lastDefaultDeviceSpecifier))
        {
            _lastDefaultDeviceSpecifier = currentDefault;
            return false;
        }

        if (string.Equals(_lastDefaultDeviceSpecifier, currentDefault, StringComparison.Ordinal))
            return false;

        details = $"OpenAL default device changed: '{_lastDefaultDeviceSpecifier}' -> '{currentDefault}'";
        _lastDefaultDeviceSpecifier = currentDefault;
        return true;
    }

    private static bool ShouldFollowSystemDefaultDevice()
    {
        ResolveServices();

        if (_configManager == null)
            return true;

        try
        {
            return string.IsNullOrWhiteSpace(_configManager.GetCVar(CVars.AudioDevice));
        }
        catch
        {
            return true;
        }
    }

    private static void CaptureDefaultDeviceSpecifier()
    {
        _lastDefaultDeviceSpecifier = GetDefaultDeviceSpecifier();
        _lastWindowsDefaultRenderEndpointId = GetWindowsDefaultRenderEndpointKey();
    }

    private static string? GetDefaultDeviceSpecifier()
    {
        return TryGetAlcString(ALDevice.Null, AlcGetString.DefaultAllDevicesSpecifier)
               ?? TryGetAlcString(ALDevice.Null, AlcGetString.DefaultDeviceSpecifier);
    }

    private static string? TryGetAlcString(ALDevice device, AlcGetString parameter)
    {
        try
        {
            var value = ALC.GetString(device, parameter);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWindowsDefaultRenderEndpointKey()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var console = TryGetWindowsDefaultRenderEndpointId(ERole.Console);
        var multimedia = TryGetWindowsDefaultRenderEndpointId(ERole.Multimedia);

        if (string.IsNullOrWhiteSpace(console) && string.IsNullOrWhiteSpace(multimedia))
            return null;

        return $"{console ?? string.Empty}|{multimedia ?? string.Empty}";
    }

    private static string? TryGetWindowsDefaultRenderEndpointId(ERole role)
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        var idPtr = IntPtr.Zero;

        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;

            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out device);
            if (hr < 0 || device == null)
                return null;

            hr = device.GetId(out idPtr);
            if (hr < 0 || idPtr == IntPtr.Zero)
                return null;

            return Marshal.PtrToStringUni(idPtr);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (idPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(idPtr);

#pragma warning disable CA1416
            if (device != null)
                Marshal.ReleaseComObject(device);

            if (enumeratorObject != null)
                Marshal.ReleaseComObject(enumeratorObject);
#pragma warning restore CA1416
        }
    }

    private static bool TryCaptureAudioManager()
    {
        if (_audioManager != null)
            return true;

        try
        {
            _audioManager = IoCManager.Resolve<IAudioManager>();
            if (_audioManager != null)
            {
                MarseyLogger.Debug($"Resolved audio manager: {_audioManager.GetType().FullName}");
                return true;
            }
        }
        catch
        {
            // Not available yet.
        }

        return false;
    }

    private static void LogStatus(string prefix)
    {
        MarseyLogger.Info(
            $"{prefix}. tasks={_tasks != null}, resourceCache={_resourceCache != null}, " +
            $"entityManager={_entityManager != null}, timing={_timing != null}, config={_configManager != null}, " +
            $"ui={_uiManager != null}, audioManager={_audioManager != null}, " +
            $"watchdog={_watchdog != null}, watchdogEnabled={_watchdogEnabled}.");
    }

    private static void ReconnectNow(string reason)
    {
        lock (Gate)
        {
            _reconnectQueued = false;
            if (_reconnectRunning)
                return;

            _reconnectRunning = true;
        }

        try
        {
            ResolveServices();
            if (!TryCaptureAudioManager() || _audioManager == null)
            {
                MarseyLogger.Warn("Audio reconnect skipped: audio manager is unavailable.");
                return;
            }

            MarseyLogger.Warn($"Audio reconnect started: {reason}");

            ClearUiSoundReferences();
            TryInvokeNoArgs(_audioManager, "Shutdown");
            ClearAudioManagerRuntimeCollections(_audioManager);
            var clearedResources = ClearCachedAudioResources();
            ResetOpenAlFields(_audioManager);
            InvokeNoArgs(_audioManager, "InitializePostWindowing");
            CaptureDefaultDeviceSpecifier();
            var sanitized = SanitizeAudioComponents();
            RestoreUiSounds();
            DrainOpenAlErrors();

            _consecutiveProbeErrors = 0;
            _lastReconnectUtc = DateTime.UtcNow;
            MarseyLogger.Info($"Audio reconnect finished. Cleared cached audio resources: {clearedResources}. Sanitized components: {sanitized}.");
        }
        catch (Exception e)
        {
            MarseyLogger.Fatal($"Audio reconnect failed: {e}");
        }
        finally
        {
            lock (Gate)
            {
                _reconnectRunning = false;
            }
        }
    }

    private static int CountLoadedAudioResources()
    {
        ResolveServices();

        if (_resourceCache == null)
            return -1;

        try
        {
            return _resourceCache.GetAllResources<AudioResource>().Count();
        }
        catch
        {
            return -1;
        }
    }

    private static int CountAudioComponents()
    {
        ResolveServices();

        if (_entityManager == null)
            return -1;

        try
        {
            var count = 0;
            var query = _entityManager.AllEntityQueryEnumerator<AudioComponent>();
            while (query.MoveNext(out _, out _))
                count++;

            return count;
        }
        catch
        {
            return -1;
        }
    }

    private static int SanitizeAudioComponents()
    {
        ResolveServices();

        if (_entityManager == null)
            return 0;

        var sanitized = 0;

        try
        {
            var query = _entityManager.AllEntityQueryEnumerator<AudioComponent>();
            while (query.MoveNext(out _, out var component))
            {
                SetComponentSource(component, new NullAudioSource(component));
                component.Loaded = true;
                component.Started = true;
                sanitized++;
            }
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"Audio component sanitize failed: {e}");
        }

        return sanitized;
    }

    private static void RestoreUiSounds()
    {
        ResolveServices();

        if (_configManager == null || _uiManager == null || _resourceCache == null || _audioManager is not IAudioManager audioManager)
            return;

        try
        {
            ClearUiSoundReferences();

            if (!_configManager.GetCVar(CVars.InterfaceAudio))
                return;

            var interfaceGain = _configManager.GetCVar(CCVars.InterfaceVolume);
            var clickSource = CreateUiSoundSource(
                audioManager,
                _configManager.GetCVar(CCVars.UIClickSound),
                CCVars.UIClickSound.DefaultValue,
                UiClickGain * interfaceGain);
            var hoverSource = CreateUiSoundSource(
                audioManager,
                _configManager.GetCVar(CCVars.UIHoverSound),
                CCVars.UIHoverSound.DefaultValue,
                UiHoverGain * interfaceGain);

            _uiManager.SetClickSound(clickSource);
            _uiManager.SetHoverSound(hoverSource);

            if (TryGetAudioUiController() is { } audioUiController)
            {
                SetFieldValue(audioUiController.GetType(), audioUiController, "_clickSource", clickSource);
                SetFieldValue(audioUiController.GetType(), audioUiController, "_hoverSource", hoverSource);
                SetFieldValue(audioUiController.GetType(), audioUiController, "_interfaceGain", interfaceGain);
            }
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"UI sound restore failed: {e.Message}");
        }
    }

    private static void ClearUiSoundReferences()
    {
        if (_uiManager != null)
        {
            var uiType = _uiManager.GetType();
            SetFieldValue(uiType, _uiManager, "_clickSource", null);
            SetFieldValue(uiType, _uiManager, "_hoverSource", null);
        }

        if (TryGetAudioUiController() is not { } audioUiController)
            return;

        var controllerType = audioUiController.GetType();
        SetFieldValue(controllerType, audioUiController, "_clickSource", null);
        SetFieldValue(controllerType, audioUiController, "_hoverSource", null);
    }

    private static IAudioSource? CreateUiSoundSource(IAudioManager audioManager, string path, string fallback, float gain)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!_resourceCache!.TryGetResource(path, out AudioResource? resource))
            resource = _resourceCache.GetResource<AudioResource>(fallback);

        var source = audioManager.CreateAudioSource(resource.AudioStream);
        if (source == null)
            return null;

        source.Gain = gain;
        source.Global = true;
        return source;
    }

    private static void DrainOpenAlErrors()
    {
        try
        {
            for (var i = 0; i < 32; i++)
            {
                if (AL.GetError() == ALError.NoError)
                    break;
            }
        }
        catch
        {
            // OpenAL may be unavailable during shutdown.
        }
    }

    private static object? TryGetAudioUiController()
    {
        if (_uiManager == null)
            return null;

        try
        {
            var field = _uiManager.GetType().GetField("_uiControllers", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(_uiManager) is not IDictionary controllers)
                return null;

            foreach (DictionaryEntry entry in controllers)
            {
                if (entry.Key is Type { FullName: "Content.Client.Audio.AudioUIController" })
                    return entry.Value;
            }
        }
        catch
        {
            // Fallback path still updates UserInterfaceManager directly.
        }

        return null;
    }

    private static bool TryApplyAudioParams(object? audioSystem, MethodInfo? applyAudioParams, AudioComponent component, IAudioSource source)
    {
        if (audioSystem == null || applyAudioParams == null)
            return false;

        try
        {
            applyAudioParams.Invoke(audioSystem, [component.Params, source]);
            return true;
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"ApplyAudioParams reflection failed: {e.Message}");
            return false;
        }
    }

    private static void ApplyAudioParamsFallback(AudioComponent component, IAudioSource source)
    {
        source.Pitch = component.Params.Pitch;
        source.Volume = component.Params.Volume;
        source.RolloffFactor = component.Params.RolloffFactor;
        source.MaxDistance = component.Params.MaxDistance;
        source.ReferenceDistance = component.Params.ReferenceDistance;
        source.Looping = component.Params.Loop;
    }

    private static float EstimatePlaybackPosition(AudioComponent component, TimeSpan length)
    {
        var position = GetRawPlaybackPosition(component);
        if (position < 0f || float.IsNaN(position))
            return 0f;

        var totalSeconds = (float)length.TotalSeconds;
        if (totalSeconds <= 0f || float.IsNaN(totalSeconds))
            return 0f;

        if (component.Params.Loop)
            return position % totalSeconds;

        return Math.Clamp(position, 0f, Math.Max(totalSeconds - PlaybackEndSafetySeconds, 0f));
    }

    private static float GetRawPlaybackPosition(AudioComponent component)
    {
        var currentTime = component.PauseTime ?? _timing?.CurTime;
        if (currentTime == null)
            return 0f;

        return (float)(currentTime.Value - component.AudioStart).TotalSeconds;
    }

    private static void RestorePlaybackState(AudioComponent component, IAudioSource source)
    {
        switch (component.State)
        {
            case AudioState.Playing:
                // Let AudioSystem.FrameUpdate restart the source and recompute gain,
                // position, velocity and occlusion exactly like normal startup.
                component.Started = false;
                component.Gain = 0f;
                break;
            case AudioState.Paused:
                component.Started = true;
                source.Pause();
                break;
            case AudioState.Stopped:
                component.Started = true;
                source.StopPlaying();
                source.PlaybackPosition = 0f;
                break;
        }
    }

    private static void SetComponentSource(AudioComponent component, IAudioSource source)
    {
        typeof(AudioComponent)
            .GetField("Source", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.SetValue(component, source);
    }

    private static void ResetOpenAlFields(object audioManager)
    {
        var type = audioManager.GetType();

        SetFieldDefault(type, audioManager, "_openALDevice");
        SetFieldDefault(type, audioManager, "_openALContext");

        SetFieldValue(type, audioManager, "IsEfxSupported", false);
        ClearCollectionField(type, audioManager, "_alcDeviceExtensions");
        ClearCollectionField(type, audioManager, "_alContextExtensions");
    }

    private static void ClearAudioManagerRuntimeCollections(object audioManager)
    {
        var type = audioManager.GetType();

        ClearCollectionField(type, audioManager, "_audioSources");
        ClearCollectionField(type, audioManager, "_bufferedAudioSources");
        ClearCollectionField(type, audioManager, "_audioSampleBuffers");
        ClearCollectionField(type, audioManager, "_sourceDisposeQueue");
        ClearCollectionField(type, audioManager, "_bufferedSourceDisposeQueue");
        ClearCollectionField(type, audioManager, "_bufferDisposeQueue");
        DrainOpenAlErrors();
    }

    private static int ClearCachedAudioResources()
    {
        ResolveServices();

        if (_resourceCache == null)
            return 0;

        try
        {
            var cacheField = _resourceCache.GetType().GetField(
                "_cachedResources",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (cacheField?.GetValue(_resourceCache) is not IDictionary cachedResources)
                return 0;

            if (!cachedResources.Contains(typeof(AudioResource)))
                return 0;

            var typeData = cachedResources[typeof(AudioResource)];
            if (typeData == null)
                return 0;

            var cleared = CountCollectionField(typeData, "Resources");
            ClearCollectionField(typeData.GetType(), typeData, "Resources");
            ClearCollectionField(typeData.GetType(), typeData, "NonExistent");
            return cleared;
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"Could not clear cached audio resources: {e.Message}");
            return 0;
        }
    }

    private static void SetFieldDefault(Type type, object instance, string name)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null)
            return;

        field.SetValue(instance, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
    }

    private static void SetFieldValue(Type type, object instance, string name, object? value)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        field?.SetValue(instance, value);
    }

    private static void ClearCollectionField(Type type, object instance, string name)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var value = field?.GetValue(instance);
        value?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(value, null);
    }

    private static int CountCollectionField(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var value = field?.GetValue(instance);
        var countProperty = value?.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        return countProperty?.GetValue(value) is int count ? count : 0;
    }

    private static void TryInvokeNoArgs(object instance, string methodName)
    {
        try
        {
            InvokeNoArgs(instance, methodName);
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"{methodName} failed but reconnect will continue: {e.Message}");
        }
    }

    private static void InvokeNoArgs(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (method == null)
            throw new MissingMethodException(instance.GetType().FullName, methodName);

        method.Invoke(instance, null);
    }

    private static string FormatHandle(ALContext context)
    {
        return context == ALContext.Null ? "null" : context.ToString() ?? "<unknown>";
    }

    private static string FormatHandle(ALDevice device)
    {
        return device == ALDevice.Null ? "null" : device.ToString() ?? "<unknown>";
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);

        [PreserveSig]
        int OpenPropertyStore(int accessMode, out IntPtr properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out int state);
    }

    private sealed class NullAudioSource : IAudioSource
    {
        public NullAudioSource(AudioComponent component)
        {
            Looping = component.Params.Loop;
            Global = component.Global;
            Pitch = component.Params.Pitch;
            Volume = component.Params.Volume;
            MaxDistance = component.Params.MaxDistance;
            RolloffFactor = component.Params.RolloffFactor;
            ReferenceDistance = component.Params.ReferenceDistance;
        }

        public void Dispose()
        {
        }

        public void Pause()
        {
            Playing = false;
        }

        public void StartPlaying()
        {
            Playing = true;
        }

        public void StopPlaying()
        {
            Playing = false;
        }

        public void Restart()
        {
            PlaybackPosition = 0f;
            Playing = true;
        }

        public bool Playing { get; set; }
        public bool Looping { get; set; }
        public bool Global { get; set; }
        public float Pitch { get; set; }
        public float MaxDistance { get; set; }
        public float RolloffFactor { get; set; }
        public float ReferenceDistance { get; set; }
        public Vector2 Position { get; set; }
        public float Volume { get; set; }
        public float Gain { get; set; }
        public float Occlusion { get; set; }
        public float PlaybackPosition { get; set; }
        public Vector2 Velocity { get; set; }

        public void SetAuxiliary(IAuxiliaryAudio? audio)
        {
        }
    }
}
