using OpenStreamMS.Core.Helpers;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Gestiona autenticación: hashing de contraseñas (PBKDF2), tokens de sesión (HMAC-SHA256),
/// validación de credenciales desde cookie o HTTP Basic Auth y freno a la fuerza bruta.
/// </summary>
public static class AuthService
{
    private const string CookieName = "osm_auth";
    private const int    TokenDays  = 30;

    // PBKDF2-SHA256: "pbkdf2$<iteraciones>$<sal base64>$<hash base64>"
    private const string HashPrefix = "pbkdf2$";
    private const int    Iterations = 210_000;

    // ── Contraseñas ───────────────────────────────────────────────────────────

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{HashPrefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Formato anterior: SHA-256 sin sal de "usuario:contraseña". Solo para migrar.</summary>
    private static string LegacyHash(string username, string password) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{username.ToLowerInvariant()}:{password}"))).ToLower();

    public static bool VerifyPassword(ServiceConfig config, string username, string password)
    {
        if (string.IsNullOrEmpty(config.AdminUsername) ||
            !string.Equals(config.AdminUsername, username, StringComparison.OrdinalIgnoreCase))
            return false;

        var stored = config.AdminPasswordHash ?? "";
        if (stored.StartsWith(HashPrefix, StringComparison.Ordinal))
            return VerifyPbkdf2(stored, password);

        // Hash antiguo: si coincide, se sustituye por PBKDF2 en el momento.
        if (!FixedTimeEquals(stored, LegacyHash(username, password))) return false;
        config.AdminPasswordHash = HashPassword(password);
        try { config.Save(); } catch { /* se reintenta en el próximo login */ }
        return true;
    }

    private static bool VerifyPbkdf2(string stored, string password)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;
        try
        {
            var salt     = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual   = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ── Freno a la fuerza bruta ───────────────────────────────────────────────

    private const int FreeAttempts = 5;
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<IPAddress, (int Failures, DateTime LockedUntil)> _failures = new();

    /// <summary>true si esa IP debe esperar antes de volver a probar una contraseña.</summary>
    public static bool IsLockedOut(IPAddress? ip) =>
        ip is not null && _failures.TryGetValue(ip, out var f) && f.LockedUntil > DateTime.UtcNow;

    /// <summary>
    /// Registra el resultado de un intento. Tras <see cref="FreeAttempts"/> fallos la espera se
    /// dobla en cada fallo (1 s, 2 s, 4 s… hasta 15 min); un acierto la borra.
    /// </summary>
    public static void RecordAttempt(IPAddress? ip, bool success)
    {
        if (ip is null) return;
        if (success) { _failures.TryRemove(ip, out _); return; }

        _failures.AddOrUpdate(ip,
            _ => (1, DateTime.MinValue),
            (_, f) =>
            {
                int n = f.Failures + 1;
                if (n <= FreeAttempts) return (n, DateTime.MinValue);
                var wait = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, n - FreeAttempts - 1), MaxLockout.TotalSeconds));
                return (n, DateTime.UtcNow + wait);
            });

        // Que una lluvia de IPs no haga crecer el diccionario sin límite.
        if (_failures.Count > 10_000)
            foreach (var (key, f) in _failures)
                if (f.LockedUntil < DateTime.UtcNow - MaxLockout) _failures.TryRemove(key, out _);
    }

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
            if (lastColon <= 0) return false;
            var secondColon = decoded.LastIndexOf(':', lastColon - 1);
            if (secondColon < 0) return false;

            var sig      = decoded[(lastColon + 1)..];
            var payload  = decoded[..lastColon];
            var username = decoded[..secondColon];
            var expiry   = long.Parse(decoded[(secondColon + 1)..lastColon]);

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return false;
            // Un token de un usuario anterior no vale tras cambiar el administrador.
            if (!string.Equals(username, config.AdminUsername, StringComparison.OrdinalIgnoreCase)) return false;
            return FixedTimeEquals(sig, HmacSign(payload, config.AuthSecret));
        }
        catch { return false; }
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private static readonly TimeSpan BasicCacheTime = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTime> _basicCache = new();

    public static bool IsAuthenticated(HttpContext ctx, ServiceConfig config)
    {
        var request = ctx.Request;

        // Cookie
        if (request.Cookies.TryGetValue(CookieName, out var cookie)
            && ValidateToken(config, cookie))
            return true;

        // Basic Auth (para clientes de API)
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // PBKDF2 cuesta ~0,1 s a propósito: una credencial ya comprobada se recuerda unos
            // minutos para no pagarlo en cada petición. La clave incluye el hash guardado, así
            // que cambiar la contraseña invalida la caché.
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth + "\n" + config.AdminPasswordHash)));
            if (_basicCache.TryGetValue(cacheKey, out var until) && until > DateTime.UtcNow) return true;

            var ip = ctx.Connection.RemoteIpAddress;
            if (IsLockedOut(ip)) return false;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..]));
                var sep     = decoded.IndexOf(':');
                if (sep > 0)
                {
                    bool ok = VerifyPassword(config, decoded[..sep], decoded[(sep + 1)..]);
                    RecordAttempt(ip, ok);
                    if (ok)
                    {
                        if (_basicCache.Count > 100) _basicCache.Clear();
                        _basicCache[Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth + "\n" + config.AdminPasswordHash)))]
                            = DateTime.UtcNow + BasicCacheTime;
                    }
                    return ok;
                }
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
            Secure   = response.HttpContext.Request.IsHttps,
            Expires  = DateTimeOffset.UtcNow.AddDays(TokenDays),
        });
    }

    public static void ClearAuthCookie(HttpResponse response) =>
        response.Cookies.Delete(CookieName);

    // ── Configuración ─────────────────────────────────────────────────────────

    public static void SetCredentials(ServiceConfig config, string username, string password)
    {
        config.AdminUsername     = username;
        config.AdminPasswordHash = HashPassword(password);
        // Clave nueva: cualquier cookie emitida antes deja de valer.
        config.AuthSecret        = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        config.Save();
    }

    // ── Privado ───────────────────────────────────────────────────────────────

    private static string HmacSign(string data, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(data))).ToLower();
}
