using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native.QuanTAlibBridge;
using TradingHub.Market;
using TradingHub.Scripting;
using TradingHub.Strategies;
using Q = QuanTAlib;

namespace TradingHub.Tests;

/// <summary>Integración con QuanTAlib: indicadores del gráfico, series de estrategias y alias en los scripts.</summary>
public class QuanTAlibTests
{
    private static readonly Instrument Inst = new() { Symbol = "MNQTEST", FeedId = "test", TickSize = 0.25m, VolumeStep = 1m, PointValue = 2m };
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Precio de la operación k (4 operaciones por minuto), con ondas para que las medias se separen.</summary>
    private static decimal Price(int k) => 20000m + Math.Round((decimal)(40 * Math.Sin(k / 23.0) + 15 * Math.Cos(k / 7.0)) * 4) / 4;

    private static Trade TradeAt(int k) => new(k + 1, T0.AddSeconds(k * 15), Price(k), 1, k % 2 == 0 ? TradeSide.Buy : TradeSide.Sell);

    private static decimal[] Series(IndicatorSlot slot, int index)
    {
        var s = (ValueDataSeries)slot.Indicator.DataSeries[index];
        return Enumerable.Range(0, slot.Indicator.CurrentBar).Select(i => s[i]).ToArray();
    }

    private static double[] Closes(int trades)
    {
        // cierre de cada vela de 1 minuto = última operación de ese minuto
        return Enumerable.Range(0, trades).GroupBy(k => k / 4).Select(g => (double)Price(g.Last())).ToArray();
    }

    [Fact]
    public void La_media_del_grafico_coincide_con_QuanTAlib_en_lote()
    {
        const int n = 4 * 300;
        var data = new InstrumentData(Inst, new SimulatedFeed());
        for (int k = 0; k < n; k++) data.Trades.Add(TradeAt(k));
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var slot = model.AddIndicator(new QtMovingAverage { Type = QtMaType.Jma, Period = 20, ShowWarmup = true });
        for (int i = 0; i < 3; i++) model.Update();

        var closes = Closes(n);
        var expected = new double[closes.Length];
        var jma = new Q.Jma(20);
        for (int i = 0; i < closes.Length; i++) expected[i] = jma.Update(new Q.TValue(T0.AddMinutes(i), closes[i])).Value;

        var got = Series(slot, 0);
        Assert.Equal(closes.Length, got.Length);
        for (int i = 0; i < got.Length; i++) Assert.True(Math.Abs(expected[i] - (double)got[i]) < 1e-6, $"vela {i}: {expected[i]} vs {got[i]}");
    }

    [Fact]
    public void En_vivo_los_ticks_de_la_ultima_vela_no_desvian_la_media()
    {
        // en vivo: llegan las operaciones de a una y el host recalcula la última vela en cada tick (isNew = false)
        const int n = 4 * 260;
        var live = new InstrumentData(Inst, new SimulatedFeed());
        for (int k = 0; k < 40; k++) live.Trades.Add(TradeAt(k));
        var liveModel = new ChartModel(live, ChartPeriod.M1, new ChartSettings());
        var liveSlot = liveModel.AddIndicator(new QtMaRibbon());
        liveModel.Update();
        var appended = (Action<int, int>?)typeof(InstrumentData)
            .GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(live);
        for (int k = 40; k < n; k++)
        {
            int start = live.Trades.Count;
            live.Trades.Add(TradeAt(k));
            appended?.Invoke(start, 1); // como si llegara del mercado: el host recalcula desde la vela que cambió
            liveModel.Update();
        }

        for (int i = 0; i < 3; i++) liveModel.Update();

        // lo mismo calculado de una sola vez
        var full = new InstrumentData(Inst, new SimulatedFeed());
        for (int k = 0; k < n; k++) full.Trades.Add(TradeAt(k));
        var fullModel = new ChartModel(full, ChartPeriod.M1, new ChartSettings());
        var fullSlot = fullModel.AddIndicator(new QtMaRibbon());
        for (int i = 0; i < 3; i++) fullModel.Update();

        Assert.Equal(fullSlot.Indicator.CurrentBar, liveSlot.Indicator.CurrentBar);
        for (int s = 0; s < 3; s++) Assert.Equal(Series(fullSlot, s), Series(liveSlot, s));

        // y la WMA 200 coincide con QuanTAlib vela por vela
        var closes = Closes(n);
        var wma = new Q.Wma(200);
        var slow = Series(liveSlot, 2);
        for (int i = 0; i < closes.Length; i++)
        {
            var r = wma.Update(new Q.TValue(T0.AddMinutes(i), closes[i]));
            if (wma.IsHot) Assert.True(Math.Abs(r.Value - (double)slow[i]) < 1e-6, $"vela {i}");
            else Assert.Equal(0m, slow[i]); // durante el calentamiento no se dibuja
        }
    }

