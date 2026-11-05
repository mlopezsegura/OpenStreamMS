using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenStreamMS.Core.Helpers;

/// <summary>Derechos de cuenta de la directiva local (LSA): "Apagar el sistema", "Permitir inicio de sesión a través de Escritorio remoto"...</summary>
internal sealed class LsaPolicy : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory, ObjectName; public int Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct LSA_UNICODE_STRING { public ushort Length, MaximumLength; public string Buffer; }
    // Al leer: el búfer de LSA no tiene por qué acabar en nulo, se lee por su longitud.
    [StructLayout(LayoutKind.Sequential)] struct LSA_UNICODE_STRING_OUT { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attrs, int access, out IntPtr handle);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LSA_UNICODE_STRING[] rights, int count);
    [DllImport("advapi32.dll")] static extern uint LsaRemoveAccountRights(IntPtr policy, byte[] sid, bool all, LSA_UNICODE_STRING[] rights, int count);
    [DllImport("advapi32.dll")] static extern uint LsaEnumerateAccountRights(IntPtr policy, byte[] sid, out IntPtr rights, out int count);
    [DllImport("advapi32.dll")] static extern uint LsaFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);

    private const int POLICY_ALL_ACCESS = 0x000F0FFF;
    private const uint STATUS_OBJECT_NAME_NOT_FOUND = 0xC0000034;   // la cuenta no tiene ningún derecho
    private readonly IntPtr _handle;

    private LsaPolicy(IntPtr handle) => _handle = handle;

    public static LsaPolicy Open()
    {
        var attrs = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        Check(LsaOpenPolicy(IntPtr.Zero, ref attrs, POLICY_ALL_ACCESS, out var h), "LsaOpenPolicy");
        return new LsaPolicy(h);
    }

    private static byte[] Bytes(SecurityIdentifier sid) { var b = new byte[sid.BinaryLength]; sid.GetBinaryForm(b, 0); return b; }

    private static LSA_UNICODE_STRING[] Right(string name) =>
        [new LSA_UNICODE_STRING { Buffer = name, Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2 + 2) }];

    public bool Has(SecurityIdentifier sid, string right)
    {
        uint st = LsaEnumerateAccountRights(_handle, Bytes(sid), out var buf, out int count);
        if (st == STATUS_OBJECT_NAME_NOT_FOUND) return false;
        Check(st, "LsaEnumerateAccountRights");
        try
        {
            int size = Marshal.SizeOf<LSA_UNICODE_STRING_OUT>();
            for (int i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<LSA_UNICODE_STRING_OUT>(buf + i * size);
                if (string.Equals(Marshal.PtrToStringUni(s.Buffer, s.Length / 2), right, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { LsaFreeMemory(buf); }
    }

    public void Add(SecurityIdentifier sid, string right) =>
        Check(LsaAddAccountRights(_handle, Bytes(sid), Right(right), 1), "LsaAddAccountRights");

    public void Remove(SecurityIdentifier sid, string right) =>
        Check(LsaRemoveAccountRights(_handle, Bytes(sid), false, Right(right), 1), "LsaRemoveAccountRights");

    /// <summary>Quita todos los derechos de la cuenta (antes de borrarla, para no dejar SIDs huérfanos en la directiva).</summary>
    public void RemoveAll(SecurityIdentifier sid)
    {
        uint st = LsaRemoveAccountRights(_handle, Bytes(sid), true, [], 0);
        if (st != STATUS_OBJECT_NAME_NOT_FOUND) Check(st, "LsaRemoveAccountRights");
    }

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what}: error {LsaNtStatusToWinError(status)}");
    }

    public void Dispose() => LsaClose(_handle);
}
