using System.Runtime.InteropServices;

namespace OpenStreamMS.Services.Seats;

/// <summary>Un monitor físico que Windows ve, esté o no en el escritorio.</summary>
public record DisplayInfo(string DevicePath, string FriendlyName, bool Active,
                          int Width, int Height, int PosX, int PosY, uint TargetId, uint AdapterLow);

/// <summary>
/// Gestiona la topología de pantallas con la API CCD (QueryDisplayConfig/SetDisplayConfig).
/// Un puesto "enciende" su monitor añadiéndolo al escritorio y lo "apaga" sacándolo, para que
/// quede libre cuando no hay puesto. Debe llamarse desde la sesión de consola (sesión 0 no vale).
/// </summary>
public static class DisplayTopology
{
    // ── CCD ──
    [StructLayout(LayoutKind.Sequential)] struct HEADER { public int type; public uint size; public uint lo; public int hi; public uint id; }
    [StructLayout(LayoutKind.Sequential)]
    struct PATH
    {
        public uint sLo; public int sHi; public uint sId, sMode, sFlags;
        public uint tLo; public int tHi; public uint tId, tMode, tech, rot, scal, rN, rD, scan; public int avail; public uint tFlags;
        public uint flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SOURCE_NAME { public HEADER h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdi; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TARGET_NAME
    {
        public HEADER h; public uint flags, tech; public ushort mfg, prod; public uint conn;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string path;
    }

    [DllImport("user32")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PATH[] p, ref uint nm, IntPtr modes, IntPtr topo);
    [DllImport("user32")] static extern int SetDisplayConfig(uint np, [In] PATH[]? p, uint nm, IntPtr modes, uint flags);
    [DllImport("user32")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME p);
    [DllImport("user32")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME p);

    const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
    const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_ALLOW_CHANGES = 0x400, SDC_SAVE_TO_DATABASE = 0x200;
    const uint DISPLAYCONFIG_PATH_ACTIVE = 1;

    /// <summary>Todos los monitores conectados, activos (en el escritorio) o no.</summary>
    public static List<DisplayInfo> Enumerate()
    {
        GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out var np, out var nm);
        var paths = new PATH[np];
        var modes = Marshal.AllocHGlobal((int)nm * 64);
        try
        {
            if (QueryDisplayConfig(QDC_ALL_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return [];
            var byTarget = new Dictionary<(uint, uint), DisplayInfo>();
            foreach (var p in paths.Take((int)np))
            {
                if (p.avail == 0) continue;
                bool active = (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
                var key = (p.tLo, p.tId);
                if (byTarget.TryGetValue(key, out var prev) && (prev.Active || !active)) continue;

                string? gdi = null; int w = 0, h = 0, x = 0, y = 0;
                if (active)
                {
                    var sn = new SOURCE_NAME { h = new HEADER { type = 1, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), lo = p.sLo, hi = p.sHi, id = p.sId } };
                    if (DisplayConfigGetDeviceInfo(ref sn) == 0) gdi = sn.gdi;
                    ReadSourceMode(modes, p.sMode, ref w, ref h, ref x, ref y);
                }
                var tn = new TARGET_NAME { h = new HEADER { type = 2, size = (uint)Marshal.SizeOf<TARGET_NAME>(), lo = p.tLo, hi = p.tHi, id = p.tId } };
                DisplayConfigGetDeviceInfo(ref tn);
                byTarget[key] = new DisplayInfo(tn.path ?? "", string.IsNullOrEmpty(tn.friendly) ? (gdi ?? "Monitor") : tn.friendly,
                                                active, w, h, x, y, p.tId, p.tLo);
            }
            return byTarget.Values.ToList();
        }
        finally { Marshal.FreeHGlobal(modes); }
    }

    static unsafe void ReadSourceMode(IntPtr modes, uint index, ref int w, ref int h, ref int x, ref int y)
    {
        if (index == 0xFFFFFFFF) return;
        byte* m = (byte*)modes + index * 64;
        // DISPLAYCONFIG_MODE_INFO: ...+16 source{ w@0? }; source mode: pixels(8) at +16, position at +28
        w = *(int*)(m + 16); h = *(int*)(m + 20); x = *(int*)(m + 28); y = *(int*)(m + 32);
    }

    /// <summary>Activa el monitor (lo añade al escritorio). Windows elige el modo.</summary>
    public static bool Activate(string devicePath)
    {
        var target = Enumerate().FirstOrDefault(d => d.DevicePath == devicePath);
        if (target is null) { Logger.Warning($"[Seats] Monitor no encontrado: {devicePath}"); return false; }
        if (target.Active) return true;

        GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var na, out var nma);
        var active = new PATH[na];
        var modes = Marshal.AllocHGlobal((int)nma * 64);
        GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out var nall, out var nmall);
        var all = new PATH[nall];
        var modesAll = Marshal.AllocHGlobal((int)nmall * 64);
        try
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref na, active, ref nma, modes, IntPtr.Zero) != 0) return false;
            if (QueryDisplayConfig(QDC_ALL_PATHS, ref nall, all, ref nmall, modesAll, IntPtr.Zero) != 0) return false;
            var used = active.Take((int)na).Select(p => p.sId).ToHashSet();
            var cand = all.Take((int)nall).First(p => p.tLo == target.AdapterLow && p.tId == target.TargetId && p.avail != 0 && !used.Contains(p.sId));
            cand.flags = DISPLAYCONFIG_PATH_ACTIVE;
            cand.sMode = cand.tMode = 0xFFFFFFFF;   // sin modo: lo elige Windows
            var combined = active.Take((int)na).Append(cand).ToArray();
            int r = SetDisplayConfig((uint)combined.Length, combined, nma, modes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
            if (r != 0) { Logger.Warning($"[Seats] SetDisplayConfig (activar) err={r}"); return false; }
            Logger.Log($"[Seats] Monitor activado: {target.FriendlyName}");
            PlaceAtCorner(devicePath);
            return true;
        }
        catch (Exception ex) { Logger.Warning($"[Seats] No se pudo activar el monitor: {ex.Message}"); return false; }
        finally { Marshal.FreeHGlobal(modes); Marshal.FreeHGlobal(modesAll); }
    }

    /// <summary>
    /// Coloca el monitor de modo que solo toque al primario por su esquina inferior derecha,
    /// evitando que el cursor de la consola cruce involuntariamente.
    /// </summary>
    public static unsafe bool PlaceAtCorner(string devicePath)
    {
        var target = Enumerate().FirstOrDefault(d => d.DevicePath == devicePath);
        if (target is null || !target.Active) return false;

        GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm);
        var paths = new PATH[np];
        var modes = Marshal.AllocHGlobal((int)nm * 64);
        try
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            byte* m = (byte*)modes;
            int primW = 0, primH = 0, seatMode = -1;
            foreach (var p in paths.Take((int)np))
            {
                byte* sm = m + p.sMode * 64;
                int x = *(int*)(sm + 28), y = *(int*)(sm + 32);
                if (p.tLo == target.AdapterLow && p.tId == target.TargetId) seatMode = (int)p.sMode;
                else if (x == 0 && y == 0) { primW = *(int*)(sm + 16); primH = *(int*)(sm + 20); }
            }
            if (seatMode < 0 || primW == 0) return false;
            *(int*)(m + seatMode * 64 + 28) = primW;
            *(int*)(m + seatMode * 64 + 32) = primH;
            int r = SetDisplayConfig(np, paths.Take((int)np).ToArray(), nm, modes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE);
            if (r != 0) { Logger.Warning($"[Seats] SetDisplayConfig (colocar en esquina) err={r}"); return false; }
            Logger.Log($"[Seats] Monitor '{target.FriendlyName}' colocado en la esquina ({primW},{primH}).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] No se pudo colocar el monitor en la esquina: {ex.Message}");
            return false;
        }
        finally { Marshal.FreeHGlobal(modes); }
    }

    /// <summary>Saca el monitor del escritorio (queda libre para el siguiente puesto).</summary>
    public static bool Deactivate(string devicePath)
    {
        var target = Enumerate().FirstOrDefault(d => d.DevicePath == devicePath);
        if (target is null || !target.Active) return true;

        GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm);
        var paths = new PATH[np];
        var modes = Marshal.AllocHGlobal((int)nm * 64);
        try
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            var keep = paths.Take((int)np).Where(p => !(p.tLo == target.AdapterLow && p.tId == target.TargetId)).ToArray();
            int r = SetDisplayConfig((uint)keep.Length, keep.Length == 0 ? null : keep, nm, modes,
                                     SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
            if (r != 0)
            {
                // Segunda vía (err 87 visto con el PIP): mismas rutas, con la del monitor marcada inactiva.
                var all = paths.Take((int)np).ToArray();
                for (int i = 0; i < all.Length; i++)
                    if (all[i].tLo == target.AdapterLow && all[i].tId == target.TargetId) all[i].flags &= ~DISPLAYCONFIG_PATH_ACTIVE;
                int r2 = SetDisplayConfig((uint)all.Length, all, nm, modes,
                                          SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
                if (r2 != 0) { Logger.Warning($"[Seats] SetDisplayConfig (desactivar) err={r}/{r2}"); return false; }
            }
            Logger.Log($"[Seats] Monitor sacado del escritorio: {target.FriendlyName}");
            return true;
        }
        catch (Exception ex) { Logger.Warning($"[Seats] No se pudo desactivar el monitor: {ex.Message}"); return false; }
        finally { Marshal.FreeHGlobal(modes); }
    }
}
