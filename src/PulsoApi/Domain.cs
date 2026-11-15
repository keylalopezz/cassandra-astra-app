namespace PulsoApi;

/// <summary>Estación meteorológica (tabla <c>estaciones</c>).</summary>
public record Estacion(string EstacionId, string Nombre, string Distrito, int Altitud);

/// <summary>Una lectura de una estación (tabla <c>lecturas_por_estacion_dia</c>).</summary>
public record Lectura(string EstacionId, DateTimeOffset Ts, double Temperatura, double Humedad);

/// <summary>Cuerpo del POST /api/lecturas.</summary>
public record NuevaLectura(string EstacionId, double Temperatura, double Humedad);

/// <summary>Estación con su última lectura, para las tarjetas del panel.</summary>
public record EstacionResumen(Estacion Estacion, Lectura? Ultima, long LecturasHoy);

/// <summary>Respuesta estándar: datos + sentencias CQL ejecutadas + tiempo total.</summary>
public record Respuesta<T>(T Data, IReadOnlyList<string> Cql, double Ms);

/// <summary>Reglas del modelo de datos, sin dependencias de Cassandra (se prueban en CI).</summary>
public static class Modelo
{
    /// <summary>Las lecturas viven 30 días (TTL de Cassandra) y luego se eliminan solas.</summary>
    public const int TtlSegundos = 30 * 24 * 60 * 60;

    public static readonly TimeSpan ZonaLima = TimeSpan.FromHours(-5);

    /// <summary>
    /// Día (hora de Perú) que forma parte de la clave de partición.
    /// Particionar por (estacion_id, dia) evita particiones infinitas:
    /// cada estación genera una partición nueva por día.
    /// </summary>
    public static DateOnly DiaLima(DateTimeOffset ts) => DateOnly.FromDateTime(ts.ToOffset(ZonaLima).DateTime);

    public static readonly IReadOnlyList<Estacion> EstacionesIniciales =
    [
        new("TAC-CERCADO", "Plaza de Armas", "Tacna (Cercado)", 562),
        new("TAC-ALIANZA", "Alto de la Alianza", "Alto de la Alianza", 650),
        new("TAC-POCOLLAY", "Pocollay", "Pocollay", 650),
        new("TAC-GREGORIO", "Gregorio Albarracín", "Crnl. Gregorio Albarracín", 600),
        new("TAC-CANDARAVE", "Candarave", "Candarave", 3415),
    ];

    /// <summary>Valida una lectura antes de escribirla. Devuelve null si es válida.</summary>
    public static string? Validar(NuevaLectura l, IEnumerable<string> estacionesValidas)
    {
        if (string.IsNullOrWhiteSpace(l.EstacionId) || !estacionesValidas.Contains(l.EstacionId))
            return $"La estación '{l.EstacionId}' no existe.";
        if (double.IsNaN(l.Temperatura) || l.Temperatura < -30 || l.Temperatura > 50)
            return "La temperatura debe estar entre -30 y 50 °C.";
        if (double.IsNaN(l.Humedad) || l.Humedad < 0 || l.Humedad > 100)
            return "La humedad debe estar entre 0 y 100 %.";
        return null;
    }

    /// <summary>
    /// Genera una lectura realista: temperatura según altitud y hora del día
    /// (curva diaria con máximo a las 14:00) más un pequeño ruido.
    /// </summary>
    public static NuevaLectura Simular(Estacion e, DateTimeOffset ahora, Random rnd)
    {
        var horaLima = ahora.ToOffset(ZonaLima).TimeOfDay.TotalHours;
        var media = 24.0 - (e.Altitud - 560) * 0.0065;          // gradiente térmico: -0,65 °C por cada 100 m
        var oscilacion = e.Altitud > 2000 ? 9.0 : 5.5;           // la sierra tiene más amplitud térmica
        var curva = Math.Cos((horaLima - 14) / 24 * 2 * Math.PI); // 1 a las 14:00, -1 a las 02:00
        var temperatura = media + oscilacion * curva + (rnd.NextDouble() - 0.5) * 1.2;
        var humedad = Math.Clamp(62 - 18 * curva + (rnd.NextDouble() - 0.5) * 6, 5, 100);
        return new NuevaLectura(e.EstacionId, Math.Round(temperatura, 1), Math.Round(humedad, 1));
    }
}
