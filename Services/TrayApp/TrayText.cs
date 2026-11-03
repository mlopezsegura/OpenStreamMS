using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace OpenStreamMS.Services.TrayApp;

/// <summary>Embedded tray catalogs follow Windows UI culture, with parent and English fallback.</summary>
internal static class TrayText
{
    private const string Prefix = "OpenStreamMS.TrayLocales.";
    private static readonly Dictionary<string, Dictionary<string, string>> Texts = LoadTexts();
    private static readonly Dictionary<string, string> Parents = new()
    {
        ["en_GB"] = "en", ["en_US"] = "en", ["pt_BR"] = "pt", ["zh_TW"] = "zh"
    };

    private static Dictionary<string, Dictionary<string, string>> LoadTexts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            result[name[Prefix.Length..^5]] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException("Invalid tray catalog");
        }
        if (!result.ContainsKey("en")) throw new InvalidOperationException("English tray catalog is missing");
        return result;
    }

    internal static string ResolveLanguage(string name)
    {
        var tag = name.Replace('_', '-').ToLowerInvariant();
        if (tag == "zh-hant" || tag.StartsWith("zh-hant-", StringComparison.Ordinal) ||
            tag == "zh-tw" || tag == "zh-hk" || tag == "zh-mo") return "zh_TW";
        var exact = Texts.Keys.FirstOrDefault(code => code.Replace('_', '-').Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var primary = tag.Split('-')[0];
        return Texts.ContainsKey(primary) ? primary : "en";
    }

    internal static string Language => ResolveLanguage(CultureInfo.CurrentUICulture.Name);

    private static string? Lookup(string key)
    {
        if (Texts[Language].TryGetValue(key, out var text)) return text;
        if (Parents.TryGetValue(Language, out var parent) && Texts[parent].TryGetValue(key, out text)) return text;
        return Texts["en"].GetValueOrDefault(key);
    }

    public static string Get(string key, params object[] args)
    {
        var text = Lookup(key) ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentUICulture, text, args);
    }

    public static string State(string state) => Lookup("state." + state) is not null
        ? Get("state." + state) : Get("state.Unknown");
}
