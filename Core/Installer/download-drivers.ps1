# Descarga el instalador de la ultima release de ViGEmBus y de HidHide (nefarius)
# en OutDir, para incluirlos en el instalador de OpenStreamMS.
param([Parameter(Mandatory)] [string] $OutDir)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Force $OutDir | Out-Null

foreach ($d in @(
    @{ Repo = 'nefarius/ViGEmBus'; Prefix = 'ViGEmBus_' },
    @{ Repo = 'nefarius/HidHide';  Prefix = 'HidHide_' })) {

    $release = Invoke-RestMethod "https://api.github.com/repos/$($d.Repo)/releases/latest" `
                                 -Headers @{ 'User-Agent' = 'OpenStreamMS' }
    $asset = $release.assets | Where-Object { $_.name -like "$($d.Prefix)*.exe" } | Select-Object -First 1
    if (-not $asset) { throw "No hay instalador .exe en la ultima release de $($d.Repo)" }

    $dest = Join-Path $OutDir $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $dest -UseBasicParsing

    # Solo instaladores firmados por nefarius
    $sig = Get-AuthenticodeSignature $dest
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Nefarius') {
        Remove-Item $dest -Force
        throw "Firma no valida en $($asset.name): $($sig.Status) $($sig.SignerCertificate.Subject)"
    }
    Write-Host "  $($asset.name) ($($release.tag_name))"
}
