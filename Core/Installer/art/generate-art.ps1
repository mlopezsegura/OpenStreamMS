# Genera las imágenes del instalador a partir de background.html y hero.html con Chrome (o Edge)
# sin ventana. Los PNG resultantes se suben al repositorio: la CI no necesita ejecutar esto.
#   powershell -ExecutionPolicy Bypass -File Core\Installer\art\generate-art.ps1
param([int]$Frames = 24)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Hace falta Chrome o Edge.' }
$profileDir = Join-Path ([IO.Path]::GetTempPath()) "osms-art-$PID"

function Shot([string]$page, [string]$hash, [int]$w, [int]$h, [string]$out, [switch]$Transparent) {
    $url  = ([Uri](Join-Path $here $page)).AbsoluteUri + "#$hash"
    $cargs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=$profileDir",
              "--window-size=$w,$h", '--virtual-time-budget=1500', "--screenshot=$out", $url)
    if ($Transparent) { $cargs = @('--default-background-color=00000000') + $cargs }
    # Chrome escribe en stderr aunque vaya bien: con Stop, PowerShell 5 lo tomaría por error.
    Start-Process -FilePath $browser -ArgumentList $cargs -Wait -WindowStyle Hidden
    if (-not (Test-Path $out)) { throw "No se generó $out" }
    Write-Host $out
}

# Fondo de las páginas: tres tamaños (100 %, 150 % y 200 % de PPP) por tema; Setup elige el mejor.
foreach ($theme in 'light', 'dark') {
    foreach ($s in @(@(596, 432), @(994, 720), @(1272, 922))) {
        Shot 'background.html' "$theme,0,$($s[0]),$($s[1])" $s[0] $s[1] (Join-Path $here "back-$theme-$($s[0]).png")
    }
    # Fotogramas de la animación (bienvenida, instalación y final), a media resolución y sin la
    # rejilla: el fondo es difuso y estirado no se nota, y así el instalador pesa poco.
    for ($i = 0; $i -lt $Frames; $i++) {
        $phase = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.####}', $i / $Frames)
        Shot 'background.html' "$theme,$phase,298,216,nogrid" 298 216 (Join-Path $here ("anim-$theme-{0:00}.png" -f $i))
    }
}
# Imagen grande (bienvenida y final) y logo pequeño, transparentes: sirven para los dos temas.
foreach ($s in @(@(164, 314), @(246, 471), @(328, 628))) {
    Shot 'hero.html' "hero,$($s[0]),$($s[1])" $s[0] $s[1] (Join-Path $here "hero-$($s[0]).png") -Transparent
}
foreach ($s in 58, 87, 116) {
    Shot 'hero.html' "small,$s,$s" $s $s (Join-Path $here "logo-$s.png") -Transparent
}
Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
