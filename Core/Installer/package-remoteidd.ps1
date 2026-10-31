# package-remoteidd.ps1 — paquete de RemoteIdd ya firmado para el instalador (build-installer.bat,
# también en la CI). Lo que en install.ps1 se hace en el equipo de destino (firmar con signtool y
# crear el catálogo con Inf2Cat, que exigen el SDK y el WDK) se hace aquí, al compilar.
#
# Firma de PRUEBA con un certificado autofirmado de un solo uso: se crea en cada compilación y
# su clave privada se borra en cuanto se ha firmado, así que nadie puede firmar nada más con él
# (ni la CI tiene que guardar secretos). Al paquete solo va la parte pública (RemoteIdd.cer), que
# install.ps1 -Package mete en Root y TrustedPublisher del equipo de destino. Para distribuir de
# verdad hace falta firma attestation de Microsoft (ver Native\RemoteIdd\build.md).
#
# Uso: package-remoteidd.ps1 -Bin <salida de build.cmd> -OutDir <carpeta del paquete>
param(
    [Parameter(Mandatory)] [string] $Bin,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'

$wdk = Join-Path $env:LOCALAPPDATA 'OpenStreamMS\wdk-nuget\10.0.26100.6584\c'
$inf2cat = "$wdk\bin\10.0.26100.0\x86\Inf2Cat.exe"
# signtool del SDK más nuevo que haya (en la CI no tiene por qué ser la misma versión que aquí).
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName

foreach ($f in 'RemoteIdd.dll', 'RemoteIdd.inf', 'OsmsRdsProvider.dll') {
    if (-not (Test-Path (Join-Path $Bin $f))) { throw "Falta $f en $Bin (ejecuta Native\RemoteIdd\build.cmd)." }
}
if (-not $signtool) { throw 'No se encuentra signtool.exe (hace falta el SDK de Windows).' }
if (-not (Test-Path $inf2cat)) { throw "No se encuentra $inf2cat (lo descarga Native\RemoteIdd\build.cmd)." }

# Certificado de un solo uso: se borra (con su clave privada) al terminar, pase lo que pase.
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=OpenStreamMS RemoteIdd Test' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3)

function Sign($file) {
    & $signtool sign /fd sha256 /s My /sha1 $cert.Thumbprint $file | Out-Null
    if ($LASTEXITCODE) { throw "signtool falló al firmar $file." }
}

try {

Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $OutDir | Out-Null
Copy-Item (Join-Path $Bin 'RemoteIdd.dll'), (Join-Path $Bin 'RemoteIdd.inf'), (Join-Path $Bin 'OsmsRdsProvider.dll') $OutDir
Sign (Join-Path $OutDir 'RemoteIdd.dll')
Sign (Join-Path $OutDir 'OsmsRdsProvider.dll')
# Inf2Cat cataloga todo lo que hay en la carpeta: solo el driver, en una aparte.
$drv = Join-Path $env:TEMP 'osms-remoteidd-catalog'
Remove-Item -Recurse -Force $drv -ErrorAction SilentlyContinue
New-Item -ItemType Directory $drv | Out-Null
Copy-Item (Join-Path $OutDir 'RemoteIdd.dll'), (Join-Path $OutDir 'RemoteIdd.inf') $drv
& $inf2cat /driver:$drv /os:10_X64 | Out-Null
if ($LASTEXITCODE) { throw 'Inf2Cat falló (¿DriverVer con fecha futura?).' }
Copy-Item (Join-Path $drv 'RemoteIdd.cat') $OutDir
Remove-Item -Recurse -Force $drv
Sign (Join-Path $OutDir 'RemoteIdd.cat')
Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'RemoteIdd.cer') | Out-Null
}
finally {
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
}

# Los scripts de instalación van con el paquete (install.ps1 -Package lo usa tal cual).
$provider = Join-Path $PSScriptRoot '..\..\Native\RemoteIdd\Provider'
Copy-Item (Join-Path $provider 'install.ps1'), (Join-Path $provider 'uninstall.ps1'), (Join-Path $provider 'termservice.ps1') $OutDir
Write-Host "Paquete RemoteIdd listo en $OutDir (certificado de un solo uso $($cert.Thumbprint), clave borrada)."
