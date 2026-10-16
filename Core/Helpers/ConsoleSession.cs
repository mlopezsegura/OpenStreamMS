using System.Runtime.InteropServices;

namespace OpenStreamMS.Core.Helpers
{
    /// <summary>
    /// Sesión de Windows de la consola física: el escritorio local de este PC.
    /// </summary>
    public static class ConsoleSession
    {
        [DllImport("kernel32.dll")]
        static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "WTSQuerySessionInformationW")]
        static extern bool WTSQuerySessionInformation(
            IntPtr hServer, uint sessionId, int wtsInfoClass,
            out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr pMemory);

        const int WTSUserName   = 5;
        const int WTSDomainName = 7;

        /// <summary>
        /// Sesión de la consola y su usuario. false si nadie tiene sesión iniciada en el PC
        /// (pantalla de inicio de sesión o cambio de usuario en curso).
        /// </summary>
        public static bool TryGetUser(out uint sessionId, out string domain, out string username)
        {
            sessionId = WTSGetActiveConsoleSessionId();
            domain    = "";
            username  = "";
            if (sessionId == 0xFFFFFFFF) return false;

            username = Query(sessionId, WTSUserName);
            domain   = Query(sessionId, WTSDomainName);
            return !string.IsNullOrEmpty(username);
        }

        static string Query(uint sessionId, int infoClass)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out IntPtr buffer, out _))
                return "";
            try { return Marshal.PtrToStringUni(buffer) ?? ""; }
            finally { WTSFreeMemory(buffer); }
        }
    }
}
