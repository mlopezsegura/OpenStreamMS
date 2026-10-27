using Microsoft.Extensions.Hosting;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Services.Session;
using System.Timers;
using Timer = System.Timers.Timer;

namespace OpenStreamMS.Services.OpenStream
{
    /// <summary>
    /// BackgroundService que monitoriza cada 5 segundos las sesiones activas
    /// gestionadas por <see cref="StreamSessionService"/> y mantiene Sunshine vivo.
    /// </summary>
    public class OpenStreamService : BackgroundService
    {
        private readonly StreamSessionService _sessionSvc;
        private readonly RdpWrapperManager _rdpWrapper;
        private readonly Timer _timer;
        private int _monitoring;

        public OpenStreamService(StreamSessionService sessionSvc, RdpWrapperManager rdpWrapper)
        {
            _sessionSvc = sessionSvc;
            _rdpWrapper = rdpWrapper;
            _timer = new Timer(5000) { AutoReset = true };
            _timer.Elapsed += (_, _) => MonitorTick();
        }

        /// <summary>
        /// Evita ticks solapados: un relanzamiento de Sunshine (verificacion de encoder
        /// incluida) puede tardar >20 s, y System.Timers.Timer seguiria disparando en
        /// paralelo → lanzamientos duplicados y hilos del pool bloqueados.
        /// </summary>
        private void MonitorTick()
        {
            if (Interlocked.Exchange(ref _monitoring, 1) == 1) return;
            try { _sessionSvc.MonitorAll(); }
            finally { Volatile.Write(ref _monitoring, 0); }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.Log("[Service] OpenStreamMS iniciado. API disponible en el puerto configurado.");
            // Antes de los puestos: si TermWrap no funciona, se vuelve a asmtron (reinicia TermService).
            try { await Task.Run(() => _rdpWrapper.EnsureMultiRdp(stoppingToken), stoppingToken); }
            catch (Exception ex) { Logger.Error($"[Service] Comprobación de multi-RDP fallida: {ex}"); }
            _timer.Start();
            _sessionSvc.AutoStartEnabled();
            await Task.Delay(Timeout.Infinite, stoppingToken)
                      .ContinueWith(_ => { }, TaskContinuationOptions.None);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _timer.Stop();
            Logger.Log("[Service] Detenido.");
            return base.StopAsync(cancellationToken);
        }
    }
}
