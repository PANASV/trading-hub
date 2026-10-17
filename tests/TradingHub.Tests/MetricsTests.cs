using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>Las métricas de un resultado cuando la cuenta pasa por cero o queda en negativo.</summary>
public class MetricsTests
{
    private static readonly DateTime T0 = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Una curva de capital con un punto por día a partir del saldo inicial.</summary>
    private static List<EquityPoint> Curve(decimal capital, params decimal[] dailyPnl)
    {
        var list = new List<EquityPoint> { new(T0, capital, 0) };
        decimal equity = capital;
        for (int i = 0; i < dailyPnl.Length; i++)
        {
            equity += dailyPnl[i];
            list.Add(new EquityPoint(T0.AddDays(i + 1), equity, 0));
        }

        return list;
    }

    private static PerformanceMetrics Metrics(List<EquityPoint> curve) => PerformanceMetrics.Compute(new List<ClosedTrade>(), curve, 10_000, true, 0, 0);

    [Fact]
    public void Perdiendo_hasta_quedar_en_negativo_el_Sharpe_es_negativo()
    {
        // pierde todos los días (−500 y −1.500 alternados): la cuenta queda en −20.000
        var m = Metrics(Curve(10_000, Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? -500m : -1_500m).ToArray()));
        Assert.True(m.SharpeRatio < 0, $"Sharpe {m.SharpeRatio}");
        Assert.True(m.SortinoRatio < 0, $"Sortino {m.SortinoRatio}");
    }

    [Fact]
    public void Recuperandose_desde_negativo_el_Sharpe_es_positivo()
    {
        // un día pierde 15.000 (queda en −5.000) y después gana 600 o 1.400 por día hasta terminar en +25.000
        var pnl = new[] { -15_000m }.Concat(Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? 600m : 1_400m)).ToArray();
        var m = Metrics(Curve(10_000, pnl));
        Assert.True(m.SharpeRatio > 0, $"Sharpe {m.SharpeRatio}");
    }

    [Fact]
    public void Por_encima_del_capital_el_retorno_sigue_siendo_sobre_el_saldo()
    {
        // siempre arriba del capital: igual que antes, cada día sobre el saldo del día anterior
        var pnl = Enumerable.Range(0, 20).Select(i => i % 3 == 2 ? -200m : 500m).ToArray();
        var m = Metrics(Curve(10_000, pnl));
        var equity = new List<double> { 10_000 };
        foreach (var p in pnl) equity.Add(equity[^1] + (double)p);
        // el primer día (el del saldo inicial) no tiene resultado
        var r = new List<double> { 0 };
        r.AddRange(equity.Skip(1).Select((e, i) => (e - equity[i]) / equity[i]));
        double mean = r.Average(), sd = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (r.Count - 1));
        Assert.Equal(mean / sd * Math.Sqrt(365), m.SharpeRatio, 6);
    }
}
