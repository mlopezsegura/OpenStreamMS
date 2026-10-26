# install.ps1 — instala RemoteIdd + proveedor RDS para PRUEBAS (firma de test).
#
# ⚠️ Pensado para una VM o un equipo de pruebas, no para el PC de los puestos:
#   - exige testsigning activado (bcdedit /set testsigning on + reinicio; con arranque
#     seguro activo hay que desactivarlo antes). Este script NO lo activa.
#   - importa un certificado de test en Root y TrustedPublisher del equipo.
# Lo que toca, y uninstall.ps1 deshace con el fichero de estado:
#   1) certificado de test        2) driver en el Driver Store (pnputil)
#   3) OsmsRdsProvider.dll en Program Files + su clase COM (HKLM)
#   4) listener nuevo WinStations\OSMS-Seat (copia de RDP-Tcp con otro puerto y este
#      proveedor). RDP-Tcp NO se modifica.
#
# Uso (administrador):  .\install.ps1 -Bin <salida de build.cmd> [-Port 3390] [-RefreshHz 120] [-RestartTermService]
# -RefreshHz: Hz de la pantalla del puesto (RDP no los transmite). Por defecto, 60.
param(
    [Parameter(Mandatory)] [string] $Bin,
    [int] $Port = 3390,
    [ValidateRange(24, 500)] [int] $RefreshHz = 60,
    [switch] $RestartTermService
)
$ErrorActionPreference = 'Stop'

$state = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-install.json'
$clsid = '{8B0F3C51-2D7A-4F6E-A1C9-5E4D2B7F0A36}'
$ws    = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations'
$wdk   = Join-Path $env:LOCALAPPDATA 'OpenStreamMS\wdk-nuget\10.0.26100.6584\c'
$sdk   = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    throw 'Hay que ejecutarlo como administrador.'
}
if (Test-Path $state) { throw "Ya instalado (ver $state). Ejecuta antes uninstall.ps1." }
if (-not ((bcdedit /enum '{current}') -match 'testsigning\s+Yes')) {
    throw 'testsigning no está activo. Solo para pruebas: bcdedit /set testsigning on y reiniciar (en una VM).'
}
foreach ($f in 'RemoteIdd.dll', 'RemoteIdd.inf', 'OsmsRdsProvider.dll') {
    if (-not (Test-Path (Join-Path $Bin $f))) { throw "Falta $f en $Bin (ejecuta build.cmd)." }
}
if (Test-Path "$ws\OSMS-Seat") { throw 'Ya existe WinStations\OSMS-Seat.' }

$done = [ordered]@{ Port = $Port }
function Save { New-Item -ItemType Directory -Force (Split-Path $state) | Out-Null; $done | ConvertTo-Json | Set-Content -Encoding utf8 $state }

# 1) certificado de test
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=OpenStreamMS RemoteIdd Test' `
        -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(1)
$cer = Join-Path $env:TEMP 'osms-remoteidd-test.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Remove-Item $cer
$done.Thumbprint = $cert.Thumbprint; Save

# 2) paquete del driver firmado
$pkg = Join-Path $env:TEMP 'osms-remoteidd-pkg'
Remove-Item -Recurse -Force $pkg -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pkg | Out-Null
Copy-Item (Join-Path $Bin 'RemoteIdd.dll'), (Join-Path $Bin 'RemoteIdd.inf') $pkg
& "$sdk\signtool.exe" sign /fd sha256 /sm /s My /sha1 $cert.Thumbprint "$pkg\RemoteIdd.dll" | Out-Null
& "$wdk\bin\10.0.26100.0\x86\Inf2Cat.exe" /driver:$pkg /os:10_X64 | Out-Null
if ($LASTEXITCODE) { throw 'Inf2Cat falló.' }
& "$sdk\signtool.exe" sign /fd sha256 /sm /s My /sha1 $cert.Thumbprint "$pkg\RemoteIdd.cat" | Out-Null
$out = pnputil /add-driver "$pkg\RemoteIdd.inf" /install
# 3010 = requiere reinicio; 259 = añadido al Driver Store sin dispositivo aún (lo crea
# Terminal Services al abrir la sesión del puesto). Ambos son éxito aquí.
if ($LASTEXITCODE -and $LASTEXITCODE -notin 3010, 259) { throw "pnputil falló ($LASTEXITCODE):`n$out" }
$done.DriverInf = ([regex]::Match(($out -join "`n"), 'oem\d+\.inf')).Value; Save

# 3) proveedor COM
$dir = Join-Path $env:ProgramFiles 'OpenStreamMS\RemoteIdd'
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item (Join-Path $Bin 'OsmsRdsProvider.dll') $dir -Force
& "$sdk\signtool.exe" sign /fd sha256 /sm /s My /sha1 $cert.Thumbprint "$dir\OsmsRdsProvider.dll" | Out-Null
regsvr32 /s "$dir\OsmsRdsProvider.dll"
if ($LASTEXITCODE) { throw 'regsvr32 falló.' }
$done.ProviderDir = $dir; Save

# 4) listener de puestos: copia de RDP-Tcp con otro puerto y este proveedor
reg copy "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" `
         "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\OSMS-Seat" /s /f | Out-Null
Set-ItemProperty "$ws\OSMS-Seat" PortNumber $Port -Type DWord
Set-ItemProperty "$ws\OSMS-Seat" LoadableProtocol_Object $clsid
Set-ItemProperty "$ws\OSMS-Seat" OsmsRefreshHz $RefreshHz -Type DWord
$done.WinStation = 'OSMS-Seat'; Save

if ($RestartTermService) {
    Write-Warning 'Reiniciando TermService: se cierran TODAS las sesiones remotas abiertas.'
    Restart-Service TermService -Force
} else {
    Write-Host 'Listo. El listener OSMS-Seat se activa al reiniciar TermService o el equipo.'
}
Write-Host "Prueba: abre una sesión en 127.0.0.1:$Port y ejecuta test\verify-seat.ps1."
