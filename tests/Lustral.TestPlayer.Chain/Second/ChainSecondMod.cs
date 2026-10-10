using Lustral.Tests.ChainLibrary;

namespace Lustral.Tests.ChainSecond;

public sealed class ChainSecondMod : Mod
{
    public override void OnLoad()
    {
        // The first mod registered itself in the same library instance before this mod loaded.
        var before = ChainRegistry.LoadedMods;
        ChainRegistry.Loaded(Info.Id);
        Log.Info($"e2e chain: second loaded after [{string.Join(", ", before)}], library {typeof(ChainRegistry).Assembly.GetName().Version}");
    }
}
