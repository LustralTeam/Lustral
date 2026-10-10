using System.ComponentModel;

namespace Lustral.Samples.PlagueInc;

/// <summary>The sample's settings, in Lustral/config/lustral.samples.plagueinc.toml.</summary>
public sealed class Settings
{
    [Description("Write a line to the log for every scene the game loads.")]
    public bool LogScenes { get; set; } = true;
}
