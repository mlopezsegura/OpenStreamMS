# verify-seat.ps1 — comprueba con un puesto REAL que RemoteIdd entrega frames.
#
# Requisitos: install.ps1 hecho y la sesión del puesto abierta desde 127.0.0.2 (OpenStreamMS
# o mstsc /v:127.0.0.2 con el usuario del puesto). Ejecutar como administrador.
# Para medir ritmo, que haya algo moviéndose en el puesto: IddCx solo presenta cuando
# cambia la pantalla.
#
# Uso: .\verify-seat.ps1 -Bin <salida de build.cmd> -User <usuario del puesto> [-Seconds 10]
param(
    [Parameter(Mandatory)] [string] $Bin,
    [Parameter(Mandatory)] [string] $User,
    [int] $Seconds = 10
)
$ErrorActionPreference = 'Stop'
$fail = 0
function Check($ok, $what) {
    Write-Host ("[{0}] {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what)
    if (-not $ok) { $script:fail = 1 }
}

# 1) Sesión RDP del usuario del puesto.
$pattern = '(?i)rdp-tcp#\d+\s+' + [regex]::Escape($User) + '\s+(\d+)\s'
$line = (qwinsta 2>$null) | Where-Object { $_ -match $pattern } | Select-Object -First 1
Check ([bool]$line) "hay una sesión RDP conectada del usuario $User"
if (-not $line) { exit 1 }
$session = [int]([regex]::Match($line, $pattern)).Groups[1].Value
Write-Host "   sesión $session"

# 2) Terminal Services cargó RemoteIdd (no RdpIdd) para esa sesión.
$dev = Get-PnpDevice -PresentOnly | Where-Object { $_.HardwareID -contains 'OsmsRemoteIdd_IndirectDisplay' }
Check ([bool]$dev) 'el dispositivo OsmsRemoteIdd_IndirectDisplay existe'
Check ($dev -and ($dev.Status -contains 'OK')) 'el driver RemoteIdd arrancó (estado OK)'

# 3) El driver recibió la sesión del proveedor y publica el slot con ese nombre.
$perf = Join-Path $Bin 'RemoteIddPerf.exe'
$out = & $perf watch $session $Seconds
$out | ForEach-Object { Write-Host "   $_" }
$result = $out | Where-Object { $_ -like 'RESULT*' } | Select-Object -First 1
Check ($result -and $result -notmatch 'error=') "el slot Global\OsmsRidd${session}Slot existe y se abre"
if ($result -and $result -notmatch 'error=') {
    $received = [int]([regex]::Match($result, 'received=(\d+)')).Groups[1].Value
    $gpu99 = [double]::Parse(([regex]::Match($result, 'gpu99=([\d.]+)')).Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    Check ($received -gt 0) "llegan frames del escritorio del puesto ($received)"
    Check ($received -le 20 -or $gpu99 -le 4.0) "latencia present -> píxel p99 <= 4 ms ($gpu99 ms)"
}

Write-Host ''
Write-Host ("RESULTADO: {0}" -f ($(if ($fail) { 'FALLO' } else { 'OK' })))
exit $fail
