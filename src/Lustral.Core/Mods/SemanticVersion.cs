using System.Globalization;

namespace Lustral.Core.Mods;

/// <summary>
/// A Semantic Versioning 2.0 version: <c>major.minor.patch[-prerelease][+build]</c>. Build metadata is ignored when comparing.
/// <c>1.2</c> and <c>1</c> are accepted as <c>1.2.0</c> and <c>1.0.0</c>.
/// </summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The prerelease identifiers; empty for a release.</summary>
    public string Prerelease { get; }

    public static SemanticVersion Create(int major, int minor, int patch, string prerelease = "") => new(major, minor, patch, prerelease);

    /// <exception cref="FormatException"><paramref name="text"/> is not a semantic version.</exception>
    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"\"{text}\" is not a semantic version (for example 1.2.0)");

    public static bool TryParse(string text, out SemanticVersion version)
    {
        version = default;
        var core = text.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0)
            core = core.Substring(0, plus);
        var prerelease = "";
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = core.Substring(dash + 1);
            core = core.Substring(0, dash);
            if (prerelease.Length == 0 || prerelease.Split('.').Any(part => part.Length == 0))
                return false;
        }

        var parts = core.Split('.');
        if (parts.Length is 0 or > 3)
            return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsDigit) ||
                !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0)
            result = Minor.CompareTo(other.Minor);
        if (result == 0)
            result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    // A release ranks above its prereleases. Identifiers are compared numerically if both are numeric, otherwise
    // ordinally.
    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return right.Length.CompareTo(left.Length);

        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var aNumber);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bNumber);
            var result = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (result != 0)
                return result;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => (Major, Minor, Patch, Prerelease).GetHashCode();
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => Prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>
/// A version range, in the syntax used by npm and Cargo: <c>^1.2.3</c> (same major version; same minor version before
/// 1.0), <c>~1.2.3</c> (same minor version), <c>&gt;=1.2.0 &lt;2.0.0</c> (all comparisons must hold), <c>=1.2.3</c>
/// (exact) and <c>*</c> (any version). A bare <c>1.2.3</c> is equivalent to <c>^1.2.3</c>, as in Cargo. Comparisons may
/// also be separated by commas (<c>&gt;=1.2, &lt;1.5</c>).
/// </summary>
public sealed class VersionRange
{
    private readonly (string Operator, SemanticVersion Version)[] _comparators;

    private VersionRange(string text, (string, SemanticVersion)[] comparators)
    {
        Text = text;
        _comparators = comparators;
    }

    public string Text { get; }

    public static VersionRange Any { get; } = new("*", []);

    /// <exception cref="FormatException"><paramref name="text"/> is not a version range.</exception>
    public static VersionRange Parse(string text) =>
        TryParse(text, out var range, out var error) ? range : throw new FormatException(error);

    public static bool TryParse(string text, out VersionRange range, out string? error)
    {
        range = null!;
        error = null;
        var trimmed = text.Trim();
        if (trimmed is "*" or "")
        {
            range = new VersionRange(trimmed.Length == 0 ? "*" : trimmed, []);
            return true;
        }

        var comparators = new List<(string, SemanticVersion)>();
        // Comparisons are separated by spaces (npm) or commas (Cargo): ">=1.2 <1.5" or ">=1.2, <1.5".
        foreach (var part in trimmed.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var op = new[] { ">=", "<=", ">", "<", "=", "^", "~" }.FirstOrDefault(o => part.StartsWith(o, StringComparison.Ordinal)) ?? "^";
            var versionText = part.StartsWith(op, StringComparison.Ordinal) ? part.Substring(op.Length) : part;
            if (!SemanticVersion.TryParse(versionText, out var version))
            {
                error = $"\"{part}\" is not a version or a comparison (for example \">=1.2.0\")";
                return false;
            }

            switch (op)
            {
                case "^":
                    comparators.Add((">=", version));
                    comparators.Add(("<", version.Major > 0 ? SemanticVersion.Create(version.Major + 1, 0, 0)
                        : version.Minor > 0 ? SemanticVersion.Create(0, version.Minor + 1, 0)
                        : SemanticVersion.Create(0, 0, version.Patch + 1)));
                    break;
                case "~":
                    comparators.Add((">=", version));
                    comparators.Add(("<", SemanticVersion.Create(version.Major, version.Minor + 1, 0)));
                    break;
                default:
                    comparators.Add((op, version));
                    break;
            }
        }

        range = new VersionRange(trimmed, comparators.ToArray());
        return true;
    }

    public bool IsSatisfiedBy(SemanticVersion version) => _comparators.All(c => c.Operator switch
    {
        ">=" => version >= c.Version,
        ">" => version > c.Version,
        "<=" => version <= c.Version,
        "<" => version < c.Version,
        _ => version == c.Version,
    });

    public override string ToString() => Text;
}
