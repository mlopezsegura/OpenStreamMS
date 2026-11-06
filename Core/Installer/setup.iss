; OpenStreamMS — Inno Setup Script
; Requiere Inno Setup 6.7+ https://jrsoftware.org/isinfo.php (fondos y tema claro/oscuro: theme.iss)
;
; Antes de compilar ejecuta: build-installer.bat
; El instalador resultante se genera en ..\dist\

#define AppName      "OpenStreamMS"
; La CI pasa la versión con /DAppVersion=x.y.z (sale del tag de la release).
#ifndef AppVersion
  #define AppVersion "1.5.1"
#endif
#define AppPublisher "mlopezsegura"
#define AppURL       "https://github.com/mlopezsegura/OpenStreamMS"
#define AppExeName   "OpenStreamMS.exe"
#define AppService   "OpenStreamMS"

[Setup]
AppId={{F2A1B3C4-D5E6-7890-ABCD-EF1234567891}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=OpenStreamMS-Setup-{#AppVersion}
; Cada archivo por separado y con diccionario moderado: con compresión sólida ultra64 un
; solo byte mal leído al descomprimir tumba la instalación entera, y gasta mucha más memoria.
Compression=lzma2/max
SolidCompression=no
; Log de la instalación en %TEMP%\Setup Log *.txt (para ver el error exacto si falla)
SetupLogging=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
; Requiere Windows 10 1809+
MinVersion=10.0.17763

[Languages]
#include "languages.generated.iss"

[CustomMessages]
#include "messages.generated.iss"

; Tema claro/oscuro y fondo propio: necesitan Inno Setup 6.7. Con uno anterior (p. ej. el de la
; imagen de la CI) sale el instalador de siempre.
#if Ver >= EncodeVer(6, 7, 0)
  #include "theme.iss"
#else
  #pragma warning "Inno Setup anterior a 6.7: instalador sin tema claro/oscuro ni fondo propio."
[Setup]
WizardStyle=modern
SetupIconFile=..\..\app.ico
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startservice"; Description: "{cm:TaskStartService}"; GroupDescription: "{cm:GroupService}"
; RemoteIdd (pantalla de los puestos) no es una tarea: se instala siempre (ver [Run], 1b).

[Files]
; Toda la salida de dotnet publish (runtime incluido)
Source: "publish\*"; DestDir: "{app}"; Excludes: "service.config.json"; Flags: ignoreversion recursesubdirs createallsubdirs
; service.config.json: solo si no existe ya (no sobreescribir configuración del usuario)
Source: "publish\service.config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--open"; \
  Comment: "{cm:IconComment}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--open"; \
  Tasks: desktopicon

[Run]
; 1. Instalar el servicio Windows (y registrar el tray en inicio automático)
Filename: "{app}\{#AppExeName}"; Parameters: "--install --silent"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "{cm:StatusInstallService}"

; 1b. Driver de pantalla de los puestos, obligatorio (como el de DuoStream): no es una tarea que
;     se pueda desmarcar. Firma de prueba: mete su certificado en los de confianza y cambia la
;     DLL de TermService (TermWrap); ver package-remoteidd.ps1. Los Hz son solo los de partida:
;     OpenStreamMS los pone en cada conexión a los fps de la sesión.
;     Quita la instalación anterior (si la hay) y pone la del paquete firmado. NO reinicia
;     TermService (se quedaba colgado al pararse y dejaba el sistema a medias): todo entra al reiniciar el equipo, que el instalador pide al final
;     (NeedRestart). Log en %ProgramData%\OpenStreamMS\remoteidd-setup.log.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\remoteidd-pkg\uninstall.ps1"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "{cm:StatusRemoveRemoteIdd}"
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\remoteidd-pkg\install.ps1"" -Package ""{app}\remoteidd-pkg"" -RefreshHz 60 -SkipTestSigningCheck"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "{cm:StatusInstallRemoteIdd}"

; 2. Iniciar el servicio (solo si el usuario marcó la tarea)
Filename: "net.exe"; Parameters: "start ""{#AppService}"""; \
  Flags: runhidden waituntilterminated; \
  Tasks: startservice; \
  StatusMsg: "{cm:StatusStartService}"

; 3. Ofrecer arrancar el icono de bandeja al terminar
Filename: "{app}\{#AppExeName}"; Parameters: "--open"; \
  Flags: nowait postinstall skipifsilent; \
  Description: "{cm:LaunchProgram,{#AppName}}"

[UninstallRun]
; Driver de pantalla de los puestos: RDP-Tcp vuelve a su protocolo y TermService a su DLL
; anterior; entra en vigor al reiniciar (UninstallNeedRestart). Sin RemoteIdd no hace nada.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\remoteidd-pkg\uninstall.ps1"""; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "UninstallRemoteIdd"

; Detener y eliminar el servicio antes de borrar archivos
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall --silent"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "UninstallService"

; Respuestas a las preguntas del principio de la desinstalación (InitializeUninstall).
Filename: "{app}\{#AppExeName}"; Parameters: "--purge-data"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "PurgeData"; Check: ShouldPurgeData
Filename: "{app}\{#AppExeName}"; Parameters: "--remove-deps"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "RemoveDeps"; Check: ShouldRemoveDeps

[UninstallDelete]
; Logs siempre. Sesiones, puestos y configuración solo si se pidió borrar los datos
; (CurUninstallStepChanged); si no, se conservan para una reinstalación.
Type: files;     Name: "{app}\openstream*.log"

[Code]
// Detiene el servicio y mata todo proceso que se ejecute desde {app} (bandeja,
// sunshine.exe y FreeRDP en cualquier sesión). Restart Manager no puede cerrar
// servicios ni procesos de otras sesiones, y dejarían DLLs bloqueadas.
procedure StopAppProcesses(const AppDir: String);
var
  Script: String;
  ScriptFile: String;
  ResultCode: Integer;
begin
  Script :=
    'param([string]$AppDir)' + #13#10 +
    'Stop-Service -Name ''{#AppService}'' -Force -ErrorAction SilentlyContinue' + #13#10 +
    '$dir = $AppDir.TrimEnd(''\'') + ''\''' + #13#10 +
    'Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }' + #13#10 +
    'Start-Sleep -Seconds 2' + #13#10;
  ScriptFile := ExpandConstant('{tmp}\stop-openstreamms.ps1');
  if SaveStringToFile(ScriptFile, Script, False) then
    Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptFile + '" -AppDir "' + AppDir + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAppProcesses(ExpandConstant('{app}'));
  Result := '';
end;

// El driver de pantalla (RemoteIdd), que se instala siempre, cambia TermService: entra en
// vigor al reiniciar el equipo.
function NeedRestart(): Boolean;
begin
  Result := True;
end;

var
  RemoteIddWasInstalled: Boolean;

function UninstallNeedRestart(): Boolean;
begin
  Result := RemoteIddWasInstalled;
end;

var
  PurgeData:  Boolean;
  RemoveDeps: Boolean;

// Dos preguntas al empezar. En silencioso: /PURGEDATA y /REMOVEDEPS.
//  - Borrar todos los datos: sesiones, puestos y configuración de {app}, y lo que OpenStreamMS
//    deja fuera (usuarios osms- y sus perfiles, carpetas _OpenStreamMS_Stream,
//    %ProgramData%\OpenStreamMS, registro y temporales). Si no, se conservan para reinstalar.
//  - Quitar también TermWrap, ViGEmBus y HidHide (otras aplicaciones pueden usarlos).
// Texto de [CustomMessages] con los %n convertidos en saltos de línea.
function Msg(const Name: String): String;
begin
  Result := CustomMessage(Name);
  StringChangeEx(Result, '%n', #13#10, True);
end;

function InitializeUninstall(): Boolean;
begin
  RemoteIddWasInstalled := FileExists(ExpandConstant('{commonappdata}\OpenStreamMS\remoteidd-install.json'));
  if UninstallSilent then
  begin
    PurgeData  := Pos('/PURGEDATA',  Uppercase(GetCmdTail)) > 0;
    RemoveDeps := Pos('/REMOVEDEPS', Uppercase(GetCmdTail)) > 0;
  end
  else
  begin
    PurgeData := MsgBox(Msg('AskPurgeData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    RemoveDeps := MsgBox(Msg('AskRemoveDeps'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
  Result := True;
end;

function ShouldPurgeData(): Boolean;
begin
  Result := PurgeData;
end;

function ShouldRemoveDeps(): Boolean;
begin
  Result := RemoveDeps;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopAppProcesses(ExpandConstant('{app}'));
  // Lo que generó el programa (sessions\, sessions.json, seats.json, service.config.json…)
  // no lo instaló el setup y Inno no lo borra solo.
  if (CurUninstallStep = usPostUninstall) and PurgeData then
    DelTree(ExpandConstant('{app}'), True, True, True);
end;
