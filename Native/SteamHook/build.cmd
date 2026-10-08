@echo off
:: Compila osms-steamhook.dll y osms-steam.exe (x64: como steam.exe; la DLL se inyecta en él).
:: Uso: build.cmd <carpeta de salida>
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0bin
if not exist "%OUT%" mkdir "%OUT%"

set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
set VSDIR=
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if not defined VSDIR (
    echo ERROR: no se encontro MSVC ^(Build Tools con C++^).
    exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

set OBJ=%TEMP%\osms-steamhook-obj
if not exist "%OBJ%" mkdir "%OBJ%"
set CFLAGS=/nologo /O2 /W4 /WX /MT /GS /DUNICODE /D_UNICODE /Fo"%OBJ%\\"

cl %CFLAGS% /LD "%~dp0steamhook.c" /Fe"%OUT%\osms-steamhook.dll" /link /DLL advapi32.lib || exit /b 1
cl %CFLAGS% "%~dp0launcher.c" /Fe"%OUT%\osms-steam.exe" /link /SUBSYSTEM:WINDOWS advapi32.lib || exit /b 1
del /q "%OUT%\osms-steamhook.lib" "%OUT%\osms-steamhook.exp" 2>nul
exit /b 0
