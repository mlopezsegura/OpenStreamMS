using Microsoft.Win32;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;
using OpenStreamMS.Services.OpenStream;
using OpenStreamMS.Services.Session;
using OpenStreamMS.Services.TrayApp;
using OpenStreamMS.Services.Drivers;
using Scalar.AspNetCore;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;

const string ServiceName    = "OpenStreamMS";
const string ServiceDisplay = "OpenStreamMS - Game Streaming Service";
const string ServiceDesc    = "Crea una sesión RDP para el usuario de streaming y lanza Sunshine en ella.";

// Se instala y desinstala con OpenStreamMS-Setup.exe (Inno Setup), que llama a estos comandos.
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "--help";

switch (command)
{
    case "--run":
        await RunService(args);
        break;
    case "--install":
        AttachParentConsole();
        Install(silent: args.Contains("--silent"));
        break;
    case "--uninstall":
        AttachParentConsole();
        Uninstall(silent: args.Contains("--silent"));
        break;
    case "--purge-data":
        // Desinstalación con "borrar todos los datos": lo que queda fuera de la carpeta del programa.
        AttachParentConsole();
        RequireAdmin();
        RunSc($"stop \"{ServiceName}\"");   // que no vuelva a crear lo que se borra
        Environment.Exit(OpenStreamMS.Core.Helpers.DataPurge.Run());
        break;
    case "--tray":
        RunTray();
        break;
    case "--open":
        // Menú Inicio y escritorio: el panel en su ventana (y el icono de bandeja si no estaba).
        RunTray(showWindow: true);
        break;
    case "--display":
        // Puestos locales: operaciones de pantalla en la sesión de consola (lanzado por el servicio).
        Environment.Exit(OpenStreamMS.Services.Seats.ConsoleDisplay.RunCommand(args));
        break;
    case "--identify":
        // Puestos locales: identificar monitor, audio o dispositivos en la consola (lanzado por el servicio).
        Environment.Exit(OpenStreamMS.Services.Seats.SeatIdentify.RunCommand(args));
        break;
    case "--seat-agent":
        // Puestos locales: agente de captura en la sesión del puesto
        if (args.Length < 2)
        {
            AttachParentConsole();
            Console.Error.WriteLine("ERROR: Falta el nombre de la tubería para --seat-agent.");
            Environment.Exit(1);
        }
        Environment.Exit(OpenStreamMS.Services.Seats.SeatAgent.Run(args[1]));
        break;
    case "--seat-host":
        // Puestos locales: presentador en la sesión de consola
        if (args.Length < 2)
        {
            AttachParentConsole();
            Console.Error.WriteLine("ERROR: Falta el nombre de la tubería para --seat-host.");
            Environment.Exit(1);
        }
        Guid hostSeatId = args.Length > 2 && Guid.TryParse(args[2], out var sid) ? sid : Guid.Empty;
        Environment.Exit(OpenStreamMS.Services.Seats.SeatHost.RunStandalone(args[1], hostSeatId));
        break;
    case "--remove-deps":
        // Desinstalación con "quitar también las dependencias": RDP Wrapper, ViGEmBus y HidHide.
        AttachParentConsole();
        RequireAdmin();
        RemoveDependencies();
        break;
    case "--help":
    case "-h":
        AttachParentConsole();
        PrintHelp();
        break;
    default:
        AttachParentConsole();
        Console.Error.WriteLine($"Comando desconocido: '{args[0]}'");
        Console.Error.WriteLine("Usa --help para ver los comandos disponibles.");
        Environment.Exit(1);
        break;
}

// ── service runner ────────────────────────────────────────────────────────────

