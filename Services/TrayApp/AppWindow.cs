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

    internal void ShowAndActivate()
    {
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

        core.Navigate(_baseUri.AbsoluteUri);
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

    private const string OfflinePage = """
        <!doctype html><html lang="es"><meta charset="utf-8">
        <body style="margin:0;height:100vh;display:grid;place-items:center;background:#0f0f14;
                     color:#c9c9d6;font:15px 'Segoe UI',sans-serif;text-align:center">
        <div><div style="font-size:18px;color:#fff;margin-bottom:8px">Esperando al servicio OpenStreamMS…</div>
        Se reintenta solo. Si no arranca, revisa el servicio en services.msc.</div>
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
