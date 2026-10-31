using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows.Forms;

namespace OpenStreamMS.Services.TrayApp;

internal sealed class TrayApp : ApplicationContext
{
    /// <summary>
    /// Marca las peticiones del icono de bandeja. El servicio solo las acepta sin contraseña si
    /// vienen de la sesión de la consola, y solo para listar, iniciar y detener sesiones y puestos.
    /// </summary>
    internal const string LocalHeader = "X-OpenStreamMS-Tray";

    private readonly string _baseUrl;
    private readonly NotifyIcon _icon;
    private readonly HttpClient _http;
    private readonly System.Windows.Forms.Timer _timer;
    // Firma del ultimo menu pintado: solo se reconstruye (handles GDI/USER) si cambia.
    private string? _menuSignature;
    private const string OfflineSignature = "<offline>";
    private readonly IDisposable _seatHostClient;
    private AppWindow? _window;

    /// <summary>
    /// Evento con nombre de la sesión: otro "OpenStreamMS.exe --open" lo señala para que el
    /// icono que ya está corriendo muestre la ventana, en vez de abrir una segunda bandeja.
    /// </summary>
    internal const string ShowWindowEvent = @"Local\OpenStreamMS_ShowWindow";
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;
    private readonly Control _ui = new();

    // Emparejamientos ya avisados ("sesión:id"), para no repetir el aviso en cada sondeo.
    private readonly HashSet<string> _notifiedPairings = [];
    private Guid? _pairingSession;   // sesión del último aviso: el clic abre su emparejamiento

