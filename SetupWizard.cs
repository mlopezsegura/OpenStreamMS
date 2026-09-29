using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpenStreamMS;

internal sealed class SetupWizard : Form
{
    // ── Constantes ────────────────────────────────────────────────────────────
    private const string AppName      = "OpenStreamMS";
    private const string AppVersion   = "1.0.0";
    private const string AppPublisher = "mlopezsegura";
    private const string AppUrl       = "https://github.com/mlopezsegura/OpenStreamMS";
    private const string UninstallReg = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OpenStreamMS";

    // Márgenes del contenido dentro de cada panel de paso
    private const int ML = 28;   // margen izquierdo
    private const int MR = 28;   // margen derecho  → ancho útil = 540 - ML - MR = 484
    private const int CW = 484;  // ancho del área de contenido (540 - ML - MR)

    private static readonly Color Accent = Color.FromArgb(124, 58, 237);
    private static readonly Color Green  = Color.FromArgb(16, 185, 129);
    private static readonly Color Red    = Color.FromArgb(220, 38, 38);
    private static readonly Color Muted  = Color.FromArgb(100, 116, 139);
    private static readonly Color Dark   = Color.FromArgb(25, 25, 50);

    // ── Estado ────────────────────────────────────────────────────────────────
    private readonly bool _isUninstall;
    private          int  _step;

