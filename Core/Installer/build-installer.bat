@echo off
setlocal

:: ── Configuración ────────────────────────────────────────────────────────────
set PROJECT=%~dp0..\..\OpenStreamMS.csproj
set PUBLISH_DIR=%~dp0publish
set DIST_DIR=%~dp0..\dist

:: Ruta habitual de Inno Setup; ajusta si lo tienes en otro lugar
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist %ISCC% set ISCC="%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

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
  --output "%PUBLISH_DIR%"

if errorlevel 1 (
    echo ERROR: dotnet publish fallo.
    exit /b 1
)

:: ── Compilar instalador ──────────────────────────────────────────────────────
echo [3/3] Compilando instalador con Inno Setup...
if not exist %ISCC% (
    echo ERROR: No se encontro Inno Setup en %ISCC%
    echo        Descargalo en https://jrsoftware.org/isinfo.php
    exit /b 1
)

%ISCC% "%~dp0setup.iss"

if errorlevel 1 (
    echo ERROR: ISCC fallo.
    exit /b 1
)

echo.
echo Instalador generado en: %DIST_DIR%
dir /b "%DIST_DIR%\*.exe" 2>nul
