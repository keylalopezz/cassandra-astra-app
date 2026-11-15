using System.Diagnostics;
using Cassandra;
using ISession = Cassandra.ISession;

namespace PulsoApi;

/// <summary>
/// Acceso a Apache Cassandra. En la nube se conecta a DataStax Astra DB con el
/// Secure Connect Bundle; en CI y en local, a un contenedor de Cassandra.
/// </summary>
public sealed class CassandraStore : IAsyncDisposable
{
    private readonly ICluster _cluster;
    private readonly ISession _session;
    public string Keyspace { get; }
    public bool EsAstra { get; }

    private PreparedStatement _insertLectura = null!;
    private PreparedStatement _contarLectura = null!;
    private PreparedStatement _lecturasDia = null!;
    private PreparedStatement _ultimaLectura = null!;
    private PreparedStatement _conteoDia = null!;
    private PreparedStatement _insertEstacion = null!;

    // Texto CQL que se muestra en la consola del panel.
    public const string CqlInsert =
        "INSERT INTO lecturas_por_estacion_dia (estacion_id, dia, ts, temperatura, humedad) VALUES (?, ?, ?, ?, ?) USING TTL 2592000";
    public const string CqlContador =
        "UPDATE conteo_lecturas_dia SET total = total + 1 WHERE dia = ? AND estacion_id = ?";
    public const string CqlLecturasDia =
        "SELECT ts, temperatura, humedad FROM lecturas_por_estacion_dia WHERE estacion_id = ? AND dia = ? LIMIT ?";
    public const string CqlUltima =
        "SELECT ts, temperatura, humedad FROM lecturas_por_estacion_dia WHERE estacion_id = ? AND dia = ? LIMIT 1";
    public const string CqlConteo =
        "SELECT estacion_id, total FROM conteo_lecturas_dia WHERE dia = ?";
    public const string CqlEstaciones =
        "SELECT estacion_id, nombre, distrito, altitud FROM estaciones";

    private CassandraStore(ICluster cluster, ISession session, string keyspace, bool esAstra)
    {
        _cluster = cluster; _session = session; Keyspace = keyspace; EsAstra = esAstra;
    }

    public static async Task<CassandraStore> ConectarAsync(IConfiguration cfg, ILogger log)
    {
        var keyspace = cfg["ASTRA_KEYSPACE"] ?? cfg["CASSANDRA_KEYSPACE"] ?? "pulso";
        var bundleB64 = cfg["ASTRA_SCB_B64"];
        var bundlePath = cfg["ASTRA_SCB_PATH"];
        var clientId = cfg["ASTRA_CLIENT_ID"];
        var secret = cfg["ASTRA_CLIENT_SECRET"];

        Builder builder = Cluster.Builder()
            .WithQueryOptions(new QueryOptions().SetConsistencyLevel(ConsistencyLevel.LocalQuorum));
        bool esAstra;

        if (!string.IsNullOrEmpty(bundleB64) || !string.IsNullOrEmpty(bundlePath))
        {
            // DataStax Astra DB: el bundle trae el host, el puerto y los certificados mTLS.
            if (string.IsNullOrEmpty(bundlePath))
            {
                bundlePath = Path.Combine(Path.GetTempPath(), "secure-connect-bundle.zip");
                await File.WriteAllBytesAsync(bundlePath, Convert.FromBase64String(bundleB64!));
            }
            builder = builder.WithCloudSecureConnectionBundle(bundlePath).WithCredentials(clientId, secret);
            esAstra = true;
        }
        else
        {
            // Cassandra local o en CI (contenedor cassandra:5.0).
            var host = cfg["CASSANDRA_CONTACT_POINTS"] ?? "localhost";
            var port = int.Parse(cfg["CASSANDRA_PORT"] ?? "9042");
            builder = builder.AddContactPoints(host.Split(',')).WithPort(port)
                .WithQueryOptions(new QueryOptions().SetConsistencyLevel(ConsistencyLevel.One));
            esAstra = false;
        }

        var cluster = builder.Build();
        var session = await cluster.ConnectAsync();

        if (!esAstra)
        {
            // En Astra el keyspace lo crea Terraform; en local lo creamos aquí.
            await session.ExecuteAsync(new SimpleStatement(
                $"CREATE KEYSPACE IF NOT EXISTS {keyspace} WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}"));
        }
        session.ChangeKeyspace(keyspace);

        var store = new CassandraStore(cluster, session, keyspace, esAstra);
        await store.CrearEsquemaAsync();
        await store.PrepararAsync();
        await store.SembrarEstacionesAsync(log);
        log.LogInformation("Conectado a {Destino}, keyspace {Keyspace}", esAstra ? "Astra DB" : "Cassandra local", keyspace);
        return store;
    }

