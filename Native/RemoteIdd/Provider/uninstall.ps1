# uninstall.ps1 — deshace install.ps1 paso a paso según el fichero de estado.
# Uso (administrador):  .\uninstall.ps1 [-RestartTermService]
param([switch] $RestartTermService)
$ErrorActionPreference = 'Continue'

$state = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-install.json'
if (-not (Test-Path $state)) { Write-Host 'No hay nada instalado.'; return }
$s = Get-Content $state -Raw | ConvertFrom-Json
$ws = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations'

# Lo primero: devolver RDP-Tcp a su protocolo original, antes de quitar la DLL, para que el
# RDP normal nunca apunte a una clase que ya no existe.
if ($s.OriginalProtocol) {
    $rdpTcp = "$ws\RDP-Tcp"
    Set-ItemProperty $rdpTcp LoadableProtocol_Object $s.OriginalProtocol
    Remove-ItemProperty $rdpTcp OsmsInnerProtocol_Object -ErrorAction SilentlyContinue
    Remove-ItemProperty $rdpTcp OsmsRefreshHz -ErrorAction SilentlyContinue
    Write-Host "RDP-Tcp restaurado a $($s.OriginalProtocol)."
}
# Instalaciones anteriores, con listener aparte.
if ($s.WinStation -and (Test-Path "$ws\$($s.WinStation)")) {
    Remove-Item -Recurse -Force "$ws\$($s.WinStation)"
    Write-Host "Listener $($s.WinStation) eliminado."
}
if ($s.ProviderDir) {
    $dll = Join-Path $s.ProviderDir 'OsmsRdsProvider.dll'
    # Start-Process -Wait: regsvr32 es gráfico y PowerShell no lo esperaría.
    if (Test-Path $dll) { Start-Process regsvr32.exe -ArgumentList "/s /u `"$dll`"" -Wait }
    if ($RestartTermService) { Restart-Service TermService -Force }
    Remove-Item -Recurse -Force $s.ProviderDir -ErrorAction SilentlyContinue
    if (Test-Path $s.ProviderDir) { Write-Warning "TermService aún tiene cargado el proveedor: borra $($s.ProviderDir) tras reiniciar." }
}
if ($s.DriverInf) { pnputil /delete-driver $s.DriverInf /uninstall /force | Out-Null; Write-Host "Driver $($s.DriverInf) eliminado." }
if ($s.Thumbprint) {
    foreach ($store in 'My', 'Root', 'TrustedPublisher') {
        Get-ChildItem "Cert:\LocalMachine\$store\$($s.Thumbprint)" -ErrorAction SilentlyContinue | Remove-Item
    }
    Write-Host 'Certificado de test eliminado.'
}
Remove-Item $state
Write-Host 'Desinstalado. Si activaste testsigning solo para esto: bcdedit /set testsigning off'
