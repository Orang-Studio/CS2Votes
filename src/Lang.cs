using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2Votes;

// small translation store. It does not use CSS localizer
internal sealed class Lang
{
    private static readonly Dictionary<string, string> ColorTags = BuildColorTags();
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _fallback;
    public Lang(string langDirectory, string fallback)
    {
        _fallback = string.IsNullOrWhiteSpace(fallback) ? "en" : fallback;
        if (!Directory.Exists(langDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(langDirectory, "*.json"))
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (table != null)
                _tables[Path.GetFileNameWithoutExtension(file)] = table;
        }
    }

    public int Count => _tables.Count;
    public string Get(string language, string key, params object[] args)
    {
        var template = Lookup(language, key) ?? Lookup(_fallback, key) ?? Lookup("en", key) ?? key;
        template = Colorize(template);
        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }
    private string? Lookup(string language, string key)
    {
        if (_tables.TryGetValue(language, out var table) && table.TryGetValue(key, out var value))
            return value;
        // lt-LT > lt
        var dash = language.IndexOf('-');
        if (dash > 0 && _tables.TryGetValue(language[..dash], out table) && table.TryGetValue(key, out value))
            return value;
        return null;
    }

    public static string Colorize(string text)
    {
        if (text.IndexOf('{') < 0)
            return text;
        foreach (var (tag, value) in ColorTags)
            text = text.Replace(tag, value, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    // remove color tags. used for server console
    public static string Strip(string text)
    {
        foreach (var (tag, value) in ColorTags)
        {
            text = text.Replace(tag, "", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(value, "");
        }
        return text.Trim();
    }

    private static Dictionary<string, string> BuildColorTags()
    {
        var tags = new Dictionary<string, string>();
        foreach (var field in typeof(ChatColors).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = field.GetValue(null)?.ToString();
            if (!string.IsNullOrEmpty(value))
                tags[$"{{{field.Name.ToLowerInvariant()}}}"] = value;
        }
        return tags;
}   }