    internal TrayApp(string baseUrl, bool showWindow = false)
    {
        _ui.CreateControl();   // para volver al hilo de la UI desde el evento
        _baseUrl = baseUrl;
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _http.DefaultRequestHeaders.Add(LocalHeader, "1");
        _seatHostClient = OpenStreamMS.Services.Seats.SeatHostControl.StartClient();

        _icon = new NotifyIcon
        {
            Icon    = CreateIcon(),
            Text    = "OpenStreamMS",
            Visible = true,
        };
        _icon.DoubleClick           += (_, _) => OpenWeb();
        _icon.BalloonTipClicked     += (_, _) =>
        {
            if (_pairingSession is { } id) OpenWeb(new Uri(new Uri(_baseUrl), $"/#pair={id}").AbsoluteUri);
        };
        _icon.ContextMenuStrip       = BuildOfflineMenu();

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEvent);
        _showWait  = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _ui.BeginInvoke(OpenWeb), null, Timeout.Infinite, executeOnlyOnce: false);

        if (showWindow) OpenWeb();
    }

    // ── web ──────────────────────────────────────────────────────────────────────

    /// <summary>Muestra el panel en su ventana; sin WebView2, en el navegador.</summary>
    /// <param name="url">Página del panel a abrir; null = la principal.</param>
    private void OpenWeb(string? url = null)
    {
        if (!AppWindow.RuntimeAvailable())
        {
            Process.Start(new ProcessStartInfo(url ?? _baseUrl) { UseShellExecute = true });
            return;
        }
        if (_window is null || _window.IsDisposed)
        {
            // Se crea al abrirla y se libera al cerrarla: WebView2 son varios procesos de Edge.
            _window = new AppWindow(_baseUrl);
            _window.FormClosed += (_, _) => { _window?.Dispose(); _window = null; };
        }
        _window.ShowAndActivate(url);
    }

    // ── refresh ──────────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        try
        {
            var sessions = await _http.GetFromJsonAsync<TraySessionDto[]>($"{_baseUrl}/api/sessions")
                           ?? [];
            TraySeatDto[] seats;
            try { seats = await _http.GetFromJsonAsync<TraySeatDto[]>($"{_baseUrl}/api/seats") ?? []; }
            catch (HttpRequestException) { seats = []; }   // servicio sin puestos para la bandeja
            await NotifyPairingsAsync(sessions);
            var signature = string.Join("|", sessions.Select(s => $"{s.Id}:{s.Name}:{s.State}")) + "#" +
                            string.Join("|", seats.Select(s => $"{s.Id}:{s.Name}:{s.State}:{s.SessionId}"));
            if (signature == _menuSignature) return;
            _menuSignature = signature;
            UpdateMenu(sessions, seats);
        }
        catch
        {
            if (_menuSignature == OfflineSignature) return;
            _menuSignature = OfflineSignature;
            var old = _icon.ContextMenuStrip;
            _icon.ContextMenuStrip = BuildOfflineMenu();
            old?.Dispose();
        }
    }

    // ── emparejamiento ───────────────────────────────────────────────────────────

    /// <summary>
    /// Un Moonlight espera PIN en alguna sesión: aviso de Windows en la consola. Al hacer clic
    /// se abre el panel con el emparejamiento de esa sesión. Cada solicitud se avisa una vez.
    /// </summary>
    private async Task NotifyPairingsAsync(TraySessionDto[] sessions)
    {
        if (!sessions.Any(s => s.State == "Running")) { _notifiedPairings.Clear(); return; }
        TrayPairingDto[] pending;
        try
        {
            pending = await _http.GetFromJsonAsync<TrayPairingDto[]>($"{_baseUrl}/api/sessions/pairings") ?? [];
        }
        catch { return; }   // servicio sin este endpoint o sin respuesta: sin aviso

        _notifiedPairings.IntersectWith(pending.Select(p => $"{p.SessionId}:{p.Id}"));
        var fresh = pending.FirstOrDefault(p => _notifiedPairings.Add($"{p.SessionId}:{p.Id}"));
        if (fresh is null) return;

        bool tv = fresh.Kind == "webrtc";
        string who = fresh.Name is { } n ? (tv ? TrayText.Get("pair.tvNamed", n) : n)
                   : fresh.Address is { } a ? TrayText.Get(tv ? "pair.tvAt" : "pair.deviceAt", a)
                   : TrayText.Get(tv ? "pair.tv" : "pair.device");
        _pairingSession = fresh.SessionId;
        _icon.ShowBalloonTip(15000, TrayText.Get("pair.title"),
            TrayText.Get("pair.body", who, fresh.SessionName),
            ToolTipIcon.Info);
    }

    // ── menus ─────────────────────────────────────────────────────────────────────

    private void UpdateMenu(TraySessionDto[] sessions, TraySeatDto[] seats)
    {
        var old  = _icon.ContextMenuStrip;
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("OpenStreamMS") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(TrayText.Get("sessions")) { Enabled = false, Font = BoldFont(menu) });
        if (sessions.Length == 0)
            menu.Items.Add(new ToolStripMenuItem(TrayText.Get("sessions.none")) { Enabled = false });
        foreach (var s in sessions)
            menu.Items.Add(PowerItem(s.Name, s.State, $"/api/sessions/{s.Id}"));

        // Un puesto sin sesión asignada no se puede encender: se ve, pero desactivado.
        if (seats.Length > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(TrayText.Get("seats")) { Enabled = false, Font = BoldFont(menu) });
            foreach (var s in seats)
            {
                var item = PowerItem(s.Name, s.State, $"/api/seats/{s.Id}");
                if (s.SessionId is null && s.State is not ("Running" or "Starting"))
                {
                    item.Enabled = false;
                    item.ToolTipText = TrayText.Get("seat.noSession");
                }
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        AddWebItem(menu);
        menu.Items.Add(new ToolStripSeparator());
        AddExitItem(menu);

        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    /// <summary>
    /// Una sesión o un puesto: "Nombre — Estado   Encender/Apagar", con un punto de color. Al
    /// pulsarlo se enciende o se apaga; los que están arrancando o deteniéndose no responden.
    /// </summary>
    private ToolStripMenuItem PowerItem(string name, string state, string apiPath)
    {
        bool on   = state is "Running";
        bool busy = state is "Starting" or "Stopping";
        var item = new ToolStripMenuItem($"{name} — {TrayText.State(state)}")
        {
            Image   = StateDot(state),
            Enabled = !busy,
        };
        if (!busy)
        {
            var action = on ? "stop" : "start";
            item.Click += async (_, _) => await PostAsync($"{apiPath}/{action}");
            item.ShortcutKeyDisplayString = TrayText.Get(on ? "action.stop" : "action.start");
        }
        return item;
    }

    private static Font BoldFont(ContextMenuStrip menu) => new(menu.Font, FontStyle.Bold);

    private static readonly Dictionary<int, Bitmap> _dots = [];

    /// <summary>Punto de color del estado: verde activo, ámbar en curso, rojo error, gris apagado.</summary>
    private static Bitmap StateDot(string state)
    {
        var color = state switch
        {
            "Running"                => Color.FromArgb(34, 197, 94),
            "Starting" or "Stopping" => Color.FromArgb(245, 158, 11),
            "Error"                  => Color.FromArgb(239, 68, 68),
            _                        => Color.FromArgb(148, 163, 184),
        };
        if (_dots.TryGetValue(color.ToArgb(), out var bmp)) return bmp;
        bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 8, 8);
        }
        return _dots[color.ToArgb()] = bmp;
    }

    private ContextMenuStrip BuildOfflineMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(TrayText.Get("offline")) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        AddWebItem(menu);
        menu.Items.Add(new ToolStripSeparator());
        AddExitItem(menu);
        return menu;
    }

    private void AddWebItem(ContextMenuStrip menu)
    {
        var item = new ToolStripMenuItem(TrayText.Get("open")) { Font = BoldFont(menu) };
        item.Click += (_, _) => OpenWeb();
        menu.Items.Add(item);
    }

    private void AddExitItem(ContextMenuStrip menu)
    {
        var item = new ToolStripMenuItem(TrayText.Get("exit"));
        // Los presentadores primero: el servicio apaga los puestos al ver sus tuberías cerradas.
        item.Click += (_, _) => { _timer.Stop(); OpenStreamMS.Services.Seats.SeatHost.CloseAll(); ExitThread(); };
        menu.Items.Add(item);
    }

    // ── API helpers ──────────────────────────────────────────────────────────────

    private async Task PostAsync(string path)
    {
        try
        {
            await _http.PostAsync(_baseUrl + path, null);
            await RefreshAsync();
        }
        catch { /* service unreachable */ }
    }

    // ── icon ─────────────────────────────────────────────────────────────────────

    private static Icon CreateIcon() =>
        OpenStreamMS.Core.Helpers.AppIcon.CreateIcon(SystemInformation.SmallIconSize);

    // ── cleanup ───────────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait.Unregister(null);
            _showEvent.Dispose();
            _window?.Dispose();
            _ui.Dispose();
            _seatHostClient.Dispose();
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _http.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed record TraySessionDto(Guid Id, string Name, string State);
internal sealed record TraySeatDto(Guid Id, string Name, string State, Guid? SessionId);
internal sealed record TrayPairingDto(Guid SessionId, string SessionName, string Id, string? Name, string? Address, string? Kind);
