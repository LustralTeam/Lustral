using System.Globalization;
using System.Text.RegularExpressions;
using Lustral.Core.Mods;

namespace Lustral.Core.GameData;

/// <summary>
/// A Unity version such as <c>2022.3.62f2</c> or <c>6000.0.67f1-DWR</c>: major, minor and patch numbers, followed by the
/// release type (alpha, beta, china, final, patch or experimental) and its number. Any suffix (as in custom builds) is
/// kept in <see cref="Text"/> but ignored in comparisons.
/// </summary>
internal readonly struct UnityVersionNumber : IComparable<UnityVersionNumber>
{
    private const string Types = "abcfpx";
    private static readonly Regex Pattern = new(@"^(\d+)\.(\d+)(?:\.(\d+))?(?:([abcfpx])(\d+))?", RegexOptions.CultureInvariant);

    private UnityVersionNumber(string text, int major, int minor, int patch, int type, int build)
    {
        Text = text;
        Major = major;
        Minor = minor;
        Patch = patch;
        Type = type;
        Build = build;
    }

    public string Text { get; }
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    // Index into "abcfpx"; a version without a release type (2022.3) is treated as final.
    private int Type { get; }
    private int Build { get; }

    /// <summary>The version used for version ranges: major.minor.patch, without the release type.</summary>
    public SemanticVersion ToSemanticVersion() => SemanticVersion.Create(Major, Minor, Patch);

    public static bool TryParse(string? text, out UnityVersionNumber version)
    {
        version = default;
        var match = text is null ? null : Pattern.Match(text.Trim());
        if (match is not { Success: true })
            return false;
        int Number(Group group) => group.Success ? int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        var type = match.Groups[4].Success ? Types.IndexOf(match.Groups[4].Value[0]) : Types.IndexOf('f');
        version = new UnityVersionNumber(text!.Trim(), Number(match.Groups[1]), Number(match.Groups[2]), Number(match.Groups[3]), type, Number(match.Groups[5]));
        return true;
    }

    public int CompareTo(UnityVersionNumber other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0)
            result = Minor.CompareTo(other.Minor);
        if (result == 0)
            result = Patch.CompareTo(other.Patch);
        if (result == 0)
            result = Type.CompareTo(other.Type);
        return result != 0 ? result : Build.CompareTo(other.Build);
    }

    public override string ToString() => Text;
}
