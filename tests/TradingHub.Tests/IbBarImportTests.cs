using System.Globalization;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Importación de velas de 1 minuto bajadas con IB Gateway (un CSV por día de trading).</summary>
public class IbBarImportTests
{
    // día de trading de CME del 30/10/2026: empieza el 29/10 a las 18:00 de Nueva York = 22:00 UTC
    private static readonly long Start = new DateTimeOffset(2026, 10, 29, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private sealed record Bar(long Time, decimal O, decimal H, decimal L, decimal C, decimal V, string Contract);

    private static List<Bar> Bars(long start, int count, decimal basePrice, string contract)
    {
        var list = new List<Bar>();
        for (int i = 0; i < count; i++)
        {
            decimal o = basePrice + (i % 17) * 0.25m;
            decimal c = o + ((i % 3) - 1) * 0.75m;
            list.Add(new Bar(start + i * 60, o, Math.Max(o, c) + 1.25m, Math.Min(o, c) - 0.5m, c, 100 + i % 50, contract));
        }

        return list;
    }

    private static void Write(string folder, string name, IEnumerable<Bar> bars)
    {
        using var w = new StreamWriter(Path.Combine(folder, name));
        w.WriteLine("time,open,high,low,close,volume,contract");
        foreach (var b in bars)
            w.WriteLine(string.Join(',', b.Time.ToString(CultureInfo.InvariantCulture), b.O.ToString(CultureInfo.InvariantCulture), b.H.ToString(CultureInfo.InvariantCulture),
                b.L.ToString(CultureInfo.InvariantCulture), b.C.ToString(CultureInfo.InvariantCulture), b.V.ToString(CultureInfo.InvariantCulture), b.Contract));
    }

    [Theory]
    [InlineData("mnq1m_2026-10-30.csv", "MNQ")]
    [InlineData("MES_2026-10-30.csv", "MES")]
    [InlineData("nq5m_2026-10-30.csv", "NQ")]
    [InlineData("m2k1m_2026-10-30.csv", "M2K")]
    [InlineData("notas.csv", null)]
    public void Deduce_el_futuro_por_el_nombre(string file, string? expected) => Assert.Equal(expected, IbBarImport.SymbolFromFileName(file));

    [Fact]
    public void Guarda_un_futuro_de_CME_con_las_velas_exactas_y_sin_duplicar()
    {
        var dir = Directory.CreateTempSubdirectory("th-ib-");
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(dir.FullName, "out")).FullName;
            var root = Path.Combine(dir.FullName, "Ticks");

            // dos días de trading: cruzan la medianoche UTC, y el segundo rola de contrato a mitad del día
            var day1 = Bars(Start - 86400, 1380, 25000m, "MNQZ6");
            var day2 = Bars(Start, 700, 25100m, "MNQZ6").Concat(Bars(Start + 700 * 60, 680, 25350m, "MNQH7")).ToList();
            Write(src, "mnq1m_2026-10-29.csv", day1);
            Write(src, "mnq1m_2026-10-30.csv", day2);
            File.WriteAllText(Path.Combine(src, "leeme.txt"), "no es un CSV de velas");

            var r = IbBarImport.ImportFolder(src, root);
            Assert.Equal("MNQ", r.Symbol);
            Assert.Equal(2, r.Files);
            Assert.Equal(day1.Count + day2.Count, r.Bars);
            Assert.Equal(0, r.BadLines);
            Assert.Equal(new[] { "MNQH7", "MNQZ6" }, r.Contracts);

            // ficha de CME: tick 0.25 en los archivos
            var days = TickFile.AvailableDays(root, IbBarImport.FeedId, "MNQ").ToList();
            Assert.Equal(r.Days, days.Count);
            Assert.Equal(0.25m, TickFile.ReadTickSize(TickFile.DayPath(root, IbBarImport.FeedId, "MNQ", days[0])));

            // las velas de 1 minuto que arma Trading Hub con esas operaciones son exactamente las del archivo
            var all = day1.Concat(day2).ToList();
            var inst = IbBarImport.InstrumentFor("MNQ");
            Assert.Equal(2m, inst.PointValue);
            var store = new TradeStore(inst);
            var from = DateTimeOffset.FromUnixTimeSeconds(all[0].Time).UtcDateTime;
            var to = DateTimeOffset.FromUnixTimeSeconds(all[^1].Time + 60).UtcDateTime;
            foreach (var t in HistoricalData.Read(root, IbBarImport.FeedId, "MNQ", from, to)) store.Add(t);
            var candles = new CandleSeries(inst, ChartPeriod.M1);
            candles.Rebuild(store);
            Assert.Equal(all.Count, candles.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var c = candles[i];
                var b = all[i];
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(b.Time).UtcDateTime, c.Time);
                Assert.Equal((b.O, b.H, b.L, b.C), (candles.TicksToPrice(c.OpenTicks), candles.TicksToPrice(c.HighTicks), candles.TicksToPrice(c.LowTicks), candles.TicksToPrice(c.CloseTicks)));
                Assert.Equal(b.V, c.Volume);
            }

            // volver a importar (por ejemplo, después de bajar un día nuevo) no duplica nada
            var again = IbBarImport.ImportFolder(src, root);
            Assert.Equal(r.Days, again.Days);
            var store2 = new TradeStore(inst);
            foreach (var t in HistoricalData.Read(root, IbBarImport.FeedId, "MNQ", from, to)) store2.Add(t);
            Assert.Equal(store.Count, store2.Count);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Una_carpeta_sin_archivos_de_velas_da_un_error_claro()
    {
        var dir = Directory.CreateTempSubdirectory("th-ib-");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => IbBarImport.ImportFolder(dir.FullName, Path.Combine(dir.FullName, "Ticks")));
            Assert.Contains("AAAA-MM-DD", ex.Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }
    [Fact]
    public async Task El_grafico_abre_la_historia_importada_y_termina_en_la_ultima_vela()
    {
        var dir = Directory.CreateTempSubdirectory("th-ib-feed-");
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(dir.FullName, "out")).FullName;
            var root = Path.Combine(dir.FullName, "Ticks");
            var bars = Bars(Start, 600, 25000m, "MNQZ6");
            Write(src, "mnq1m_2026-10-30.csv", bars);
            IbBarImport.ImportFolder(src, root);

            using var feed = new TradingHub.Feeds.StoredHistoryFeed(root, IbBarImport.FeedId, "IB Gateway", IbBarImport.InstrumentFor);
            var inst = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
            Assert.Equal("MNQ", inst.Symbol);
            Assert.Equal(IbBarImport.FeedId, inst.FeedId);
            Assert.False(feed.IsLive);

            // la última operación cae dentro del último minuto importado
            var last = await feed.LastTradeTimeAsync(inst, CancellationToken.None);
            var lastBar = DateTimeOffset.FromUnixTimeSeconds(bars[^1].Time).UtcDateTime;
            Assert.NotNull(last);
            Assert.InRange(last!.Value, lastBar, lastBar.AddMinutes(1));

            // como en el gráfico: 3 horas de historia que terminan en la última vela (no «ahora», que no tiene datos)
            var data = new InstrumentData(inst, feed);
            data.Start(DateTime.UtcNow.AddHours(-3));
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                await Task.Delay(20);
            }

            data.Stop();
            Assert.False(data.IsLoadingHistory);
            Assert.InRange(data.Trades.Count, 180 * 4, 181 * 4); // 4 operaciones por vela, unas 3 horas
            Assert.Equal(bars[^1].C, data.LastPrice);
            Assert.True(data.MarketClosed);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