    // ── Header ────────────────────────────────────────────────────────────────
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 72, BackColor = Color.FromArgb(13, 13, 26) };
    private readonly Label _hTitle = new() { Location = new(20, 12), Size = new(500, 30), ForeColor = Color.White, BackColor = Color.Transparent };
    private readonly Label _hSub   = new() { Location = new(20, 46), Size = new(500, 20), ForeColor = Muted,       BackColor = Color.Transparent };

    // ── Panels de pasos (sin Padding — las posiciones son absolutas) ──────────
    private readonly Panel _pWelcome  = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Panel _pOptions  = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Panel _pProgress = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Panel _pDone     = new() { Dock = DockStyle.Fill, BackColor = Color.White };

    // ── Controles de opciones ─────────────────────────────────────────────────
    private readonly TextBox  _txtDir     = new() { Width = 364 };   // 364 + 4 gap + 88 browse = 456 < 484
    private readonly CheckBox _chkDesktop = new() { Text = "Crear acceso directo en el escritorio",    Checked = true,  AutoSize = true };
    private readonly CheckBox _chkService = new() { Text = "Iniciar el servicio al completar",         Checked = true,  AutoSize = true };
    private readonly CheckBox _chkTray    = new() { Text = "Iniciar el icono de bandeja al completar", Checked = true,  AutoSize = true };

    // ── Controles de desinstalación ───────────────────────────────────────────
    private readonly CheckBox _chkKeepData = new()
    {
        Text     = "Conservar sesiones y configuración (sessions.json, service.config.json y carpeta sessions/)",
        Checked  = false,
        AutoSize = true,
    };
    private readonly CheckBox _chkCleanDeps = new()
    {
        Text     = "Borrado completo: eliminar también RDP Wrapper y ViGEmBus",
        Checked  = false,
        AutoSize = true,
    };

    // ── Controles de progreso ─────────────────────────────────────────────────
    private readonly ProgressBar _pbar    = new() { Height = 22, Style = ProgressBarStyle.Continuous };
    private readonly Label       _pStatus = new() { ForeColor = Muted, AutoSize = true };
    private readonly RichTextBox _pLog    = new()
    {
        BackColor   = Color.FromArgb(240, 240, 248),
        ForeColor   = Color.FromArgb(50,  50,  80),
        ReadOnly    = true,
        BorderStyle = BorderStyle.None,
        ScrollBars  = RichTextBoxScrollBars.Vertical,
    };

    // ── Controles de finalización ─────────────────────────────────────────────
    private readonly CheckBox _chkOpenWeb = new()
    {
        Text    = "Abrir el panel de control  (http://localhost:5000)",
        Checked = true,
        AutoSize = true,
    };

    // ── Botones de navegación ─────────────────────────────────────────────────
    private readonly Button _btnBack   = new();
    private readonly Button _btnNext   = new();
    private readonly Button _btnCancel = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    internal SetupWizard(bool isUninstall)
    {
        _isUninstall = isUninstall;

        SuspendLayout();
        ConfigureForm();
        BuildHeader();
        BuildSteps();
        BuildFooter();
        ResumeLayout(performLayout: false);

        ShowStep(0);
    }

    // ── Form setup ────────────────────────────────────────────────────────────

    void ConfigureForm()
    {
        Text            = _isUninstall ? $"Desinstalar {AppName}" : $"Instalar {AppName}";
        ClientSize      = new(540, 440);
        MinimumSize     = new(540 + 16, 440 + 39);
        MaximumSize     = MinimumSize;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);
    }

    void BuildHeader()
    {
        _hTitle.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        _hSub.Font   = new Font("Segoe UI", 8.5f);
        _header.Controls.AddRange(new Control[] { _hTitle, _hSub });
        Controls.Add(_header);
    }

    void BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(248, 248, 252) };
        footer.Controls.Add(new Label { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(220, 220, 235) });

        StyleBtn(_btnBack,   "← Atrás",    primary: false); _btnBack.Location   = new(208, 13);
        StyleBtn(_btnNext,   "Siguiente →", primary: true);  _btnNext.Location   = new(316, 13);
        StyleBtn(_btnCancel, "Cancelar",    primary: false); _btnCancel.Location = new(428, 13);

        _btnBack  .Click += (_, _) => ShowStep(_step - 1);
        _btnNext  .Click += OnNext;
        _btnCancel.Click += (_, _) => { if (ConfirmCancel()) Close(); };

        footer.Controls.AddRange(new Control[] { _btnBack, _btnNext, _btnCancel });
        Controls.Add(footer);
    }

    void BuildSteps()
    {
        var all = StepPanels();
        foreach (var p in all) { p.Visible = false; Controls.Add(p); }

        if (_isUninstall)
        {
            // Paso 0 — Confirmar desinstalación
            Lbl(_pWelcome, $"Desinstalar {AppName}", ML, 20, bold: true, size: 14f);
            Lbl(_pWelcome,
                "¿Estás seguro de que quieres desinstalar OpenStreamMS?\n\n" +
                "• Se detendrá y eliminará el servicio de Windows\n" +
                "• Se eliminarán los accesos directos y reglas de firewall\n" +
                "• Se eliminarán los archivos del directorio de instalación",
                ML, 54, muted: true);

            _chkKeepData.Location = new(ML, 160);
            _pWelcome.Controls.Add(_chkKeepData);
            Lbl(_pWelcome,
                "Marca esta casilla para conservar tus sesiones y credenciales.\n" +
                "Si la dejas sin marcar (recomendado para reinstalación limpia) se borrará todo.",
                ML, 184, muted: true, size: 8.25f);

            _chkCleanDeps.Location = new(ML, 226);
            _pWelcome.Controls.Add(_chkCleanDeps);
            Lbl(_pWelcome,
                "Borrado completo. Desmárcala si usas esas dependencias con otras aplicaciones.",
                ML, 250, muted: true, size: 8.25f);
        }
        else
        {
            // Paso 0 — Bienvenida
            Lbl(_pWelcome, AppName,               ML, 20, bold: true, size: 18f);
            Lbl(_pWelcome, $"Versión {AppVersion}", ML, 60, muted: true);
            Lbl(_pWelcome,
                "Bienvenido al instalador de OpenStreamMS.\n\n" +
                "Este asistente instalará el servicio de streaming de juegos\n" +
                "para Windows con Sunshine y Moonlight.\n\n" +
                "Se requieren permisos de administrador.",
                ML, 90);

            // Paso 1 — Opciones
            Lbl(_pOptions, "Directorio de instalación:", ML, 16, bold: true);
            _txtDir.Location = new(ML, 42);
            _txtDir.Text     = DefaultInstallDir;

            var browse = new Button
            {
                Text      = "Examinar…",
                Location  = new(ML + 364 + 4, 40),
                Size      = new(88, 26),
                FlatStyle = FlatStyle.System,
            };
            browse.Click += (_, _) =>
            {
                using var d = new FolderBrowserDialog { SelectedPath = Path.GetDirectoryName(_txtDir.Text) ?? DefaultInstallDir };
                if (d.ShowDialog() == DialogResult.OK)
                    _txtDir.Text = Path.Combine(d.SelectedPath, AppName);
            };

            Lbl(_pOptions, "Opciones:", ML, 82, bold: true);
            _chkDesktop.Location = new(ML, 106);
            _chkService.Location = new(ML, 132);
            _chkTray   .Location = new(ML, 158);

            _pOptions.Controls.AddRange(new Control[] { _txtDir, browse, _chkDesktop, _chkService, _chkTray });
        }

        // Paso progreso (compartido)
        _pLog.Font    = new Font("Consolas", 8f);
        _pStatus.Font = new Font("Segoe UI", 8.5f);

        Lbl(_pProgress, _isUninstall ? "Desinstalando…" : "Instalando…", ML, 20, bold: true, size: 11f);
        _pbar  .Location = new(ML, 54);  _pbar  .Width = CW;
        _pStatus.Location = new(ML, 84); _pStatus.MaximumSize = new(CW, 0);
        _pLog  .Location = new(ML, 110); _pLog  .Size  = new(CW, 170);
        _pProgress.Controls.AddRange(new Control[] { _pbar, _pStatus, _pLog });

        // Paso final (compartido)
        _pDone.Controls.Add(new Label
        {
            Text      = "✓",
            Location  = new(ML, 24),
            Size      = new(46, 46),
            Font      = new Font("Segoe UI", 22f, FontStyle.Bold),
            ForeColor = Green,
        });
        Lbl(_pDone, _isUninstall ? "Desinstalación completada" : "Instalación completada",          ML + 52, 30, bold: true, size: 14f);
        Lbl(_pDone, _isUninstall ? "OpenStreamMS se ha desinstalado correctamente."
                                 : "OpenStreamMS está instalado y listo para usarse.",               ML + 52, 60, muted: true);
        if (!_isUninstall) { _chkOpenWeb.Location = new(ML, 110); _pDone.Controls.Add(_chkOpenWeb); }
    }

    // ── Navegación ────────────────────────────────────────────────────────────

    void ShowStep(int step)
    {
        var panels = StepPanels();
        foreach (var p in panels) p.Visible = false;
        panels[step].Visible = true;
        _step = step;

        bool isLast = step == panels.Length - 1;

        _btnBack  .Visible = !_isUninstall && step == 1;
        _btnCancel.Visible = !isLast;
        _btnNext  .Visible = step != panels.Length - 2; // ocultar durante el progreso

        if (_isUninstall)
        {
            switch (step)
            {
                case 0:
                    _hTitle.Text  = $"Desinstalar {AppName}";
                    _hSub.Text    = "Eliminar el servicio y todos los archivos instalados";
                    _btnNext.Text = "Desinstalar";
                    PaintBtn(_btnNext, Red);
                    break;
                case 2:
                    _hTitle.Text  = "Desinstalación completada";
                    _hSub.Text    = "";
                    _btnNext.Text = "Cerrar";
                    PaintBtn(_btnNext, Accent);
                    break;
            }
            return;
        }

        switch (step)
        {
            case 0:
                _hTitle.Text  = $"Instalar {AppName}";
                _hSub.Text    = $"Versión {AppVersion}  —  Servicio de streaming para Windows";
                _btnNext.Text = "Siguiente →";
                PaintBtn(_btnNext, Accent);
                break;
            case 1:
                _hTitle.Text  = "Opciones de instalación";
                _hSub.Text    = "Elige el directorio y las opciones de arranque";
                _btnNext.Text = "Instalar";
                PaintBtn(_btnNext, Accent);
                break;
            case 2:
                _hTitle.Text = "Instalando…";
                _hSub.Text   = "Por favor espera mientras se instala OpenStreamMS";
                break;
            case 3:
                _hTitle.Text  = "Instalación completada";
                _hSub.Text    = "OpenStreamMS está listo para usarse";
                _btnNext.Text = "Finalizar";
                PaintBtn(_btnNext, Green);
                break;
        }
    }

    async void OnNext(object? sender, EventArgs e)
    {
        if (_isUninstall)
        {
            if (_step == 0) { ShowStep(1); await RunUninstallAsync(); ShowStep(2); }
            else            { Close(); }
            return;
        }

        switch (_step)
        {
            case 0: ShowStep(1); break;
            case 1: ShowStep(2); await RunInstallAsync(); ShowStep(3); break;
            case 3:
                if (_chkOpenWeb.Checked)
                    Process.Start(new ProcessStartInfo("http://localhost:5000") { UseShellExecute = true });
                Close();
                break;
        }
    }

    // ── Lógica de instalación ─────────────────────────────────────────────────

    async Task RunInstallAsync()
    {
        var dir = _txtDir.Text.Trim();
        var src = Path.GetDirectoryName(Environment.ProcessPath)!;

        await Task.Run(() =>
        {
            Log(5,  "Creando directorio de instalación…");
            Directory.CreateDirectory(dir);

            if (!PathsEqual(src, dir))
            {
                Log(10, "Deteniendo servicio existente…");
                Run("net.exe", "stop OpenStreamMS");
                KillOtherInstances();

                Log(15, "Copiando archivos…");
                CopyDir(src, dir);
            }

            Log(50, "Registrando servicio de Windows…");
            Run(Path.Combine(dir, "OpenStreamMS.exe"), "--install --silent");

            Log(68, "Preparando icono de aplicación…");
            var exe     = Path.Combine(dir, "OpenStreamMS.exe");
            var icoPath = Path.Combine(dir, "app.ico");
            if (!File.Exists(icoPath))
                GenerateAppIcon(icoPath);

            Log(72, "Creando accesos directos…");
            MakeShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                             AppName, $"{AppName}.lnk"),
                exe, "--tray", icoPath);
            if (_chkDesktop.Checked)
                MakeShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                                 $"{AppName}.lnk"),
                    exe, "--tray", icoPath);

            Log(85, "Registrando en Agregar o quitar programas…");
            WriteUninstallReg(dir, exe, icoPath);

            if (_chkService.Checked)
            {
                Log(93, "Iniciando servicio…");
                Run("net.exe", "start OpenStreamMS");
            }
            if (_chkTray.Checked)
            {
                Log(98, "Iniciando icono de bandeja…");
                Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true });
            }

            Log(100, "Completado.");
        });
    }

    async Task RunUninstallAsync()
    {
        var dir        = GetInstallDir();
        bool cleanDeps = _chkCleanDeps.Checked;
        bool keepData  = _chkKeepData.Checked;

        await Task.Run(() =>
        {
            Log(5, "Deteniendo y eliminando servicio…");
            var exe = dir is not null ? Path.Combine(dir, "OpenStreamMS.exe") : null;
            if (exe is not null && File.Exists(exe))
                Run(exe, "--uninstall --silent");

            // Sin esto, el rd /s /q de más abajo deja restos: sunshine.exe y wfreerdp.exe
            // siguen vivos dentro de las sesiones RDP huérfanas y mantienen ficheros bloqueados.
            Log(15, "Cerrando procesos asociados (sunshine.exe, wfreerdp.exe)…");
            KillOtherInstances();

            Log(25, "Eliminando reglas de firewall creadas por OpenStreamMS…");
            RemoveFirewallRules();

            if (cleanDeps)
            {
                Log(40, "Desinstalando RDP Wrapper…");
                TryUninstallRdpWrapper();

                Log(60, "Desinstalando ViGEmBus…");
                TryUninstallViGEmBus();
            }

            Log(78, "Eliminando accesos directos…");
            TryDel(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                                $"{AppName}.lnk"));
            TryDelDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName));
            if (dir is not null) TryDel(Path.Combine(dir, "app.ico"));

            Log(86, "Eliminando registro…");
            Registry.LocalMachine.DeleteSubKey(UninstallReg, throwOnMissingSubKey: false);

            if (dir is not null)
            {
                Log(92, keepData
                    ? "Eliminando programa (conservando sesiones y configuración)…"
                    : "Eliminando todos los archivos de instalación…");
                ScheduleDelete(dir, keepData);
            }

            Log(100, "Completado.");
        });
    }

    /// <summary>
    /// Borra todas las reglas de firewall cuyo nombre comience con
    /// "OpenStreamMS - Sunshine ". Se corren vía PowerShell
    /// (<c>Remove-NetFirewallRule</c>) porque <c>netsh</c> no soporta wildcards
    /// en el nombre para <c>delete</c>.
    /// </summary>
    static void RemoveFirewallRules()
    {
        const string script =
            "try { Get-NetFirewallRule -DisplayName 'OpenStreamMS - Sunshine *' -ErrorAction SilentlyContinue | " +
            "Remove-NetFirewallRule -ErrorAction SilentlyContinue } catch { }; " +
            "try { Remove-NetFirewallRule -DisplayName 'OpenStreamMS - Web API' -ErrorAction SilentlyContinue } catch { }";
        Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"");
    }

    /// <summary>
    /// Si existe <c>%ProgramFiles%\RDP Wrapper\RDPWInst.exe</c>, lo ejecuta con
    /// <c>-u</c> para restaurar la <c>termsrv.dll</c> original. No lanza excepción
    /// si no está instalado.
    /// </summary>
    static void TryUninstallRdpWrapper()
    {
        try
        {
            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper");
            var inst = Path.Combine(installDir, "RDPWInst.exe");
            if (!File.Exists(inst)) return;

            Run(inst, "-u");
            try { Directory.Delete(installDir, recursive: true); }
            catch { /* puede quedar algún archivo bloqueado */ }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Busca la entrada de desinstalación de ViGEmBus en el registro y ejecuta
    /// su <c>UninstallString</c> en modo silencioso (<c>/quiet /uninstall /norestart</c>).
    /// </summary>
    static void TryUninstallViGEmBus()
    {
        foreach (var root in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            try
            {
                using var parent = Registry.LocalMachine.OpenSubKey(root);
                if (parent is null) continue;
                foreach (var subName in parent.GetSubKeyNames())
                {
                    using var sub = parent.OpenSubKey(subName);
                    var display = sub?.GetValue("DisplayName") as string;
                    if (display is null ||
                        !display.Contains("ViGEmBus", StringComparison.OrdinalIgnoreCase)) continue;

                    var cmd = sub?.GetValue("QuietUninstallString") as string
                           ?? sub?.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(cmd)) continue;

                    var (e, a) = SplitCmd(cmd.Trim());
                    // Forzamos flags silenciosos si el comando registrado es interactivo
                    foreach (var flag in new[] { "/quiet", "/uninstall", "/norestart" })
                        if (!a.Contains(flag, StringComparison.OrdinalIgnoreCase))
                            a = (a + " " + flag).Trim();
                    Run(e, a);
                    return;
                }
            }
            catch { /* best effort */ }
        }
    }

    static (string Exe, string Args) SplitCmd(string cmd)
    {
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].TrimStart());
        }
        var sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd[..sp], cmd[(sp + 1)..]) : (cmd, "");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void Log(int pct, string msg) => Invoke(() =>
    {
        _pbar.Value    = Math.Clamp(pct, 0, 100);
        _pStatus.Text  = msg;
        _pLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        _pLog.ScrollToCaret();
    });

    static void CopyDir(string src, string dst)
    {
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));

        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel  = Path.GetRelativePath(src, f);
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (Path.GetFileName(f).Equals("service.config.json", StringComparison.OrdinalIgnoreCase) && File.Exists(dest))
                continue;
            File.Copy(f, dest, overwrite: true);
        }
    }

    static void KillOtherInstances()
    {
        var self = Environment.ProcessId;
        string[] names = ["OpenStreamMS", "wfreerdp", "sunshine"];
        foreach (var name in names)
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (p.Id == self) continue;
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5_000); }
            catch { /* proceso ya terminado */ }
            finally { p.Dispose(); }
        }
    }

    static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        });
        p?.WaitForExit(30_000);
    }

    static void MakeShortcut(string lnk, string target, string args = "", string? iconPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var icon   = iconPath ?? target;
        var psBody = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{lnk}');" +
                     $"$s.TargetPath='{target}';" +
                     $"$s.Arguments='{args}';" +
                     $"$s.IconLocation='{icon},0';" +
                     $"$s.Save()";
        using var p = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{psBody}\"")
        { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(10_000);
    }

    static void WriteUninstallReg(string dir, string exe, string? iconPath = null)
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallReg)!;
        k.SetValue("DisplayName",          AppName);
        k.SetValue("DisplayVersion",       AppVersion);
        k.SetValue("Publisher",            AppPublisher);
        k.SetValue("URLInfoAbout",         AppUrl);
        k.SetValue("InstallLocation",      dir);
        k.SetValue("DisplayIcon",          iconPath is not null ? $"{iconPath},0" : $"{exe},0");
        k.SetValue("UninstallString",      $"\"{exe}\" --setup --uninstall");
        k.SetValue("QuietUninstallString", $"\"{exe}\" --setup --uninstall");
        k.SetValue("InstallDate",          DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    static void GenerateAppIcon(string destPath)
    {
        static byte[] RenderPng(int size)
        {
            using var bmp = new System.Drawing.Bitmap(size, size,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Transparent);

            // Purple circle
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(124, 58, 237));
            g.FillEllipse(bg, 0, 0, size - 1, size - 1);

            // Camera body rectangle
            float pw  = Math.Max(1f, size / 16f);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, pw);
            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            int bx = (int)(size * 0.10f), bw2 = (int)(size * 0.48f);
            int bh = (int)(size * 0.34f), by  = (size - bh) / 2;
            g.DrawRectangle(pen, bx, by, bw2, bh);

            // Play arrow (viewfinder/lens)
            float tx = bx + bw2 + size * 0.04f;
            float th = bh * 0.80f, ty = (size - th) / 2f;
            var pts = new System.Drawing.PointF[]
            {
                new(tx,                   ty),
                new(tx + size * 0.19f,    ty + th / 2f),
                new(tx,                   ty + th),
            };
            using var wb = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.FillPolygon(wb, pts);

            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }

        int[] sizes  = [16, 24, 32, 48, 256];
        var   images = sizes.Select(RenderPng).ToArray();

        int headerSize = 6 + 16 * sizes.Length;
        var offsets    = new int[sizes.Length];
        offsets[0]     = headerSize;
        for (int i = 1; i < sizes.Length; i++)
            offsets[i] = offsets[i - 1] + images[i - 1].Length;

        using var fs = File.Create(destPath);
        using var bw = new BinaryWriter(fs);

        // ICONDIR header
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);

        // ICONDIRENTRY for each size
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
            bw.Write(dim); bw.Write(dim);
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)images[i].Length);
            bw.Write((uint)offsets[i]);
        }

        foreach (var img in images) bw.Write(img);
    }

    static string? GetInstallDir()
    {
        using var k = Registry.LocalMachine.OpenSubKey(UninstallReg);
        return k?.GetValue("InstallLocation") as string;
    }

    /// <summary>
    /// Programa la eliminación del directorio de instalación tras 3 s, ya que el .exe
    /// que ejecuta el wizard vive dentro de él y no se puede borrar mientras corre.
    /// Si <paramref name="keepData"/> = true, conserva <c>sessions.json</c>,
    /// <c>service.config.json</c> y la carpeta <c>sessions/</c> para que sobrevivan a una reinstalación.
    /// </summary>
    static void ScheduleDelete(string dir, bool keepData = false)
    {
        var bat = Path.Combine(Path.GetTempPath(), "osm_cleanup.bat");
        string script;

        if (keepData)
        {
            // PowerShell con -EncodedCommand para evitar el infierno de comillas anidadas
            // (cmd dentro de bat dentro de string interpolada). Borra todo lo del directorio
            // que NO esté en la lista de items a conservar.
            var ps = $@"$keep = @('sessions.json','service.config.json','sessions')
Get-ChildItem -LiteralPath '{dir.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue | Where-Object {{ $keep -notcontains $_.Name }} | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
            script =
                "@echo off\r\n" +
                "timeout /t 3 /nobreak >nul\r\n" +
                $"powershell.exe -NoProfile -NonInteractive -EncodedCommand {encoded}\r\n" +
                "del \"%~f0\"\r\n";
        }
        else
        {
            script =
                "@echo off\r\n" +
                "timeout /t 3 /nobreak >nul\r\n" +
                $"rd /s /q \"{dir}\"\r\n" +
                "del \"%~f0\"\r\n";
        }

        File.WriteAllText(bat, script);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        { CreateNoWindow = true, UseShellExecute = false });
    }

    static bool PathsEqual(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\').Equals(
            Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static void TryDel(string p)    { try { File.Delete(p); }            catch { } }
    static void TryDelDir(string p) { try { Directory.Delete(p, true); } catch { } }

    static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    bool ConfirmCancel() =>
        _step >= 2 ||
        MessageBox.Show("¿Cancelar la instalación?", AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    Panel[] StepPanels() => _isUninstall
        ? new[] { _pWelcome, _pProgress, _pDone }
        : new[] { _pWelcome, _pOptions,  _pProgress, _pDone };

    // ── UI helpers ────────────────────────────────────────────────────────────

    static void StyleBtn(Button b, string text, bool primary)
    {
        b.Text      = text;
        b.Size      = new(100, 30);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        if (primary) { b.BackColor = Accent; b.ForeColor = Color.White; }
        else
        {
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(80, 80, 100);
            b.FlatAppearance.BorderSize  = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 220);
        }
    }

    static void PaintBtn(Button b, Color c) { b.BackColor = c; b.ForeColor = Color.White; }

    // AutoSize=true + MaximumSize limita el ancho pero deja que el alto crezca libremente
    static void Lbl(Panel p, string text, int x, int y,
                    bool bold = false, float size = 9f, bool muted = false) =>
        p.Controls.Add(new Label
        {
            Text        = text,
            Location    = new(x, y),
            AutoSize    = true,
            MaximumSize = new(540 - x - MR, 0),
            Font        = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor   = muted ? Muted : Dark,
        });
}
