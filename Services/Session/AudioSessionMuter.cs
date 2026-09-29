using System.Diagnostics;
using System.Runtime.InteropServices;
namespace OpenStreamMS.Services.Session;

/// <summary>
/// Mute per-process del audio que un PID concreto está reproduciendo en CUALQUIER
/// endpoint render activo del host. Usa WASAPI <c>IAudioSessionManager2</c> +
/// <c>IAudioSessionControl2.GetProcessId</c> + <c>ISimpleAudioVolume.SetMute</c>.
///
/// Caso de uso: silenciar a wfreerdp.exe en el host. wfreerdp recibe el audio
/// redirigido por RDP y lo reproduce en su endpoint default (físico). Mute de su
/// audio session = host mudo. Sunshine, dentro de la sesión RDP, sigue capturando
/// el audio en el endpoint origen ("Remote Audio") por loopback antes del transporte
/// RDP, así que Moonlight no se ve afectado.
/// </summary>
public static class AudioSessionMuter
{
    // ── GUIDs (Core Audio API estándar Win10/11) ──────────────────────────

    static readonly Guid CLSID_MMDeviceEnumerator    = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IAudioSessionManager2   = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    const uint COINIT_MULTITHREADED = 0x0;
    const int  S_OK                 = 0;
    const int  S_FALSE              = 1;
    const int  RPC_E_CHANGED_MODE   = unchecked((int)0x80010106);

    [DllImport("ole32.dll")]
    static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    static extern void CoUninitialize();

