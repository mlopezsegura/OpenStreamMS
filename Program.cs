using Microsoft.Win32;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;
using OpenStreamMS.Services.OpenStream;
using OpenStreamMS.Services.Session;
using OpenStreamMS.Services.TrayApp;
using OpenStreamMS.Services.VigEmBus;
using Scalar.AspNetCore;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;

const string ServiceName    = "OpenStreamMS";
const string ServiceDisplay = "OpenStreamMS - Game Streaming Service";
const string ServiceDesc    = "Crea una sesión RDP para el usuario de streaming y lanza Sunshine en ella.";

// When double-clicked (no args) and not yet installed → show setup wizard
var command = args.Length > 0
    ? args[0].ToLowerInvariant()
    : IsRunningFromProgramFiles() ? "--help" : "--setup";

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
    case "--tray":
        RunTray();
        break;
    case "--setup":
        RunSetup(args);
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

    var builder = WebApplication.CreateBuilder(args);

    // Raíz del contenido = directorio del exe (para encontrar wwwroot correctamente)
    builder.Host.UseContentRoot(AppDomain.CurrentDomain.BaseDirectory);
    builder.Host.UseWindowsService(options => options.ServiceName = ServiceName);
    builder.WebHost.UseUrls($"http://0.0.0.0:{config.ApiPort}");

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<StreamSessionService>();
    builder.Services.AddSingleton<SunshineProxy>();
    builder.Services.AddSingleton<RdpWrapperManager>();
    builder.Services.AddSingleton<TermWrapManager>();
    builder.Services.AddSingleton<ViGEmBusManager>();
    builder.Services.AddHostedService<OpenStreamService>();

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

    // ── Middleware de autenticación (debe ir antes de static files) ───────────
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "/";
        var cfg  = ctx.RequestServices.GetRequiredService<ServiceConfig>();

        // Rutas públicas que no necesitan autenticación
        if (IsPublicPath(path)) { await next(); return; }

        // Acceso local sin auth (tray app u otras herramientas locales)
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            ctx.Connection.RemoteIpAddress is { } remoteIp &&
            IPAddress.IsLoopback(remoteIp))
        {
            await next(); return;
        }

        // Primera vez: aún no se han configurado credenciales → pantalla de setup
        if (string.IsNullOrEmpty(cfg.AdminUsername))
        {
            ctx.Response.Redirect("/setup.html");
            return;
        }

        // ¿Autenticado? (cookie o Basic Auth)
        if (AuthService.IsAuthenticated(ctx.Request, cfg)) { await next(); return; }

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

        if (!AuthService.VerifyPassword(cfg, username, password))
            return Results.Redirect("/login.html?error=1");

        AuthService.SetAuthCookie(ctx.Response, cfg, username);
        Logger.Log($"[Auth] Login correcto: {username}");
        return Results.Redirect("/");
    });

    app.MapPost("/setup", async (HttpContext ctx, ServiceConfig cfg) =>
    {
        // Solo disponible si todavía no hay credenciales
        if (!string.IsNullOrEmpty(cfg.AdminUsername))
            return Results.Redirect("/login.html");

        var form     = await ctx.Request.ReadFormAsync();
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Results.Redirect("/setup.html?error=1");

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

    // ── TermWrap API (recomendado — auto-detecta offsets de termsrv.dll) ──────
    app.MapTermWrapApi();

    // ── RDP Wrapper API (fallback por si TermWrap se rompe en una build) ──────
    app.MapRdpWrapperApi();

    // ── ViGEmBus API ──────────────────────────────────────────────────────────
    app.MapViGEmBusApi();

    // ── Power API ─────────────────────────────────────────────────────────────
    app.MapPowerApi();

    Logger.Log($"[Service] Web disponible en http://localhost:{config.ApiPort}");
    Logger.Log($"[Service] API docs en http://localhost:{config.ApiPort}/scalar/v1");

    await app.RunAsync();
}

// ── tray runner ──────────────────────────────────────────────────────────────

static void RunTray()
{
    using var mutex = new Mutex(true, "OpenStreamMS_Tray", out var isNew);
    if (!isNew)
        return; // already running

    var config = ServiceConfig.Load();
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    using var tray = new TrayApp($"http://localhost:{config.ApiPort}");
    Application.Run(tray);
}

// ── setup wizard ─────────────────────────────────────────────────────────────

static void RunSetup(string[] args)
{
    bool isUninstall = args.Contains("--uninstall");

    if (!IsAdmin())
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                Verb            = "runas",
                Arguments       = isUninstall ? "--setup --uninstall" : "--setup",
                UseShellExecute = true,
            });
        }
        catch { /* user cancelled UAC prompt */ }
        return;
    }

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new OpenStreamMS.SetupWizard(isUninstall));
}

static bool IsAdmin()
{
    using var id = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}

static bool IsRunningFromProgramFiles()
{
    var exe   = Environment.ProcessPath ?? "";
    var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    return exe.StartsWith(pf,    StringComparison.OrdinalIgnoreCase)
        || exe.StartsWith(pfx86, StringComparison.OrdinalIgnoreCase);
}

// ── Rutas públicas (sin autenticación) ───────────────────────────────────────

static bool IsPublicPath(string path) =>
    path.StartsWith("/login",   StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/setup",   StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/style",   StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/app.js",  StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/i18n.js", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase);

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
    if (!silent) Console.WriteLine("[Uninstall] Regla de firewall de la API eliminada.");

    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
    key?.DeleteValue("OpenStreamMSTray", throwOnMissingValue: false);
    if (!silent) Console.WriteLine("[Uninstall] Icono de bandeja eliminado del inicio automático.");
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
          OpenStreamMS.exe --setup             Abrir el asistente gráfico de instalación
          OpenStreamMS.exe --setup --uninstall Abrir el asistente gráfico de desinstalación
          OpenStreamMS.exe --install           Instalar y registrar el servicio de Windows
          OpenStreamMS.exe --uninstall         Detener y eliminar el servicio de Windows
          OpenStreamMS.exe --tray              Mostrar icono en la bandeja del sistema
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
