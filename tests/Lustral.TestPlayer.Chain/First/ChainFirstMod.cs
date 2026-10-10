using Lustral.Tests.ChainLibrary;

namespace Lustral.Tests.ChainFirst;

public sealed class ChainFirstMod : Mod
{
    public override void OnLoad()
    {
        ChainRegistry.Loaded(Info.Id);
        Log.Info("e2e chain: first loaded");
    }
}
