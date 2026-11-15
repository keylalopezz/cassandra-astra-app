using PulsoApi;

namespace PulsoApi.Tests;

public class ModeloTests
{
    [Fact]
    public void DiaLima_usa_la_hora_de_Peru_para_la_particion()
    {
        // 03:00 UTC del 16 de noviembre todavía es 15 de noviembre en Lima (UTC-5).
        var ts = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 15), Modelo.DiaLima(ts));
    }

    [Fact]
    public void DiaLima_cambia_de_dia_a_medianoche_de_Lima()
    {
        var ts = new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 16), Modelo.DiaLima(ts));
    }

    [Theory]
    [InlineData("TAC-CERCADO", 24.5, 60, true)]
    [InlineData("NO-EXISTE", 24.5, 60, false)]
    [InlineData("TAC-CERCADO", 80, 60, false)]
    [InlineData("TAC-CERCADO", 24.5, 120, false)]
    public void Validar_rechaza_lecturas_fuera_de_rango(string id, double t, double h, bool valida)
    {
        var ids = Modelo.EstacionesIniciales.Select(e => e.EstacionId);
        Assert.Equal(valida, Modelo.Validar(new NuevaLectura(id, t, h), ids) is null);
    }

    [Fact]
    public void Simular_respeta_la_altitud()
    {
        var rnd = new Random(7);
        var mediodia = new DateTimeOffset(2026, 11, 16, 19, 0, 0, TimeSpan.Zero); // 14:00 en Lima
        var costa = Modelo.Simular(Modelo.EstacionesIniciales.First(e => e.EstacionId == "TAC-CERCADO"), mediodia, rnd);
        var sierra = Modelo.Simular(Modelo.EstacionesIniciales.First(e => e.EstacionId == "TAC-CANDARAVE"), mediodia, rnd);
        Assert.True(costa.Temperatura > sierra.Temperatura, "La costa debe ser más cálida que la sierra");
        Assert.Null(Modelo.Validar(costa, Modelo.EstacionesIniciales.Select(e => e.EstacionId)));
        Assert.Null(Modelo.Validar(sierra, Modelo.EstacionesIniciales.Select(e => e.EstacionId)));
    }

    [Fact]
    public void Esquema_usa_clave_compuesta_y_contador()
    {
        Assert.Contains(CassandraStore.Esquema, s => s.Contains("PRIMARY KEY ((estacion_id, dia), ts)"));
        Assert.Contains(CassandraStore.Esquema, s => s.Contains("total       counter"));
        Assert.Contains("USING TTL 2592000", CassandraStore.CqlInsert);
        Assert.Equal(2592000, Modelo.TtlSegundos);
    }
}