    [Fact]
    public void Camarilla_usa_la_formula_de_TradingView()
    {
        // H = 20100, L = 19900, C = 20050 → rango 200
        var lv = CamarillaLevels.From(20100m, 19900m, 20050m);
        Assert.Equal(20050m + 200m * 1.1m / 12m, lv.H1);
        Assert.Equal(20105m, lv.H3);   // C + 200 × 1.1/4
        Assert.Equal(20160m, lv.H4);   // C + 200 × 1.1/2
        Assert.Equal(19995m, lv.L3);
        Assert.Equal(19940m, lv.L4);
        Assert.Equal(20100m / 19900m * 20050m, lv.H5);
        Assert.Equal(lv.H5 + 1.168m * (lv.H5 - lv.H4), lv.H6);
        Assert.Equal(20050m - (lv.H5 - 20050m), lv.L5);
    }

    [Fact]
    public void Camarilla_del_grafico_toma_la_sesion_anterior()
    {
        // dos días: el primero define los niveles que se dibujan en el segundo
        var data = new InstrumentData(Inst, new SimulatedFeed());
        long id = 1;
        decimal h1 = 0, l1 = decimal.MaxValue, c1 = 0;
        for (int m = 0; m < 2 * 1440; m += 5)
        {
            var t = T0.AddMinutes(m);
            decimal p = 20000m + Math.Round((decimal)(60 * Math.Sin(m / 97.0)) * 4) / 4;
            data.Trades.Add(new Trade(id++, t, p, 1, TradeSide.Buy));
            if (t.Date == T0.Date)
            {
                h1 = Math.Max(h1, p);
                l1 = Math.Min(l1, p);
                c1 = p;
            }
        }

        var model = new ChartModel(data, ChartPeriod.M5, new ChartSettings());
        var slot = model.AddIndicator(new QtCamarilla { SessionMode = CamarillaSession.Chart }); // sesión del gráfico: 00:00 UTC
        for (int i = 0; i < 3; i++) model.Update();

        var ind = slot.Indicator;
        var h3 = (ValueDataSeries)ind.DataSeries[0];
        var expected = CamarillaLevels.From(h1, l1, c1);
        int firstDay2 = 1440 / 5;
        Assert.Equal(0m, h3[firstDay2 - 1]);          // el primer día no tiene sesión anterior
        Assert.Equal(expected.H3, h3[firstDay2]);
        Assert.Equal(expected.H3, h3[ind.CurrentBar - 1]);
    }

    [Fact]
    public void Camarilla_por_defecto_cambia_de_dia_a_las_18_de_Nueva_York()
    {
        // octubre = horario de verano: 18:00 EDT = 22:00 UTC
        var cut = new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
        var data = new InstrumentData(Inst, new SimulatedFeed());
        long id = 1;
        decimal h = 0, l = decimal.MaxValue, c = 0;
        for (int m = 0; m < 2 * 1440; m += 5)
        {
            var t = T0.AddMinutes(m);
            decimal p = 20000m + Math.Round((decimal)(80 * Math.Sin(m / 61.0)) * 4) / 4;
            data.Trades.Add(new Trade(id++, t, p, 1, TradeSide.Buy));
            if (t < cut)
            {
                h = Math.Max(h, p);
                l = Math.Min(l, p);
                c = p;
            }
        }

        var model = new ChartModel(data, ChartPeriod.M5, new ChartSettings());
        var slot = model.AddIndicator(new QtCamarilla());
        for (int i = 0; i < 3; i++) model.Update();

        var h4 = (ValueDataSeries)slot.Indicator.DataSeries.First(x => x.Name == "H4");
        int firstAfterCut = (int)(cut - T0).TotalMinutes / 5;
        Assert.Equal(0m, h4[firstAfterCut - 1]);                             // antes de las 18:00 no hay día anterior
        Assert.Equal(CamarillaLevels.From(h, l, c).H4, h4[firstAfterCut]);   // a las 18:00 usa el día que terminó
        Assert.Equal(CamarillaLevels.From(h, l, c).H4, h4[firstAfterCut + 100]);
    }