static async Task RunService(string[] args)
{
    var config = ServiceConfig.Load();

    // Secretos fuera del alcance de los demás usuarios del equipo (puestos, sesiones de streaming).
    SecureFile.RestrictToAdmins(ServiceConfig.FilePath);
    SecureFile.RestrictToAdmins(StreamSessionService.SessionsFilePath);
    var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS");
    SecureFile.RestrictToAdmins(Path.Combine(programData, "state"));
    SecureFile.RestrictToAdmins(Path.Combine(programData, "state-index"));
    ServiceConfig.PublishApiPort(config.ApiPort);

    var builder = WebApplication.CreateBuilder(args);

    // Raíz del contenido = directorio del exe (para encontrar wwwroot correctamente)
    builder.Host.UseContentRoot(AppDomain.CurrentDomain.BaseDirectory);
    builder.Host.UseWindowsService(options => options.ServiceName = ServiceName);
    builder.WebHost.UseUrls($"http://0.0.0.0:{config.ApiPort}");

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<StreamSessionService>();
    builder.Services.AddSingleton<SunshineProxy>();
    builder.Services.AddSingleton<RdpWrapperManager>();
    builder.Services.AddSingleton<ViGEmBusManager>();
    builder.Services.AddSingleton<HidHideManager>();
    builder.Services.AddSingleton<OpenStreamMS.Services.Seats.SeatManager>();
    builder.Services.AddHostedService<OpenStreamService>();
    builder.Services.AddHostedService<OpenStreamMS.Services.Gamepad.GamepadSessionIsolation>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OpenStreamMS.Services.Seats.SeatManager>());

    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));

    builder.Services.AddOpenApi(opt =>
    {
        opt.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title       = "OpenStreamMS API";
            doc.Info.Version     = "v1";
            doc.Info.Description = "API REST para gestionar sesiones de streaming con Sunshine y RDP.";
            return Task.CompletedTask;
        });
    });

    var app = builder.Build();

    // ── Cabeceras de seguridad y freno a peticiones de otros sitios ───────────
    app.Use(async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"]  = "nosniff";
        headers["X-Frame-Options"]         = "DENY";
        headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        // same-origin y no no-referrer: con no-referrer el navegador manda "Origin: null" en
        // los formularios propios (login) y el freno de abajo los tomaría por ajenos.
        headers["Referrer-Policy"]         = "same-origin";

        // Una web cualquiera abierta en un navegador de la red podría lanzar POST contra el
        // panel (apagar el PC, borrar sesiones…) aprovechando credenciales Basic recordadas.
        // Los navegadores marcan esas peticiones con Origin / Sec-Fetch-Site.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) &&
            IsCrossSite(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    });

    // ── Middleware de autenticación (debe ir antes de static files) ───────────
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "/";
        var cfg  = ctx.RequestServices.GetRequiredService<ServiceConfig>();

        // Rutas públicas que no necesitan autenticación
        if (IsPublicPath(path)) { await next(); return; }

        // Icono de bandeja de la consola: solo lo que muestra su menú, sin contraseña.
        // "Viene de este PC" no basta: los puestos y las sesiones de streaming también son
        // este PC, con otros usuarios. Se exige que el proceso esté en la sesión de la consola.
        if (IsTrayRequest(ctx) && IsConsoleLocal(ctx)) { await next(); return; }

        // Primera vez: aún no se han configurado credenciales → pantalla de setup
        if (string.IsNullOrEmpty(cfg.AdminUsername))
        {
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                ctx.Response.Redirect("/setup.html");
            return;
        }

        // ¿Autenticado? (cookie o Basic Auth)
        if (AuthService.IsAuthenticated(ctx, cfg)) { await next(); return; }

        // No autenticado
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"OpenStreamMS\"";
        }
        else
        {
            ctx.Response.Redirect("/login.html");
        }
    });

    // ── Archivos estáticos (wwwroot/) ─────────────────────────────────────────
    // UseDefaultFiles mapea / → /index.html y debe ir antes de UseStaticFiles
    app.UseDefaultFiles();
    app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        // no-cache + must-revalidate: el navegador SÍ cachea pero revalida cada vez
        // (ETag/Last-Modified). Resultado: 304 si no cambió, 200 con la versión nueva
        // si cambió. Sin esto, los .js/.css cacheados pueden quedarse desincronizados
        // del .html y disparar ReferenceError al llamar funciones nuevas.
        OnPrepareResponse = ctx =>
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate"
    });

    // ── Endpoints de autenticación ────────────────────────────────────────────
    app.MapPost("/login", async (HttpContext ctx, ServiceConfig cfg) =>
    {
        var form     = await ctx.Request.ReadFormAsync();
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();

        var ip = ctx.Connection.RemoteIpAddress;
        if (AuthService.IsLockedOut(ip))
            return Results.Redirect("/login.html?error=1");

        bool ok = AuthService.VerifyPassword(cfg, username, password);
        AuthService.RecordAttempt(ip, ok);
        if (!ok)
        {
            Logger.Warning($"[Auth] Login fallido desde {ip}: '{username}'");
            return Results.Redirect("/login.html?error=1");
        }

        AuthService.SetAuthCookie(ctx.Response, cfg, username);
        Logger.Log($"[Auth] Login correcto: {username}");
        return Results.Redirect("/");
    });

    app.MapPost("/setup", async (HttpContext ctx, ServiceConfig cfg) =>
    {
        // Solo disponible si todavía no hay credenciales
        if (!string.IsNullOrEmpty(cfg.AdminUsername))
            return Results.Redirect("/login.html");

        // Mientras no hay administrador, el primero que llegue lo crea: solo desde la consola
        // de este PC, no desde cualquier equipo de la red ni desde un puesto o sesión remota.
        if (!IsConsoleLocal(ctx))
        {
            Logger.Warning($"[Auth] Intento de configuración inicial rechazado desde {ctx.Connection.RemoteIpAddress}.");
            return Results.Redirect("/setup.html?error=remote");
        }

        var form     = await ctx.Request.ReadFormAsync();
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Results.Redirect("/setup.html?error=1");
        if (password.Length < 8)
            return Results.Redirect("/setup.html?error=short");

        AuthService.SetCredentials(cfg, username, password);
        Logger.Log($"[Auth] Credenciales configuradas para '{username}'.");
        return Results.Redirect("/login.html?setup=1");
    });

    app.MapPost("/logout", (HttpContext ctx) =>
    {
        AuthService.ClearAuthCookie(ctx.Response);
        return Results.Redirect("/login.html");
    });

    // ── OpenAPI / Scalar ──────────────────────────────────────────────────────
    app.MapOpenApi();
    app.MapScalarApiReference(opt =>
    {
        opt.Title             = "OpenStreamMS";
        opt.Theme             = ScalarTheme.Purple;
        opt.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });

    // ── Session API ───────────────────────────────────────────────────────────
    app.MapSessionApi();

    // ── Seats API (puestos locales) ───────────────────────────────────────────
    app.MapSeatApi();

    // ── RDP Wrapper API (varias sesiones RDP simultáneas en Windows cliente) ──
    app.MapRdpWrapperApi();

    // ── Drivers (ViGEmBus, HidHide) API ───────────────────────────────────────
    app.MapDriverApis();

    // ── Power API ─────────────────────────────────────────────────────────────
    app.MapPowerApi();

    Logger.Log($"[Service] Web disponible en http://localhost:{config.ApiPort}");
    Logger.Log($"[Service] API docs en http://localhost:{config.ApiPort}/scalar/v1");

    await app.RunAsync();
}

