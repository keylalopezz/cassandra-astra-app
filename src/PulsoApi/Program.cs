using System.Diagnostics;
using PulsoApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<StoreHolder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StoreHolder>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var version = app.Configuration["APP_VERSION"] ?? "local";
var inicio = DateTimeOffset.UtcNow;

// Salud del servicio: Render la usa para saber si el contenedor está listo.
app.MapGet("/health", async (StoreHolder h) =>
{
    if (h.Store is null) return Results.Json(new { status = "starting", error = h.Error }, statusCode: 503);
    var info = await h.Store.InfoAsync();
    return Results.Ok(new { status = "ok", ms = Math.Round(info.Ms, 1) });
});

// Información del despliegue: versión (commit), motor y datacenter.
app.MapGet("/api/info", async (StoreHolder h) =>
{
    var store = h.Requerir();
    var info = await store.InfoAsync();
    return Results.Ok(new
    {
        app = "Pulso Tacna",
        version,
        motor = store.EsAstra ? "DataStax Astra DB (Apache Cassandra)" : "Apache Cassandra",
        cassandra = info.Version,
        datacenter = info.Datacenter,
        keyspace = store.Keyspace,
        latenciaMs = Math.Round(info.Ms, 1),
        iniciado = inicio,
    });
});

// Estaciones con su última lectura y el conteo del día (contador de Cassandra).
app.MapGet("/api/estaciones", async (StoreHolder h) =>
{
    var store = h.Requerir();
    var sw = Stopwatch.StartNew();
    var hoy = Modelo.DiaLima(DateTimeOffset.UtcNow);
    var estaciones = await store.EstacionesAsync();
    var conteo = await store.ConteoDelDiaAsync(hoy);
    var resumen = new List<EstacionResumen>();
    foreach (var e in estaciones)
    {
        var ultima = await store.UltimaAsync(e.EstacionId, hoy) ?? await store.UltimaAsync(e.EstacionId, hoy.AddDays(-1));
        resumen.Add(new EstacionResumen(e, ultima, conteo.GetValueOrDefault(e.EstacionId)));
    }
    return Results.Ok(new Respuesta<List<EstacionResumen>>(resumen,
        [CassandraStore.CqlEstaciones, CassandraStore.CqlConteo, CassandraStore.CqlUltima],
        Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
});

// Serie de tiempo de una estación: lee la partición de hoy y la de ayer.
app.MapGet("/api/estaciones/{id}/lecturas", async (string id, int? horas, StoreHolder h) =>
{
    var store = h.Requerir();
    var sw = Stopwatch.StartNew();
    var ahora = DateTimeOffset.UtcNow;
    var desde = ahora.AddHours(-(horas ?? 24));
    var hoy = Modelo.DiaLima(ahora);
    var lecturas = (await store.LecturasDelDiaAsync(id, hoy, 500))
        .Concat(await store.LecturasDelDiaAsync(id, hoy.AddDays(-1), 500))
        .Where(l => l.Ts >= desde).OrderBy(l => l.Ts).ToList();
    return Results.Ok(new Respuesta<List<Lectura>>(lecturas, [CassandraStore.CqlLecturasDia + "   -- partición de hoy y de ayer"],
        Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
});

// Registrar una lectura (INSERT con TTL + contador).
app.MapPost("/api/lecturas", async (NuevaLectura nueva, StoreHolder h) =>
{
    var store = h.Requerir();
    var estaciones = await store.EstacionesAsync();
    var error = Modelo.Validar(nueva, estaciones.Select(e => e.EstacionId));
    if (error is not null) return Results.BadRequest(new { error });
    var sw = Stopwatch.StartNew();
    var lectura = await store.RegistrarAsync(nueva, DateTimeOffset.UtcNow);
    return Results.Ok(new Respuesta<Lectura>(lectura, [CassandraStore.CqlInsert, CassandraStore.CqlContador],
        Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
});

// Simulador: genera una lectura por estación (lo usa el panel y el workflow de ingesta).
app.MapPost("/api/simular", async (int? rondas, int? intervalo, StoreHolder h) =>
{
    var store = h.Requerir();
    var n = Math.Clamp(rondas ?? 1, 1, 48);
    var minutos = Math.Clamp(intervalo ?? 3, 1, 60);
    var sw = Stopwatch.StartNew();
    var rnd = Random.Shared;
    var estaciones = await store.EstacionesAsync();
    // Las rondas se reparten hacia atrás en el tiempo (intervalo en minutos) para dibujar una curva.
    var ahora = DateTimeOffset.UtcNow;
    var pendientes = Enumerable.Range(0, n)
        .SelectMany(i => estaciones.Select(e => (e, ts: ahora.AddMinutes(-(n - 1 - i) * minutos))))
        .Select(x => (x.ts, lectura: Modelo.Simular(x.e, x.ts, rnd))).ToList();
    // Cassandra escala con escrituras concurrentes: hasta 32 en vuelo a la vez.
    using var limite = new SemaphoreSlim(32);
    var escritas = await Task.WhenAll(pendientes.Select(async x =>
    {
        await limite.WaitAsync();
        try { return await store.RegistrarAsync(x.lectura, x.ts); }
        finally { limite.Release(); }
    }));
    return Results.Ok(new Respuesta<int>(escritas.Length, [CassandraStore.CqlInsert, CassandraStore.CqlContador],
        Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
});

app.Run();

/// <summary>Conecta con Cassandra en segundo plano (con reintentos) sin bloquear el arranque.</summary>
public sealed class StoreHolder(IConfiguration cfg, ILogger<StoreHolder> log) : BackgroundService
{
    public CassandraStore? Store { get; private set; }
    public string? Error { get; private set; }

    public CassandraStore Requerir() =>
        Store ?? throw new BadHttpRequestException("La base de datos todavía se está conectando.", 503);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        for (var intento = 1; !ct.IsCancellationRequested; intento++)
        {
            try
            {
                Store = await CassandraStore.ConectarAsync(cfg, log);
                Error = null;
                return;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                log.LogWarning("Intento {N} de conexión a Cassandra falló: {Error}", intento, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * intento)), ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (Store is not null) await Store.DisposeAsync();
        await base.StopAsync(ct);
    }
}

public partial class Program;
