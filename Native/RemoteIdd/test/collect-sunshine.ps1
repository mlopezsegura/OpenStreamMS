# collect-sunshine.ps1 — copia el log y la captura configurada de cada Sunshine de puesto a
# %ProgramData%\OpenStreamMS\diag\, legible sin administrador, para diagnosticar RemoteIdd.
# No copia credenciales: del sunshine.conf solo las claves de captura, encoder y salida.
# Uso (administrador):  .\collect-sunshine.ps1
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Hay que ejecutarlo como administrador.'
}
$out = Join-Path $env:ProgramData 'OpenStreamMS\diag'
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem (Join-Path $env:ProgramFiles 'OpenStreamMS\sessions') -Directory | ForEach-Object {
    $cfg = Join-Path $_.FullName 'Sunshine\config'
    $log = Join-Path $cfg 'sunshine.log'
    if (Test-Path $log) {
        Get-Content $log -Tail 600 | Set-Content -Encoding utf8 (Join-Path $out "$($_.Name)-sunshine.log")
    }
    $conf = Join-Path $cfg 'sunshine.conf'
    if (Test-Path $conf) {
        Get-Content $conf | Where-Object { $_ -match '^\s*(capture|encoder|output_name|adapter_name|min_fps_factor|fps)\s*=' } |
            Set-Content -Encoding utf8 (Join-Path $out "$($_.Name)-sunshine.conf.txt")
    }
}
icacls $out /grant '*S-1-5-32-545:(OI)(CI)(R)' /T | Out-Null
Get-ChildItem $out | Select-Object Name, Length, LastWriteTime | Format-Table
