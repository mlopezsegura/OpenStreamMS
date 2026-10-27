# trace-iddcx.ps1 — captura el ETW de IddCx y UMDF mientras se abre un puesto, para ver por qué
# falla el driver (p. ej. IddCxAdapterInitAsync 0xC000000D sin más detalle).
# Uso (administrador):  .\trace-iddcx.ps1   → arranca, abre el puesto, pulsa Enter.
# Deja en %ProgramData%\OpenStreamMS\iddcx-trace.etl y iddcx-trace.txt (texto legible).
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Hay que ejecutarlo como administrador.'
}
$dir  = Join-Path $env:ProgramData 'OpenStreamMS'
$etl  = Join-Path $dir 'iddcx-trace.etl'
$txt  = Join-Path $dir 'iddcx-trace.txt'
$name = 'osms-iddcx'
# IddCx (el mismo GUID que activa rdpidd.inf), UMDF y la pila de pantalla remota de Windows.
$providers = @(
    '{D92BCB52-FA78-406F-A9A5-2037509FADEA}'    # IddCx
    'Microsoft-Windows-DriverFrameworks-UserMode'
    '{7B7A87D4-6D7C-4C87-8E3D-29F31BFF02B8}'    # RdpIdd
)
logman stop $name -ets 2>$null | Out-Null
Remove-Item $etl, $txt -ErrorAction SilentlyContinue
logman create trace $name -o $etl -ets -p $providers[0] 0xffffffffffffffff 0xff | Out-Null
foreach ($p in $providers[1..2]) { logman update trace $name -ets -p $p 0xffffffffffffffff 0xff | Out-Null }
Write-Host 'Capturando. Abre ahora el puesto en OpenStreamMS y, cuando falle, pulsa Enter.' -ForegroundColor Cyan
[void](Read-Host)
logman stop $name -ets | Out-Null
Get-WinEvent -Path $etl -Oldest -ErrorAction SilentlyContinue | ForEach-Object {
    $props = ($_.Properties | ForEach-Object { $_.Value }) -join ' | '
    '{0:HH:mm:ss.fff} [{1}] {2} id={3} {4} {5}' -f $_.TimeCreated, $_.ProcessId, $_.ProviderName, $_.Id, ($_.Message -replace '\s+', ' '), $props
} | Set-Content -Encoding utf8 $txt
# Respaldo: tracerpt decodifica también eventos TraceLogging que Get-WinEvent deja sin texto.
tracerpt $etl -o (Join-Path $dir 'iddcx-trace.xml') -of XML -y | Out-Null
icacls $txt /grant '*S-1-5-32-545:(R)' | Out-Null
icacls (Join-Path $dir 'iddcx-trace.xml') /grant '*S-1-5-32-545:(R)' | Out-Null
Write-Host "Listo: $txt (y iddcx-trace.xml)" -ForegroundColor Green
