@echo off
:: Compila y prueba RemoteIdd sin instalar el WDK: usa el paquete NuGet oficial
:: Microsoft.Windows.WDK.x64, descomprimido en una caché del usuario.
:: Uso: build.cmd [carpeta de salida]
::   1) compila y ejecuta test\SharedFrameTest.exe (si falla, para)
::   2) compila y ejecuta RemoteIddPerf.exe: rendimiento entre procesos en la GPU real
::      (se salta con la variable RIDD_NOPERF=1, p. ej. en una máquina sin GPU)
::   2b) compila y prueba el proveedor de protocolo (OsmsRdsProvider.dll)
::   3) compila RemoteIdd.dll (IddCx 1.4, UMDF 2.33) y sella + valida RemoteIdd.inf
:: No firma ni instala nada.
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0bin
if not exist "%OUT%" mkdir "%OUT%"

set WDKVER=10.0.26100.6584
set WDKCACHE=%LOCALAPPDATA%\OpenStreamMS\wdk-nuget\%WDKVER%
set W=%WDKCACHE%\c
set WINC=%W%\Include\10.0.26100.0
set WLIB=%W%\Lib\10.0.26100.0\um\x64

set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
set VSDIR=
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if not defined VSDIR (
    echo ERROR: no se encontro MSVC ^(Build Tools con C++^).
    exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

set OBJ=%TEMP%\osms-remoteidd-obj
if not exist "%OBJ%" mkdir "%OBJ%"

:: ---- 1) test del contrato SharedFrame.h (no necesita WDK) ----
cl /nologo /O2 /W4 /WX /EHsc /std:c++17 /Fo"%OBJ%\\" "%~dp0test\SharedFrameTest.cpp" /Fe"%OBJ%\SharedFrameTest.exe" || exit /b 1
"%OBJ%\SharedFrameTest.exe" || exit /b 1

:: ---- 1b) test de rendimiento (no necesita WDK) ----
cl /nologo /O2 /W4 /WX /EHsc /std:c++17 /DUNICODE /D_UNICODE /Fo"%OBJ%\\" ^
  "%~dp0test\PerfTest.cpp" "%~dp0FramePublisher.cpp" "%~dp0FrameConsumer.cpp" ^
  /Fe"%OUT%\RemoteIddPerf.exe" /link d3d11.lib dxgi.lib avrt.lib advapi32.lib || exit /b 1
if not "%RIDD_NOPERF%"=="1" "%OUT%\RemoteIddPerf.exe" || exit /b 1

:: ---- 1c) proveedor de protocolo RDS: test con objetos falsos + DLL ----
cl /nologo /O2 /W4 /WX /EHsc /std:c++17 /DUNICODE /D_UNICODE /Fo"%OBJ%\\" ^
  "%~dp0test\ProviderTest.cpp" "%~dp0Provider\RdsProvider.cpp" ^
  /Fe"%OBJ%\RemoteIddProviderTest.exe" /link ole32.lib cfgmgr32.lib advapi32.lib || exit /b 1
"%OBJ%\RemoteIddProviderTest.exe" || exit /b 1
cl /nologo /O2 /W4 /WX /EHsc /std:c++17 /MT /GS /guard:cf /DUNICODE /D_UNICODE /LD /Fo"%OBJ%\\" ^
  "%~dp0Provider\RdsProvider.cpp" /Fe"%OUT%\OsmsRdsProvider.dll" ^
  /link /DEF:"%~dp0Provider\OsmsRdsProvider.def" /GUARD:CF ole32.lib cfgmgr32.lib advapi32.lib || exit /b 1
del /q "%OUT%\OsmsRdsProvider.lib" "%OUT%\OsmsRdsProvider.exp" 2>nul

:: ---- 2) WDK NuGet (una sola vez, ~110 MB) ----
if not exist "%WINC%\um\iddcx\1.4\IddCx.h" (
    echo Descargando Microsoft.Windows.WDK.x64 %WDKVER%...
    if not exist "%WDKCACHE%" mkdir "%WDKCACHE%"
    curl.exe -fsSL -o "%WDKCACHE%\wdk.nupkg" "https://api.nuget.org/v3-flatcontainer/microsoft.windows.wdk.x64/%WDKVER%/microsoft.windows.wdk.x64.%WDKVER%.nupkg" || exit /b 1
    tar.exe -xf "%WDKCACHE%\wdk.nupkg" -C "%WDKCACHE%" c || exit /b 1
    del /q "%WDKCACHE%\wdk.nupkg"
)
if not exist "%WINC%\um\iddcx\1.4\IddCx.h" (
    echo ERROR: el paquete del WDK no trae IddCx 1.4.
    exit /b 1
)

:: ---- 3) driver ----
:: C4324 sale de las cabeceras de WDF (wdfrequest.h), no del código del driver.
cl /nologo /c /O2 /W4 /WX /wd4324 /EHsc /std:c++17 /MT /GS /guard:cf /Zi /Fo"%OBJ%\\" /Fd"%OBJ%\\" ^
  /DUNICODE /D_UNICODE /D_WINDLL /DWIN32_LEAN_AND_MEAN ^
  /DUMDF_VERSION_MAJOR=2 /DUMDF_VERSION_MINOR=33 /DUMDF_USING_NTSTATUS ^
  /DIDDCX_VERSION_MAJOR=1 /DIDDCX_VERSION_MINOR=4 /DIDDCX_MINIMUM_VERSION_REQUIRED=4 ^
  /I"%WINC%\um\iddcx\1.4" /I"%W%\Include\wdf\umdf\2.33" /I"%WINC%\um" /I"%WINC%\shared" ^
  "%~dp0Driver.cpp" || exit /b 1
:: FramePublisher no es código UMDF: sin UMDF_USING_NTSTATUS (ocultaría los STATUS_* de winnt.h).
cl /nologo /c /O2 /W4 /WX /EHsc /std:c++17 /MT /GS /guard:cf /Zi /Fo"%OBJ%\\" /Fd"%OBJ%\\" ^
  /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN "%~dp0FramePublisher.cpp" || exit /b 1
link /nologo /DLL /OUT:"%OUT%\RemoteIdd.dll" /PDB:"%OUT%\RemoteIdd.pdb" /DEBUG /INCREMENTAL:NO /GUARD:CF ^
  /NODEFAULTLIB:kernel32.lib "%OBJ%\Driver.obj" "%OBJ%\FramePublisher.obj" ^
  "%W%\Lib\wdf\umdf\x64\2.33\WdfDriverStubUm.lib" "%WLIB%\iddcx\1.4\iddcxstub.lib" ^
  OneCoreUAP.lib d3d11.lib dxgi.lib avrt.lib ntdll.lib || exit /b 1
del /q "%OUT%\RemoteIdd.lib" "%OUT%\RemoteIdd.exp" 2>nul

:: ---- 4) INF sellado y validado ----
copy /y "%~dp0RemoteIdd.inf" "%OUT%\RemoteIdd.inf" >nul
"%W%\bin\10.0.26100.0\x64\stampinf.exe" -f "%OUT%\RemoteIdd.inf" -a amd64 -u 2.33.0 -d * -v 0.1.0.0 >nul || exit /b 1
"%W%\tools\10.0.26100.0\x64\infverif.exe" /w "%OUT%\RemoteIdd.inf" || exit /b 1

echo RemoteIdd compilado en %OUT% (sin firmar, sin instalar).
exit /b 0
