namespace OpenStreamMS.Services.Seats;

/// <summary>Campos de entrada de un descriptor de informe HID (solo lo que necesita un puesto).</summary>
internal sealed class HidReportLayout
{
    /// <summary>Campo variable: un valor por uso (eje, botón, modificador).</summary>
    public sealed record Field(byte ReportId, int BitOffset, int BitSize, ushort Page, ushort Usage, int LogicalMin, bool Relative);
    /// <summary>Campo de array: Count huecos con los usos activos (teclas pulsadas).</summary>
    public sealed record ArrayField(byte ReportId, int BitOffset, int BitSize, int Count, ushort Page, ushort UsageMin, int LogicalMin);

    public List<Field> Fields { get; } = [];
    public List<ArrayField> Arrays { get; } = [];
    public bool UsesReportIds { get; private set; }

    public static HidReportLayout Empty => new();

    public string Summary =>
        $"{Fields.Count(f => f.Page == 1 && f.Usage is 0x30 or 0x31)} ejes, " +
        $"{Fields.Count(f => f.Page == 9)} botones, " +
        $"{Fields.Count(f => f.Page == 7) + Arrays.Where(a => a.Page == 7).Sum(a => a.Count)} teclas" +
        (UsesReportIds ? ", con ids de informe" : "");

    /// <summary>Formato fijo del protocolo boot: 1 = teclado (modificadores, reservado, 6 teclas), 2 = ratón (botones, X, Y).</summary>
    public static HidReportLayout? Boot(byte protocol) => protocol switch
    {
        1 => Parse(Convert.FromHexString("05010906A101050719E029E715002501750195088102950175088101950675081500256505071900296581" + "00C0")),
        2 => Parse(Convert.FromHexString("05010902A1010901A100050919012903150025019503750181029501750581010501093009311581257F7508950281" + "06C0C0")),
        _ => null,
    };

    public static HidReportLayout Parse(ReadOnlySpan<byte> d)
    {
        var l = new HidReportLayout();
        // Estado global (con pila para PUSH/POP) y local
        ushort page = 0; int logMin = 0, size = 0, count = 0; byte reportId = 0;
        var stack = new Stack<(ushort, int, int, int, byte)>();
        var usages = new List<uint>(); uint usageMin = 0, usageMax = 0; bool haveRange = false;
        var offsets = new Dictionary<byte, int>();

        for (int i = 0; i < d.Length;)
        {
            byte prefix = d[i];
            if (prefix == 0xFE) { i += 3 + (i + 1 < d.Length ? d[i + 1] : 0); continue; }   // ítem largo
            int n = (prefix & 3) == 3 ? 4 : prefix & 3;
            if (i + 1 + n > d.Length) break;
            uint u = 0; for (int k = 0; k < n; k++) u |= (uint)d[i + 1 + k] << (8 * k);
            int s = n switch { 1 => (sbyte)u, 2 => (short)u, _ => (int)u };
            int tag = prefix & 0xFC;
            i += 1 + n;

            switch (tag)
            {
                // Globales
                case 0x04: page = (ushort)u; break;
                case 0x14: logMin = s; break;
                case 0x74: size = (int)u; break;
                case 0x84: reportId = (byte)u; l.UsesReportIds = true; break;
                case 0x94: count = (int)u; break;
                case 0xA4: stack.Push((page, logMin, size, count, reportId)); break;
                case 0xB4: if (stack.Count > 0) (page, logMin, size, count, reportId) = stack.Pop(); break;
                // Locales
                case 0x08: usages.Add(n == 4 ? u : ((uint)page << 16) | u); break;
                case 0x18: usageMin = n == 4 ? u : ((uint)page << 16) | u; haveRange = true; break;
                case 0x28: usageMax = n == 4 ? u : ((uint)page << 16) | u; haveRange = true; break;
                // Principales
                case 0x80:   // Input
                {
                    int off = offsets.GetValueOrDefault(reportId);
                    bool constant = (u & 1) != 0, variable = (u & 2) != 0, relative = (u & 4) != 0;
                    if (!constant && variable)
                    {
                        for (int k = 0; k < count; k++)
                        {
                            uint us = k < usages.Count ? usages[k]
                                    : haveRange && usageMin + (uint)(k - usages.Count) <= usageMax ? usageMin + (uint)(k - usages.Count)
                                    : usages.Count > 0 ? usages[^1] : 0;
                            if (us != 0)
                                l.Fields.Add(new Field(reportId, off + k * size, size, (ushort)(us >> 16), (ushort)us, logMin, relative));
                        }
                    }
                    else if (!constant)
                    {
                        uint first = haveRange ? usageMin : usages.Count > 0 ? usages[0] : ((uint)page << 16);
                        l.Arrays.Add(new ArrayField(reportId, off, size, count, (ushort)(first >> 16), (ushort)first, logMin));
                    }
                    offsets[reportId] = off + size * count;
                    goto case 0x00;
                }
                case 0x90: case 0xB0:   // Output / Feature: no ocupan el informe de entrada
                case 0xA0: case 0xC0:   // Collection / End Collection
                case 0x00:
                    usages.Clear(); haveRange = false; usageMin = usageMax = 0;
                    break;
            }
        }
        return l;
    }

