using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// ¿Tiene el usuario de Windows de una sesión el login de Steam guardado?
///
/// Steam guarda el "Recordarme" en el perfil de cada usuario de Windows
/// (<c>%LOCALAPPDATA%\Steam\local.vdf</c>, bloque <c>ConnectCache</c>, cifrado con DPAPI).
/// Un usuario dedicado nuevo no lo tiene: Steam arranca en la ventana de "Iniciar sesión"
/// y "Steam Big Picture" no llega a abrirse. Solo se mira si el bloque tiene alguna
/// entrada; el contenido (el token) no se lee ni se expone.
/// </summary>
internal static partial class SteamLoginProbe
{
    internal const string Saved   = "saved";
    internal const string Missing = "missing";

    static readonly ConcurrentDictionary<string, (DateTime At, string? State)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);

    [GeneratedRegex("\"ConnectCache\"\\s*\\{\\s*\"", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectCacheWithEntry();

    /// <summary>
    /// <see cref="Saved"/>, <see cref="Missing"/> o null (Steam no instalado o no se sabe).
    /// <paramref name="accountName"/>: "usuario" (local) o "DOMINIO\usuario".
    /// </summary>
    internal static string? GetState(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        if (Cache.TryGetValue(accountName, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            return hit.State;

        var state = Probe(accountName);
        Cache[accountName] = (DateTime.UtcNow, state);
        return state;
    }

    static string? Probe(string accountName)
    {
        try
        {
            if (!SteamInstalled()) return null;

            // Sin perfil el usuario no ha entrado nunca en Windows: tampoco en Steam.
            var profile = IsolatedUserManager.GetProfilePath(accountName);
            if (profile is null) return Missing;

            // Sin File.Exists: da false también sin permiso de lectura, y eso no es "sin login".
            var vdf = Path.Combine(profile, "AppData", "Local", "Steam", "local.vdf");
            return ConnectCacheWithEntry().IsMatch(File.ReadAllText(vdf)) ? Saved : Missing;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing;
        }
        catch
        {
            return null;   // sin permiso o bloqueado: no se sabe
        }
    }

    static bool SteamInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        return key?.GetValue("InstallPath") is string dir && File.Exists(Path.Combine(dir, "steam.exe"));
    }
}
