using Q = QuanTAlib;

namespace TradingHub.Strategies;

/// <summary>
/// Cualquier indicador de QuanTAlib (de un solo valor) como serie de una estrategia.
/// Se actualiza al cierre de cada vela, igual que los indicadores propios: <c>ind[0]</c> es la última vela cerrada.
/// </summary>
public sealed class QtSeries : Series
{
    private readonly Q.AbstractBase _ind;
    private readonly PriceSource _source;

    internal QtSeries(Q.AbstractBase indicator, PriceSource source)
    {
        _ind = indicator ?? throw new ArgumentNullException(nameof(indicator));
        _source = source;
        Name = indicator.Name;
    }

    /// <summary>El indicador de QuanTAlib, por si hace falta leer otras propiedades.</summary>
    public Q.AbstractBase Indicator => _ind;

    /// <summary>Listo cuando QuanTAlib marca el indicador como "caliente" (IsHot).</summary>
    public override bool IsReady => _ind.IsHot;

    internal override void Update(BarSeries bars)
    {
        var b = bars[0];
        var r = _ind.Update(new Q.TValue(b.Time, (double)Source(b, _source)), true);
        Push(ToDec(r.Value));
    }

    internal static decimal ToDec(double v) => double.IsFinite(v) && Math.Abs(v) < 7.9e27 ? (decimal)v : 0m;
}

/// <summary>
/// Niveles Camarilla de la sesión anterior, con la fórmula de TradingView (H1..H4 = C ± rango × 1.1/12, 1.1/6,
/// 1.1/4, 1.1/2) más H5/H6 y L5/L6. El valor de la serie es H3; los demás niveles están como propiedades.
/// No usa el Pivotcam de QuanTAlib porque sus constantes (1.0833/12 … 1.5/12) no coinciden con TradingView.
/// </summary>
public sealed class CamarillaSeries : Series
{
    private readonly Func<DateTime, DateOnly> _sessionOf;
    private DateOnly? _session;
    private decimal _h, _l, _c;
    private bool _ready;

    internal CamarillaSeries(Func<DateTime, DateOnly> sessionOf)
    {
        _sessionOf = sessionOf;
        Name = "Camarilla";
    }

    /// <summary>Listo cuando ya terminó al menos una sesión completa.</summary>
    public override bool IsReady => _ready;

    public decimal PP { get; private set; }
    public decimal H1 { get; private set; }
    public decimal H2 { get; private set; }
    public decimal H3 { get; private set; }
    public decimal H4 { get; private set; }
    public decimal H5 { get; private set; }
    public decimal H6 { get; private set; }
    public decimal L1 { get; private set; }
    public decimal L2 { get; private set; }
    public decimal L3 { get; private set; }
    public decimal L4 { get; private set; }
    public decimal L5 { get; private set; }
    public decimal L6 { get; private set; }

    internal override void Update(BarSeries bars)
    {
        var b = bars[0];
        var s = _sessionOf(b.Time);
        if (_session != s)
        {
            if (_session != null) SetLevels(_h, _l, _c);
            _session = s;
            _h = b.High;
            _l = b.Low;
        }
        else
        {
            _h = Math.Max(_h, b.High);
            _l = Math.Min(_l, b.Low);
        }

        _c = b.Close;
        Push(_ready ? H3 : 0m);
    }

    /// <summary>Calcula los niveles a partir del máximo, mínimo y cierre de una sesión.</summary>
    internal void SetLevels(decimal h, decimal l, decimal c)
    {
        decimal r = h - l;
        PP = (h + l + c) / 3m;
        H1 = c + r * 1.1m / 12m;
        H2 = c + r * 1.1m / 6m;
        H3 = c + r * 1.1m / 4m;
        H4 = c + r * 1.1m / 2m;
        H5 = l != 0 ? h / l * c : H4;
        H6 = H5 + 1.168m * (H5 - H4);
        L1 = c - r * 1.1m / 12m;
        L2 = c - r * 1.1m / 6m;
        L3 = c - r * 1.1m / 4m;
        L4 = c - r * 1.1m / 2m;
        L5 = c - (H5 - c);
        L6 = c - (H6 - c);
        _ready = true;
    }
}