    public static int Read(ReadOnlySpan<byte> data, int bitOffset, int bitSize, bool signed)
    {
        long v = 0;
        for (int b = 0; b < bitSize; b++)
        {
            int bit = bitOffset + b;
            if ((bit >> 3) >= data.Length) break;
            if ((data[bit >> 3] & (1 << (bit & 7))) != 0) v |= 1L << b;
        }
        if (signed && bitSize < 64 && (v & (1L << (bitSize - 1))) != 0) v -= 1L << bitSize;
        return (int)v;
    }
}

/// <summary>Convierte informes HID en eventos de ratón (botones RI_MOUSE_*) y de teclado (scancodes set 1).</summary>
internal sealed class HidTranslator(HidReportLayout layout, Action<SeatInputEvent> emit)
{
    private int _buttons;                                   // bit n = botón n+1
    private readonly HashSet<uint> _keys = [];              // (página << 16) | uso pulsados
    private readonly HashSet<uint> _now = [];

    // RI_MOUSE_* down/up por botón (1..5)
    private static readonly (ushort Down, ushort Up)[] ButtonFlags =
        [(0x0001, 0x0002), (0x0004, 0x0008), (0x0010, 0x0020), (0x0040, 0x0080), (0x0100, 0x0200)];

    public void OnReport(ReadOnlySpan<byte> report)
    {
        byte id = 0;
        if (layout.UsesReportIds)
        {
            if (report.Length == 0) return;
            id = report[0];
            report = report[1..];
        }

        int dx = 0, dy = 0, wheel = 0, hwheel = 0, buttons = _buttons;
        bool mouse = false, keyboard = false;
        _now.Clear();

        foreach (var f in layout.Fields)
        {
            if (f.ReportId != id) continue;
            int v = HidReportLayout.Read(report, f.BitOffset, f.BitSize, f.LogicalMin < 0);
            switch (f.Page)
            {
                case 1 when f.Usage == 0x30 && f.Relative: dx = v; mouse = true; break;
                case 1 when f.Usage == 0x31 && f.Relative: dy = v; mouse = true; break;
                case 1 when f.Usage == 0x38: wheel = v; mouse = true; break;
                case 0x0C when f.Usage == 0x238: hwheel = v; mouse = true; break;
                case 9 when f.Usage is >= 1 and <= 5:
                    mouse = true;
                    if (v != 0) buttons |= 1 << (f.Usage - 1); else buttons &= ~(1 << (f.Usage - 1));
                    break;
                case 7 or 0x0C:
                    keyboard = true;
                    if (v != 0) _now.Add(((uint)f.Page << 16) | f.Usage);
                    break;
            }
        }
        foreach (var a in layout.Arrays)
        {
            if (a.ReportId != id || a.Page is not (7 or 0x0C)) continue;
            keyboard = true;
            for (int k = 0; k < a.Count; k++)
            {
                int v = HidReportLayout.Read(report, a.BitOffset + k * a.BitSize, a.BitSize, false);
                if (v == 0) continue;
                uint usage = (uint)(a.UsageMin + v - a.LogicalMin);
                if (a.Page == 7 && usage is >= 1 and <= 3) return;   // ErrorRollOver: informe sin datos válidos
                _now.Add(((uint)a.Page << 16) | usage);
            }
        }

        if (mouse) EmitMouse(dx, dy, buttons, wheel, hwheel);
        if (keyboard) EmitKeys(id);
    }

    private void EmitMouse(int dx, int dy, int buttons, int wheel, int hwheel)
    {
        ushort flags = 0;
        int changed = buttons ^ _buttons;
        for (int b = 0; b < 3; b++)
            if ((changed & (1 << b)) != 0) flags |= (buttons & (1 << b)) != 0 ? ButtonFlags[b].Down : ButtonFlags[b].Up;
        _buttons = buttons;

        if (dx != 0 || dy != 0 || flags != 0) emit(new SeatInputEvent { Kind = SeatInputEvent.Mouse, Flags = flags, Dx = dx, Dy = dy });
        // Botones X, rueda y rueda horizontal comparten mouseData en SendInput: un evento cada uno.
        for (int b = 3; b < 5; b++)
            if ((changed & (1 << b)) != 0)
                emit(new SeatInputEvent { Kind = SeatInputEvent.Mouse, Flags = (buttons & (1 << b)) != 0 ? ButtonFlags[b].Down : ButtonFlags[b].Up });
        if (wheel != 0) emit(new SeatInputEvent { Kind = SeatInputEvent.Mouse, Flags = 0x0400, Data = (short)(wheel * 120) });
        if (hwheel != 0) emit(new SeatInputEvent { Kind = SeatInputEvent.Mouse, Flags = 0x0800, Data = (short)(hwheel * 120) });
    }

