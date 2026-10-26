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
    /// vienen de la sesión de la consola, y solo para listar, iniciar y detener sesiones.
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
    private void OpenWeb()
    {
        if (!AppWindow.RuntimeAvailable())
        {
            Process.Start(new ProcessStartInfo(_baseUrl) { UseShellExecute = true });
            return;
        }
        if (_window is null || _window.IsDisposed)
        {
            // Se crea al abrirla y se libera al cerrarla: WebView2 son varios procesos de Edge.
            _window = new AppWindow(_baseUrl);
            _window.FormClosed += (_, _) => { _window?.Dispose(); _window = null; };
        }
        _window.ShowAndActivate();
    }

    // ── refresh ──────────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        try
        {
            var sessions = await _http.GetFromJsonAsync<TraySessionDto[]>($"{_baseUrl}/api/sessions")
                           ?? [];
            var signature = string.Join("|", sessions.Select(s => $"{s.Id}:{s.Name}:{s.State}"));
            if (signature == _menuSignature) return;
            _menuSignature = signature;
            UpdateMenu(sessions);
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

    // ── menus ─────────────────────────────────────────────────────────────────────

    private void UpdateMenu(TraySessionDto[] sessions)
    {
        var old  = _icon.ContextMenuStrip;
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("OpenStreamMS") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        if (sessions.Length == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("Sin sesiones configuradas") { Enabled = false });
        }
        else
        {
            foreach (var s in sessions)
            {
                var label = new ToolStripMenuItem($"{s.Name}  [{s.State}]") { Enabled = false };
                menu.Items.Add(label);

                if (s.State is "Running" or "Starting")
                {
                    var id   = s.Id;
                    var stop = new ToolStripMenuItem($"  Detener '{s.Name}'");
                    stop.Click += async (_, _) => await PostAsync($"/api/sessions/{id}/stop");
                    menu.Items.Add(stop);
                }
                else if (s.State is "Stopped" or "Created" or "Error")
                {
                    var id    = s.Id;
                    var start = new ToolStripMenuItem($"  Iniciar '{s.Name}'");
                    start.Click += async (_, _) => await PostAsync($"/api/sessions/{id}/start");
                    menu.Items.Add(start);
                }
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        AddWebItem(menu);
        menu.Items.Add(new ToolStripSeparator());
        AddExitItem(menu);

        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private ContextMenuStrip BuildOfflineMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("OpenStreamMS — sin conexión") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        AddWebItem(menu);
        menu.Items.Add(new ToolStripSeparator());
        AddExitItem(menu);
        return menu;
    }

    private void AddWebItem(ContextMenuStrip menu)
    {
        var item = new ToolStripMenuItem("Abrir OpenStreamMS");
        item.Click += (_, _) => OpenWeb();
        menu.Items.Add(item);
    }

    private void AddExitItem(ContextMenuStrip menu)
    {
        var item = new ToolStripMenuItem("Salir");
        item.Click += (_, _) => { _timer.Stop(); ExitThread(); };
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
