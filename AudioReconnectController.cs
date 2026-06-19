using Content.Shared.CCVar;
using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Robust.Client.Audio;
using Robust.Client.Audio.Midi;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared;
using Robust.Shared.Asynchronous;
using Robust.Shared.Audio;
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
    private const int MidiSampleRate = 44100;
    private const int MidiBufferCount = MidiSampleRate / 2205;
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
    private static IMidiManager? _midiManager;
    private static ThreadingTimer? _watchdog;

    private static bool _watchdogEnabled = true;
    private static bool _reconnectQueued;
    private static bool _reconnectRunning;
    private static bool _openAlUnavailableLogged;
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

        try
        {
            if (_midiManager == null && IoCManager.Resolve<IMidiManager>() is { } midiManager)
            {
                _midiManager = midiManager;
                MarseyLogger.Debug("Resolved IMidiManager.");
            }
        }
        catch
        {
            // MIDI is optional and may not be initialized.
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
        if (!IsOpenAlBridgeReady())
        {
            details = "OpenAL compatibility bridge is not injected or incomplete";
            if (!_openAlUnavailableLogged)
            {
                _openAlUnavailableLogged = true;
                MarseyLogger.Warn($"OpenAL compatibility bridge unavailable; watchdog probe disabled. {details}");
            }

            return true;
        }

        if (CompatBridge.OpenAlIsAvailable != null && !CompatBridge.OpenAlIsAvailable())
        {
            details = CompatBridge.OpenAlUnavailableReason?.Invoke() ?? "OpenAL unavailable";
            if (!_openAlUnavailableLogged)
            {
                _openAlUnavailableLogged = true;
                MarseyLogger.Warn($"OpenAL compatibility adapter unavailable; watchdog probe disabled. {details}");
            }

            return true;
        }

        DrainOpenAlErrors();

        var context = CompatBridge.OpenAlGetCurrentContext?.Invoke();
        if (CompatBridge.OpenAlIsNullContext != null && CompatBridge.OpenAlIsNullContext(context))
        {
            details = "OpenAL current context is null";
            return false;
        }

        var device = CompatBridge.OpenAlGetContextsDevice?.Invoke(context);
        if (CompatBridge.OpenAlIsNullDevice != null && CompatBridge.OpenAlIsNullDevice(device))
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
            CompatBridge.OpenAlListenerGain?.Invoke(GetCurrentMasterGain());
        }
        catch (Exception e)
        {
            details = $"OpenAL listener probe threw {e.GetType().Name}: {e.Message}";
            return false;
        }

        var listenerError = CompatBridge.OpenAlGetError?.Invoke();
        if (CompatBridge.OpenAlIsNoError != null && !CompatBridge.OpenAlIsNoError(listenerError))
        {
            details = $"OpenAL listener probe error={CompatBridge.OpenAlFormatError?.Invoke(listenerError)}";
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

    private static bool IsOpenAlBridgeReady()
    {
        return CompatBridge.OpenAlIsAvailable != null
               && CompatBridge.OpenAlUnavailableReason != null
               && CompatBridge.OpenAlGetCurrentContext != null
               && CompatBridge.OpenAlIsNullContext != null
               && CompatBridge.OpenAlGetContextsDevice != null
               && CompatBridge.OpenAlIsNullDevice != null
               && CompatBridge.OpenAlListenerGain != null
               && CompatBridge.OpenAlGetListenerGain != null
               && CompatBridge.OpenAlGetError != null
               && CompatBridge.OpenAlIsNoError != null
               && CompatBridge.OpenAlFormatError != null
               && CompatBridge.OpenAlGenSource != null
               && CompatBridge.OpenAlIsSource != null
               && CompatBridge.OpenAlDeleteSource != null
               && CompatBridge.OpenAlGetInteger != null
               && CompatBridge.OpenAlGetDefaultAllDevicesSpecifier != null
               && CompatBridge.OpenAlGetDefaultDeviceSpecifier != null;
    }

    private static bool TryProbeSource(out string details)
    {
        var source = 0;

        try
        {
            DrainOpenAlErrors();
            source = CompatBridge.OpenAlGenSource?.Invoke() ?? 0;
            var error = CompatBridge.OpenAlGetError?.Invoke();
            var isSource = source != 0 && (CompatBridge.OpenAlIsSource?.Invoke(source) ?? false);
            details = $"source={source}, isSource={isSource}, error={CompatBridge.OpenAlFormatError?.Invoke(error)}";
            return isSource && (CompatBridge.OpenAlIsNoError?.Invoke(error) ?? false);
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
                        CompatBridge.OpenAlDeleteSource?.Invoke(source);
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
            var gain = CompatBridge.OpenAlGetListenerGain?.Invoke() ?? 1f;
            if (gain >= 0f && !float.IsNaN(gain))
                return gain;
        }
        catch
        {
            // Fall through to a harmless default.
        }

        return 1f;
    }

    private static int TryGetAlcConnected(object? device)
    {
        if (CompatBridge.OpenAlIsNullDevice != null && CompatBridge.OpenAlIsNullDevice(device))
            return -1;

        try
        {
            return CompatBridge.OpenAlGetInteger?.Invoke(device, AlcConnected) ?? -1;
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
        return CompatBridge.OpenAlGetDefaultAllDevicesSpecifier?.Invoke()
               ?? CompatBridge.OpenAlGetDefaultDeviceSpecifier?.Invoke();
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

            var suspendedMidiRenderers = SuspendMidiRenderers();
            ClearUiSoundReferences();
            TryInvokeNoArgs(_audioManager, "Shutdown");
            ClearAudioManagerRuntimeCollections(_audioManager);
            var clearedResources = ClearCachedAudioResources();
            ResetOpenAlFields(_audioManager);
            InvokeNoArgs(_audioManager, "InitializePostWindowing");
            CaptureDefaultDeviceSpecifier();
            var restoredMidiRenderers = RestoreMidiRenderers(suspendedMidiRenderers);
            RefreshMidiManagerAfterRestore(restoredMidiRenderers > 0);
            var sanitized = SanitizeAudioComponents();
            RestoreUiSounds();
            DrainOpenAlErrors();

            _consecutiveProbeErrors = 0;
            _lastReconnectUtc = DateTime.UtcNow;
            MarseyLogger.Info($"Audio reconnect finished. Cleared cached audio resources: {clearedResources}. Sanitized components: {sanitized}. Restored MIDI renderers: {restoredMidiRenderers}/{suspendedMidiRenderers.Count}.");
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

    private static List<MidiRendererRecovery> SuspendMidiRenderers()
    {
        ResolveServices();

        if (_midiManager == null)
            return [];

        try
        {
            var renderers = new List<MidiRendererRecovery>();
            var dummySource = GetDummyBufferedAudioSource();
            if (dummySource == null)
                return renderers;

            foreach (var renderer in _midiManager.Renderers)
            {
                if (renderer.Disposed)
                    continue;

                try
                {
                    lock (renderer)
                    {
                        if (renderer.Disposed)
                            continue;

                        var sourceState = MidiSourceState.Capture(GetRendererSource(renderer) as IAudioSource);

                        if (SetRendererSource(renderer, dummySource))
                            renderers.Add(new MidiRendererRecovery(renderer, sourceState));
                    }
                }
                catch
                {
                    // A racing MIDI thread may already be cleaning this renderer.
                }
            }

            return renderers;
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"MIDI renderer suspend failed: {e.Message}");
            return [];
        }
    }

    private static int RestoreMidiRenderers(IReadOnlyList<MidiRendererRecovery> renderers)
    {
        if (_audioManager == null || renderers.Count == 0)
            return 0;

        var restored = 0;

        foreach (var recovery in renderers)
        {
            var renderer = recovery.Renderer;
            if (renderer.Disposed)
                continue;

            try
            {
                var source = CreateMidiBufferedSource(_audioManager);
                if (source == null)
                    continue;

                SetProperty(source, "SampleRate", MidiSampleRate);
                InvokeNoArgs(source, "EmptyBuffers");
                recovery.SourceState.Apply(source as IAudioSource);
                InvokeNoArgs(source, "StartPlaying");

                lock (renderer)
                {
                    if (renderer.Disposed)
                        continue;

                    if (SetRendererSource(renderer, source))
                        restored++;
                }
            }
            catch (Exception e)
            {
                MarseyLogger.Warn($"MIDI renderer restore failed: {e.Message}");
            }
        }

        return restored;
    }

    private static void RefreshMidiManagerAfterRestore(bool restoredAny)
    {
        if (!restoredAny || _midiManager == null)
            return;

        try
        {
            var type = _midiManager.GetType();
            SetFieldValue(type, _midiManager, "_gainDirty", true);
            SetFieldValue(type, _midiManager, "_nextUpdate", TimeSpan.Zero);
            _midiManager.FrameUpdate(0f);
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"MIDI manager refresh failed: {e.Message}");
        }
    }

    private static object? CreateMidiBufferedSource(object audioManager)
    {
        var method = audioManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!method.Name.EndsWith("CreateBufferedAudioSource", StringComparison.Ordinal))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType == typeof(int) &&
                       parameters[1].ParameterType == typeof(bool);
            });

        return method?.Invoke(audioManager, [MidiBufferCount, true]);
    }

    private static object? GetDummyBufferedAudioSource()
    {
        var type = AccessTools.TypeByName("Robust.Shared.Audio.Sources.DummyBufferedAudioSource");
        return type
            ?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
    }

    private static object? GetRendererSource(IMidiRenderer renderer)
    {
        return renderer
            .GetType()
            .GetProperty("Source", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(renderer);
    }

    private static bool SetRendererSource(IMidiRenderer renderer, object source)
    {
        var property = renderer.GetType().GetProperty(
            "Source",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (property == null || !property.CanWrite)
            return false;

        property.SetValue(renderer, source);
        return true;
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
                if (CompatBridge.OpenAlIsNoError != null && CompatBridge.OpenAlIsNoError(CompatBridge.OpenAlGetError?.Invoke()))
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

    private static void SetProperty(object instance, string name, object? value)
    {
        var property = instance
            .GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        property?.SetValue(instance, value);
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

    private sealed record MidiRendererRecovery(IMidiRenderer Renderer, MidiSourceState SourceState);

    private sealed class MidiSourceState
    {
        private readonly bool _hasSource;
        private readonly bool _looping;
        private readonly bool _global;
        private readonly Vector2 _position;
        private readonly Vector2 _velocity;
        private readonly float _pitch;
        private readonly float _gain;
        private readonly float _maxDistance;
        private readonly float _rolloffFactor;
        private readonly float _referenceDistance;
        private readonly float _occlusion;

        private MidiSourceState(
            bool hasSource,
            bool looping,
            bool global,
            Vector2 position,
            Vector2 velocity,
            float pitch,
            float gain,
            float maxDistance,
            float rolloffFactor,
            float referenceDistance,
            float occlusion)
        {
            _hasSource = hasSource;
            _looping = looping;
            _global = global;
            _position = position;
            _velocity = velocity;
            _pitch = pitch;
            _gain = gain;
            _maxDistance = maxDistance;
            _rolloffFactor = rolloffFactor;
            _referenceDistance = referenceDistance;
            _occlusion = occlusion;
        }

        public static MidiSourceState Capture(IAudioSource? source)
        {
            if (source == null)
                return Empty;

            try
            {
                return new MidiSourceState(
                    true,
                    source.Looping,
                    source.Global,
                    source.Position,
                    source.Velocity,
                    source.Pitch,
                    source.Gain,
                    source.MaxDistance,
                    source.RolloffFactor,
                    source.ReferenceDistance,
                    source.Occlusion);
            }
            catch
            {
                return Empty;
            }
        }

        public void Apply(IAudioSource? source)
        {
            if (!_hasSource || source == null)
                return;

            try
            {
                source.Looping = _looping;
                source.Global = _global;
                source.Position = _position;
                source.Velocity = _velocity;
                source.Pitch = _pitch;
                source.MaxDistance = _maxDistance;
                source.RolloffFactor = _rolloffFactor;
                source.ReferenceDistance = _referenceDistance;
                source.Occlusion = _occlusion;
                source.Gain = _gain;
            }
            catch
            {
                // Source state restore is best-effort. The MIDI manager frame update will reapply live parameters.
            }
        }

        private static MidiSourceState Empty { get; } = new(
            false,
            false,
            false,
            Vector2.Zero,
            Vector2.Zero,
            1f,
            1f,
            AudioParams.Default.MaxDistance,
            AudioParams.Default.RolloffFactor,
            AudioParams.Default.ReferenceDistance,
            0f);
    }
}
