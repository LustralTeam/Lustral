using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Lustral.Core.Config;

/// <summary>
/// <see cref="ModConfig"/> implementation for TOML files. Settings classes are read by reflection and files are parsed
/// with Tomlyn; a custom writer adds each setting's description as a comment.
/// </summary>
internal sealed class TomlModConfig(string filePath, string modName, ILog log) : ModConfig
{
    public override string FilePath => filePath;

    public override T Bind<T>()
    {
        WriteSchema(typeof(T));
        var settings = new T();
        if (!File.Exists(filePath))
        {
            Write(settings);
            return settings;
        }

        DocumentSyntax document;
        try
        {
            document = Toml.Parse(File.ReadAllText(filePath), filePath);
        }
        catch (IOException e)
        {
            log.Error($"Could not read {filePath}; all settings use their defaults: {e.Message}");
            return settings;
        }
        if (document.HasErrors)
        {
            var error = document.Diagnostics.First(d => d.Kind == DiagnosticMessageKind.Error);
            log.Error($"{filePath} is not valid TOML; all settings use their defaults and the file is left unchanged: {error}");
            return settings;
        }

        if (Read(document.ToModel(), settings, prefix: ""))
            Write(settings);
        return settings;
    }

    public override void Save<T>(T settings)
    {
        Write(settings ?? throw new ArgumentNullException(nameof(settings)));
        WriteSchema(settings.GetType());
    }

    /// <summary>Writes the settings schema next to the file for Lustral Hub (<see cref="SettingsSchema"/>), only if it has changed.</summary>
    private void WriteSchema(Type settingsType)
    {
        if (settingsType.GetConstructor(Type.EmptyTypes) is null)
            return; // Defaults come from a new instance.
        var path = Path.Combine(Path.GetDirectoryName(filePath)!, Path.GetFileNameWithoutExtension(filePath) + ".schema.json");
        try
        {
            var schema = SettingsSchema.Describe(Path.GetFileNameWithoutExtension(filePath), modName, settingsType);
            if (File.Exists(path) && File.ReadAllText(path) == schema)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, schema, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TargetInvocationException or MissingMethodException)
        {
            log.Debug($"Could not write {Path.GetFileName(path)}: {e.Message}");
        }
    }

    // Populates target from the table. Returns whether any setting was missing from the file, in which case the file is
    // rewritten.
    private bool Read(TomlTable table, object target, string prefix)
    {
        var missing = false;
        var properties = Settings(target.GetType()).ToList();
        foreach (var property in properties)
        {
            var key = prefix + SnakeCase(property.Name);
            if (!table.TryGetValue(SnakeCase(property.Name), out var value))
            {
                // A setting with a null default is not written, so it is not considered missing.
                missing |= property.GetValue(target) is not null;
                continue;
            }

            if (IsSection(property.PropertyType))
            {
                if (value is TomlTable section)
                {
                    var nested = property.GetValue(target) ?? Activator.CreateInstance(property.PropertyType)!;
                    missing |= Read(section, nested, key + ".");
                    property.SetValue(target, nested);
                }
                else
                {
                    log.Warn($"{FileName}: {key} must be a table ([{key}]); ignoring it");
                }
                continue;
            }

            if (TryConvert(value, property.PropertyType, out var converted))
                property.SetValue(target, converted);
            else
                log.Warn($"{FileName}: {key} = {Describe(value)} is not {Expected(property.PropertyType)}; using {Format(property.GetValue(target), property.PropertyType) ?? "nothing"}");
        }

        var known = new HashSet<string>(properties.Select(p => SnakeCase(p.Name)), StringComparer.Ordinal);
        foreach (var unknown in table.Keys.Where(k => !known.Contains(k)))
            log.Warn($"{FileName}: unknown setting {prefix}{unknown}; check the spelling");
        return missing;
    }

    private void Write(object settings)
    {
        var text = new StringBuilder();
        text.Append("# Settings for ").Append(modName).AppendLine(".");
        text.AppendLine("# Changes take effect the next time the game starts. New settings are added when the mod is updated.");
        WriteTable(text, settings, header: null, comments: []);

        var directory = Path.GetDirectoryName(filePath)!;
        try
        {
            Directory.CreateDirectory(directory);
            // Written to a temporary file first, so that an interrupted write never leaves a partial file.
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            File.Copy(temporary, filePath, overwrite: true);
            File.Delete(temporary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not write {filePath}: {e.Message}");
        }
    }

    private static void WriteTable(StringBuilder text, object table, string? header, IEnumerable<string> comments)
    {
        if (header is not null)
        {
            text.AppendLine();
            foreach (var line in comments)
                text.Append("# ").AppendLine(line);
            text.Append('[').Append(header).AppendLine("]");
        }

        var properties = Settings(table.GetType()).ToList();
        foreach (var property in properties.Where(p => !IsSection(p.PropertyType)))
        {
            var value = Format(property.GetValue(table), property.PropertyType);
            if (value is null)
                continue;
            text.AppendLine();
            foreach (var line in Comments(property))
                text.Append("# ").AppendLine(line);
            text.Append(SnakeCase(property.Name)).Append(" = ").AppendLine(value);
        }

        // Tables are written after all plain keys, because in TOML, keys after a table header belong to that table.
        foreach (var property in properties.Where(p => IsSection(p.PropertyType)))
        {
            var nested = property.GetValue(table) ?? Activator.CreateInstance(property.PropertyType)!;
            var name = SnakeCase(property.Name);
            WriteTable(text, nested, header is null ? name : $"{header}.{name}", Comments(property));
        }
    }

    private static IEnumerable<string> Comments(PropertyInfo property)
    {
        if (property.GetCustomAttribute<DescriptionAttribute>() is { Description: { Length: > 0 } description })
        {
            foreach (var line in description.Split('\n'))
                yield return line.TrimEnd('\r');
        }
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type.IsEnum)
            yield return $"One of: {string.Join(", ", Enum.GetNames(type))}";
    }

