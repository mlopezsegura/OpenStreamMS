using System.Diagnostics;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Muestras de latencia (ms) de una ventana de tiempo, para el resumen que el agente y el
/// presentador escriben cada 10 s. Las marcas son QPC, que es el mismo reloj en todos los
/// procesos del equipo, así que se pueden restar marcas tomadas en otro proceso.
/// Solo la usa un hilo cada instancia.
/// </summary>
internal sealed class SeatLatency(string name)
{
    private readonly List<double> _samples = new(4096);

    public static double Ms(long fromQpc, long toQpc) => (toQpc - fromQpc) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Añade el tiempo desde <paramref name="fromQpc"/> hasta ahora (0 = sin marca, se ignora).</summary>
    public void AddSince(long fromQpc)
    {
        if (fromQpc == 0) return;
        double ms = Ms(fromQpc, Stopwatch.GetTimestamp());
        if (ms is >= 0 and < 10_000 && _samples.Count < 100_000) _samples.Add(ms);
    }

    /// <summary>"nombre p50/p95/máx (n)" y vacía la ventana; "" si no hubo muestras.</summary>
    public string Flush()
    {
        if (_samples.Count == 0) return "";
        _samples.Sort();
        double P(double q) => _samples[Math.Min(_samples.Count - 1, (int)(q * _samples.Count))];
        string s = $" {name}={P(0.5):F1}/{P(0.95):F1}/{_samples[^1]:F1}ms(n={_samples.Count})";
        _samples.Clear();
        return s;
    }
}
