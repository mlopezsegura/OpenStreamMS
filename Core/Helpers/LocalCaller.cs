using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OpenStreamMS.Core.Helpers
{
    /// <summary>
    /// Identifica el proceso local al otro lado de una conexión (TCP de loopback o tubería) y su
    /// sesión de Windows. En este equipo conviven la consola, las sesiones de streaming y los
    /// puestos, cada una con un usuario distinto: "viene de este PC" no basta para confiar.
    /// </summary>
    public static class LocalCaller
    {
        [DllImport("kernel32.dll")]
        static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerSessionId(SafeHandle pipe, out uint serverSessionId);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort,
                                               int af, int tableClass, uint reserved);

        const int AF_INET  = 2;
        const int AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        /// <summary>Sesión de un proceso, o null si ya no existe.</summary>
        public static uint? SessionOf(int pid) =>
            pid > 0 && ProcessIdToSessionId((uint)pid, out var sid) ? sid : null;

        /// <summary>true si el proceso corre en la sesión de la consola física.</summary>
        public static bool IsInConsoleSession(int pid)
        {
            var console = WTSGetActiveConsoleSessionId();
            return console != 0xFFFFFFFF && SessionOf(pid) == console;
        }

        public static int PipeClientPid(NamedPipeServerStream pipe) =>
            GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : 0;

        public static int PipeServerPid(NamedPipeClientStream pipe) =>
            GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : 0;

        /// <summary>
        /// Sesión del servidor de la tubería. Un usuario normal no puede abrir el proceso del
        /// servicio (SYSTEM) para preguntarle la sesión, pero la tubería sí la dice.
        /// </summary>
        public static uint? PipeServerSession(NamedPipeClientStream pipe) =>
            GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var sid) ? sid : null;

        /// <summary>
        /// PID del proceso local que abrió la conexión TCP <paramref name="remote"/> → <paramref name="local"/>
        /// (vistas desde el servidor). 0 si no es de loopback o no aparece en la tabla.
        /// </summary>
        public static int TcpClientPid(IPAddress? remote, int remotePort, int localPort)
        {
            if (remote is null || !IPAddress.IsLoopback(remote)) return 0;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            return remote.AddressFamily == AddressFamily.InterNetwork
                ? FindOwner(AF_INET, rowSize: 24, portsAt: (8, 16), pidAt: 20, remotePort, localPort)
                : FindOwner(AF_INET6, rowSize: 56, portsAt: (20, 44), pidAt: 52, remotePort, localPort);
        }

        /// <summary>
        /// Busca en la tabla TCP la fila del cliente: su puerto local es el remoto del servidor y
        /// viceversa. Los puertos van en orden de red en los 2 bytes bajos del DWORD.
        /// </summary>
        static int FindOwner(int af, int rowSize, (int Local, int Remote) portsAt, int pidAt,
                             int clientPort, int serverPort)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    uint rc = GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                    if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
                    if (rc != 0) return 0;

                    int rows = Marshal.ReadInt32(buf);
                    for (int i = 0; i < rows; i++)
                    {
                        var row = buf + 4 + i * rowSize;
                        if (Port(row + portsAt.Local) == clientPort && Port(row + portsAt.Remote) == serverPort)
                            return Marshal.ReadInt32(row + pidAt);
                    }
                    return 0;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return 0;
        }

        static int Port(IntPtr p) => (Marshal.ReadByte(p) << 8) | Marshal.ReadByte(p + 1);
    }
}