    internal static IEnumerable<PropertyInfo> SettingsOf(Type type) => Settings(type);

    internal static bool IsSectionType(Type type) => IsSection(type);

    internal static Type? ElementTypeOf(Type type) => ElementType(type);

    // Public instance properties with a public getter and setter, in declaration order.
    private static IEnumerable<PropertyInfo> Settings(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken);

    // A nested settings class: any type that is not a value type, a string or a collection.
    private static bool IsSection(Type type) =>
        type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type);

    private static bool TryConvert(object? value, Type type, out object? result)
    {
        result = null;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            switch (value)
            {
                case bool b when underlying == typeof(bool):
                    result = b;
                    return true;
                case string s when underlying == typeof(string):
                    result = s;
                    return true;
                case string s when underlying.IsEnum:
                    var name = Enum.GetNames(underlying).FirstOrDefault(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase));
                    result = name is null ? null : Enum.Parse(underlying, name);
                    return name is not null;
                case long l when IsInteger(underlying):
                    result = Convert.ChangeType(l, underlying, CultureInfo.InvariantCulture);
                    return true;
                case long l when IsFloatingPoint(underlying):
                    result = Convert.ChangeType(l, underlying, CultureInfo.InvariantCulture);
                    return true;
                case double d when IsFloatingPoint(underlying):
                    result = Convert.ChangeType(d, underlying, CultureInfo.InvariantCulture);
                    return true;
                case TomlArray array when ElementType(underlying) is { } element:
                    var items = Array.CreateInstance(element, array.Count);
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (!TryConvert(array[i], element, out var item))
                            return false;
                        items.SetValue(item, i);
                    }
                    if (underlying.IsArray)
                    {
                        result = items;
                    }
                    else
                    {
                        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                        foreach (var item in items)
                            list.Add(item);
                        result = list;
                    }
                    return true;
                default:
                    return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    // T[], List<T>, IList<T>, IReadOnlyList<T>, IEnumerable<T> and other types to which a List<T> or T[] can be assigned.
    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        if (!type.IsGenericType || type.GetGenericArguments().Length != 1)
            return null;
        var element = type.GetGenericArguments()[0];
        return type.IsAssignableFrom(typeof(List<>).MakeGenericType(element)) ? element : null;
    }

    private static bool IsInteger(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
        type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

    private static bool IsFloatingPoint(Type type) => type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    /// <returns>The value as TOML, or null if there is nothing to write (TOML has no null value).</returns>
    internal static string? Format(object? value, Type type)
    {
        switch (value)
        {
            case null:
                return null;
            case bool b:
                return b ? "true" : "false";
            case string s:
                return Quote(s);
            case Enum e:
                return Quote(e.ToString());
            case float f:
                return FormatFloat(f, f.ToString("R", CultureInfo.InvariantCulture));
            case double d:
                return FormatFloat(d, d.ToString("R", CultureInfo.InvariantCulture));
            case decimal m:
                return FormatFloat((double)m, m.ToString(CultureInfo.InvariantCulture));
            case IFormattable number when IsInteger(Nullable.GetUnderlyingType(type) ?? type):
                return number.ToString(null, CultureInfo.InvariantCulture);
            case IEnumerable items when ElementType(Nullable.GetUnderlyingType(type) ?? type) is { } element:
                var formatted = items.Cast<object?>().Select(item => Format(item, element)).Where(item => item is not null);
                return $"[{string.Join(", ", formatted)}]";
            default:
                return null;
        }
    }

    private static string FormatFloat(double value, string text)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";
        // TOML parses 1 as an integer; writing 1.0 keeps the value a float.
        return text.IndexOfAny(['.', 'E', 'e']) >= 0 ? text : text + ".0";
    }

    private static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\t': quoted.Append("\\t"); break;
                case < ' ' or '\u007f': quoted.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)); break;
                default: quoted.Append(c); break;
            }
        }
        return quoted.Append('"').ToString();
    }

    // ShowFps -> show_fps, HUDScale -> hud_scale, Level2Name -> level2_name.
    internal static string SnakeCase(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                if (i > 0 && previous != '_' && (char.IsLower(previous) || char.IsDigit(previous) || char.IsLower(next)))
                    text.Append('_');
                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    private static string Describe(object? value) => value switch
    {
        string s => Quote(s),
        TomlArray => "an array",
        TomlTable => "a table",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "nothing",
    };

    private static string Expected(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(bool))
            return "true or false";
        if (underlying == typeof(string))
            return "a quoted string";
        if (underlying.IsEnum)
            return $"one of {string.Join(", ", Enum.GetNames(underlying))}";
        if (IsInteger(underlying))
            return $"an integer within the range of {underlying.Name}";
        if (IsFloatingPoint(underlying))
            return "a number";
        return ElementType(underlying) is { } element ? $"a list of {Expected(element)}" : underlying.Name;
    }

    private string FileName => Path.GetFileName(filePath);
}
