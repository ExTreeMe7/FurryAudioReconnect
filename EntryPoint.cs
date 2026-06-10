using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace FurryAudioReconnect;

public sealed class EntryPoint : GameShared
{
    public override void PostInit()
    {
        MarseyLogger.Info("GameShared.PostInit called.");

        try
        {
            IoCManager.BuildGraph();
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"BuildGraph skipped/failed: {e.Message}");
        }

        AudioReconnectController.Initialize();
    }

    public override void Update(ModUpdateLevel level, FrameEventArgs frameEventArgs)
    {
        if (level == ModUpdateLevel.FramePostEngine)
            AudioReconnectController.Tick();
    }
}
