using OpenStreamMS.Core.Helpers;
using System.Security.Cryptography;
using System.Text;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Gestiona autenticación: hashing de contraseñas, tokens de sesión (HMAC-SHA256) y
/// validación de credenciales tanto desde cookie como desde HTTP Basic Auth.
/// </summary>
public static class AuthService
{
    private const string CookieName = "osm_auth";
    private const int    TokenDays  = 30;

    // ── Contraseñas ───────────────────────────────────────────────────────────

    public static string HashPassword(string username, string password) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{username.ToLowerInvariant()}:{password}"))).ToLower();

    public static bool VerifyPassword(ServiceConfig config, string username, string password) =>
        !string.IsNullOrEmpty(config.AdminUsername)
        && string.Equals(config.AdminUsername, username, StringComparison.OrdinalIgnoreCase)
        && config.AdminPasswordHash == HashPassword(username, password);

    // ── Tokens de sesión (cookie) ─────────────────────────────────────────────

    public static string CreateToken(ServiceConfig config, string username)
    {
        var expiry  = DateTimeOffset.UtcNow.AddDays(TokenDays).ToUnixTimeSeconds();
        var payload = $"{username}:{expiry}";
        var sig     = HmacSign(payload, config.AuthSecret);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}:{sig}"));
    }

    public static bool ValidateToken(ServiceConfig config, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(config.AuthSecret)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            // format: username:expiry:signature
            var lastColon   = decoded.LastIndexOf(':');
            var secondColon = decoded.LastIndexOf(':', lastColon - 1);
            if (lastColon < 0 || secondColon < 0) return false;

            var sig     = decoded[(lastColon + 1)..];
            var payload = decoded[..lastColon];
            var expiry  = long.Parse(decoded[(secondColon + 1)..lastColon]);

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return false;
            return sig == HmacSign(payload, config.AuthSecret);
        }
        catch { return false; }
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    public static bool IsAuthenticated(HttpRequest request, ServiceConfig config)
    {
        // Cookie
        if (request.Cookies.TryGetValue(CookieName, out var cookie)
            && ValidateToken(config, cookie))
            return true;

        // Basic Auth (para clientes de API)
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..]));
                var sep     = decoded.IndexOf(':');
                if (sep > 0)
                    return VerifyPassword(config, decoded[..sep], decoded[(sep + 1)..]);
            }
            catch { /* credencial malformada */ }
        }

        return false;
    }

    public static void SetAuthCookie(HttpResponse response, ServiceConfig config, string username)
    {
        var token = CreateToken(config, username);
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Expires  = DateTimeOffset.UtcNow.AddDays(TokenDays),
        });
    }

    public static void ClearAuthCookie(HttpResponse response) =>
        response.Cookies.Delete(CookieName);

    // ── Configuración ─────────────────────────────────────────────────────────

    public static void SetCredentials(ServiceConfig config, string username, string password)
    {
        config.AdminUsername     = username;
        config.AdminPasswordHash = HashPassword(username, password);
        if (string.IsNullOrEmpty(config.AuthSecret))
            config.AuthSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        config.Save();
    }

    // ── Privado ───────────────────────────────────────────────────────────────

    private static string HmacSign(string data, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(data))).ToLower();
}