    private sealed class ConQuanTAlib : Strategy
    {
        public QtSeries Jma = null!;
        public CamarillaSeries Cam = null!;

        protected override void OnInit()
        {
            Jma = Qt(new Q.Jma(10));
            Cam = Camarilla("America/New_York", 18);
        }

        protected override void OnBar(Bar bar)
        {
        }
    }

    [Fact]
    public void Las_estrategias_usan_indicadores_de_QuanTAlib()
    {
        var list = new List<Trade>();
        for (int k = 0; k < 4 * 1440 * 2; k += 4) list.Add(new Trade(k + 1, T0.AddSeconds(k * 15), Price(k), 1, TradeSide.Buy));
        var s = new ConQuanTAlib();
        var r = new BacktestEngine(s, TickData.FromTrades(Inst, list), new BacktestSettings
        {
            FeedId = "test",
            Symbol = "MNQTEST",
            FromUtc = T0,
            ToUtc = T0.AddDays(2),
            Period = "M1",
            InitialCapital = 1000,
        }).Run();

        Assert.Null(r.Error);
        Assert.True(s.Jma.IsReady);
        Assert.True(s.Cam.IsReady);
        Assert.True(s.Cam.H4 > s.Cam.H3 && s.Cam.H3 > s.Cam.L3 && s.Cam.L3 > s.Cam.L4);
        Assert.InRange(s.Jma.Value, 19900m, 20100m);
    }

    [Fact]
    public void Los_scripts_pueden_usar_Q_como_alias_de_QuanTAlib()
    {
        // fuerza que QuanTAlib esté cargado, como en la app
        _ = typeof(Q.Ema).Assembly;
        var dir = Directory.CreateTempSubdirectory("th-qt-");
        try
        {
            var file = Path.Combine(dir.FullName, "MiEstrategia.cs");
            File.WriteAllText(file, """
                using TradingHub.Strategies;
                public class MiEstrategia : Strategy
                {
                    private QtSeries _kama = null!;
                    protected override void OnInit() => _kama = Qt(new Q.Kama(10), PriceSource.Typical);
                    protected override void OnBar(Bar bar) { if (_kama.IsReady && bar.Close > _kama[0]) { } }
                }
                """);
            var res = new ScriptCompiler().Compile(new[] { file }, "QtAliasTest");
            Assert.True(res.Success, string.Join("\n", res.Diagnostics));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

/// <summary>El gráfico por defecto: velas como TradingView (no footprint) con sus colores.</summary>
public class DefaultChartTests
{
    [Fact]
    public void Por_defecto_son_velas_con_los_colores_de_TradingView()
    {
        Assert.Equal(TradingHub.Charting.ChartStyle.Candles, new TradingHub.Charting.ChartSettings().Style);
        Assert.Equal(new SkiaSharp.SKColor(8, 153, 129), TradingHub.Charting.ChartTheme.Base.UpCandle);
        Assert.Equal(new SkiaSharp.SKColor(242, 54, 69), TradingHub.Charting.ChartTheme.Base.DownCandle);
        var ribbon = new TradingHub.Indicators.Native.QuanTAlibBridge.QtMaRibbon();
        Assert.Equal((QtMaType.Ema, 9, QtMaType.Ema, 60, QtMaType.Wma, 200), (ribbon.FastType, ribbon.FastPeriod, ribbon.MidType, ribbon.MidPeriod, ribbon.SlowType, ribbon.SlowPeriod));
    }
}
