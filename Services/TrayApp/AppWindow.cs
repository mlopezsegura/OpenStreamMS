using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OpenStreamMS.Services.TrayApp;

/// <summary>
/// El panel web en una ventana propia (WebView2), sin barra de direcciones ni pestañas.
/// Lo que apunta fuera del panel (Sunshine, GitHub) se abre en el navegador.
/// </summary>
internal sealed class AppWindow : Form
{
    private readonly Uri _baseUri;
    private readonly WebView2 _web;
    private readonly System.Windows.Forms.Timer _retry;
    private string? _startUrl;

    internal AppWindow(string baseUrl)
    {
        _baseUri = new Uri(baseUrl);

        Text          = "OpenStreamMS";
        Icon          = OpenStreamMS.Core.Helpers.AppIcon.CreateIcon(SystemInformation.IconSize);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize   = new Size(720, 480);
        Size          = new Size(1280, 860);
        BackColor     = Color.FromArgb(15, 15, 20);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);

        // Si el servicio aún no responde (recién arrancado el equipo), se reintenta solo.
        _retry = new System.Windows.Forms.Timer { Interval = 3000 };
        _retry.Tick += (_, _) => { _retry.Stop(); _web.CoreWebView2?.Navigate(_baseUri.AbsoluteUri); };

        Load += async (_, _) => await InitAsync();
    }

    /// <summary>Hay un runtime de WebView2 instalado (viene con Windows 11 y con Edge).</summary>
    internal static bool RuntimeAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    /// <param name="url">Página del panel a mostrar (p. ej. con <c>#pair=</c>); null = la que tenga.</param>
    internal void ShowAndActivate(string? url = null)
    {
        if (url is not null)
        {
            if (_web.CoreWebView2 is { } core) core.Navigate(url);
            else _startUrl = url;   // InitAsync aún no ha terminado
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    private async Task InitAsync()
    {
        // Perfil propio por usuario: guarda la cookie de sesión del panel entre aperturas.
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenStreamMS", "WebView2");
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[App] WebView2 no disponible, se abre el navegador: {ex.Message}");
            OpenExternal(_baseUri.AbsoluteUri);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled          = false;
        core.Settings.IsStatusBarEnabled          = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;   // F5, Ctrl+F

        // theme.js manda el color de la barra del panel cada vez que cambia el tema.
        core.WebMessageReceived += (_, e) =>
        {
            if (IsPanel(e.Source)) OnThemeMessage(e.WebMessageAsJson);
        };

        core.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "OpenStreamMS" : core.DocumentTitle;

        // target="_blank" y window.open: fuera de la ventana
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsPanel(e.Uri)) core.Navigate(e.Uri);
            else OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (IsPanel(e.Uri) || e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            core.NavigateToString(OfflinePage);
            _retry.Start();
        };

        core.Navigate(_startUrl ?? _baseUri.AbsoluteUri);
    }

    private bool IsPanel(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        string.Equals(u.Authority, _baseUri.Authority, StringComparison.OrdinalIgnoreCase) &&
        u.Scheme == _baseUri.Scheme;

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warning($"[App] No se pudo abrir {u.AbsoluteUri}: {ex.Message}"); }
    }

    // ── Barra de título del color del tema ──────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // Windows 10 20H1+
    private const int DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;   // Windows 11

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCaption(BackColor, dark: true);   // hasta que el panel diga su tema
    }

    /// <summary><c>{"type":"theme","color":"rgb(r, g, b)","dark":true}</c> desde theme.js.</summary>
    private void OnThemeMessage(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.GetString() != "theme") return;
            bool dark = root.TryGetProperty("dark", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
            var m = System.Text.RegularExpressions.Regex.Match(
                root.TryGetProperty("color", out var c) ? c.GetString() ?? "" : "",
                @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})");
            if (!m.Success) { ApplyCaption(null, dark); return; }
            var color = Color.FromArgb(Math.Min(255, int.Parse(m.Groups[1].Value)),
                                       Math.Min(255, int.Parse(m.Groups[2].Value)),
                                       Math.Min(255, int.Parse(m.Groups[3].Value)));
            BackColor = color;
            ApplyCaption(color, dark);
        }
        catch (Exception ex) { Logger.Warning($"[App] Tema de la ventana no aplicado: {ex.Message}"); }
    }

    /// <summary>Modo oscuro de la barra (Windows 10/11) y, en Windows 11, su color y el del texto.</summary>
    private void ApplyCaption(Color? caption, bool dark)
    {
        if (!IsHandleCreated) return;
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        if (caption is not { } c) return;
        int colorRef = c.R | (c.G << 8) | (c.B << 16);
        int text = dark ? 0x00FFFFFF : 0x00000000;
        DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private static string OfflinePage => $"""
        <!doctype html><html lang="{TrayText.Language}"><meta charset="utf-8">
        <body style="margin:0;height:100vh;display:grid;place-items:center;background:#0f0f14;
                     color:#c9c9d6;font:15px 'Segoe UI',sans-serif;text-align:center">
        <div><div style="font-size:18px;color:#fff;margin-bottom:8px">{System.Net.WebUtility.HtmlEncode(TrayText.Get("offline.title"))}</div>
        {System.Net.WebUtility.HtmlEncode(TrayText.Get("offline.hint"))}</div>
        """;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _retry.Dispose();
            _web.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
