# install.ps1 — instala RemoteIdd + proveedor RDS para PRUEBAS (firma de test).
#
# ⚠️ Pensado para una VM o un equipo de pruebas, no para el PC de los puestos:
#   - exige testsigning activado (bcdedit /set testsigning on + reinicio; con arranque
#     seguro activo hay que desactivarlo antes). Este script NO lo activa.
#   - importa un certificado de test en Root y TrustedPublisher del equipo.
# Lo que toca, y uninstall.ps1 deshace con el fichero de estado:
#   1) certificado de test        2) driver en el Driver Store (pnputil)
#   3) OsmsRdsProvider.dll en Program Files + su clase COM (HKLM)
#   4) TermService: ServiceDll pasa a TermWrap (llccd, v0.7 con hash fijo; el original, que
#      suele ser rdpwrap.dll, se guarda en el estado). En Windows cliente termsrv rechaza toda
#      pila de protocolo que no sea la de Microsoft (CRemoteConnectionManager::IsAllowNonRDPStack):
#      sin el parche, TermService no carga el proveedor y RDP-Tcp no escucha. RDP Wrapper de
#      asmtron no lo parchea; TermWrap sí, y además hace la multisesión.
#   5) RDP-Tcp: LoadableProtocol_Object pasa a este proveedor (el original se guarda en
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

# Log del proveedor (lo escribe TermService, que corre como NETWORK SERVICE; *S-1-5-20).
$log = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-provider.log'
New-Item -ItemType File -Force $log | Out-Null
icacls $log /grant '*S-1-5-20:(M)' /grant '*S-1-5-32-545:(R)' | Out-Null
$done.ProviderLog = $log; Save
# Log del driver (lo escribe el WUDFHost, que corre como LOCAL SERVICE; *S-1-5-19).
$dlog = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-driver.log'
New-Item -ItemType File -Force $dlog | Out-Null
icacls $dlog /grant '*S-1-5-19:(M)' /grant '*S-1-5-32-545:(R)' | Out-Null
$done.DriverLog = $dlog; Save

# 4) TermWrap como ServiceDll de TermService (ver cabecera). Versión y hashes fijos: la DLL
#    corre dentro de TermService.
$svcParams = 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters'
$twDir = Join-Path $env:ProgramFiles 'RDP Wrapper'
$twDll = '%ProgramFiles%\RDP Wrapper\TermWrap.dll'
$serviceDll = (Get-Item $svcParams).GetValue('ServiceDll', $null, 'DoNotExpandEnvironmentNames')
if ($serviceDll -notmatch 'TermWrap\.dll$') {
    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'TermWrap: solo se contempla x64.' }
    $twHashes = @{
        'TermWrap.dll' = '0D213D44A0FE0E6D174D81D7D986C29D306466F460E429B1B946D994F562A2D4'
        'Zydis.dll'    = 'AFBDBAF38B8F2789731AB83700B1F4F0095056C3F073288CFE773A103DDBDF81'
    }
    $twTmp = Join-Path $env:TEMP 'osms-termwrap'
    Remove-Item -Recurse -Force $twTmp -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $twTmp | Out-Null
    $zip = Join-Path $twTmp 'TermWrap-0.7.zip'
    Invoke-WebRequest -UseBasicParsing 'https://github.com/llccd/TermWrap/releases/download/v0.7/TermWrap-0.7.zip' -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne '096CC075E4D2EBBF2587F19D5E9A3003181E1D3AE44C514E0532BA222F82AFC4') {
        throw 'TermWrap-0.7.zip no tiene el hash esperado.'
    }
    Expand-Archive $zip $twTmp
    New-Item -ItemType Directory -Force $twDir | Out-Null
    $copied = @()
    foreach ($f in $twHashes.Keys) {
        $src = Join-Path $twTmp "x64\$f"
        if ((Get-FileHash $src -Algorithm SHA256).Hash -ne $twHashes[$f]) { throw "$f no tiene el hash esperado." }
        $dst = Join-Path $twDir $f
        # Solo se anota lo que no estaba: uninstall.ps1 no borra un TermWrap/Zydis ajeno.
        if (-not (Test-Path $dst)) { $copied += $dst }
        Copy-Item $src $dst -Force
    }
    Remove-Item -Recurse -Force $twTmp
    $done.OriginalServiceDll = $serviceDll; $done.TermWrapFiles = $copied; Save
    Set-ItemProperty $svcParams ServiceDll $twDll -Type ExpandString
    Write-Host "TermService: ServiceDll $serviceDll -> $twDll"
}
# El autoupdate.bat de asmtron (tarea programada al arrancar) reinstala rdpwrap.dll como
# ServiceDll si no lo encuentra: visto el 2026-10-27, deshacía TermWrap en cada arranque. Se
# desactivan sus tareas mientras RemoteIdd esté instalado; uninstall.ps1 las reactiva.
$autoTasks = @(Get-ScheduledTask | Where-Object {
    $_.State -ne 'Disabled' -and ($_.Actions | Where-Object { "$($_.Execute) $($_.Arguments)" -match 'autoupdate\.bat|RDPWInst' })
})
if ($autoTasks) {
    $done.DisabledTasks = @($autoTasks | ForEach-Object { "$($_.TaskPath)|$($_.TaskName)" }); Save
    $autoTasks | Disable-ScheduledTask | Out-Null
    Write-Host "Desactivadas las tareas de autoupdate de RDP Wrapper: $($done.DisabledTasks -join ', ')"
} else {
    Write-Warning 'No se encontró la tarea de autoupdate de RDP Wrapper: si algo reinstala rdpwrap al arrancar, TermWrap se perderá.'
}

# 5) RDP-Tcp: este proveedor delante del de Microsoft. El original primero en el estado,
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
