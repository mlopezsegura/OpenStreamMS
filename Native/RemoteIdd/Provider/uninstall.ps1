# uninstall.ps1 — deshace install.ps1 paso a paso según el fichero de estado.
# Uso (administrador):  .\uninstall.ps1 [-RestartTermService]
param([switch] $RestartTermService)
$ErrorActionPreference = 'Continue'

$state = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-install.json'
if (-not (Test-Path $state)) { Write-Host 'No hay nada instalado.'; return }
$s = Get-Content $state -Raw | ConvertFrom-Json
$ws = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations'

if ($s.WinStation -and (Test-Path "$ws\$($s.WinStation)")) {
    Remove-Item -Recurse -Force "$ws\$($s.WinStation)"
    Write-Host "Listener $($s.WinStation) eliminado (RDP-Tcp no se tocó)."
}
if ($s.ProviderDir) {
    $dll = Join-Path $s.ProviderDir 'OsmsRdsProvider.dll'
    if (Test-Path $dll) { regsvr32 /s /u $dll }
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
