using OpenStreamMS.Services;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Configura un perfil de AppData independiente para la sesión de stream,
/// permitiendo que las mismas apps (Steam, Chrome, etc.) corran en paralelo
/// en la sesión local y en la sesión de stream sin conflictos de lock files.
/// </summary>
internal static class StreamProfileSetup
{
    internal static readonly string[] ManagedEnvironmentKeys =
    {
        "USERPROFILE",
        "HOME",
        "HOMEDRIVE",
        "HOMEPATH",
        "LOCALAPPDATA",
        "APPDATA",
        "LOCALAPPDATALOW",
        "TEMP",
        "TMP",
        "XDG_DATA_HOME",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME",
        "OPENSTREAMMS_STREAM_PROFILE",
        "OPENSTREAMMS_STREAM_PROFILE_ROOT",
    };

    // Unicode a la fuerza: sin CharSet, .NET enlaza WTSEnumerateProcessesA y pProcessName llega
    // en ANSI; leído con PtrToStringUni no coincide con ningún nombre (Explorer nunca se cerraba).
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSEnumerateProcesses(
        IntPtr hServer, int Reserved, int Version,
        out IntPtr ppProcessInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTS_PROCESS_INFO
    {
        public int    SessionId;
        public int    ProcessId;
        public IntPtr pProcessName;
        public IntPtr pUserSid;
    }

    const uint PROCESS_TERMINATE = 0x0001;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Lee el entorno base de la sesión, crea un perfil aislado para stream
    /// y devuelve el diccionario de overrides listo para pasarlo a LaunchInSession.
    /// </summary>
    internal static Dictionary<string, string> BuildOverrides(uint sessionId)
    {
        var sessionVars = new ProcessInSession().GetSessionEnvVars(sessionId);

        var localAppData = sessionVars.GetValueOrDefault("LOCALAPPDATA", "");
        var appData      = sessionVars.GetValueOrDefault("APPDATA",      "");

        if (string.IsNullOrEmpty(localAppData) || string.IsNullOrEmpty(appData))
            throw new InvalidOperationException(
                "[StreamProfile] No se pudieron leer las rutas de perfil de la sesión.");

        var userProfile    = ResolveUserProfile(sessionVars, localAppData);
        var streamProfile  = Path.Combine(userProfile, "_OpenStreamMS_Stream");
        var streamLocal    = Path.Combine(streamProfile, "AppData", "Local");
        var streamRoaming  = Path.Combine(streamProfile, "AppData", "Roaming");
        var streamLocalLow = Path.Combine(streamProfile, "AppData", "LocalLow");
        var streamTemp     = Path.Combine(streamLocal, "Temp");
        var xdgRoot        = Path.Combine(streamLocal, "Xdg");

        Directory.CreateDirectory(streamProfile);
        Directory.CreateDirectory(streamLocal);
        Directory.CreateDirectory(streamRoaming);
        Directory.CreateDirectory(streamLocalLow);
        Directory.CreateDirectory(streamTemp);
        Directory.CreateDirectory(Path.Combine(xdgRoot, "data"));
        Directory.CreateDirectory(Path.Combine(xdgRoot, "config"));
        Directory.CreateDirectory(Path.Combine(xdgRoot, "cache"));
        Directory.CreateDirectory(Path.Combine(xdgRoot, "state"));

        foreach (var knownFolder in new[]
        {
            "Desktop",
            "Documents",
            "Downloads",
            "Music",
            "Pictures",
            "Saved Games",
            "Videos",
        })
        {
            Directory.CreateDirectory(Path.Combine(streamProfile, knownFolder));
        }

        var (homeDrive, homePath) = SplitHomePath(streamProfile);

        Logger.Log($"[StreamProfile] Sandbox de perfil independiente: {streamProfile}");
        Logger.Log($"[StreamProfile] AppData aislado: local={streamLocal}; roaming={streamRoaming}");

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = streamProfile,
            ["HOME"] = streamProfile,
            ["LOCALAPPDATA"] = streamLocal,
            ["APPDATA"] = streamRoaming,
            ["LOCALAPPDATALOW"] = streamLocalLow,
            ["TEMP"] = streamTemp,
            ["TMP"] = streamTemp,
            ["XDG_DATA_HOME"] = Path.Combine(xdgRoot, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(xdgRoot, "config"),
            ["XDG_CACHE_HOME"] = Path.Combine(xdgRoot, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(xdgRoot, "state"),
            ["OPENSTREAMMS_STREAM_PROFILE"] = "1",
            ["OPENSTREAMMS_STREAM_PROFILE_ROOT"] = streamProfile,
        };

        if (!string.IsNullOrWhiteSpace(homeDrive))
            overrides["HOMEDRIVE"] = homeDrive;
        if (!string.IsNullOrWhiteSpace(homePath))
            overrides["HOMEPATH"] = homePath;

        return overrides;
    }

    /// <summary>
    /// Mata Explorer en la sesión y lo relanza con el entorno redirigido,
    /// para que todas las apps que el usuario abra desde la sesión hereden el perfil de stream.
    /// </summary>
    internal static void RestartExplorer(uint sessionId, Dictionary<string, string> overrides)
    {
        var killed = KillInSession(sessionId, "explorer.exe");
        if (killed.Count == 0)
            Logger.Warning($"[StreamProfile] No había Explorer que reiniciar en la sesión {sessionId}.");
        // El nuevo Explorer solo hereda el entorno si es el shell: si el viejo sigue vivo, se le
        // pasa el control y sale, y las apps de la sesión comparten perfil con la consola.
        foreach (var pid in killed)
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.WaitForExit(3000); } catch { /* ya salió */ }

        // explorer.exe está en %windir%, no en System32
        var explorerPath = Path.Combine(
            Path.GetDirectoryName(Environment.SystemDirectory)!, "explorer.exe");

        new ProcessInSession().LaunchInSession(sessionId, explorerPath, arguments: null, envOverrides: overrides);
        Logger.Log($"[StreamProfile] Explorer reiniciado en sesión {sessionId} con perfil de stream.");
    }

    // ── Logoff ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cierra la sesión Windows con el logoff de Windows y espera a que desaparezca. Si una
    /// aplicación la retiene, termina solo los procesos del propio usuario (nunca dwm, fontdrvhost,
    /// LogonUI ni los de sistema, que pertenecen a otras cuentas) y vuelve a pedir el logoff.
    /// True si la sesión ya no existe.
    /// </summary>
    /// <remarks>
    /// Antes se mataban a mano todos los procesos de la sesión a la vez que se cortaba el cliente
    /// RDP: la sesión se quedaba a medio desconectar (evento 40 sin 24), TermService se bloqueaba y
    /// el equipo ya no podía apagarse ni reiniciarse.
    /// </remarks>
    internal static bool LogoffSession(uint sessionId)
    {
        if (sessionId == 0) return false;

        var account = RdpSessionCreator.GetSessionAccount(sessionId);
        if (RdpSessionCreator.LogoffAndWait(sessionId, TimeSpan.FromSeconds(30))) return true;

        Logger.Warning($"[StreamProfile] La sesión {sessionId} no se cerró en 30 s; se terminan los procesos de " +
                       $"'{account}' y se reintenta el logoff.");
        int killed = account is null ? 0 : KillUserProcesses(sessionId, account);
        if (RdpSessionCreator.LogoffAndWait(sessionId, TimeSpan.FromSeconds(15)))
        {
            Logger.Log($"[StreamProfile] Sesión {sessionId} cerrada tras terminar {killed} proceso(s) del usuario.");
            return true;
        }
        Logger.Error($"[StreamProfile] La sesión {sessionId} sigue abierta tras el logoff.");
        return false;
    }

    /// <summary>Procesos que nunca se terminan aunque, por lo que sea, aparezcan con el usuario.</summary>
    static readonly HashSet<string> SessionInfrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "winlogon.exe", "wininit.exe", "smss.exe", "dwm.exe",
        "fontdrvhost.exe", "LogonUI.exe", "WUDFHost.exe", "rdpclip.exe",
    };

