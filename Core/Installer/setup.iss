; OpenStreamMS — Inno Setup Script
; Requiere Inno Setup 6.3+ https://jrsoftware.org/isinfo.php
;
; Antes de compilar ejecuta: build-installer.bat
; El instalador resultante se genera en ..\dist\

#define AppName      "OpenStreamMS"
; La CI pasa la versión con /DAppVersion=x.y.z (sale del tag de la release).
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "mlopezsegura"
#define AppURL       "https://github.com/mlopezsegura/OpenStreamMS"
#define AppExeName   "OpenStreamMS.exe"
#define AppService   "OpenStreamMS"

[Setup]
AppId={{F2A1B3C4-D5E6-7890-ABCD-EF1234567891}
AppName={#AppName}
AppVersion={#AppVersion}
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
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
; Requiere Windows 10 1809+
MinVersion=10.0.17763

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Iconos adicionales:"
Name: "startservice"; Description: "Iniciar el servicio ahora tras la instalación"; GroupDescription: "Servicio:"

[Files]
; Toda la salida de dotnet publish (runtime incluido)
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; service.config.json: solo si no existe ya (no sobreescribir configuración del usuario)
Source: "publish\service.config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--tray"; \
  Comment: "Panel de control de OpenStreamMS"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--tray"; \
  Tasks: desktopicon

[Run]
; 1. Instalar el servicio Windows (y registrar el tray en inicio automático)
Filename: "{app}\{#AppExeName}"; Parameters: "--install --silent"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Registrando servicio de Windows e instalando drivers (ViGEmBus, HidHide)..."

; 2. Iniciar el servicio (solo si el usuario marcó la tarea)
Filename: "net.exe"; Parameters: "start ""{#AppService}"""; \
  Flags: runhidden waituntilterminated; \
  Tasks: startservice; \
  StatusMsg: "Iniciando servicio..."

; 3. Ofrecer arrancar el icono de bandeja al terminar
Filename: "{app}\{#AppExeName}"; Parameters: "--tray"; \
  Flags: nowait postinstall skipifsilent; \
  Description: "Iniciar {#AppName} ahora"

[UninstallRun]
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

var
  PurgeData:  Boolean;
  RemoveDeps: Boolean;

// Dos preguntas al empezar. En silencioso: /PURGEDATA y /REMOVEDEPS.
//  - Borrar todos los datos: sesiones, puestos y configuración de {app}, y lo que OpenStreamMS
//    deja fuera (usuarios osms- y sus perfiles, carpetas _OpenStreamMS_Stream,
//    %ProgramData%\OpenStreamMS, registro y temporales). Si no, se conservan para reinstalar.
//  - Quitar también RDP Wrapper, ViGEmBus y HidHide (otras aplicaciones pueden usarlos).
function InitializeUninstall(): Boolean;
begin
  if UninstallSilent then
  begin
    PurgeData  := Pos('/PURGEDATA',  Uppercase(GetCmdTail)) > 0;
    RemoveDeps := Pos('/REMOVEDEPS', Uppercase(GetCmdTail)) > 0;
  end
  else
  begin
    PurgeData := MsgBox('¿Borrar también todos los datos de OpenStreamMS?' + #13#10#13#10 +
      'Sesiones, puestos y configuración; los usuarios osms- de las sesiones aisladas y sus ' +
      'perfiles (incluidos juegos y ajustes guardados en ellos); perfiles de stream, ' +
      '%ProgramData%\OpenStreamMS y ficheros temporales.' + #13#10#13#10 +
      'Si respondes No, se conservan las sesiones y la configuración para una reinstalación.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    RemoveDeps := MsgBox('¿Desinstalar también RDP Wrapper, ViGEmBus y HidHide?' + #13#10#13#10 +
      'Responde No si otras aplicaciones los usan.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
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
