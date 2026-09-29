using OpenStreamMS.Services;
using System.Runtime.InteropServices;

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

    [DllImport("wtsapi32.dll", SetLastError = true)]
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
        KillInSession(sessionId, "explorer.exe");
        Thread.Sleep(400);

        // explorer.exe está en %windir%, no en System32
        var explorerPath = Path.Combine(
            Path.GetDirectoryName(Environment.SystemDirectory)!, "explorer.exe");

        new ProcessInSession().LaunchInSession(sessionId, explorerPath, arguments: null, envOverrides: overrides);
        Logger.Log($"[StreamProfile] Explorer reiniciado en sesión {sessionId} con perfil de stream.");
    }

    // ── Logoff ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Termina todos los procesos del usuario en la sesión indicada,
    /// forzando el cierre de la sesión Windows de forma fiable.
    /// </summary>
    internal static void LogoffSession(uint sessionId)
    {
        if (sessionId == 0) return;

        if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out IntPtr pProcs, out int count))
        {
            Logger.Warning($"[StreamProfile] LogoffSession: WTSEnumerateProcesses falló ({Marshal.GetLastWin32Error()})");
            return;
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

                if (info.SessionId != (int)sessionId) continue;
                if (info.ProcessId == 0) continue;

                // No tocar procesos críticos del sistema
                var name = Marshal.PtrToStringUni(info.pProcessName) ?? "";
                if (name.Equals("csrss.exe",    StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("winlogon.exe",  StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("wininit.exe",   StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("smss.exe",      StringComparison.OrdinalIgnoreCase))
                    continue;

                pids.Add(info.ProcessId);
            }
        }
        finally { WTSFreeMemory(pProcs); }

        foreach (var pid in pids)
        {
            IntPtr hProc = OpenProcess(PROCESS_TERMINATE, false, pid);
            if (hProc == IntPtr.Zero) continue;
            TerminateProcess(hProc, 1);
            CloseHandle(hProc);
        }

        Logger.Log($"[StreamProfile] Sesión {sessionId} cerrada ({pids.Count} procesos terminados).");
    }

    // ── Privados ──────────────────────────────────────────────────────────────

    static void KillInSession(uint sessionId, string processName)
    {
        if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out IntPtr pProcs, out int count))
            return;

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
                if (hProc != IntPtr.Zero)
                {
                    TerminateProcess(hProc, 1);
                    CloseHandle(hProc);
                    Logger.Log($"[StreamProfile] '{name}' (PID {info.ProcessId}) terminado en sesión {sessionId}.");
                }
            }
        }
        finally { WTSFreeMemory(pProcs); }
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