    private void EmitKeys(byte reportId)
    {
        // Solo las teclas de este informe: un teclado con varios informes (teclas normales y
        // multimedia) no debe soltar las de otro informe al llegar este.
        var mine = KeysOfReport(reportId);
        foreach (var k in _keys.Where(k => mine.Contains(k >> 16) && !_now.Contains(k)).ToList())
        {
            _keys.Remove(k);
            Key(k, up: true);
        }
        foreach (var k in _now)
            if (_keys.Add(k)) Key(k, up: false);
    }

    private HashSet<uint> KeysOfReport(byte id)
    {
        var pages = new HashSet<uint>();
        foreach (var f in layout.Fields) if (f.ReportId == id) pages.Add(f.Page);
        foreach (var a in layout.Arrays) if (a.ReportId == id) pages.Add(a.Page);
        return pages;
    }

    private void Key(uint key, bool up)
    {
        ushort page = (ushort)(key >> 16), usage = (ushort)key;
        ushort sc = page == 7 ? HidKeys.Keyboard(usage) : HidKeys.Consumer(usage);
        if (sc == 0) return;
        ushort flags = (ushort)((up ? 0x01 : 0) | ((sc & 0x100) != 0 ? 0x02 : 0) | ((sc & 0x200) != 0 ? 0x04 : 0));
        emit(new SeatInputEvent { Kind = SeatInputEvent.Keyboard, Code = (ushort)(sc & 0xFF), Flags = flags });
    }

    /// <summary>Al desconectarse o apagar el puesto: que no quede nada pulsado en la sesión.</summary>
    public void ReleaseAll()
    {
        if (_buttons != 0) EmitMouse(0, 0, 0, 0, 0);
        foreach (var k in _keys) Key(k, up: true);
        _keys.Clear();
    }
}

/// <summary>Uso HID → scancode set 1 (bit 0x100 = prefijo E0, 0x200 = Pausa). Tabla de Microsoft "USB HID to PS/2 Scan Code Translation".</summary>
internal static class HidKeys
{
    private static readonly ushort[] KeyboardTable = Build();

    public static ushort Keyboard(ushort usage) => usage < KeyboardTable.Length ? KeyboardTable[usage] : (ushort)0;

    public static ushort Consumer(ushort usage) => usage switch
    {
        0xB5 => 0x119, 0xB6 => 0x110, 0xB7 => 0x124, 0xCD => 0x122, 0xE2 => 0x120,
        0xE9 => 0x130, 0xEA => 0x12E, 0x183 => 0x16D, 0x18A => 0x16C, 0x192 => 0x121,
        0x194 => 0x16B, 0x221 => 0x165, 0x223 => 0x132, 0x224 => 0x16A, 0x225 => 0x169,
        0x226 => 0x168, 0x227 => 0x167, 0x22A => 0x166,
        _ => 0,
    };

    private static ushort[] Build()
    {
        var t = new ushort[0xE8];
        // a..z
        ushort[] letters = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
                            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
        for (int i = 0; i < letters.Length; i++) t[0x04 + i] = letters[i];
        // 1..0
        for (int i = 0; i < 10; i++) t[0x1E + i] = (ushort)(0x02 + i);
        ushort[] row = [0x1C, 0x01, 0x0E, 0x0F, 0x39, 0x0C, 0x0D, 0x1A, 0x1B, 0x2B, 0x2B, 0x27, 0x28, 0x29, 0x33, 0x34, 0x35, 0x3A];
        for (int i = 0; i < row.Length; i++) t[0x28 + i] = row[i];
        // F1..F12
        ushort[] f = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (int i = 0; i < f.Length; i++) t[0x3A + i] = f[i];
        ushort[] nav = [0x137, 0x46, 0x245, 0x152, 0x147, 0x149, 0x153, 0x14F, 0x151, 0x14D, 0x14B, 0x150, 0x148];
        for (int i = 0; i < nav.Length; i++) t[0x46 + i] = nav[i];
        // Teclado numérico (0x53..0x63), 0x64 \ no US, 0x65 Aplicación, 0x66 Power, 0x67 = numérico
        ushort[] pad = [0x45, 0x135, 0x37, 0x4A, 0x4E, 0x11C, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52, 0x53, 0x56, 0x15D, 0x15E, 0x59];
        for (int i = 0; i < pad.Length; i++) t[0x53 + i] = pad[i];
        // F13..F24
        ushort[] fx = [0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76];
        for (int i = 0; i < fx.Length; i++) t[0x68 + i] = fx[i];
        t[0x85] = 0x7E; t[0x87] = 0x73; t[0x88] = 0x70; t[0x89] = 0x7D; t[0x8A] = 0x79; t[0x8B] = 0x7B;
        // Modificadores
        ushort[] mods = [0x1D, 0x2A, 0x38, 0x15B, 0x11D, 0x36, 0x138, 0x15C];
        for (int i = 0; i < mods.Length; i++) t[0xE0 + i] = mods[i];
        return t;
    }
}
