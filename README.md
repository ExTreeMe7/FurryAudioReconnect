# Furry Audio Reconnect

RobustToolbox audio reconnect

Current stable version: `1.0.20`

## Build

```powershell
dotnet build .\FurryAudioReconnect.csproj -c Release
```

Output:

```text
bin\Release\net10.0\FurryAudioReconnect.dll
```

Deploy to FurryLoader:

```powershell
$FurryLoaderRoot = "<path-to-FurryLoader>"
$modPath = Join-Path $FurryLoaderRoot "bin\publish\Windows\Marsey\Mods\FurryAudioReconnect.dll"
Copy-Item .\bin\Release\net10.0\FurryAudioReconnect.dll $modPath -Force
```

## Notes

- This is a `SubverterPatch`, not a loader source patch.
- Hidesey level `Unconditional` disables Subversion in this loader fork, so this DLL will not load in that mode.
- The watchdog schedules OpenAL probes and reconnects on the Robust main thread through `ITaskManager`.
- Reconnect clears stale audio resources and lets new sounds load naturally in the new OpenAL context.
- Version `1.0.4` removes the brittle `GameController.Update` Harmony hook and lets `AudioSystem.FrameUpdate` restart restored playing sources.
- Version `1.0.5` defers runtime initialization from `MarseyEntry` to `GameShared.PostInit` to avoid blocking early client startup.
- Version `1.0.6` restores UI click/hover audio sources after reconnect and removes console commands.
- Version `1.0.7` no longer restores one-shot entity sounds after reconnect to avoid resurrecting stale hit/swing effects.
- Version `1.0.8` sanitizes all existing audio components with a no-op source after reconnect instead of replaying stale sounds.
- Version `1.0.9` guards `AudioManager.CreateAudioSource` against stale OpenAL buffers and lazily reloads the matching audio resource when possible.
- Version `1.0.10` treats old resource-backed `AudioStream` instances as stale even if their OpenAL buffer id was reused for another sound.
- Version `1.0.11` rebuilds cached audio resources without calling `AudioResource.Reload`, avoiding stale buffer-id deletion during reconnect.
- Version `1.0.12` removes the `CreateAudioSource` hook and clears cached `AudioResource` entries instead of eagerly rebuilding them.
- Version `1.0.13` clears OpenAL dispose queues after reconnect and lowers watchdog detection latency.
- Version `1.0.14` ignores stale queued OpenAL errors during health checks to avoid false reconnect loops.
- Version `1.0.15` reconnects when the system default audio output changes while the previous output is still connected.
- Version `1.0.16` also tracks the Windows CoreAudio default render endpoint so returned default devices are detected.
- Version `1.0.16` is the previous stable release. Patch diagnostics are silent during normal operation and only fatal patch errors are written to file.
- Version `1.0.17` disposes active MIDI renderers before reconnect and guards the MIDI render thread against stale buffered audio sources.
- Version `1.0.18` keeps active MIDI renderers alive by swapping them to a dummy source during reconnect and assigning a fresh buffered source afterwards.
- Version `1.0.19` preserves MIDI source state across reconnect and forces `MidiManager` to refresh restored renderers immediately.
- Version `1.0.20` avoids accepting a changed default device as current until reconnect actually finishes, preventing rapid switch/cooldown races.

## How it works

`FurryAudioReconnect` is a client-side audio recovery patch for SS14/RobustToolbox.
It does not modify the loader itself: FurryLoader/Marsey loads the DLL as a `SubverterPatch`,
then `MarseyEntry.Entry()` waits until `Content.Client` is present and applies Harmony patches
from the DLL.

Runtime initialization is done by `EntryPoint : GameShared`.
`PostInit()` builds/resolves the Robust IoC graph and calls `AudioReconnectController.Initialize()`.
`Update()` calls `AudioReconnectController.Tick()` during `FramePostEngine`, which keeps service
references fresh if they were unavailable during early startup.