    /// <summary>Termina los procesos de la sesión cuyo dueño es esa cuenta. Devuelve cuántos.</summary>
    static int KillUserProcesses(uint sessionId, string account)
    {
        SecurityIdentifier userSid;
        try { userSid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier)); }
        catch (Exception ex)
        {
            Logger.Warning($"[StreamProfile] No se pudo resolver el SID de '{account}': {ex.Message}");
            return 0;
        }

        if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out IntPtr pProcs, out int count))
        {
            Logger.Warning($"[StreamProfile] WTSEnumerateProcesses falló ({Marshal.GetLastWin32Error()})");
            return 0;
        }

        int    size = Marshal.SizeOf<WTS_PROCESS_INFO>();
        IntPtr cur  = pProcs;
        var    pids = new List<int>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_PROCESS_INFO>(cur);
                cur += size;
                if (info.SessionId != (int)sessionId || info.ProcessId == 0 || info.pUserSid == IntPtr.Zero) continue;
                if (SessionInfrastructure.Contains(Marshal.PtrToStringUni(info.pProcessName) ?? "")) continue;
                if (new SecurityIdentifier(info.pUserSid) != userSid) continue;
                pids.Add(info.ProcessId);
            }
        }
        finally { WTSFreeMemory(pProcs); }

        int killed = 0;
        foreach (var pid in pids)
        {
            IntPtr hProc = OpenProcess(PROCESS_TERMINATE, false, pid);
            if (hProc == IntPtr.Zero) continue;
            if (TerminateProcess(hProc, 1)) killed++;
            CloseHandle(hProc);
        }
        return killed;
    }

    // ── Privados ──────────────────────────────────────────────────────────────

    /// <summary>Termina los procesos con ese nombre en la sesión y devuelve sus PID.</summary>
    static List<int> KillInSession(uint sessionId, string processName)
    {
        var killed = new List<int>();
        if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out IntPtr pProcs, out int count))
        {
            Logger.Warning($"[StreamProfile] WTSEnumerateProcesses falló ({Marshal.GetLastWin32Error()})");
            return killed;
        }

        int    size = Marshal.SizeOf<WTS_PROCESS_INFO>();
        IntPtr cur  = pProcs;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_PROCESS_INFO>(cur);
                cur += size;

                if (info.SessionId != (int)sessionId) continue;

                var name = Marshal.PtrToStringUni(info.pProcessName) ?? "";
                if (!name.Equals(processName, StringComparison.OrdinalIgnoreCase)) continue;

                IntPtr hProc = OpenProcess(PROCESS_TERMINATE, false, info.ProcessId);
                if (hProc == IntPtr.Zero)
                {
                    Logger.Warning($"[StreamProfile] No se pudo abrir '{name}' (PID {info.ProcessId}): {Marshal.GetLastWin32Error()}");
                    continue;
                }
                if (TerminateProcess(hProc, 1)) killed.Add(info.ProcessId);
                else Logger.Warning($"[StreamProfile] No se pudo terminar '{name}' (PID {info.ProcessId}): {Marshal.GetLastWin32Error()}");
                CloseHandle(hProc);
                Logger.Log($"[StreamProfile] '{name}' (PID {info.ProcessId}) terminado en sesión {sessionId}.");
            }
        }
        finally { WTSFreeMemory(pProcs); }
        return killed;
    }

    static string ResolveUserProfile(Dictionary<string, string> sessionVars, string localAppData)
    {
        if (sessionVars.TryGetValue("USERPROFILE", out var userProfile) &&
            !string.IsNullOrWhiteSpace(userProfile))
            return userProfile;

        var appDataDir = Directory.GetParent(localAppData);
        var profileDir = appDataDir?.Parent;
        return profileDir?.FullName ?? localAppData;
    }

    static (string Drive, string PathPart) SplitHomePath(string profilePath)
    {
        var root = Path.GetPathRoot(profilePath);
        if (string.IsNullOrWhiteSpace(root))
            return ("", profilePath);

        var drive = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, profilePath);
        var homePath = relative == "."
            ? Path.DirectorySeparatorChar.ToString()
            : Path.DirectorySeparatorChar + relative;

        return (drive, homePath);
    }
}