    /// <summary>Esquema diseñado a partir de las consultas (query-first design).</summary>
    public static readonly string[] Esquema =
    [
        """
        CREATE TABLE IF NOT EXISTS estaciones (
            estacion_id text PRIMARY KEY,
            nombre      text,
            distrito    text,
            altitud     int
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS lecturas_por_estacion_dia (
            estacion_id text,
            dia         date,
            ts          timestamp,
            temperatura double,
            humedad     double,
            PRIMARY KEY ((estacion_id, dia), ts)
        ) WITH CLUSTERING ORDER BY (ts DESC)
        """,
        """
        CREATE TABLE IF NOT EXISTS conteo_lecturas_dia (
            dia         date,
            estacion_id text,
            total       counter,
            PRIMARY KEY ((dia), estacion_id)
        )
        """,
    ];

    private async Task CrearEsquemaAsync()
    {
        foreach (var ddl in Esquema)
            await _session.ExecuteAsync(new SimpleStatement(ddl));
    }

    private async Task PrepararAsync()
    {
        _insertLectura = await _session.PrepareAsync(CqlInsert);
        _contarLectura = await _session.PrepareAsync(CqlContador);
        _lecturasDia = await _session.PrepareAsync(CqlLecturasDia);
        _ultimaLectura = await _session.PrepareAsync(CqlUltima);
        _conteoDia = await _session.PrepareAsync(CqlConteo);
        _insertEstacion = await _session.PrepareAsync(
            "INSERT INTO estaciones (estacion_id, nombre, distrito, altitud) VALUES (?, ?, ?, ?)");
    }

    private async Task SembrarEstacionesAsync(ILogger log)
    {
        var rs = await _session.ExecuteAsync(new SimpleStatement("SELECT estacion_id FROM estaciones LIMIT 1"));
        if (rs.Any()) return;
        foreach (var e in Modelo.EstacionesIniciales)
            await _session.ExecuteAsync(_insertEstacion.Bind(e.EstacionId, e.Nombre, e.Distrito, e.Altitud));
        log.LogInformation("Se registraron {N} estaciones iniciales", Modelo.EstacionesIniciales.Count);
    }

    private static LocalDate ToLocalDate(DateOnly d) => new(d.Year, d.Month, d.Day);

    public async Task<IReadOnlyList<Estacion>> EstacionesAsync()
    {
        var rs = await _session.ExecuteAsync(new SimpleStatement(CqlEstaciones));
        return rs.Select(r => new Estacion(r.GetValue<string>("estacion_id"), r.GetValue<string>("nombre"),
                r.GetValue<string>("distrito"), r.GetValue<int>("altitud")))
            .OrderBy(e => e.Altitud).ThenBy(e => e.EstacionId).ToList();
    }

    public async Task<Lectura> RegistrarAsync(NuevaLectura n, DateTimeOffset ts)
    {
        var dia = ToLocalDate(Modelo.DiaLima(ts));
        // El INSERT y el contador son independientes: se envían en paralelo.
        await Task.WhenAll(
            _session.ExecuteAsync(_insertLectura.Bind(n.EstacionId, dia, ts, n.Temperatura, n.Humedad)),
            _session.ExecuteAsync(_contarLectura.Bind(dia, n.EstacionId)));
        return new Lectura(n.EstacionId, ts, n.Temperatura, n.Humedad);
    }

    public async Task<IReadOnlyList<Lectura>> LecturasDelDiaAsync(string estacionId, DateOnly dia, int limite)
    {
        var rs = await _session.ExecuteAsync(_lecturasDia.Bind(estacionId, ToLocalDate(dia), limite));
        return rs.Select(r => new Lectura(estacionId, r.GetValue<DateTimeOffset>("ts"),
            r.GetValue<double>("temperatura"), r.GetValue<double>("humedad"))).ToList();
    }

    public async Task<Lectura?> UltimaAsync(string estacionId, DateOnly dia)
    {
        var row = (await _session.ExecuteAsync(_ultimaLectura.Bind(estacionId, ToLocalDate(dia)))).FirstOrDefault();
        return row is null ? null : new Lectura(estacionId, row.GetValue<DateTimeOffset>("ts"),
            row.GetValue<double>("temperatura"), row.GetValue<double>("humedad"));
    }

    public async Task<Dictionary<string, long>> ConteoDelDiaAsync(DateOnly dia)
    {
        var rs = await _session.ExecuteAsync(_conteoDia.Bind(ToLocalDate(dia)));
        return rs.ToDictionary(r => r.GetValue<string>("estacion_id"), r => r.GetValue<long>("total"));
    }

    public async Task<(string Version, string Datacenter, double Ms)> InfoAsync()
    {
        var sw = Stopwatch.StartNew();
        var row = (await _session.ExecuteAsync(new SimpleStatement("SELECT release_version, data_center FROM system.local"))).First();
        return (row.GetValue<string>("release_version"), row.GetValue<string>("data_center"), sw.Elapsed.TotalMilliseconds);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.ShutdownAsync();
        await _cluster.ShutdownAsync();
    }
}
