using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace OpenStreamMS.Services.Localization;

/// <summary>
/// Request-scoped culture resolver. Stores the resolved culture for the current HTTP request.
/// Thread-safe via AsyncLocal; does not modify global CultureInfo.CurrentUICulture.
/// </summary>
internal sealed class RequestCulture
{
    private static readonly AsyncLocal<RequestCulture?> _current = new();

    /// <summary>Resolved culture for the current request, or null if not set.</summary>
    public CultureInfo? Culture { get; }

    /// <summary>Resolved catalog code (e.g., "ru", "de", "zh_TW") for the current request, or null if not set.</summary>
    public string? CatalogCode { get; }

    private RequestCulture(CultureInfo? culture, string? catalogCode)
    {
        Culture = culture;
        CatalogCode = catalogCode;
    }

    /// <summary>Gets the request culture for the current async context, or null if not set.</summary>
    public static RequestCulture? Current => _current.Value;

    /// <summary>Sets the request culture for the current async context.</summary>
    public static IDisposable Set(RequestCulture culture)
    {
        var previous = _current.Value;
        _current.Value = culture;
        return new CultureScope(previous);
    }

    /// <summary>Sets the request culture from a culture code string.</summary>
    public static IDisposable Set(string? cultureCode)
    {
        CultureInfo? culture = null;
        string? catalogCode = null;
        if (!string.IsNullOrEmpty(cultureCode))
        {
            catalogCode = BackendText.ResolveLanguage(cultureCode);
            try { culture = CultureInfo.GetCultureInfo(catalogCode.Replace('_', '-')); }
            catch { culture = null; }
        }
        return Set(new RequestCulture(culture, catalogCode));
    }

    /// <summary>Gets the catalog code for the current request, or falls back to system language.</summary>
    public static string GetCatalogCodeOrSystem() => Current?.CatalogCode ?? BackendText.SystemLanguage;

    /// <summary>Gets the CultureInfo for the current request, or falls back to CurrentUICulture.</summary>
    public static CultureInfo GetCultureInfoOrSystem() => Current?.Culture ?? CultureInfo.CurrentUICulture;

    /// <summary>Helper to get a localized string for the current request culture.</summary>
    public static string T(string key, params object[] args)
    {
        var code = GetCatalogCodeOrSystem();
        return BackendText.GetForCulture(code, key, args);
    }

    /// <summary>Scope that restores the previous culture on disposal.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly RequestCulture? _previous;
        public CultureScope(RequestCulture? previous) => _previous = previous;
        public void Dispose() => _current.Value = _previous;
    }
}