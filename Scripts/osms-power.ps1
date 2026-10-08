# OpenStreamMS: apaga o reinicia el PC desde una app de Moonlight (Reboot / Power Off).
#
# Lo lanza Sunshine dentro de la sesión de stream, con un usuario sin privilegios y con
# otros usuarios conectados: ahí shutdown.exe falla ("hay otras personas usando este
# equipo"). Por eso se pide al servicio de OpenStreamMS (SYSTEM), que hace un
# shutdown.exe /f sin preguntar. La API no pide credenciales desde localhost.
param(
    [Parameter(Mandatory)] [ValidateSet('restart', 'shutdown')] [string] $Action
)

$ErrorActionPreference = 'Stop'

$port = 5000
$config = Join-Path $PSScriptRoot '..\service.config.json'
if (Test-Path $config) {
    $p = (Get-Content $config -Raw | ConvertFrom-Json).ApiPort
    if ($p) { $port = $p }
}

$body = @{
    delaySeconds = 3   # margen para que Moonlight cierre el stream
    message      = "OpenStreamMS: $(if ($Action -eq 'restart') { 'reinicio' } else { 'apagado' }) desde Moonlight"
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/api/power/$Action" `
                  -ContentType 'application/json' -Body $body | Out-Null
