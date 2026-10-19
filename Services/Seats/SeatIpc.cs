using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenStreamMS.Services.Seats;

/// <summary>Tipos de mensaje entre servicio, anfitrión (consola) y agentes (sesiones).</summary>
internal enum SeatMsg : byte
{
    Json        = 1,   // control: objeto JSON con "op"
    Frame       = 2,   // agente → anfitrión: [int índice][long qpc]
    Audio       = 3,   // agente → anfitrión: PCM en el formato anunciado
    Input       = 4,   // anfitrión → agente: SeatInputEvent
    CursorShape = 5,   // agente → anfitrión: [int tipo][int w][int h][int pitch][int hotX][int hotY][bytes]
    CursorPos   = 6,   // agente → anfitrión: [int x][int y][int visible]
}

/// <summary>Evento de entrada de un teclado o ratón físico (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SeatInputEvent
{
    public const byte Mouse = 1, Keyboard = 2;

    public byte   Kind;
    public byte   Reserved;
    /// <summary>Ratón: botones RI_MOUSE_*. Teclado: flags RI_KEY_* (BREAK, E0, E1).</summary>
    public ushort Flags;
    /// <summary>Ratón: rueda (ya en unidades WHEEL_DELTA).</summary>
    public short  Data;
    /// <summary>Teclado: scancode (MakeCode).</summary>
    public ushort Code;
    public int    Dx;
    public int    Dy;

    public static readonly int Size = Marshal.SizeOf<SeatInputEvent>();
}

/// <summary>
/// Canal de mensajes sobre una tubería: <c>[int32 longitud][byte tipo][datos]</c>.
/// Las escrituras se serializan con un cerrojo; las lecturas, desde un solo hilo.
/// </summary>
internal sealed class SeatChannel(Stream stream) : IDisposable
{
    public const int MaxMessage = 64 * 1024 * 1024;
    private readonly object _write = new();
    private readonly byte[] _header = new byte[5];

    public Stream Stream => stream;

    public void Send(SeatMsg type, ReadOnlySpan<byte> payload)
    {
        Span<byte> hdr = stackalloc byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(hdr, payload.Length);
        hdr[4] = (byte)type;
        lock (_write)
        {
            stream.Write(hdr);
            stream.Write(payload);
            stream.Flush();
        }
    }

    /// <summary>Igual que <see cref="Send"/> pero con un prefijo (p. ej. el id del puesto).</summary>
    public void Send(SeatMsg type, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> payload)
    {
        Span<byte> hdr = stackalloc byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(hdr, prefix.Length + payload.Length);
        hdr[4] = (byte)type;
        lock (_write)
        {
            stream.Write(hdr);
            stream.Write(prefix);
            stream.Write(payload);
            stream.Flush();
        }
    }

    public void SendJson(JsonObject obj) => Send(SeatMsg.Json, JsonSerializer.SerializeToUtf8Bytes(obj));

    /// <summary>Bloquea hasta el siguiente mensaje; null si la tubería se cerró.</summary>
    public (SeatMsg Type, byte[] Data)? Receive()
    {
        if (!ReadExactly(_header)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(_header);
        if (len < 0 || len > MaxMessage) throw new InvalidDataException($"Mensaje de {len} bytes");
        var data = new byte[len];
        if (len > 0 && !ReadExactly(data)) return null;
        return ((SeatMsg)_header[4], data);
    }

    private bool ReadExactly(byte[] buf)
    {
        try
        {
            stream.ReadExactly(buf);
            return true;
        }
        catch (EndOfStreamException) { return false; }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    public static JsonObject ParseJson(byte[] data) =>
        JsonNode.Parse(data) as JsonObject ?? throw new InvalidDataException("JSON no es un objeto");

    public void Dispose() => stream.Dispose();
}

internal static class SeatPipes
{
    /// <summary>
    /// Tubería del servicio para un proceso de puesto: solo SYSTEM y el usuario indicado
    /// pueden abrirla. El nombre es aleatorio y se pasa por línea de comandos.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name, SecurityIdentifier? client = null)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(client ?? new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 1 << 20, 1 << 20, security);
    }

    public static string NewName(string kind) => $"OpenStreamMS.Seat.{kind}.{Guid.NewGuid():N}";

    /// <summary>
    /// Espera a que se conecte el proceso esperado. El nombre de la tubería no es secreto
    /// (cualquier usuario puede listar \\.\pipe\), así que otro usuario del equipo podría
    /// adelantarse: se mira el PID del cliente y, si no es el esperado, se le desconecta y se
    /// sigue esperando hasta que venza <paramref name="ct"/>. Devuelve el PID aceptado.
    /// </summary>
    public static int AcceptFrom(NamedPipeServerStream server, Func<int, bool> isExpected, string what, CancellationToken ct)
    {
        while (true)
        {
            server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
            int pid = OpenStreamMS.Core.Helpers.LocalCaller.PipeClientPid(server);
            if (pid > 0 && isExpected(pid)) return pid;

            Logger.Warning($"[Seats] Tubería del {what}: rechazado el proceso {pid}, no es el esperado.");
            server.Disconnect();
        }
    }

    /// <summary>
    /// Lado del cliente (anfitrión o agente). Asíncrona (overlapped) a la fuerza: con un handle
    /// síncrono Windows serializa la E/S, y una lectura bloqueada esperando entrada impediría
    /// escribir fotogramas/audio desde otro hilo (la imagen se congela y ambos lados se bloquean).
    /// </summary>
    public static SeatChannel Connect(string name, int timeoutMs = 15000)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(timeoutMs);
        RequireServiceServer(client);
        return new SeatChannel(client);
    }

    /// <summary>
    /// El otro extremo debe ser el servicio (sesión 0, donde no hay usuarios). Si otro usuario
    /// hubiera creado antes una tubería con ese nombre, podría mandar órdenes falsas al
    /// presentador de la consola (p. ej. qué teclados capturar) o al agente.
    /// </summary>
    public static void RequireServiceServer(NamedPipeClientStream client)
    {
        if (OpenStreamMS.Core.Helpers.LocalCaller.PipeServerSession(client) != 0)
        {
            int pid = OpenStreamMS.Core.Helpers.LocalCaller.PipeServerPid(client);
            client.Dispose();
            throw new UnauthorizedAccessException($"La tubería no es del servicio (proceso {pid}).");
        }
    }

    public static byte[] GuidBytes(Guid id) => id.ToByteArray();
}