// ── tray runner ──────────────────────────────────────────────────────────────

static void RunTray(bool showWindow = false)
{
    using var mutex = new Mutex(true, "OpenStreamMS_Tray", out var isNew);
    if (!isNew)
    {
        // Ya hay icono en esta sesión: que abra él la ventana.
        if (showWindow && EventWaitHandle.TryOpenExisting(TrayApp.ShowWindowEvent, out var show))
            using (show) show.Set();
        return;
    }

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    using var tray = new TrayApp($"http://localhost:{ServiceConfig.ReadApiPort()}", showWindow);
    Application.Run(tray);
}

// ── Rutas públicas (sin autenticación) ───────────────────────────────────────

static bool IsPublicPath(string path) => path.ToLowerInvariant() is
    "/login" or "/login.html" or "/setup" or "/setup.html" or "/style.css" or
    "/app.js" or "/seats.js" or "/i18n.js" or "/favicon.svg" or "/favicon.ico";

// ── Llamadas locales ─────────────────────────────────────────────────────────

/// <summary>Cabecera que pone el icono de bandeja. Una web no puede añadirla a una petición a otro sitio.</summary>
static bool IsTrayRequest(HttpContext ctx)
{
    if (!ctx.Request.Headers.ContainsKey(TrayApp.LocalHeader)) return false;
    var segments = (ctx.Request.Path.Value ?? "").Trim('/').ToLowerInvariant().Split('/');
    // GET /api/sessions  ·  POST /api/sessions/{id}/start|stop
    return segments is ["api", "sessions"]
        ? HttpMethods.IsGet(ctx.Request.Method)
        : HttpMethods.IsPost(ctx.Request.Method) &&
          segments is ["api", "sessions", var id, "start" or "stop"] && Guid.TryParse(id, out _);
}

