#requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$bin     = Join-Path $root 'bin\Debug\net10.0-windows'
$srcExe  = Join-Path $root 'FreeRDP\wfreerdp.exe'

# Directorio REAL donde corre el servicio (ImagePath del SCM), NO bin\Debug.
# Si no se copia aqui, el build nunca llega al servicio en ejecucion.
$install = (Get-CimInstance Win32_Service -Filter "Name='OpenStreamMS'").PathName -replace '^"?([^"]+)\\OpenStreamMS\.exe.*$', '$1'
if (-not $install) { $install = 'C:\Program Files\OpenStreamMS' }
Write-Host "Install dir (servicio): $install" -ForegroundColor DarkCyan

Write-Host '== 1. Stop service + kill leftovers ==' -ForegroundColor Cyan
Stop-Service OpenStreamMS -Force
(Get-Service OpenStreamMS).WaitForStatus('Stopped','00:00:30')
# sunshine incluido: su encoder se fija al arrancar; matarlo fuerza re-sondeo limpio.
Get-Process wfreerdp,mstsc,sunshine -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Write-Host '== 2. Build ==' -ForegroundColor Cyan
Push-Location $root
dotnet build -c Debug
Pop-Location

Write-Host '== 3. Deploy al directorio del servicio (solo binarios, NO config/state/sessions) ==' -ForegroundColor Cyan
foreach ($f in 'OpenStreamMS.dll','OpenStreamMS.exe','OpenStreamMS.pdb') {
    $s = Join-Path $bin $f
    if (Test-Path $s) { Copy-Item $s (Join-Path $install $f) -Force; Write-Host "   -> $f" -ForegroundColor Green }
}
# wfreerdp H264 al install y a bin (belt-and-suspenders)
Copy-Item $srcExe (Join-Path $install 'FreeRDP\wfreerdp.exe') -Force
Copy-Item $srcExe (Join-Path $bin     'FreeRDP\wfreerdp.exe') -Force
Write-Host "   -> FreeRDP\wfreerdp.exe (install + bin)" -ForegroundColor Green

Write-Host '== 4. Verify codec flags en el binario desplegado ==' -ForegroundColor Cyan
# OJO: wfreerdp escribe un warning de deprecacion a stderr. Con 2>&1 + ErrorActionPreference=Stop
# eso se convierte en ErrorRecord y ABORTA el script antes del paso 5. 2>$null lo descarta;
# try/catch hace el chequeo no critico (solo informativo).
try {
    $b = Join-Path $install 'FreeRDP\wfreerdp.exe'
    $cfg = (& $b /buildconfig 2>$null | Out-String) -split ' '
    $cfg | Select-String -Pattern 'WITH_GFX_H264=|WITH_MEDIA_FOUNDATION=|WITH_GFX_AV1=' | ForEach-Object { Write-Host "   $_" -ForegroundColor Green }
} catch {
    Write-Host "   (no se pudo leer buildconfig: $($_.Exception.Message))" -ForegroundColor DarkYellow
}

Write-Host '== 5. Start service ==' -ForegroundColor Cyan
Start-Service OpenStreamMS
Write-Host "service: $((Get-Service OpenStreamMS).Status)" -ForegroundColor Green

Write-Host ''
Write-Host 'DONE. Arranca una sesion 4K60 NUEVA y mira el log de Sunshine:' -ForegroundColor Yellow
Write-Host '  debe decir "Creating encoder [h264_amf]" (GPU), NO "[libx264" (CPU).' -ForegroundColor Yellow
Write-Host 'Press Enter to close...'
[void][System.Console]::ReadLine()
