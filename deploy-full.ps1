# Despliega bin\Debug al directorio del servicio instalado: exe/dll + Sunshine, FreeRDP, wwwroot y runtimes.
# NO toca datos: sessions\, sessions.json, service.config.json ni logs.
# Se auto-eleva (UAC) si no se ejecuta como administrador.
param([switch]$NoBuild)

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($NoBuild) { $argList += '-NoBuild' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin  = Join-Path $root 'bin\Debug\net10.0-windows'

# Directorio REAL del servicio (ImagePath del SCM), no bin\Debug.
$svc = Get-CimInstance Win32_Service -Filter "Name='OpenStreamMS'"
$install = if ($svc) { $svc.PathName -replace '^"?([^"]+)\\OpenStreamMS\.exe.*$', '$1' } else { 'C:\Program Files\OpenStreamMS' }
Write-Host "Install dir (servicio): $install" -ForegroundColor DarkCyan

if (-not $NoBuild) {
    Write-Host '== 1. Build ==' -ForegroundColor Cyan
    Push-Location $root
    dotnet build -c Debug
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Build fallido.' }
    Pop-Location
}

Write-Host '== 2. Stop service + procesos hijos ==' -ForegroundColor Cyan
if ($svc -and $svc.State -ne 'Stopped') {
    Stop-Service OpenStreamMS -Force
    (Get-Service OpenStreamMS).WaitForStatus('Stopped', '00:00:30')
}
# Tray incluido: tiene abierto OpenStreamMS.exe y bloquearia la copia.
Get-Process wfreerdp, mstsc, sunshine -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process OpenStreamMS -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($install, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
Start-Sleep -Seconds 1

Write-Host '== 3. Copiar binarios ==' -ForegroundColor Cyan
Get-ChildItem $bin -File | Where-Object { $_.Extension -in '.exe', '.dll', '.pdb', '.json', '.ico' -and
                                          $_.Name -notin 'service.config.json', 'sessions.json' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $install $_.Name) -Force }
foreach ($d in 'Sunshine', 'FreeRDP', 'wwwroot', 'runtimes') {
    # /MIR: carpeta identica a bin (borra restos de versiones anteriores). Solo contienen binarios.
    robocopy (Join-Path $bin $d) (Join-Path $install $d) /MIR /NFL /NDL /NJH /NJS /R:2 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy fallo en $d (codigo $LASTEXITCODE)" }
    Write-Host "   -> $d" -ForegroundColor Green
}

Write-Host '== 4. Versiones desplegadas ==' -ForegroundColor Cyan
Write-Host "   Sunshine: $((Get-Item (Join-Path $install 'Sunshine\sunshine.exe')).VersionInfo.ProductVersion)"
Write-Host "   wfreerdp: $((Get-Item (Join-Path $install 'FreeRDP\wfreerdp.exe')).VersionInfo.FileVersion)"

Write-Host '== 5. Start service ==' -ForegroundColor Cyan
Start-Service OpenStreamMS
Write-Host "service: $((Get-Service OpenStreamMS).Status)" -ForegroundColor Green

Write-Host ''
Write-Host 'Arranca una sesion y revisa su log:' -ForegroundColor Yellow
Write-Host '  "Actualizando Sunshine de la instancia", "Encoder hardware confirmado", "mstsc (AVC420 hw)"' -ForegroundColor Yellow
Write-Host 'Pulsa Enter para cerrar...'
[void][Console]::ReadLine()
