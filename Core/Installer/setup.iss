; OpenStreamMS — Inno Setup Script
; Requiere Inno Setup 6.3+ https://jrsoftware.org/isinfo.php
;
; Antes de compilar ejecuta: build-installer.bat
; El instalador resultante se genera en ..\dist\

#define AppName      "OpenStreamMS"
#define AppVersion   "1.0.0"
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
Compression=lzma2/ultra64
SolidCompression=yes
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

[UninstallDelete]
; Eliminar logs y archivos generados en tiempo de ejecución
Type: files;     Name: "{app}\openstream*.log"
Type: files;     Name: "{app}\sessions.json"
Type: filesandordirs; Name: "{app}"

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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopAppProcesses(ExpandConstant('{app}'));
end;
