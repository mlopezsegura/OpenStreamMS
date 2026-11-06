; Aspecto del instalador: tema claro u oscuro según Windows, fondo con los colores de la marca y
; fondo animado en la bienvenida, la instalación y el final. Las imágenes salen de
; art\generate-art.ps1 (requiere Inno Setup 6.7+). Lo incluye setup.iss.

; ISCC /DWizardMode=light|dark fuerza un tema (para revisar el aspecto); por defecto, dynamic.
#ifndef WizardMode
  #define WizardMode "dynamic"
#endif

[Setup]
; dynamic: sigue el modo claro/oscuro de Windows. El fondo propio activa el estilo windows11.
WizardStyle=modern {#WizardMode}
; La bienvenida (Inno la oculta por defecto) enseña el logo sobre el fondo animado.
DisableWelcomePage=no
#if WizardMode == "dark"
WizardBackImageFile=art\back-dark-*.png
#else
WizardBackImageFile=art\back-light-*.png
#endif
WizardBackImageFileDynamicDark=art\back-dark-*.png
; Color del resto de ventanas (idioma, desinstalación, avisos).
WizardBackColor=#f7f5ff
WizardBackColorDynamicDark=#0f0f1a
WizardImageFile=art\hero-*.png
WizardSmallImageFile=art\logo-*.png
SetupIconFile=..\..\app.ico

[Files]
; Fondos fijos y fotogramas, solo para el [Code] de abajo: no se instalan.
Source: "art\back-*.png"; DestDir: "{tmp}"; Flags: dontcopy noencryption
Source: "art\anim-*.png"; DestDir: "{tmp}"; Flags: dontcopy noencryption

[Code]
function SetTimer(hWnd: HWND; nIDEvent: UINT_PTR; uElapse: UINT; lpTimerFunc: LongWord): UINT_PTR;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd: HWND; uIDEvent: UINT_PTR): BOOL;
  external 'KillTimer@user32.dll stdcall';
function SystemParametersInfoBool(uiAction, uiParam: UINT; var pvParam: BOOL; fWinIni: UINT): BOOL;
  external 'SystemParametersInfoW@user32.dll stdcall';

// Ajustes > Accesibilidad > Efectos de animación (SPI_GETCLIENTAREAANIMATION).
function WindowsAnimationsOn: Boolean;
var
  Enabled: BOOL;
begin
  Enabled := True;
  if SystemParametersInfoBool($1042, 0, Enabled, 0) then Result := Enabled else Result := True;
end;

const
  AnimFrameCount = 24;
  AnimIntervalMs = 140;

var
  AnimFrames: TArrayOfGraphic;
  AnimFrame: Integer;
  AnimTimer: UINT_PTR;
  AnimShown: Boolean;

// Solo en páginas con poco que leer: en las de opciones el fondo se queda quieto.
function AnimPage: Boolean;
begin
  Result := (WizardForm <> nil) and
            ((WizardForm.CurPageID = wpWelcome) or (WizardForm.CurPageID = wpInstalling) or
             (WizardForm.CurPageID = wpFinished));
end;

procedure ShowBackFrame(const Index: Integer);
var
  One: TArrayOfGraphic;
begin
  SetLength(One, 1);
  One[0] := AnimFrames[Index];
  WizardSetBackImage(One, True, True, 255);
end;

function BackFile(const Theme: String; const Index: Integer): String;
begin
  case Index of
    0: Result := 'back-' + Theme + '-596.png';
    1: Result := 'back-' + Theme + '-994.png';
  else
    Result := 'back-' + Theme + '-1272.png';
  end;
end;

// Vuelve al fondo fijo de [Setup] (varios tamaños, con la rejilla de puntos).
procedure ShowStaticBack;
var
  Images: TArrayOfGraphic;
  I: Integer;
  Theme: String;
begin
  if IsDarkInstallMode then Theme := 'dark' else Theme := 'light';
  SetLength(Images, 3);
  try
    for I := 0 to 2 do begin
      Images[I] := TPngImage.Create;
      Images[I].LoadFromFile(ExpandConstant('{tmp}\') + BackFile(Theme, I));
    end;
    WizardSetBackImage(Images, True, True, 255);
  finally
    for I := 0 to 2 do
      if Images[I] <> nil then Images[I].Free;
  end;
end;

procedure AnimTick(Wnd: HWND; Message: UINT; IdEvent: UINT_PTR; TickTime: DWORD);
begin
  try
    if AnimPage then begin
      AnimFrame := (AnimFrame + 1) mod AnimFrameCount;
      ShowBackFrame(AnimFrame);
      AnimShown := True;
    end else if AnimShown then begin
      ShowStaticBack;
      AnimShown := False;
    end;
  except
    // Un fallo de la animación no puede tumbar la instalación: se para y queda el fondo fijo.
    KillTimer(0, AnimTimer);
    AnimTimer := 0;
    Log('Animación del fondo desactivada: ' + GetExceptionMessage);
  end;
end;

<event('InitializeWizard')>
procedure InitializeWizard_Theme;
var
  I: Integer;
  Theme, Name: String;
begin
  // Sin animación en silencioso, con alto contraste o con las animaciones de Windows quitadas.
  if WizardSilent or HighContrastActive or not WindowsAnimationsOn then Exit;
  if IsDarkInstallMode then Theme := 'dark' else Theme := 'light';
  try
    for I := 0 to 2 do ExtractTemporaryFile(BackFile(Theme, I));
    SetLength(AnimFrames, AnimFrameCount);
    for I := 0 to AnimFrameCount - 1 do begin
      Name := Format('anim-%s-%.2d.png', [Theme, I]);
      ExtractTemporaryFile(Name);
      AnimFrames[I] := TPngImage.Create;
      AnimFrames[I].LoadFromFile(ExpandConstant('{tmp}\') + Name);
    end;
    AnimTimer := SetTimer(0, 0, AnimIntervalMs, CreateCallback(@AnimTick));
  except
    Log('Sin animación del fondo: ' + GetExceptionMessage);
    SetLength(AnimFrames, 0);
  end;
end;

<event('DeinitializeSetup')>
procedure DeinitializeSetup_Theme;
var
  I: Integer;
begin
  if AnimTimer <> 0 then KillTimer(0, AnimTimer);
  AnimTimer := 0;
  for I := 0 to GetArrayLength(AnimFrames) - 1 do
    if AnimFrames[I] <> nil then AnimFrames[I].Free;
  SetLength(AnimFrames, 0);
end;
