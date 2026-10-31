# termservice.ps1 — utilidades de TermService para install.ps1 y uninstall.ps1.

# Copia un fichero que TermService puede tener cargado (el proveedor, TermWrap). Windows deja
# renombrar una DLL en uso aunque no sobrescribirla: la vieja se aparta como .old-* (se borra en
# la siguiente instalación) y la nueva entra con su nombre; TermService la carga al reiniciar.
function Copy-InUse([string] $Source, [string] $Destination) {
    $dir = Split-Path $Destination
    Get-ChildItem $dir -Filter '*.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    try { Copy-Item $Source $Destination -Force -ErrorAction Stop }
    catch {
        $old = "$Destination.old-$([DateTime]::Now.Ticks)"
        Rename-Item $Destination (Split-Path $old -Leaf) -ErrorAction Stop
        Copy-Item $Source $Destination -Force -ErrorAction Stop
        Write-Host "$(Split-Path $Destination -Leaf) estaba en uso: el nuevo entra al reiniciar (el viejo queda como $(Split-Path $old -Leaf))."
    }
}

# Reinicio de TermService que no se queda colgado (install.ps1 y uninstall.ps1 con
# -RestartTermService; el instalador no lo usa: pide reiniciar el equipo).
#
# Restart-Service espera sin límite, y TermService puede quedarse en "deteniéndose" (visto el
# 2026-10-31 al desinstalar RemoteIdd desde el instalador: el instalador no terminaba nunca).
# Si no se para a tiempo, se mata su proceso (svchost propio, grupo termsvcs): las sesiones
# remotas ya se estaban cerrando de todas formas.
function Restart-TermService([int] $TimeoutSeconds = 30) {
    $svc = Get-Service TermService
    if ($svc.Status -ne 'Stopped') {
        Stop-Service TermService -Force -NoWait -ErrorAction SilentlyContinue
        try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($TimeoutSeconds)) }
        catch {
            $id = (Get-CimInstance Win32_Service -Filter "Name='TermService'").ProcessId
            # Solo si el proceso es suyo: con poca memoria Windows agrupa servicios en un svchost.
            $shared = @(Get-CimInstance Win32_Service -Filter "ProcessId=$id").Count -gt 1
            if (-not $id -or $shared) { throw "TermService no se detiene y su proceso ($id) es compartido: reinicia el equipo." }
            Write-Warning "TermService no se detuvo en $TimeoutSeconds s: se fuerza el cierre de su proceso ($id)."
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
        }
    }
    Start-Service TermService
    Write-Host 'TermService reiniciado.'
}
