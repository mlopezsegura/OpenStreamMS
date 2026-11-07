using Microsoft.AspNetCore.Http;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Localization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenStreamMS.Tests")]

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Middleware that resolves the request culture from:
/// 1. User's saved preference (if authenticated)
/// 2. Accept-Language header (with q-value support)
/// 3. System culture (fallback)
/// Sets RequestCulture for the request scope.
/// </summary>
internal sealed class RequestCultureMiddleware
{
    private readonly RequestDelegate _next;

    public RequestCultureMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ServiceConfig config)
    {
        string? cultureCode = null;

        // 1. Try to get user's saved language preference (if authenticated)
        if (AuthService.IsAuthenticated(context, config))
        {
            // For now, we don't store user language preference in config.
            // This could be extended to read from a user profile in the future.
        }

        // 2. Accept-Language header
        if (string.IsNullOrEmpty(cultureCode))
        {
            var acceptLanguage = context.Request.Headers["Accept-Language"].ToString();
            if (!string.IsNullOrEmpty(acceptLanguage))
            {
                cultureCode = ParseAcceptLanguage(acceptLanguage);
            }
        }

        // 3. Set the request culture for this scope
        using (RequestCulture.Set(cultureCode))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Parses Accept-Language header and returns the best supported language code.
    /// Supports formats like: "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7"
    /// Priority: highest q-value wins, equal q-values preserve header order.
    /// Ignores malformed entries, q=0 entries, and unsupported languages.
    /// Returns null if no supported language found (falls back to system/English).
    /// </summary>
    internal static string? ParseAcceptLanguage(string header)
    {
        if (string.IsNullOrEmpty(header))
            return null;

        var entries = new List<(string lang, double q, int index)>();
        var parts = header.Split(',');
        
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (string.IsNullOrEmpty(part))
                continue;

            var segments = part.Split(';');
            var langTag = segments[0].Trim();
            
            if (string.IsNullOrEmpty(langTag))
                continue;

            double q = 1.0;
            bool qValid = false;
            bool hasQValue = false;
            
            // Parse q-value if present
            for (int j = 1; j < segments.Length; j++)
            {
                var seg = segments[j].Trim();
                if (seg.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                {
                    var qStr = seg.Substring(2).Trim();
                    if (double.TryParse(qStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedQ))
                    {
                        if (parsedQ >= 0 && parsedQ <= 1)
                        {
                            q = parsedQ;
                            qValid = true;
                        }
                    }
                    hasQValue = true;
                    break; // Only first q= is considered
                }
            }

            // If q-value was present but invalid, skip this entry
            if (hasQValue && !qValid)
                continue;
            
            if (q <= 0)
                continue; // Ignore q=0 entries

            entries.Add((langTag, q, entries.Count));
        }

        // Sort by q-value descending, then by original order (stable sort via index)
        var sorted = entries
            .OrderByDescending(e => e.q)
            .ThenBy(e => e.index)
            .ToList();

        // Find first supported language
        foreach (var entry in sorted)
        {
            var normalized = BackendText.ResolveLanguage(entry.lang);
            // Accept if it's a non-English supported language, or explicitly English
            if (normalized != "en" || entry.lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        return null; // No supported language found
    }
}