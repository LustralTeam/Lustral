using System.Globalization;
using System.Text;

namespace Lustral.Core.Logging;

/// <summary>
/// Unity's rich text tags in log messages: <c>&lt;color=red&gt;</c>, <c>&lt;color=#rrggbb&gt;</c>, <c>&lt;b&gt;</c>,
/// <c>&lt;i&gt;</c>, <c>&lt;u&gt;</c>, <c>&lt;s&gt;</c> and <c>&lt;size=…&gt;</c>. The console window renders them, and log
/// files contain the text without them. As in Unity, a tag is recognized only with its closing tag, so text that merely
/// resembles a tag (<c>List&lt;int&gt;</c>, or the compiler-generated <c>&lt;i&gt;5__2</c> in a Harmony message) is left
/// unchanged.
/// </summary>
internal static class RichText
{
    private const int MaxTagLength = 40;

    // Unity's named colors, plus "gray" as an alternative to "grey".
    private static readonly Dictionary<string, Rgb> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aqua"] = new(0x00, 0xFF, 0xFF),
        ["black"] = new(0x00, 0x00, 0x00),
        ["blue"] = new(0x00, 0x00, 0xFF),
        ["brown"] = new(0xA5, 0x2A, 0x2A),
        ["cyan"] = new(0x00, 0xFF, 0xFF),
        ["darkblue"] = new(0x00, 0x00, 0xA0),
        ["fuchsia"] = new(0xFF, 0x00, 0xFF),
        ["green"] = new(0x00, 0x80, 0x00),
        ["grey"] = new(0x80, 0x80, 0x80),
        ["gray"] = new(0x80, 0x80, 0x80),
        ["lightblue"] = new(0xAD, 0xD8, 0xE6),
        ["lime"] = new(0x00, 0xFF, 0x00),
        ["magenta"] = new(0xFF, 0x00, 0xFF),
        ["maroon"] = new(0x80, 0x00, 0x00),
        ["navy"] = new(0x00, 0x00, 0x80),
        ["olive"] = new(0x80, 0x80, 0x00),
        ["orange"] = new(0xFF, 0xA5, 0x00),
        ["purple"] = new(0x80, 0x00, 0x80),
        ["red"] = new(0xFF, 0x00, 0x00),
        ["silver"] = new(0xC0, 0xC0, 0xC0),
        ["teal"] = new(0x00, 0x80, 0x80),
        ["white"] = new(0xFF, 0xFF, 0xFF),
        ["yellow"] = new(0xFF, 0xFF, 0x00),
    };

    private enum Kind
    {
        Bold,
        Italic,
        Underline,
        Strike,
        Color,
        Size,
    }

    /// <summary>Returns the text without rich text tags.</summary>
    public static string Strip(string text)
    {
        if (FindTags(text) is not { } tags)
            return text;
        var builder = new StringBuilder(text.Length);
        var at = 0;
        foreach (var tag in tags)
        {
            builder.Append(text, at, tag.Start - at);
            at = tag.End;
        }
        return builder.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>
    /// Appends the text with its tags converted to ANSI styles. <paramref name="baseStyle"/> is the initial ANSI style, which
    /// is restored as tags close.
    /// </summary>
    public static void AppendAnsi(StringBuilder builder, string text, string baseStyle)
    {
        builder.Append(baseStyle);
        if (FindTags(text) is not { } tags)
        {
            builder.Append(text);
            return;
        }
        var open = new List<Tag>();
        var at = 0;
        foreach (var tag in tags)
        {
            builder.Append(text, at, tag.Start - at);
            at = tag.End;
            // Recognized tags are properly nested, so a closing tag closes the innermost open tag.
            if (tag.Close)
                open.RemoveAt(open.Count - 1);
            else
                open.Add(tag);
            builder.Append(Ansi.Reset).Append(baseStyle);
            AppendStyle(builder, open);
        }
        builder.Append(text, at, text.Length - at);
    }

    public static bool TryParseColor(string value, out Rgb color)
    {
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[value.Length - 1] == value[0])
            value = value.Substring(1, value.Length - 2);
        if (value.Length > 0 && value[0] == '#')
            return TryParseHex(value.Substring(1), out color);
        return Names.TryGetValue(value, out color);
    }

    private static void AppendStyle(StringBuilder builder, List<Tag> open)
    {
        Rgb? color = null;
        bool bold = false, italic = false, underline = false, strike = false;
        foreach (var tag in open)
        {
            switch (tag.Kind)
            {
                case Kind.Color: color = tag.Color; break;
                case Kind.Bold: bold = true; break;
                case Kind.Italic: italic = true; break;
                case Kind.Underline: underline = true; break;
                case Kind.Strike: strike = true; break;
            }
        }
        if (color is { } rgb)
            builder.Append(Ansi.Foreground(rgb));
        if (bold)
            builder.Append(Ansi.Bold);
        if (italic)
            builder.Append(Ansi.Italic);
        if (underline)
            builder.Append(Ansi.Underline);
        if (strike)
            builder.Append(Ansi.Strike);
    }

    /// <summary>Returns the recognized tags in order: each with its closing tag, with all pairs properly nested.</summary>
    private static List<Tag>? FindTags(string text)
    {
        var start = text.IndexOf('<');
        if (start < 0)
            return null;
        List<Tag>? tags = null;
        while (start >= 0)
        {
            if (TryReadTag(text, start, out var tag))
            {
                (tags ??= []).Add(tag);
                start = text.IndexOf('<', tag.End);
            }
            else
            {
                start = text.IndexOf('<', start + 1);
            }
        }
        if (tags is null)
            return null;

        // Pairs each closing tag with the innermost open tag of the same kind. Tags left open inside that pair, and closing
        // tags without a matching open tag, are treated as text.
        var open = new List<Tag>();
        foreach (var tag in tags)
        {
            if (!tag.Close)
            {
                open.Add(tag);
                continue;
            }
            var match = open.FindLastIndex(o => o.Kind == tag.Kind);
            if (match < 0)
                continue;
            open[match].Counts = true;
            tag.Counts = true;
            open.RemoveRange(match, open.Count - match);
        }
        tags.RemoveAll(t => !t.Counts);
        return tags.Count == 0 ? null : tags;
    }

    private static bool TryReadTag(string text, int start, out Tag tag)
    {
        tag = null!;
        var end = text.IndexOf('>', start + 1);
        if (end < 0 || end - start - 1 > MaxTagLength)
            return false;
        var inside = text.Substring(start + 1, end - start - 1);
        var close = inside.StartsWith("/", StringComparison.Ordinal);
        if (close)
            inside = inside.Substring(1);
        var equals = inside.IndexOf('=');
        var name = equals < 0 ? inside : inside.Substring(0, equals);
        var value = equals < 0 ? null : inside.Substring(equals + 1);
        if (inside.IndexOf('<') >= 0 || !TryKind(name, out var kind) || (close && value is not null))
            return false;

        var color = default(Rgb);
        if (!close)
        {
            var takesValue = kind is Kind.Color or Kind.Size;
            if (takesValue != (value is not null) || value is { Length: 0 })
                return false;
            if (kind == Kind.Color && !TryParseColor(value!, out color))
                return false;
        }
        tag = new Tag { Start = start, End = end + 1, Kind = kind, Close = close, Color = color };
        return true;
    }

    private static bool TryKind(string name, out Kind kind)
    {
        switch (name.ToLowerInvariant())
        {
            case "b": kind = Kind.Bold; return true;
            case "i": kind = Kind.Italic; return true;
            case "u": kind = Kind.Underline; return true;
            case "s": kind = Kind.Strike; return true;
            case "color": kind = Kind.Color; return true;
            case "size": kind = Kind.Size; return true;
            default: kind = default; return false;
        }
    }

    // #rgb, #rgba, #rrggbb or #rrggbbaa; the alpha component is ignored by the console.
    private static bool TryParseHex(string hex, out Rgb color)
    {
        color = default;
        if (hex.Length is not (3 or 4 or 6 or 8))
            return false;
        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        if (hex.Length <= 4)
        {
            color = new Rgb((byte)(Nibble(hex[0]) * 17), (byte)(Nibble(hex[1]) * 17), (byte)(Nibble(hex[2]) * 17));
            return true;
        }
        color = new Rgb(Byte(hex, 0), Byte(hex, 2), Byte(hex, 4));
        return true;
    }

    private static int Nibble(char c) => Uri.FromHex(c);

    private static byte Byte(string hex, int at) => byte.Parse(hex.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private sealed class Tag
    {
        public int Start;
        public int End;
        public Kind Kind;
        public bool Close;
        public Rgb Color;
        public bool Counts;
    }
}

/// <summary>A 24-bit color.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>ANSI escape sequences, for consoles with virtual terminal processing.</summary>
internal static class Ansi
{
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Italic = "\u001b[3m";
    public const string Underline = "\u001b[4m";
    public const string Strike = "\u001b[9m";

    public static string Foreground(Rgb color) => $"\u001b[38;2;{color.R};{color.G};{color.B}m";
}
