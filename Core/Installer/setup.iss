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
; El primero es el que se usa si el idioma de Windows no está en la lista.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

; Textos propios del instalador en cada idioma. Los de Inno (botones, páginas) salen del .isl.
[CustomMessages]
spanish.GroupService=Servicio:
english.GroupService=Service:
spanish.TaskStartService=Iniciar el servicio ahora tras la instalación
english.TaskStartService=Start the service right after installing
spanish.GroupSeats=Puestos locales:
english.GroupSeats=Local seats:
spanish.TaskRemoteIdd=Instalar el driver de pantalla de los puestos (RemoteIdd, experimental: certificado de prueba; reinicia Escritorio remoto)
english.TaskRemoteIdd=Install the seat display driver (RemoteIdd, experimental: test certificate; restarts Remote Desktop)
spanish.IconComment=Panel de control de OpenStreamMS
english.IconComment=OpenStreamMS control panel
spanish.StatusInstallService=Registrando servicio de Windows e instalando drivers (ViGEmBus, HidHide)...
english.StatusInstallService=Registering the Windows service and installing drivers (ViGEmBus, HidHide)...
spanish.StatusRemoveRemoteIdd=Quitando el driver de pantalla anterior (RemoteIdd)...
english.StatusRemoveRemoteIdd=Removing the previous display driver (RemoteIdd)...
spanish.StatusInstallRemoteIdd=Instalando el driver de pantalla de los puestos (RemoteIdd)...
english.StatusInstallRemoteIdd=Installing the seat display driver (RemoteIdd)...
spanish.StatusStartService=Iniciando servicio...
english.StatusStartService=Starting the service...
spanish.AskPurgeData=¿Borrar también todos los datos de OpenStreamMS?%n%nSesiones, puestos y configuración; los usuarios osms- de las sesiones aisladas y sus perfiles (incluidos juegos y ajustes guardados en ellos); perfiles de stream, ProgramData\OpenStreamMS y ficheros temporales.%n%nSi respondes No, se conservan las sesiones y la configuración para una reinstalación.
english.AskPurgeData=Also delete all OpenStreamMS data?%n%nSessions, seats and settings; the osms- users of isolated sessions and their profiles (including games and settings saved in them); stream profiles, ProgramData\OpenStreamMS and temporary files.%n%nIf you answer No, sessions and settings are kept for a reinstall.
spanish.AskRemoveDeps=¿Desinstalar también RDP Wrapper, ViGEmBus y HidHide?%n%nResponde No si otras aplicaciones los usan.
english.AskRemoveDeps=Also uninstall RDP Wrapper, ViGEmBus and HidHide?%n%nAnswer No if other applications use them.