    // ── COM interfaces ────────────────────────────────────────────────────

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection coll);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice dev);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr p);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr p);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice dev);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        // 2 slots heredados de IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr eventCtx, int streamFlags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume   (IntPtr eventCtx, int streamFlags, out IntPtr vol);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
        [PreserveSig] int RegisterSessionNotification  (IntPtr p);
        [PreserveSig] int UnregisterSessionNotification(IntPtr p);
        [PreserveSig] int RegisterDuckNotification  ([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr p);
        [PreserveSig] int UnregisterDuckNotification(IntPtr p);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount  (out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventCtx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventCtx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid eventCtx);
        [PreserveSig] int RegisterAudioSessionNotification  (IntPtr p);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr p);
    }

    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // 9 slots heredados de IAudioSessionControl, declarados aquí planos para
        // que la vtable cuadre cuando hagamos QueryInterface desde IAudioSessionControl.
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventCtx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventCtx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid eventCtx);
        [PreserveSig] int RegisterAudioSessionNotification  (IntPtr p);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr p);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier        (out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId                (out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventCtx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid eventCtx);
        [PreserveSig] int GetMute(out bool mute);
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Mute persistente: reintenta hasta <paramref name="timeoutMs"/> hasta encontrar
    /// y silenciar al menos una sesión de audio del PID. wfreerdp puede tardar varios
    /// segundos en abrir su WASAPI render stream (lo abre solo cuando el RDP server
    /// empieza a redirigir audio = cuando una app dentro de la sesión RDP toca algo).
    /// Devuelve número total de sesiones silenciadas.
    /// </summary>
    public static int MuteProcess(uint pid, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int total = 0;
        int attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            int n = TryMuteOnce(pid);
            if (n > 0)
            {
                total += n;
                Logger.Log($"[AudioMuter] PID {pid}: {n} sesion(es) silenciada(s) en intento {attempt}.");
                return total;
            }
            Thread.Sleep(750);
        }
        Logger.Warning($"[AudioMuter] PID {pid} no expuso ninguna sesión audio tras {timeoutMs}ms.");
        return total;
    }

    /// <summary>
    /// Lanza un hilo en background que repetidamente busca y silencia sesiones de
    /// <paramref name="pid"/>. Útil porque wfreerdp puede cerrar y reabrir su audio
    /// session según el flujo del juego; queremos mantenerlo muteado mientras viva
    /// el proceso. El hilo termina cuando <paramref name="pid"/> ya no existe.
    /// </summary>
    public static void StartPersistentMute(uint pid)
    {
        var t = new Thread(() =>
        {
            var hasLoggedMute = false;
            try
            {
                while (true)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)pid);
                        if (process.HasExited) return;
                    }
                    catch { return; } // proceso muerto → fuera

                    var muted = TryMuteOnce(pid);
                    if (muted > 0 && !hasLoggedMute)
                    {
                        hasLoggedMute = true;
                        Logger.Log($"[AudioMuter] PID {pid}: {muted} sesion(es) silenciada(s).");
                    }
                    Thread.Sleep(2000);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioMuter] Thread persistente terminó: {ex.Message}");
            }
        }) { IsBackground = true, Name = $"AudioMuter-{pid}" };
        t.Start();
        Logger.Log($"[AudioMuter] Persistent mute thread iniciado para PID {pid}.");
    }

    // ── Private ────────────────────────────────────────────────────────────

    static int TryMuteOnce(uint pid)
    {
        int muted = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        bool uninitializeCom = false;
        try
        {
            uninitializeCom = InitializeComForThread();
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;

            // eRender=0, DEVICE_STATE_ACTIVE=1
            int hr = enumerator.EnumAudioEndpoints(0, 1, out collection);
            if (hr != 0) return 0;

            collection.GetCount(out uint devCount);
            for (uint i = 0; i < devCount; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) != 0 || device is null) continue;
                    muted += MuteOnDevice(device, pid);
                }
                finally { if (device != null) Marshal.ReleaseComObject(device); }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[AudioMuter] TryMuteOnce: {ex.Message}");
        }
        finally
        {
            if (collection != null) Marshal.ReleaseComObject(collection);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            if (uninitializeCom) CoUninitialize();
        }
        return muted;
    }

    static int MuteOnDevice(IMMDevice device, uint pid)
    {
        int muted = 0;
        var asmIid = IID_IAudioSessionManager2;
        // CLSCTX_INPROC_SERVER = 1
        if (device.Activate(ref asmIid, 1, IntPtr.Zero, out object asmObj) != 0 || asmObj is null)
            return 0;

        var sessionMgr = (IAudioSessionManager2)asmObj;
        IAudioSessionEnumerator? sessEnum = null;
        try
        {
            if (sessionMgr.GetSessionEnumerator(out sessEnum) != 0 || sessEnum is null) return 0;
            sessEnum.GetCount(out int sessCount);

            for (int s = 0; s < sessCount; s++)
            {
                IAudioSessionControl? ctl = null;
                try
                {
                    if (sessEnum.GetSession(s, out ctl) != 0 || ctl is null) continue;
                    if (TryGetPid(ctl, out uint sessPid) && sessPid == pid && TryMute(ctl))
                        muted++;
                }
                finally { if (ctl != null) Marshal.ReleaseComObject(ctl); }
            }
        }
        finally
        {
            if (sessEnum != null) Marshal.ReleaseComObject(sessEnum);
            Marshal.ReleaseComObject(sessionMgr);
        }
        return muted;
    }

    static bool TryGetPid(IAudioSessionControl ctl, out uint pid)
    {
        pid = 0;
        try
        {
            var ctl2 = ctl as IAudioSessionControl2;
            return ctl2 is not null && ctl2.GetProcessId(out pid) == 0 && pid != 0;
        }
        catch { return false; }
    }

    static bool TryMute(IAudioSessionControl ctl)
    {
        try
        {
            var vol = ctl as ISimpleAudioVolume;
            if (vol is null) return false;

            Guid noEvt = Guid.Empty;
            return vol.SetMute(true, ref noEvt) == 0;
        }
        catch { return false; }
    }

    static bool InitializeComForThread()
    {
        var hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        if (hr is S_OK or S_FALSE) return true;
        if (hr == RPC_E_CHANGED_MODE) return false;

        Logger.Warning($"[AudioMuter] CoInitializeEx fallo: 0x{hr:X8}");
        return false;
    }
}
