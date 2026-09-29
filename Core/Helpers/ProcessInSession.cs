using OpenStreamMS.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Core.Helpers
{
    public class ProcessInSession
    {
        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQueryUserToken(uint sessionId, out IntPtr Token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string? lpApplicationName,
            string? lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        const uint WAIT_TIMEOUT               = 0x00000102;
        const uint CREATE_NO_WINDOW           = 0x08000000;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        const int  STARTF_USESHOWWINDOW       = 0x00000001;
        const short SW_HIDE                   = 0;
        const uint TOKEN_ADJUST_PRIVILEGES    = 0x0020;
        const uint TOKEN_QUERY                = 0x0008;
        const uint SE_PRIVILEGE_ENABLED       = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        static void EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
                return;
            try
            {
                if (!LookupPrivilegeValue(null, name, out LUID luid)) return;
                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges     = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
                };
                AdjustTokenPrivileges(hToken, false, ref tp, (uint)Marshal.SizeOf(tp), IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(hToken); }
        }

        /// <summary>
        /// Lanza <paramref name="exe"/> en la sesión indicada y devuelve el PID del proceso creado.
        /// Si <paramref name="arguments"/> no es null, se pasa como línea de comandos (formato Win32:
        /// ya quoteado por el llamador). Si <paramref name="envOverrides"/> no es null, sobreescribe
        /// esas variables de entorno en el bloque del usuario antes de crear el proceso.
        /// </summary>
        public int LaunchInSession(uint sessionId, string exe,
                                   string? arguments = null,
                                   Dictionary<string, string>? envOverrides = null,
                                   bool waitForExit = false,
                                   uint waitTimeoutMs = 30_000,
                                   uint priorityClass = 0)
        {
            EnablePrivilege("SeTcbPrivilege");
            EnablePrivilege("SeAssignPrimaryTokenPrivilege");
            EnablePrivilege("SeIncreaseQuotaPrivilege");

            if (!WTSQueryUserToken(sessionId, out IntPtr token))
                throw new Exception($"WTSQueryUserToken falló para sesión {sessionId}: error {Marshal.GetLastWin32Error()}");

            IntPtr envBlock     = IntPtr.Zero;
            bool   customBlock  = false;
            try
            {
                if (!CreateEnvironmentBlock(out envBlock, token, false))
                {
                    envBlock = IntPtr.Zero;
                    Logger.Warning($"[ProcessInSession] CreateEnvironmentBlock falló (error {Marshal.GetLastWin32Error()}), usando entorno vacío");
                }

                if (envOverrides is { Count: > 0 } && envBlock != IntPtr.Zero)
                {
                    var vars = ParseEnvBlock(envBlock);
                    DestroyEnvironmentBlock(envBlock);
                    envBlock = IntPtr.Zero;
                    foreach (var (k, v) in envOverrides)
                        vars[k] = v;
                    envBlock    = BuildEnvBlock(vars);
                    customBlock = true;
                }

                var si = new STARTUPINFO
                {
                    cb          = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop   = "winsta0\\default",
                    dwFlags     = STARTF_USESHOWWINDOW,
                    wShowWindow = SW_HIDE
                };

                uint flags = CREATE_NO_WINDOW | priorityClass;
                if (envBlock != IntPtr.Zero)
                    flags |= CREATE_UNICODE_ENVIRONMENT;

                // GetDirectoryName devuelve "" (NO null) para nombres sin ruta como
                // "powershell.exe", y CreateProcessAsUser con workDir vacío falla con
                // ERROR_INVALID_NAME (123). Tratamos vacío y null igual.
                var dir = Path.GetDirectoryName(exe);
                string workDir = string.IsNullOrEmpty(dir) ? Environment.SystemDirectory : dir;

                // CreateProcessAsUserW puede modificar lpCommandLine in-place, así que nunca pasamos
                // una literal. Construimos "<exe quoted> <args>" o null si no hay args.
                string? cmdLine = arguments is null
                    ? null
                    : $"\"{exe}\" {arguments}";

                if (!CreateProcessAsUser(
                        token, exe, cmdLine,
                        IntPtr.Zero, IntPtr.Zero, false,
                        flags, envBlock, workDir,
                        ref si, out PROCESS_INFORMATION pi))
                    throw new Exception($"CreateProcessAsUser falló para '{exe}': error {Marshal.GetLastWin32Error()}");

                CloseHandle(pi.hThread);

                if (waitForExit)
                {
                    var result = WaitForSingleObject(pi.hProcess, waitTimeoutMs);
                    if (result == WAIT_TIMEOUT)
                        Logger.Warning($"[ProcessInSession] WaitForSingleObject timeout ({waitTimeoutMs}ms) para '{exe}'");
                }

                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally
            {
                if (envBlock != IntPtr.Zero)
                {
                    if (customBlock) Marshal.FreeHGlobal(envBlock);
                    else             DestroyEnvironmentBlock(envBlock);
                }
                CloseHandle(token);
            }
        }

        /// <summary>
        /// Devuelve un diccionario con las variables de entorno del usuario de la sesión indicada.
        /// </summary>
        public Dictionary<string, string> GetSessionEnvVars(uint sessionId)
        {
            EnablePrivilege("SeTcbPrivilege");
            if (!WTSQueryUserToken(sessionId, out IntPtr token))
                return new();
            try
            {
                if (!CreateEnvironmentBlock(out IntPtr envBlock, token, false))
                    return new();
                try   { return ParseEnvBlock(envBlock); }
                finally { DestroyEnvironmentBlock(envBlock); }
            }
            finally { CloseHandle(token); }
        }

        // ── Env block helpers ─────────────────────────────────────────────────

        static Dictionary<string, string> ParseEnvBlock(IntPtr block)
        {
            var vars   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int offset = 0;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(IntPtr.Add(block, offset * 2));
                if (string.IsNullOrEmpty(entry)) break;
                var eq = entry.IndexOf('=');
                if (eq > 0) vars[entry[..eq]] = entry[(eq + 1)..];
                offset += entry.Length + 1;
            }
            return vars;
        }

        static IntPtr BuildEnvBlock(Dictionary<string, string> vars)
        {
            var sb = new StringBuilder();
            foreach (var (k, v) in vars.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(k).Append('=').Append(v).Append('\0');
            sb.Append('\0');

            string  s     = sb.ToString();
            IntPtr  block = Marshal.AllocHGlobal(s.Length * 2);
            for (int i = 0; i < s.Length; i++)
                Marshal.WriteInt16(block, i * 2, s[i]);
            return block;
        }
    }
}
