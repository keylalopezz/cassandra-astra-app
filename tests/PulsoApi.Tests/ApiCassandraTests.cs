using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PulsoApi;

namespace PulsoApi.Tests;

/// <summary>
/// Pruebas de integración contra un Cassandra real. En GitHub Actions se levanta
/// un contenedor cassandra:5.0 y se define CASSANDRA_CONTACT_POINTS.
/// Sin esa variable, las pruebas se omiten.
/// </summary>
public class ApiCassandraTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly bool HayCassandra = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASSANDRA_CONTACT_POINTS"));
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ApiCassandraTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<HttpClient> ClienteListoAsync()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 60; i++)
        {
            var r = await client.GetAsync("/health");
            if (r.StatusCode == HttpStatusCode.OK) return client;
            await Task.Delay(2000);
        }
        throw new TimeoutException("Cassandra no respondió a tiempo");
    }

    [Fact]
    public async Task Info_reporta_el_motor_Cassandra()
    {
        if (!HayCassandra) return;
        var client = await ClienteListoAsync();
        var info = await client.GetFromJsonAsync<JsonElement>("/api/info", Json);
        Assert.Contains("Cassandra", info.GetProperty("motor").GetString());
        Assert.Equal("pulso", info.GetProperty("keyspace").GetString());
    }

    [Fact]
    public async Task Se_siembran_las_cinco_estaciones()
    {
        if (!HayCassandra) return;
        var client = await ClienteListoAsync();
        var r = await client.GetFromJsonAsync<JsonElement>("/api/estaciones", Json);
        Assert.Equal(Modelo.EstacionesIniciales.Count, r.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Registrar_lectura_la_devuelve_primero_y_suma_el_contador()
    {
        if (!HayCassandra) return;
        var client = await ClienteListoAsync();
        var antes = await LecturasHoyAsync(client, "TAC-POCOLLAY");

        var post = await client.PostAsJsonAsync("/api/lecturas", new NuevaLectura("TAC-POCOLLAY", 21.7, 55.0));
        post.EnsureSuccessStatusCode();

        var serie = await client.GetFromJsonAsync<JsonElement>("/api/estaciones/TAC-POCOLLAY/lecturas?horas=1", Json);
        var ultima = serie.GetProperty("data").EnumerateArray().Last();
        Assert.Equal(21.7, ultima.GetProperty("temperatura").GetDouble());
        Assert.Equal(antes + 1, await LecturasHoyAsync(client, "TAC-POCOLLAY"));
    }

    [Fact]
    public async Task Lectura_invalida_devuelve_400()
    {
        if (!HayCassandra) return;
        var client = await ClienteListoAsync();
        var post = await client.PostAsJsonAsync("/api/lecturas", new NuevaLectura("TAC-POCOLLAY", 99, 55));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Simular_escribe_una_lectura_por_estacion_y_ronda()
    {
        if (!HayCassandra) return;
        var client = await ClienteListoAsync();
        var r = await client.PostAsync("/api/simular?rondas=2", null);
        var json = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(2 * Modelo.EstacionesIniciales.Count, json.GetProperty("data").GetInt32());
    }

    [Fact]
    public async Task Historial_reparte_las_lecturas_en_24_horas()
    {
        if (!HayCassandra) return;
        var client = await ClienteListoAsync();
        var r = await client.PostAsync("/api/simular?rondas=48&intervalo=30", null);
        r.EnsureSuccessStatusCode();
        var serie = await client.GetFromJsonAsync<JsonElement>("/api/estaciones/TAC-CANDARAVE/lecturas?horas=24", Json);
        var horas = serie.GetProperty("data").EnumerateArray().Select(l => l.GetProperty("ts").GetDateTimeOffset()).ToList();
        Assert.True((horas.Max() - horas.Min()).TotalHours >= 23, "El historial debe cubrir al menos 23 horas");
    }

    private static async Task<long> LecturasHoyAsync(HttpClient client, string id)
    {
        var r = await client.GetFromJsonAsync<JsonElement>("/api/estaciones", Json);
        return r.GetProperty("data").EnumerateArray()
            .First(e => e.GetProperty("estacion").GetProperty("estacionId").GetString() == id)
            .GetProperty("lecturasHoy").GetInt64();
    }
}
