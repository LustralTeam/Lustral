using System.Globalization;
using System.Text;

namespace Lustral.Core.Updates;

/// <summary>
/// A minimal JSON reader for update sources (the GitHub API and feeds). System.Text.Json cannot be used on Mono, because
/// its netstandard2.0 build includes polyfills that conflict with games' own copies (the same reason Tomlyn is pinned).
/// Objects are read as <c>Dictionary&lt;string, object?&gt;</c>, arrays as <c>List&lt;object?&gt;</c> and numbers as
/// <c>double</c>.
/// </summary>
internal static class Json
{
    private const int MaxDepth = 64;

    /// <exception cref="FormatException">The text is not valid JSON.</exception>
    public static object? Parse(string text)
    {
        var reader = new Reader(text);
        var value = reader.Value(0);
        reader.SkipWhitespace();
        if (!reader.AtEnd)
            throw reader.Error("text after the value");
        return value;
    }

    public static Dictionary<string, object?>? Object(object? value) => value as Dictionary<string, object?>;

    public static List<object?> Array(object? value) => value as List<object?> ?? [];

    public static string? String(Dictionary<string, object?>? obj, string key) =>
        obj is not null && obj.TryGetValue(key, out var value) ? value as string : null;

    public static bool Bool(Dictionary<string, object?>? obj, string key) =>
        obj is not null && obj.TryGetValue(key, out var value) && value is true;

    public static long? Number(Dictionary<string, object?>? obj, string key) =>
        obj is not null && obj.TryGetValue(key, out var value) && value is double number ? (long)number : null;

    private sealed class Reader(string text)
    {
        private int _at;

        public bool AtEnd => _at >= text.Length;

        public object? Value(int depth)
        {
            if (depth > MaxDepth)
                throw Error("nesting too deep");
            SkipWhitespace();
            if (AtEnd)
                throw Error("a value");
            switch (text[_at])
            {
                case '{':
                    return ObjectValue(depth);
                case '[':
                    return ArrayValue(depth);
                case '"':
                    return StringValue();
                case 't':
                    Expect("true");
                    return true;
                case 'f':
                    Expect("false");
                    return false;
                case 'n':
                    Expect("null");
                    return null;
                default:
                    return NumberValue();
            }
        }

        private Dictionary<string, object?> ObjectValue(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            _at++; // {
            SkipWhitespace();
            if (Peek() == '}')
            {
                _at++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                    throw Error("a property name");
                var key = StringValue();
                SkipWhitespace();
                if (Peek() != ':')
                    throw Error("':'");
                _at++;
                result[key] = Value(depth + 1);
                SkipWhitespace();
                var next = Peek();
                _at++;
                if (next == '}')
                    return result;
                if (next != ',')
                    throw Error("',' or '}'");
            }
        }

        private List<object?> ArrayValue(int depth)
        {
            var result = new List<object?>();
            _at++; // [
            SkipWhitespace();
            if (Peek() == ']')
            {
                _at++;
                return result;
            }
            while (true)
            {
                result.Add(Value(depth + 1));
                SkipWhitespace();
                var next = Peek();
                _at++;
                if (next == ']')
                    return result;
                if (next != ',')
                    throw Error("',' or ']'");
            }
        }

        private string StringValue()
        {
            _at++; // "
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Error("the end of a string");
                var c = text[_at++];
                if (c == '"')
                    return builder.ToString();
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (AtEnd)
                    throw Error("an escape");
                var escape = text[_at++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (_at + 4 > text.Length || !int.TryParse(text.Substring(_at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw Error("four hex digits");
                        builder.Append((char)code);
                        _at += 4;
                        break;
                    default:
                        throw Error("a valid escape");
                }
            }
        }

        private double NumberValue()
        {
            var start = _at;
            while (!AtEnd && "+-0123456789.eE".IndexOf(text[_at]) >= 0)
                _at++;
            if (_at == start || !double.TryParse(text.Substring(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw Error("a value");
            return number;
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(text, _at, word, 0, word.Length) != 0)
                throw Error($"'{word}'");
            _at += word.Length;
        }

        private char Peek() => AtEnd ? '\0' : text[_at];

        public void SkipWhitespace()
        {
            while (!AtEnd && text[_at] is ' ' or '\t' or '\r' or '\n')
                _at++;
        }

        public FormatException Error(string expected) => new($"bad JSON at character {_at}: expected {expected}");
    }
}
