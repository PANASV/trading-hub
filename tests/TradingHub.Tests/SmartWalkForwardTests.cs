using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>El motor de validación: sesiones, ticks comprimidos, búsqueda bayesiana, meseta, PBO y CPCV.</summary>
public class SmartWalkForwardTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };

    /// <summary>Anota cada sesión nueva que le avisa el motor.</summary>
    private sealed class SessionSpy : Strategy
    {
        public List<DateOnly> Sessions { get; } = new();

        protected override void OnSessionStart(DateOnly session) => Sessions.Add(session);

        protected override void OnBar(Bar bar)
        {
        }
    }

    [Fact]
    public void La_sesion_cambia_en_los_mismos_ticks_tambien_con_cambio_de_horario()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var session = new SessionSettings { TimeZone = tz, Start = TimeSpan.FromHours(18) };
        foreach (var start in new[] { new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc) })
        {
            var list = new List<Trade>();
            for (int i = 0; i < 6 * 24 * 12; i++) list.Add(new Trade(i + 1, start.AddMinutes(5 * i), 100 + i % 9, 1, TradeSide.Buy));
            var data = TickData.FromTrades(Inst, list);

            // lo que tiene que dar: la fecha de sesión de cada tick, como antes (con zona horaria en cada uno)
            var expected = new List<DateOnly>();
            DateOnly last = default;
            foreach (var t in list)
            {
                var d = session.SessionDate(t.Time);
                if (last != default && d != last) expected.Add(d);
                last = d;
            }

            var spy = new SessionSpy();
            var settings = new BacktestSettings
            {
                FeedId = "test", Symbol = "TEST", FromUtc = start, ToUtc = start.AddDays(6), Period = "M15", TimeZoneId = "America/New_York",
                SessionStart = TimeSpan.FromHours(18), CommissionPercent = 0, SlippageTicks = 0,
            };
            var r = new BacktestEngine(spy, data, settings).Run();
            Assert.True(r.Ok, r.Error);
            Assert.Equal(expected, spy.Sessions);
        }
    }

    /// <summary>
    /// Minutos con ráfagas: varias operaciones seguidas al mismo precio y lado en el mismo segundo (como en un futuro de
    /// verdad), una tendencia suave con vaivén y algo de ruido.
    /// </summary>
    private static TickData Bursty(int days, int seed = 3)
    {
        var rnd = new Random(seed);
        var list = new List<Trade>();
        var t0 = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        double price = 1000;
        long id = 1;
        for (int m = 0; m < days * 24 * 60; m++)
        {
            price += 0.05 + 6 * Math.Sin(m / 90.0) * 0.08 + (rnd.NextDouble() - 0.5) * 2;
            decimal p = Math.Round((decimal)price);
            var side = rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell;
            int burst = 1 + rnd.Next(5);
            for (int k = 0; k < burst; k++) list.Add(new Trade(id++, t0.AddMinutes(m).AddMilliseconds(k * 40), p, 1 + rnd.Next(3), side));
            list.Add(new Trade(id++, t0.AddMinutes(m).AddSeconds(20), p + 1, 1, TradeSide.Buy));
        }

        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Days(TickData data) => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = data.FirstTime.Date, ToUtc = data.LastTime.Date.AddDays(1), Period = "M15",
        CommissionPercent = 0, CommissionPerUnit = 0.5m, SlippageTicks = 1, InitialCapital = 100_000,
    };

    /// <summary>Compra cuando rompe el máximo de las últimas N velas y sale a las «Hold» velas (con stop).</summary>
    private sealed class Breakout : Strategy
    {
        [Parameter, Optimize(4, 40, 4)]
        public int Channel { get; set; } = 12;

        [Parameter, Optimize(2, 12, 2)]
        public int Hold { get; set; } = 4;

        private int _entry = -1000;

        protected override void OnBar(Bar bar)
        {
            if (Bars.Count <= Channel) return;
            decimal max = 0;
            for (int i = 1; i <= Channel; i++) max = Math.Max(max, Bars[i].High);
            if (IsFlat && bar.Close > max)
            {
                Buy(1);
                _entry = CurrentBar;
            }
            else if (IsLong && CurrentBar - _entry >= Hold) ClosePosition();
        }
    }

    /// <summary>Lo mismo pero mirando cada operación (no se puede comprimir).</summary>
    private sealed class TickWatcher : Strategy
    {
        protected override void OnTick(Trade trade)
        {
        }
    }

    [Fact]
    public void Juntar_los_ticks_repetidos_no_cambia_las_velas_ni_las_operaciones()
    {
        var data = Bursty(4);
        var packed = data.Compress();
        Assert.True(packed.Compressed && packed.Count < data.Count * 0.6, $"{packed.Count} de {data.Count}");
        var settings = Days(data);
        var a = new BacktestEngine(new Breakout(), data, settings).Run();
        var b = new BacktestEngine(new Breakout(), packed, settings).Run();
        Assert.True(a.Ok && b.Ok, a.Error ?? b.Error);
        Assert.True(a.Trades.Count > 10, $"{a.Trades.Count} operaciones");
        Assert.Equal(a.Trades.Count, b.Trades.Count);
        for (int i = 0; i < a.Trades.Count; i++)
        {
            Assert.Equal(a.Trades[i].EntryPrice, b.Trades[i].EntryPrice);
            Assert.Equal(a.Trades[i].ExitPrice, b.Trades[i].ExitPrice);
            Assert.Equal(a.Trades[i].EntryTime.Ticks / TimeSpan.TicksPerSecond, b.Trades[i].EntryTime.Ticks / TimeSpan.TicksPerSecond);
        }

        Assert.Equal(a.Metrics.NetProfit, b.Metrics.NetProfit);
        Assert.True(Optimizer.CanCompress(typeof(Breakout), "M15"));
        Assert.False(Optimizer.CanCompress(typeof(Breakout), "Tick500"));
        Assert.False(Optimizer.CanCompress(typeof(TickWatcher), "M15"));
    }

    [Fact]
    public void La_bayesiana_encuentra_lo_bueno_con_muchas_menos_pruebas_que_la_grilla()
    {
        var space = new ParameterSpace(30, 30);
        double F(int[] x) => 100 * Math.Exp(-(Math.Pow(x[0] - 21, 2) + Math.Pow(x[1] - 8, 2)) / 40.0);
        int calls = 0;
        var found = TpeSearch.Run(space, pts =>
        {
            calls += pts.Count;
            return pts.Select(F).ToArray();
        }, budget: 80, seed: 5, batch: 8);
        Assert.True(calls <= 80, $"{calls} pruebas");
        double best = found.Max(f => f.Score);
        Assert.True(best >= 90, $"mejor {best:0.0} de 100 con {calls} pruebas de 900");
    }

    [Fact]
    public void La_meseta_le_gana_al_pico_aislado()
    {
        var space = new ParameterSpace(9, 9);
        var rows = space.All().Select(x =>
        {
            double f = -5;
            if (x[0] == 1 && x[1] == 1) f = 10; // pico solo: las vecinas pierden
            if (Math.Abs(x[0] - 6) <= 1 && Math.Abs(x[1] - 6) <= 1) f = 6; // zona entera que gana
            return (x, f);
        }).ToList();
        int pick = RobustSelection.Select(rows);
        Assert.Equal(new[] { 6, 6 }, rows[pick].x);
    }

    [Fact]
    public void PBO_con_puro_ruido_es_alto_y_con_ventaja_real_es_bajo()
    {
        var rnd = new Random(11);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Sin(2 * Math.PI * rnd.NextDouble());
        // con puro ruido la PBO vale 0,5 en promedio (en una sola historia puede caer lejos): se promedian varias
        var noise = Enumerable.Range(0, 10).Select(_ => Overfitting.Pbo(Enumerable.Range(0, 24).Select(_ => Enumerable.Range(0, 500).Select(_ => Gauss() * 100).ToArray()).ToList())!.Value).ToList();
        var edge = Enumerable.Range(0, 24).Select(i => Enumerable.Range(0, 500).Select(_ => 6.0 * i + Gauss() * 100).ToArray()).ToList();
        var e = Overfitting.Pbo(edge)!.Value;
        Assert.All(noise, n => Assert.Equal(252, n.Splits));
        double pbo = noise.Average(n => n.Pbo), loss = noise.Average(n => n.Loss);
        Assert.InRange(pbo, 0.3, 0.7);
        Assert.True(e.Pbo < 0.15 && e.Loss < 0.05, $"con ventaja: PBO {e.Pbo:0.00}, pérdida {e.Loss:0.00}");
        Assert.True(loss > e.Loss + 0.2, $"ruido {loss:0.00} vs ventaja {e.Loss:0.00}");
    }

    [Fact]
    public void La_CPCV_arma_varios_caminos_completos_fuera_de_muestra()
    {
        var rnd = new Random(2);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Sin(2 * Math.PI * rnd.NextDouble());
        var space = new ParameterSpace(6);
        var index = space.All().ToList();
        var daily = index.Select(x => Enumerable.Range(0, 400).Select(_ => 8.0 * x[0] + Gauss() * 60).ToArray()).ToList();
        var perDay = index.Select(_ => Enumerable.Repeat(1, 400).ToArray()).ToList();
        var paths = Overfitting.Cpcv(index, daily, perDay, 5, 100_000, false, groups: 8, embargo: 1);
        Assert.Equal(7, paths.Count);
        Assert.All(paths, p => Assert.Equal(400, p.Days));
        Assert.True(paths.Count(p => p.Net > 0) >= 6, string.Join(", ", paths.Select(p => p.Net)));
    }

    [Fact]
    public void El_walk_forward_corre_cada_combinacion_una_sola_vez_y_mide_el_sobreajuste()
    {
        var data = Bursty(45, 7);
        var settings = Days(data);
        var ranges = new List<ParameterRange>
        {
            new() { Name = nameof(Breakout.Channel), Min = 4, Max = 20, Step = 4, IsInteger = true },
            new() { Name = nameof(Breakout.Hold), Min = 2, Max = 6, Step = 2, IsInteger = true },
        };
        var wf = Optimizer.WalkForward(() => new Breakout(), new(), ranges, data, settings, FitnessMetric.NetProfit, 12, 6, false, 3, null, CancellationToken.None);
        Assert.Equal("grilla", wf.Search);
        Assert.Equal(15, wf.SpaceSize);
        Assert.Equal(15, wf.Evaluated); // 15 combinaciones, cada una una vez (no una por ventana)
        Assert.True(wf.Compression < 0.7, $"compresión {wf.Compression}");
        Assert.True(wf.Windows.Count >= 4);
        // cada operación fuera de muestra entró dentro de una ventana fuera de muestra
        Assert.All(wf.OutSampleTrades, t => Assert.Contains(wf.Windows, w => t.EntryTime >= w.OutSampleFrom && t.EntryTime < w.OutSampleTo));
        Assert.NotNull(wf.Pbo);
        Assert.True(wf.PboSplits > 0 && wf.CpcvPaths.Count >= 3, $"PBO {wf.PboSplits} particiones, {wf.CpcvPaths.Count} caminos");

        // con muchas combinaciones busca con la bayesiana y corre bastante menos que la grilla
        var big = new List<ParameterRange>
        {
            new() { Name = nameof(Breakout.Channel), Min = 2, Max = 40, Step = 1, IsInteger = true },
            new() { Name = nameof(Breakout.Hold), Min = 1, Max = 20, Step = 1, IsInteger = true },
        };
        var tpe = Optimizer.WalkForward(() => new Breakout(), new(), big, data, settings, FitnessMetric.NetProfit, 12, 6, false, 3, null, CancellationToken.None,
            budget: 16);
        Assert.Equal("bayesiana", tpe.Search);
        Assert.True(tpe.SpaceSize == 780 && tpe.Evaluated < 780 / 3, $"corrió {tpe.Evaluated} de {tpe.SpaceSize}");
        Assert.True(tpe.Windows.Count >= 4);
    }
}