/// <summary>
/// La petición la hace un proceso de la sesión de la consola física, hacia localhost (el
/// nombre del Host descarta el DNS rebinding: una web que resuelva su dominio a 127.0.0.1).
/// </summary>
static bool IsConsoleLocal(HttpContext ctx)
{
    var host = ctx.Request.Host.Host.ToLowerInvariant();
    if (host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1")) return false;
    var conn = ctx.Connection;
    int pid = LocalCaller.TcpClientPid(conn.RemoteIpAddress, conn.RemotePort, conn.LocalPort);
    return pid > 0 && LocalCaller.IsInConsoleSession(pid);
}

/// <summary>Petición lanzada por un navegador desde una página de otro sitio.</summary>
static bool IsCrossSite(HttpRequest req)
{
    var origin = req.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && origin != "null")
        return !Uri.TryCreate(origin, UriKind.Absolute, out var o) ||
               !string.Equals(o.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase);
    if (origin == "null") return true;
    return req.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site";
}

// ── helpers CLI ───────────────────────────────────────────────────────────────

static void Install(bool silent = false)
{
    RequireAdmin();

    var exePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("No se pudo obtener la ruta del ejecutable.");

    var configPath = Path.Combine(Path.GetDirectoryName(exePath)!, "service.config.json");

    if (!silent)
    {
        Console.WriteLine($"[Install] Ejecutable : {exePath}");
        Console.WriteLine($"[Install] Configuración: {configPath}");
        Console.WriteLine();
    }

    RunSc($"create \"{ServiceName}\" binPath= \"{exePath} --run\" start= auto DisplayName= \"{ServiceDisplay}\"");
    RunSc($"description \"{ServiceName}\" \"{ServiceDesc}\"");

    var apiPort = ServiceConfig.Load().ApiPort;
    AddApiFirewallRule(apiPort);
    if (!silent) Console.WriteLine($"[Install] Regla de firewall añadida para puerto {apiPort} (TCP).");

    // Drivers de mando: ViGEmBus (mando virtual) y HidHide (aislamiento por sesión)
    foreach (NefariusDriverManager driver in new NefariusDriverManager[] { new ViGEmBusManager(), new HidHideManager() })
    {
        driver.EnsureInstalled();
        var st = driver.GetStatus();
        Console.WriteLine(st.Installed
            ? $"[Install] {driver.Name} instalado ({st.InstalledVersion ?? "versión desconocida"}){(st.RebootRequired ? "; requiere reiniciar Windows" : "")}."
            : $"[Install] AVISO: no se pudo instalar {driver.Name}; puede instalarse desde el panel web.");
    }

    // El asistente de instalación antiguo dejaba su propia entrada en "Aplicaciones": ahora solo
    // existe la del instalador (Inno Setup), que es el que desinstala.
    Registry.LocalMachine.DeleteSubKeyTree(
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OpenStreamMS", throwOnMissingSubKey: false);

    // Registrar icono de bandeja en inicio automático (todos los usuarios)
    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    using (var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true))
    {
        key?.SetValue("OpenStreamMSTray", $"\"{exePath}\" --tray");
        Console.WriteLine("[Install] Icono de bandeja registrado para inicio automático.");
    }
    Console.WriteLine();

    if (!silent)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine("[Install] AVISO: No se encontró service.config.json.");
            Console.WriteLine($"[Install]        Se creará uno por defecto en {configPath} al iniciar el servicio.");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("[Install] service.config.json encontrado.");
        }

        Console.Write("[Install] ¿Iniciar el servicio ahora? (S/n): ");
        var answer = Console.ReadLine()?.Trim().ToUpperInvariant();
        if (answer is "" or "S" or "Y")
        {
            RunSc($"start \"{ServiceName}\"");
            Console.WriteLine("[Install] Servicio iniciado.");
        }
        else
        {
            Console.WriteLine($"[Install] Puedes iniciarlo más tarde con:  sc start {ServiceName}");
        }

        Console.WriteLine("[Install] Instalación completada.");
    }
}