french.GroupService=Service :
portuguese.GroupService=Serviço:
brazilianportuguese.GroupService=Serviço:
french.TaskStartService=Démarrer le service juste après l'installation
portuguese.TaskStartService=Iniciar o serviço logo após a instalação
brazilianportuguese.TaskStartService=Iniciar o serviço logo após a instalação
french.GroupSeats=Postes locaux :
portuguese.GroupSeats=Postos locais:
brazilianportuguese.GroupSeats=Postos locais:
french.TaskRemoteIdd=Installer le pilote d'affichage des postes (RemoteIdd, expérimental : certificat de test ; redémarre le Bureau à distance)
portuguese.TaskRemoteIdd=Instalar o controlador de ecrã dos postos (RemoteIdd, experimental: certificado de teste; reinicia o Ambiente de Trabalho Remoto)
brazilianportuguese.TaskRemoteIdd=Instalar o controlador de ecrã dos postos (RemoteIdd, experimental: certificado de teste; reinicia o Ambiente de Trabalho Remoto)
french.IconComment=Panneau de contrôle OpenStreamMS
portuguese.IconComment=Painel de controlo do OpenStreamMS
brazilianportuguese.IconComment=Painel de controlo do OpenStreamMS
french.StatusInstallService=Enregistrement du service Windows et installation des pilotes (ViGEmBus, HidHide)...
portuguese.StatusInstallService=A registar o serviço do Windows e a instalar controladores (ViGEmBus, HidHide)...
brazilianportuguese.StatusInstallService=A registar o serviço do Windows e a instalar controladores (ViGEmBus, HidHide)...
french.StatusRemoveRemoteIdd=Suppression de l'ancien pilote d'affichage (RemoteIdd)...
portuguese.StatusRemoveRemoteIdd=A remover o controlador de ecrã anterior (RemoteIdd)...
brazilianportuguese.StatusRemoveRemoteIdd=A remover o controlador de ecrã anterior (RemoteIdd)...
french.StatusInstallRemoteIdd=Installation du pilote d'affichage des postes (RemoteIdd)...
portuguese.StatusInstallRemoteIdd=A instalar o controlador de ecrã dos postos (RemoteIdd)...
brazilianportuguese.StatusInstallRemoteIdd=A instalar o controlador de ecrã dos postos (RemoteIdd)...
french.StatusStartService=Démarrage du service...
portuguese.StatusStartService=A iniciar o serviço...
brazilianportuguese.StatusStartService=A iniciar o serviço...
french.AskPurgeData=Supprimer aussi toutes les données d'OpenStreamMS ?%n%nSessions, postes et paramètres ; les utilisateurs osms- des sessions isolées et leurs profils (y compris les jeux et réglages qui y sont enregistrés) ; les profils de stream, ProgramData\OpenStreamMS et les fichiers temporaires.%n%nSi vous répondez Non, les sessions et les paramètres sont conservés pour une réinstallation.
portuguese.AskPurgeData=Eliminar também todos os dados do OpenStreamMS?%n%nSessões, postos e definições; os utilizadores osms- das sessões isoladas e os respetivos perfis (incluindo jogos e definições guardados neles); perfis de stream, ProgramData\OpenStreamMS e ficheiros temporários.%n%nSe responder Não, as sessões e as definições são mantidas para uma reinstalação.
brazilianportuguese.AskPurgeData=Eliminar também todos os dados do OpenStreamMS?%n%nSessões, postos e definições; os utilizadores osms- das sessões isoladas e os respetivos perfis (incluindo jogos e definições guardados neles); perfis de stream, ProgramData\OpenStreamMS e ficheiros temporários.%n%nSe responder Não, as sessões e as definições são mantidas para uma reinstalação.
french.AskRemoveDeps=Désinstaller aussi RDP Wrapper, ViGEmBus et HidHide ?%n%nRépondez Non si d'autres applications les utilisent.
portuguese.AskRemoveDeps=Desinstalar também o RDP Wrapper, o ViGEmBus e o HidHide?%n%nResponda Não se outras aplicações os utilizarem.
brazilianportuguese.AskRemoveDeps=Desinstalar também o RDP Wrapper, o ViGEmBus e o HidHide?%n%nResponda Não se outras aplicações os utilizarem.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startservice"; Description: "{cm:TaskStartService}"; GroupDescription: "{cm:GroupService}"
; RemoteIdd (pantalla de los puestos). Firma de prueba: mete su certificado en los de confianza,
; cambia la DLL de TermService (TermWrap) y reinicia Escritorio remoto. Ver package-remoteidd.ps1.
Name: "remoteidd"; Description: "{cm:TaskRemoteIdd}"; GroupDescription: "{cm:GroupSeats}"

[Files]
; Toda la salida de dotnet publish (runtime incluido)
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
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

; 1b. Driver de pantalla de los puestos: quita la instalación anterior (si la hay) y pone la
;     del paquete firmado. NO reinicia TermService (se quedaba colgado al pararse y dejaba el
;     sistema a medias): todo entra al reiniciar el equipo, que el instalador pide al final
;     (NeedRestart). Log en %ProgramData%\OpenStreamMS\remoteidd-setup.log.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\remoteidd-pkg\uninstall.ps1"""; \
  Flags: runhidden waituntilterminated; Tasks: remoteidd; \
  StatusMsg: "{cm:StatusRemoveRemoteIdd}"
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\remoteidd-pkg\install.ps1"" -Package ""{app}\remoteidd-pkg"" -RefreshHz 120 -SkipTestSigningCheck"; \
  Flags: runhidden waituntilterminated; Tasks: remoteidd; \
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

// El driver de pantalla (RemoteIdd) cambia TermService: entra en vigor al reiniciar el equipo.
function NeedRestart(): Boolean;
begin
  Result := WizardIsTaskSelected('remoteidd');
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
//  - Quitar también RDP Wrapper, ViGEmBus y HidHide (otras aplicaciones pueden usarlos).
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
