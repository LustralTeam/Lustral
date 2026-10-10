using System.ComponentModel;

namespace Lustral.Samples.Bombanana;

/// <summary>The sample's settings, in Lustral/config/lustral.samples.bombanana.toml.</summary>
public sealed class Settings
{
    [Description("Write a line to the log for every scene the game loads.")]
    public bool LogScenes { get; set; } = true;
}
