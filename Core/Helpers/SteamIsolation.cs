using Microsoft.Win32;
using OpenStreamMS.Services;
using System.Security.Principal;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Steam solo se aísla si pasa por <c>steam\osms-steam.exe</c>, que le inyecta
/// osms-steamhook.dll (sin el gancho, el steam.exe de una sesión aislada encuentra el Steam de
/// la consola, le pasa la orden y se cierra). Al arrancar una sesión aislada se dejan en su
/// perfil las dos entradas que la llevan por ahí desde el primer momento:
///   - el protocolo steam:// en el HKCU del usuario aislado, y
///   - "Steam (aislado)" en su escritorio y su menú Inicio.
/// El acceso directo común de Steam (menú Inicio de todos los usuarios) no se toca: abierto
/// desde ahí en una sesión aislada sigue sin gancho.
/// </summary>
internal static class SteamIsolation
{
    private const string ShortcutName = "Steam (aislado).lnk";

    private static string Launcher => Path.Combine(AppContext.BaseDirectory, "steam", "osms-steam.exe");

    public static void PrepareSession(uint rdpSessionId, string username, Guid sessionId)
    {
        if (!File.Exists(Launcher) || SteamExe() is not { } steamExe) return;
        try
        {
            RegisterProtocol(username);

            var env = new ProcessInSession().GetSessionEnvVars(rdpSessionId);
            var places = new List<string>();
            if (env.TryGetValue("USERPROFILE", out var profile) && profile.Length > 0)
                places.Add(Path.Combine(profile, "Desktop"));
            if (env.TryGetValue("APPDATA", out var appData) && appData.Length > 0)
                places.Add(Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs"));

            foreach (var dir in places)
            {
                Directory.CreateDirectory(dir);
                CreateShortcut(Path.Combine(dir, ShortcutName), steamExe);
            }
            Logger.Log($"[SteamIsolation] Steam preparado para '{username}': steam:// y {places.Count} acceso(s) directo(s) por osms-steam.exe.", sessionId);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SteamIsolation] No se pudo preparar Steam para '{username}': {ex.Message}", sessionId);
        }
    }

    /// <summary>steam:// del usuario (HKU\&lt;SID&gt;; su hive está cargado mientras tiene sesión).</summary>
    private static void RegisterProtocol(string username)
    {
        var sid = new NTAccount(username).Translate(typeof(SecurityIdentifier)).Value;
        using var hive = Registry.Users.OpenSubKey(sid, writable: true)
            ?? throw new InvalidOperationException("el registro del usuario no está cargado");
        using (var k = hive.CreateSubKey(@"Software\Classes\steam"))
        {
            k.SetValue("", "URL:steam protocol");
            k.SetValue("URL Protocol", "");
        }
        using (var k = hive.CreateSubKey(@"Software\Classes\steam\Shell\Open\Command"))
            k.SetValue("", $"\"{Launcher}\" -- \"%1\"");   // mismo formato que RegisterSteamProtocol del lanzador
    }

    private static void CreateShortcut(string path, string steamExe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell no disponible");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = Launcher;
            link.WorkingDirectory = Path.GetDirectoryName(Launcher);
            link.IconLocation = $"{steamExe},0";
            link.Description = "Steam aislado de las demás sesiones (OpenStreamMS)";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static string? SteamExe()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                     ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
        var exe = key?.GetValue("InstallPath") is string dir ? Path.Combine(dir, "steam.exe") : null;
        return exe is not null && File.Exists(exe) ? exe : null;
    }
}