The patch captures the current `Robust.Client.Audio.AudioManager` in two ways:

1. It resolves `IAudioManager` from Robust IoC when possible.
2. It uses a Harmony postfix on `AudioManager.InitializePostWindowing()` as a fallback capture point.

After initialization, a watchdog timer runs once per second. The timer does not touch OpenAL from a
background thread directly. Instead, it schedules `ProbeOnMainThread()` through `ITaskManager`, so all
OpenAL checks and reconnect operations happen on the Robust main thread.

The watchdog requests a reconnect when one of these conditions is detected:

1. The current OpenAL context is missing.
2. The current OpenAL device is missing.
3. `ALC_EXT_disconnect` reports `ALC_CONNECTED=0`.
4. A lightweight OpenAL listener/source probe fails.
5. The OpenAL default device specifier changes while the game is following the system default device.
6. On Windows, the CoreAudio default render endpoint changes for console or multimedia output.

The Windows CoreAudio check is important because OpenAL may keep reporting the same default device
specifier after a device is unplugged and plugged back in. Tracking the endpoint ID lets the patch
notice that the real Windows default output changed even when OpenAL's string does not.

When reconnect is queued, the patch applies a short cooldown and runs `ReconnectNow()` on the main
thread. The reconnect sequence is:

1. Clear UI click/hover sound references so they do not point at disposed OpenAL sources.
2. Call `AudioManager.Shutdown()` to close the old OpenAL context/device.
3. Clear AudioManager runtime collections and pending OpenAL dispose queues.
4. Clear cached `AudioResource` entries from `IResourceCache`.
5. Reset private AudioManager OpenAL fields and extension caches.
6. Call `AudioManager.InitializePostWindowing()` to create a fresh OpenAL device and context.
7. Capture the new default device identifiers.
8. Restore active MIDI renderers with fresh buffered audio sources and reapply their source state.
9. Replace existing entity `AudioComponent.Source` values with a no-op `NullAudioSource`.
10. Recreate UI click/hover sounds from the new OpenAL context.
11. Drain stale OpenAL errors left from the old context.

The patch intentionally clears cached audio resources instead of eagerly reloading every sound.
New sounds are loaded naturally by Robust when they are needed. This avoids stale OpenAL buffer IDs,
duplicated resource keys, wrong sounds being attached to later events, and replaying old one-shot
sounds after reconnect.

Existing world audio components are sanitized with `NullAudioSource` instead of being replayed.
Looping or currently active sounds are allowed to recover through normal Robust audio flow when the
game asks for them again. This is less aggressive, but it avoids resurrecting stale hit, swing, step,
ambient, or UI sounds after the OpenAL context has been rebuilt.

MIDI renderers need separate handling because they own buffered audio sources and run through the
MIDI manager's render/update path. Before reconnect, active renderers are temporarily moved to a
dummy buffered source so the MIDI thread cannot touch a disposed OpenAL source while the device is
being rebuilt. After reconnect, each renderer gets a fresh buffered source with the original source
state reapplied. The patch then marks the MIDI manager's gain/update state dirty and runs one frame
update so restored MIDI audio can resume without waiting for the normal update interval.

## Logging

The reconnect itself may cause Robust/OpenAL to print normal backend initialization lines such as:

```text
clyde.oal: HRTF specifier count: 1
clyde.oal: OpenAL Vendor: OpenAL Community
clyde.oal: OpenAL Renderer: OpenAL Soft
clyde.oal: OpenAL Version: 1.1 ALSOFT ...
clyde.oal: HRTF status: Enabled
```

Those lines are emitted by the client audio backend (`clyde.oal`) after OpenAL is initialized again.
They are not direct output from this patch.

Patch diagnostics go through `MarseyLogger`, but normal operation is intentionally silent.
`Info`, `Debug`, and `Warn` messages are ignored. Only fatal patch errors are written best-effort to a diagnostic log file:

```text
%TEMP%\FurryAudioReconnect.log
```
