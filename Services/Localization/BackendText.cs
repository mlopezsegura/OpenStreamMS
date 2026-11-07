using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace OpenStreamMS.Services.Localization;

/// <summary>Embedded backend catalogs with request-scoped culture support.</summary>
internal static class BackendText
{
    private const string Prefix = "OpenStreamMS.BackendLocales.";
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
                ?? throw new InvalidOperationException("Invalid backend catalog");
        }
        if (!result.ContainsKey("en")) throw new InvalidOperationException("English backend catalog is missing");
        return result;
    }

    /// <summary>Resolve a language tag to a supported catalog code.</summary>
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

    /// <summary>System culture (for background tasks and system logs).</summary>
    internal static string SystemLanguage => ResolveLanguage(CultureInfo.CurrentUICulture.Name);

    private static string? Lookup(string lang, string key)
    {
        if (Texts[lang].TryGetValue(key, out var text)) return text;
        if (Parents.TryGetValue(lang, out var parent) && Texts[parent].TryGetValue(key, out text)) return text;
        return Texts["en"].GetValueOrDefault(key);
    }

    /// <summary>Get translation for the system culture (background tasks, logs).</summary>
    public static string Get(string key, params object[] args)
    {
        var lang = SystemLanguage;
        var text = Lookup(lang, key) ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentUICulture, text, args);
    }

    /// <summary>Get translation for a specific culture code (e.g., "ru", "de", "zh_TW").</summary>
    public static string GetForCulture(string cultureCode, string key, params object[] args)
    {
        var lang = ResolveLanguage(cultureCode);
        var text = Lookup(lang, key) ?? key;
        var culture = CultureInfo.GetCultureInfo(lang.Replace('_', '-'));
        return args.Length == 0 ? text : string.Format(culture, text, args);
    }

    /// <summary>Get translation for a CultureInfo instance.</summary>
    public static string GetForCultureInfo(CultureInfo culture, string key, params object[] args)
    {
        var lang = ResolveLanguage(culture.Name);
        var text = Lookup(lang, key) ?? key;
        return args.Length == 0 ? text : string.Format(culture, text, args);
    }
}