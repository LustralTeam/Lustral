using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Lustral.Core.Config;

/// <summary>
/// <c>Lustral/config/&lt;id&gt;.schema.json</c>, written next to a mod's settings file whenever the mod binds its
/// settings. It describes each setting's key, type, default value, description and, for enumerations, allowed values,
/// so that Lustral Hub can display a settings page instead of the text file. The TOML file remains the source of the
/// values.
/// <code>
/// { "format": 1, "mod": "sxxr0.betterhud", "name": "Better HUD", "settings": [
///   { "key": "show_fps", "type": "bool", "default": false, "description": "Show the frame rate in the corner." },
///   { "key": "quality", "type": "enum", "options": ["Low", "High"], "default": "High" },
///   { "key": "hud", "type": "section", "settings": [ ... ] } ] }
/// </code>
/// Types: bool, string, integer, number, enum, list (with <c>items</c>, and <c>options</c> for enumerations) and
/// section. <c>nullable</c> marks a setting that may be omitted.
/// </summary>
internal static class SettingsSchema
{
    public const int Format = 1;

    public static string Describe(string modId, string modName, Type settingsType)
    {
        var text = new StringBuilder();
        text.Append("{\n  \"format\": ").Append(Format)
            .Append(",\n  \"mod\": ").Append(Quote(modId))
            .Append(",\n  \"name\": ").Append(Quote(modName))
            .Append(",\n  \"settings\": ");
        Settings(text, settingsType, Activator.CreateInstance(settingsType), "  ");
        text.Append("\n}\n");
        return text.ToString();
    }

    private static void Settings(StringBuilder text, Type settingsType, object? defaults, string indent)
    {
        var properties = TomlModConfig.SettingsOf(settingsType).ToList();
        text.Append('[');
        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            text.Append(i == 0 ? "\n" : ",\n").Append(indent).Append("  { \"key\": ").Append(Quote(TomlModConfig.SnakeCase(property.Name)));
            var value = defaults is null ? null : property.GetValue(defaults);
            if (TomlModConfig.IsSectionType(property.PropertyType))
            {
                text.Append(", \"type\": \"section\"");
                Description(text, property);
                text.Append(", \"settings\": ");
                Settings(text, property.PropertyType, value ?? Activator.CreateInstance(property.PropertyType), indent + "    ");
                text.Append(" }");
                continue;
            }
            // Nullable<T>, or a reference type with a null default; either may be omitted from the file, as TOML has no null
            // value.
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null || (!property.PropertyType.IsValueType && value is null);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            text.Append(", \"type\": ").Append(Quote(TypeName(type)));
            if (type.IsEnum)
                Options(text, type);
            if (TomlModConfig.ElementTypeOf(type) is { } element)
            {
                element = Nullable.GetUnderlyingType(element) ?? element;
                text.Append(", \"items\": ").Append(Quote(TypeName(element)));
                if (element.IsEnum)
                    Options(text, element);
            }
            if (nullable)
                text.Append(", \"nullable\": true");
            text.Append(", \"default\": ").Append(Value(value));
            Description(text, property);
            text.Append(" }");
        }
        text.Append(properties.Count == 0 ? "]" : "\n" + indent + "]");
    }

    private static string TypeName(Type type)
    {
        if (type == typeof(bool))
            return "bool";
        if (type == typeof(string))
            return "string";
        if (type.IsEnum)
            return "enum";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
            type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
            return "integer";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return "number";
        return TomlModConfig.ElementTypeOf(type) is not null ? "list" : "unsupported";
    }

    private static void Options(StringBuilder text, Type enumType) =>
        text.Append(", \"options\": [").Append(string.Join(", ", Enum.GetNames(enumType).Select(Quote))).Append(']');

    private static void Description(StringBuilder text, PropertyInfo property)
    {
        if (property.GetCustomAttribute<DescriptionAttribute>() is { Description: { Length: > 0 } description })
            text.Append(", \"description\": ").Append(Quote(description));
    }

    private static string Value(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => Quote(s),
        Enum e => Quote(e.ToString()),
        float f => double.IsNaN(f) || double.IsInfinity(f) ? "null" : ((double)f).ToString("R", CultureInfo.InvariantCulture),
        double d => double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable number when value.GetType().IsPrimitive || value is decimal => number.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Value)) + "]",
        _ => "null",
    };

    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }
}
