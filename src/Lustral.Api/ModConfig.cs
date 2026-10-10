namespace Lustral;

/// <summary>
/// A mod's settings file, <c>Lustral/config/&lt;id&gt;.toml</c> in the game folder. Users edit the file, and the mod reads
/// it into a settings class.
/// <code>
/// public sealed class Settings
/// {
///     [Description("Show the frame rate in the corner.")]
///     public bool ShowFps { get; set; }
///     public float Volume { get; set; } = 0.8f;
/// }
///
/// var settings = Config.Bind&lt;Settings&gt;();   // in the mod class
/// </code>
/// The file is stored outside the mod's folder, so settings are preserved when the mod is updated or reinstalled.
/// </summary>
public abstract class ModConfig
{
    /// <summary>The absolute path of the settings file.</summary>
    public abstract string FilePath { get; }

    /// <summary>
    /// Reads the settings file into a new instance of <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Each public property with a getter and a setter is a setting. Its key in the file is the property name in snake_case
    /// (<c>ShowFps</c> becomes <c>show_fps</c>), and its initial value is the default. Supported types are <c>bool</c>,
    /// <c>string</c>, integer and floating-point types, enumerations (stored by name), arrays and lists of these types, and
    /// nested classes, which are stored as TOML tables. A <c>[Description]</c> attribute (System.ComponentModel) is written
    /// as a comment above the setting.
    /// <para>
    /// Missing settings keep their defaults, and the file is rewritten with all settings so that users can see them;
    /// comments added by users are not preserved. Invalid values and unknown settings are logged and ignored. If the file
    /// is not valid TOML, it is left unchanged and all settings keep their defaults.
    /// </para>
    /// </remarks>
    public abstract T Bind<T>() where T : class, new();

    /// <summary>Writes <paramref name="settings"/> to the settings file, for example after the user changes them in game.</summary>
    public abstract void Save<T>(T settings) where T : class;
}
