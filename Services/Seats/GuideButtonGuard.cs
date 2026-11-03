using Microsoft.Win32;
using OpenStreamMS.Core.Helpers;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// El botón Guía de un mando no pasa por HidHide: Windows lo reparte a la Xbox Game Bar de
/// todas las sesiones, así que un puesto abría la Game Bar en todos los demás y en la consola.
/// Mientras haya puestos encendidos se apaga "Abrir la Game Bar con el botón del mando"
/// (<c>UseNexusForGameBarEnabled = 0</c>) a los usuarios de sus sesiones y al de la consola, y
/// al apagar el último que lo necesita se deja como estaba. El valor anterior se guarda en el
/// propio registro del usuario, así que sobrevive a un reinicio del servicio.
/// </summary>
internal static class GuideButtonGuard
{
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string NexusValue = "UseNexusForGameBarEnabled";
    private const string SavedKey   = @"Software\OpenStreamMS\SeatGuide";
    private const string SavedValue = "PrevUseNexusForGameBarEnabled";
    private const int Absent = -1;   // no estaba el valor: al restaurar se borra

    private static readonly object _lock = new();
    /// <summary>SID → cuántos puestos encendidos lo necesitan (su sesión, o la consola).</summary>
    private static readonly Dictionary<string, int> _users = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Usuarios con los que un puesto encendido cuenta: el de su sesión y el de la consola.</summary>
    public static IReadOnlyList<string> UsersFor(uint seatWindowsSession)
    {
        var sids = new List<string>();
        uint console = ConsoleSession.TryGetUser(out var c, out _, out _) ? c : 0;
        foreach (var s in new[] { seatWindowsSession, console })
            if (s != 0 && s != uint.MaxValue && SidOfSession(s) is { } sid && !sids.Contains(sid, StringComparer.OrdinalIgnoreCase))
                sids.Add(sid);
        return sids;
    }

    public static void Engage(IReadOnlyList<string> sids)
    {
        lock (_lock)
            foreach (var sid in sids)
            {
                _users[sid] = _users.GetValueOrDefault(sid) + 1;
                if (_users[sid] == 1) Disable(sid);
            }
    }

    public static void Release(IReadOnlyList<string> sids)
    {
        lock (_lock)
            foreach (var sid in sids)
            {
                if (!_users.TryGetValue(sid, out int n)) continue;
                if (n > 1) { _users[sid] = n - 1; continue; }
                _users.Remove(sid);
                Restore(sid);
            }
    }

    private static void Disable(string sid)
    {
        try
        {
            using var gameBar = Registry.Users.CreateSubKey($@"{sid}\{GameBarKey}", writable: true);
            using var saved = Registry.Users.CreateSubKey($@"{sid}\{SavedKey}", writable: true);
            if (gameBar is null || saved is null) return;   // registro del usuario no cargado
            // Si ya hay un valor guardado (el servicio se cerró con puestos encendidos), ese es el bueno.
            if (saved.GetValue(SavedValue) is null)
                saved.SetValue(SavedValue, gameBar.GetValue(NexusValue) is int prev ? prev : Absent, RegistryValueKind.DWord);
            gameBar.SetValue(NexusValue, 0, RegistryValueKind.DWord);
            Logger.Log($"[Seats] Botón Guía: Game Bar del mando desactivada para {sid} mientras haya puestos.");
        }
        catch (Exception ex) { Logger.Warning($"[Seats] Botón Guía: no se pudo desactivar la Game Bar del mando para {sid}: {ex.Message}"); }
    }

    private static void Restore(string sid)
    {
        try
        {
            using var saved = Registry.Users.OpenSubKey($@"{sid}\{SavedKey}", writable: true);
            if (saved?.GetValue(SavedValue) is not int prev) return;   // no lo cambiamos nosotros, o hive no cargado
            using (var gameBar = Registry.Users.CreateSubKey($@"{sid}\{GameBarKey}", writable: true))
            {
                if (prev == Absent) gameBar?.DeleteValue(NexusValue, throwOnMissingValue: false);
                else gameBar?.SetValue(NexusValue, prev, RegistryValueKind.DWord);
            }
            saved.DeleteValue(SavedValue, throwOnMissingValue: false);
            Logger.Log($"[Seats] Botón Guía: Game Bar del mando restaurada para {sid}.");
        }
        catch (Exception ex) { Logger.Warning($"[Seats] Botón Guía: no se pudo restaurar la Game Bar del mando para {sid}: {ex.Message}"); }
    }

    // ── Usuario de una sesión de Windows ──

    private static string? SidOfSession(uint sessionId)
    {
        try
        {
            string? user = Query(sessionId, WTSUserName), domain = Query(sessionId, WTSDomainName);
            if (string.IsNullOrEmpty(user)) return null;
            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch { return null; }
    }

    private const int WTSUserName = 5, WTSDomainName = 7;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static string? Query(uint sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }
}
