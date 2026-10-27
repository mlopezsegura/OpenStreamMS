# install.ps1 — instala RemoteIdd + proveedor RDS para PRUEBAS (firma de test).
#
# ⚠️ Pensado para una VM o un equipo de pruebas, no para el PC de los puestos:
#   - exige testsigning activado (bcdedit /set testsigning on + reinicio; con arranque
#     seguro activo hay que desactivarlo antes). Este script NO lo activa.
#   - importa un certificado de test en Root y TrustedPublisher del equipo.
# Lo que toca, y uninstall.ps1 deshace con el fichero de estado:
#   1) certificado de test        2) driver en el Driver Store (pnputil)
#   3) OsmsRdsProvider.dll en Program Files + su clase COM (HKLM)
#   4) RDP-Tcp: LoadableProtocol_Object pasa a este proveedor (el original se guarda en
#      OsmsInnerProtocol_Object y en el estado) + OsmsRefreshHz.
# Windows cliente (Pro) solo levanta el listener RDP-Tcp, así que el proveedor va ahí, como
# hace DuoStream. Solo las conexiones de puesto (cliente 127.0.0.2) reciben RemoteIdd; el
# resto de conexiones RDP siguen enteras con el protocolo y el RdpIdd de Microsoft.
#
# Uso (administrador):  .\install.ps1 -Bin <salida de build.cmd> [-RefreshHz 120] [-AllConnections] [-RestartTermService]
# -AllConnections: RemoteIdd también en las sesiones RDP que llegan por la red, no solo en los
#                  puestos locales. Por defecto, solo conexiones desde el propio equipo.
# -RefreshHz: Hz de la pantalla del puesto (RDP no los transmite). Por defecto, 60.
param(
    [Parameter(Mandatory)] [string] $Bin,
    [ValidateRange(24, 500)] [int] $RefreshHz = 60,
    [switch] $AllConnections,
    [switch] $RestartTermService
)
$ErrorActionPreference = 'Stop'

$state = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-install.json'
$clsid = '{8B0F3C51-2D7A-4F6E-A1C9-5E4D2B7F0A36}'
$ws    = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations'
$wdk   = Join-Path $env:LOCALAPPDATA 'OpenStreamMS\wdk-nuget\10.0.26100.6584\c'
$sdk   = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64"

# Rol integrado y no el nombre del grupo: en Windows en español es "Administradores".
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Hay que ejecutarlo como administrador.'
}
if (Test-Path $state) { throw "Ya instalado (ver $state). Ejecuta antes uninstall.ps1." }
# Opciones de arranque en vigor (no depende del idioma, a diferencia de la salida de bcdedit).
if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').SystemStartOptions -notmatch 'TESTSIGNING') {
    throw 'testsigning no está activo. Solo para pruebas: bcdedit /set testsigning on y reiniciar (en una VM).'
}
foreach ($f in 'RemoteIdd.dll', 'RemoteIdd.inf', 'OsmsRdsProvider.dll') {
    if (-not (Test-Path (Join-Path $Bin $f))) { throw "Falta $f en $Bin (ejecuta build.cmd)." }
}
$rdpTcp = "$ws\RDP-Tcp"
$original = (Get-ItemProperty $rdpTcp).LoadableProtocol_Object
if (-not $original) { throw 'RDP-Tcp no tiene LoadableProtocol_Object: no es el RDP de Windows esperado.' }
if ($original -eq $clsid) { throw 'RDP-Tcp ya apunta a este proveedor (¿instalación anterior a medias?).' }

$done = [ordered]@{}

# signtool: parar si falla (si no, el error saldría después como "driver sin firmar").
function Sign($file) {
    & "$sdk\signtool.exe" sign /fd sha256 /sm /s My /sha1 $cert.Thumbprint $file | Out-Null
    if ($LASTEXITCODE) { throw "signtool falló al firmar $file." }
}
# regsvr32 es una aplicación gráfica: PowerShell no la espera ni recoge su código.
function RegSvr($arguments) {
    $p = Start-Process regsvr32.exe -ArgumentList $arguments -Wait -PassThru
    if ($p.ExitCode) { throw "regsvr32 $arguments falló ($($p.ExitCode))." }
}
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
Sign "$pkg\RemoteIdd.dll"
& "$wdk\bin\10.0.26100.0\x86\Inf2Cat.exe" /driver:$pkg /os:10_X64 | Out-Null
if ($LASTEXITCODE) { throw 'Inf2Cat falló.' }
Sign "$pkg\RemoteIdd.cat"
$out = pnputil /add-driver "$pkg\RemoteIdd.inf" /install
# 3010 = requiere reinicio; 259 = añadido al Driver Store sin dispositivo aún (lo crea
# Terminal Services al abrir la sesión del puesto). Ambos son éxito aquí.
if ($LASTEXITCODE -and $LASTEXITCODE -notin 3010, 259) { throw "pnputil falló ($LASTEXITCODE):`n$out" }
$done.DriverInf = ([regex]::Match(($out -join "`n"), 'oem\d+\.inf')).Value; Save

# 3) proveedor COM
$dir = Join-Path $env:ProgramFiles 'OpenStreamMS\RemoteIdd'
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item (Join-Path $Bin 'OsmsRdsProvider.dll') $dir -Force
Sign "$dir\OsmsRdsProvider.dll"
RegSvr "/s `"$dir\OsmsRdsProvider.dll`""
$done.ProviderDir = $dir; Save

# 4) RDP-Tcp: este proveedor delante del de Microsoft. El original primero en el estado,
#    para que uninstall.ps1 lo restaure aunque algo falle a continuación.
$done.OriginalProtocol = $original; Save
Set-ItemProperty $rdpTcp OsmsInnerProtocol_Object $original
Set-ItemProperty $rdpTcp OsmsRefreshHz $RefreshHz -Type DWord
Set-ItemProperty $rdpTcp OsmsRemoteIddAllConnections ([int][bool]$AllConnections) -Type DWord
Set-ItemProperty $rdpTcp LoadableProtocol_Object $clsid
$done.RdpTcpPatched = $true; Save

if ($RestartTermService) {
    Write-Warning 'Reiniciando TermService: se cierran TODAS las sesiones remotas abiertas.'
    Restart-Service TermService -Force
} else {
    Write-Host 'Listo. El proveedor se carga al reiniciar TermService o el equipo.'
}
Write-Host 'Prueba: mstsc /v:127.0.0.2 con un usuario de puesto y test\verify-seat.ps1 -User <usuario>.'
