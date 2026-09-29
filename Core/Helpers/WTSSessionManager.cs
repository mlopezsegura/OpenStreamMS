using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Core.Helpers
{
    public class SessionManager
    {
        [DllImport("wtsapi32.dll")]
        static extern bool WTSEnumerateSessions(
            IntPtr hServer,
            int Reserved,
            int Version,
            out IntPtr ppSessionInfo,
            out int pCount);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr pMemory);

        [StructLayout(LayoutKind.Sequential)]
        public struct WTS_SESSION_INFO
        {
            public int SessionId;
            public string pWinStationName;
            public int State;
        }

        public List<int> GetActiveSessions()
        {
            IntPtr pSessions;
            int count;

            WTSEnumerateSessions(IntPtr.Zero, 0, 1, out pSessions, out count);

            List<int> sessions = new List<int>();

            IntPtr current = pSessions;
            int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));

            for (int i = 0; i < count; i++)
            {
                WTS_SESSION_INFO si = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);
                sessions.Add(si.SessionId);
                current += size;
            }

            WTSFreeMemory(pSessions);
            return sessions;
        }
    }
}