static void Uninstall(bool silent = false)
{
    RequireAdmin();
    if (!silent) Console.WriteLine("[Uninstall] Deteniendo servicio...");
    RunSc($"stop \"{ServiceName}\"");
    Thread.Sleep(2000);
    if (!silent) Console.WriteLine("[Uninstall] Eliminando servicio...");
    RunSc($"delete \"{ServiceName}\"");
    if (!silent) Console.WriteLine("[Uninstall] Servicio eliminado.");

    RemoveApiFirewallRule();
    // Las de cada sesión ("OpenStreamMS - Sunshine <nombre> ..."): netsh no admite comodines.
    RunProcess("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-NetFirewallRule -DisplayName 'OpenStreamMS - Sunshine *' " +
                                 "-ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue\"");
    if (!silent) Console.WriteLine("[Uninstall] Reglas de firewall de la API y de las sesiones eliminadas.");

    // Sin el servicio nadie las mantendría: los mandos quedarían ocultos a otras sesiones
    OpenStreamMS.Services.Gamepad.GamepadSessionIsolation.ClearJails();
    if (!silent) Console.WriteLine("[Uninstall] Aislamiento de mandos por sesión retirado de HidHide.");

    // Lo que los puestos cambian en el sistema mientras están encendidos, por si el servicio
    // se paró sin deshacerlo, y el derecho de apagar que se quitó al grupo Usuarios.
    OpenStreamMS.Services.Seats.UsbCapture.RestoreAll();
    OpenStreamMS.Services.Seats.ConsoleLockGuard.Release();
    OpenStreamMS.Core.Helpers.ShutdownRight.Restore();
    if (!silent) Console.WriteLine("[Uninstall] Ratones/teclados de los puestos, bloqueo de la consola y derecho de apagar restaurados.");

    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
    key?.DeleteValue("OpenStreamMSTray", throwOnMissingValue: false);
    if (!silent) Console.WriteLine("[Uninstall] Icono de bandeja eliminado del inicio automático.");
}

