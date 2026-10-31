@echo off
setlocal

:: ── Configuración ────────────────────────────────────────────────────────────
set PROJECT=%~dp0..\..\OpenStreamMS.csproj
set PUBLISH_DIR=%~dp0publish
set DIST_DIR=%~dp0..\dist

:: Ruta habitual de Inno Setup; ajusta si lo tienes en otro lugar
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist %ISCC% set ISCC="%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

:: Versión del instalador: APP_VERSION (la CI la saca del tag) o la del setup.iss
set VERSION_ARGS=
set ISCC_ARGS=
if defined APP_VERSION (
    set VERSION_ARGS=-p:Version=%APP_VERSION%
    set ISCC_ARGS=/DAppVersion=%APP_VERSION%
)

:: ── Limpiar salida anterior ──────────────────────────────────────────────────
echo [1/3] Limpiando publicacion anterior...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%DIST_DIR%"    mkdir "%DIST_DIR%" 2>nul

:: ── dotnet publish ───────────────────────────────────────────────────────────
echo [2/3] Publicando con dotnet...
dotnet publish "%PROJECT%" ^
  --configuration Release ^
  --runtime win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishReadyToRun=true ^
  %VERSION_ARGS% ^
  --output "%PUBLISH_DIR%"

if errorlevel 1 (
    echo ERROR: dotnet publish fallo.
    exit /b 1
)

echo [2b/3] Compilando osms-steamhook (x64)...
call "%~dp0..\..\Native\SteamHook\build.cmd" "%PUBLISH_DIR%\steam"
if errorlevel 1 (
    echo ERROR: no se pudo compilar Native\SteamHook.
    exit /b 1
)

:: Instaladores firmados de ViGEmBus y HidHide (ultima release de GitHub): --install
:: los ejecuta en silencio si faltan, sin depender de tener internet al instalar.
echo [2c/3] Descargando drivers (ViGEmBus, HidHide)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0download-drivers.ps1" -OutDir "%PUBLISH_DIR%\drivers"
if errorlevel 1 (
    echo ERROR: no se pudieron descargar los drivers.
    exit /b 1
)

:: ── Compilar instalador ──────────────────────────────────────────────────────
:: Driver de pantalla de los puestos (RemoteIdd): compilado, probado y firmado aquí, con un
:: certificado de prueba de un solo uso (ver package-remoteidd.ps1). Va siempre en el
:: instalador; instalarlo es una tarea que se puede desmarcar.
echo [2d/3] Compilando y firmando RemoteIdd...
set RIDD_NOPERF=1
call "%~dp0..\..\Native\RemoteIdd\build.cmd"
if errorlevel 1 (
    echo ERROR: no se pudo compilar Native\RemoteIdd.
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-remoteidd.ps1" -Bin "%~dp0..\..\Native\RemoteIdd\bin" -OutDir "%PUBLISH_DIR%\remoteidd-pkg"
if errorlevel 1 (
    echo ERROR: no se pudo preparar el paquete de RemoteIdd.
    exit /b 1
)

echo [3/3] Compilando instalador con Inno Setup...
if not exist %ISCC% (
    echo ERROR: No se encontro Inno Setup en %ISCC%
    echo        Descargalo en https://jrsoftware.org/isinfo.php
    exit /b 1
)

%ISCC% %ISCC_ARGS% "%~dp0setup.iss"

if errorlevel 1 (
    echo ERROR: ISCC fallo.
    exit /b 1
)

echo.
echo Instalador generado en: %DIST_DIR%
dir /b "%DIST_DIR%\*.exe" 2>nul