static void RemoveDependencies()
{
    // RDP Wrapper: su instalador restaura la termsrv.dll original con -u.
    var rdpDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper");
    var rdpInst = Path.Combine(rdpDir, "RDPWInst.exe");
    if (File.Exists(rdpInst))
    {
        RunProcess(rdpInst, "-u");
        try { Directory.Delete(rdpDir, recursive: true); } catch { /* algún fichero bloqueado */ }
        Console.WriteLine("[Uninstall] RDP Wrapper desinstalado.");
    }

    foreach (NefariusDriverManager driver in new NefariusDriverManager[] { new ViGEmBusManager(), new HidHideManager() })
    {
        try
        {
            driver.UninstallAsync().GetAwaiter().GetResult();
            Console.WriteLine($"[Uninstall] {driver.Name} desinstalado.");
        }
        catch (Exception ex) { Console.WriteLine($"[Uninstall] {driver.Name}: {ex.Message}"); }
    }
}

static void RunProcess(string exe, string args)
{
    using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
    p.WaitForExit(120_000);
}

static void AddApiFirewallRule(int port)
{
    const string name = "OpenStreamMS - Web API";
    RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
    RunNetsh($"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port} profile=any");
}

static void RemoveApiFirewallRule()
{
    RunNetsh("advfirewall firewall delete rule name=\"OpenStreamMS - Web API\"");
}

static void RunNetsh(string args)
{
    var psi = new ProcessStartInfo("netsh.exe", args)
    {
        UseShellExecute        = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        CreateNoWindow         = true,
    };
    using var p = Process.Start(psi)!;
    p.WaitForExit();
}

static void RunSc(string scArgs)
{
    var psi = new ProcessStartInfo("sc.exe", scArgs)
    {
        UseShellExecute        = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        CreateNoWindow         = true,
    };
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    var out_ = p.StandardOutput.ReadToEnd().Trim();
    var err_ = p.StandardError.ReadToEnd().Trim();
    if (!string.IsNullOrEmpty(out_)) Console.WriteLine(out_);
    if (!string.IsNullOrEmpty(err_)) Console.WriteLine(err_);
}

static void RequireAdmin()
{
    using var id = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("ERROR: Esta operación requiere permisos de administrador.");
        Console.Error.WriteLine("       Ejecuta el .exe con 'Ejecutar como administrador'.");
        Environment.Exit(1);
    }
}

static void AttachParentConsole()
{
    // Attach to the parent process console (cmd/PowerShell) if one exists.
    // Fails silently when launched without a console (tray, service, double-click).
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);
    AttachConsole(-1 /* ATTACH_PARENT_PROCESS */);
}

static void PrintHelp()
{
    Console.WriteLine($"""
        OpenStreamMS — Game Streaming Service

        Uso:
          OpenStreamMS.exe --run               Ejecutar el servicio con web y API REST
          OpenStreamMS.exe --install           Instalar y registrar el servicio de Windows
          OpenStreamMS.exe --uninstall         Detener y eliminar el servicio de Windows
          OpenStreamMS.exe --remove-deps       Desinstalar RDP Wrapper, ViGEmBus y HidHide
          OpenStreamMS.exe --purge-data        Borrar los datos fuera de la carpeta del programa
                                               (ProgramData, usuarios osms-, perfiles de stream, temporales)
          OpenStreamMS.exe --tray              Mostrar icono en la bandeja del sistema
          OpenStreamMS.exe --open              Abrir el panel en su ventana (y la bandeja)
          OpenStreamMS.exe --seat-agent <pipe> Ejecutar agente de puesto en la sesión
          OpenStreamMS.exe --seat-host <pipe>  Ejecutar presentador de puesto en la consola
          OpenStreamMS.exe --help              Mostrar esta ayuda

        Interfaz web:
          http://localhost:<ApiPort>/          Panel de control (requiere login)
          http://localhost:<ApiPort>/scalar/v1 Documentación API interactiva

        Configuración (service.config.json):
          SunshineExePath  — ruta a sunshine.exe (relativa o absoluta)
          RdpBackground    — true para ocultar la ventana RDP (producción)
          ApiPort          — puerto del servidor web/API (defecto: 5000)
          AdminUsername    — usuario del panel web (se configura en el primer arranque)
        """);
}
