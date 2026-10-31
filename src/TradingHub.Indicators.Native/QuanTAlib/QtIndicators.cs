using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using Q = QuanTAlib;

namespace TradingHub.Indicators.Native.QuanTAlibBridge;

/// <summary>Categoría de la biblioteca para los indicadores basados en QuanTAlib.</summary>
public static class QtCategories
{
    public const string QuanTAlib = "QuanTAlib";
}

/// <summary>
/// Base para indicadores que usan el modo streaming de QuanTAlib.
/// Trading Hub llama a OnCalculate varias veces sobre la última vela (cada tick) y a veces vuelve atrás algunas
/// velas; QuanTAlib lo resuelve con <c>isNew</c>: true para una vela nueva, false para corregir la actual.
/// Si el host salta o retrocede más de una vela, se reinicia el estado y se reproduce la historia.
/// </summary>
public abstract class QtIndicator : Indicator
{
    private int _last = -1;

    protected QtIndicator(bool useCandles = false) : base(useCandles)
    {
    }

    /// <summary>Vuelve el estado interno a cero (indicadores de QuanTAlib y acumuladores propios).</summary>
    protected abstract void ResetState();

    /// <summary>Procesa una vela. <paramref name="isNew"/> = false significa "la misma vela con datos nuevos".</summary>
    protected abstract void Step(int bar, decimal value, bool isNew);

    protected sealed override void OnRecalculate()
    {
        _last = -1;
        ResetState();
    }

    protected sealed override void OnCalculate(int bar, decimal value)
    {
        if (bar > _last + 1 || bar < _last)
        {
            ResetState();
            for (int b = 0; b < bar; b++) Step(b, SourceAt(b), true);
            _last = bar - 1;
        }

        Step(bar, value, bar == _last + 1);
        _last = bar;
    }

    /// <summary>Hora de apertura de la vela (para el TValue de QuanTAlib).</summary>
    protected DateTime TimeOf(int bar) => GetCandle(bar).Time;

    private decimal SourceAt(int bar) => SourceDataSeries[bar] is decimal d ? d : Convert.ToDecimal(SourceDataSeries[bar]);

    /// <summary>Convierte el double de QuanTAlib a decimal; 0 (no se dibuja) si no es un número válido.</summary>
    protected static decimal ToDec(double v) => double.IsFinite(v) && Math.Abs(v) < 7.9e27 ? (decimal)v : 0m;
}

/// <summary>Tipos de media disponibles desde QuanTAlib.</summary>
public enum QtMaType
{
    [Display(Name = "SMA")] Sma,
    [Display(Name = "EMA")] Ema,
    [Display(Name = "WMA")] Wma,
    [Display(Name = "HMA (Hull)")] Hma,
    [Display(Name = "ALMA (Arnaud Legoux)")] Alma,
    [Display(Name = "JMA (Jurik)")] Jma,
    [Display(Name = "KAMA (Kaufman)")] Kama,
    [Display(Name = "T3 (Tillson)")] T3,
    [Display(Name = "ZLEMA (lag cero)")] Zlema,
    [Display(Name = "DEMA")] Dema,
    [Display(Name = "TEMA")] Tema,
    [Display(Name = "VIDYA")] Vidya,
    [Display(Name = "SuperSmoother (Ehlers)")] SuperSmoother,
}

public static class QtFactory
{
    public static Q.AbstractBase Create(QtMaType type, int period) => type switch
    {
        QtMaType.Sma => new Q.Sma(period),
        QtMaType.Wma => new Q.Wma(period),
        QtMaType.Hma => new Q.Hma(Math.Max(2, period)),
        QtMaType.Alma => new Q.Alma(period),
        QtMaType.Jma => new Q.Jma(period),
        QtMaType.Kama => new Q.Kama(period),
        QtMaType.T3 => new Q.T3(period),
        QtMaType.Zlema => new Q.Zlema(period),
        QtMaType.Dema => new Q.Dema(period),
        QtMaType.Tema => new Q.Tema(period),
        QtMaType.Vidya => new Q.Vidya(period),
        QtMaType.SuperSmoother => new Q.Ssf2(Math.Max(2, period)),
        _ => new Q.Ema(period),
    };
}

/// <summary>Una media móvil de QuanTAlib (13 tipos).</summary>
[DisplayName("QuanTAlib · Media móvil")]
[Category(QtCategories.QuanTAlib)]
[Description("Media móvil calculada con QuanTAlib: SMA, EMA, WMA, Hull, ALMA, Jurik, KAMA, T3, ZLEMA, DEMA, TEMA, VIDYA o SuperSmoother de Ehlers.")]
public class QtMovingAverage : QtIndicator
{
    private readonly ValueDataSeries _ma = new("MA", "Media") { Color = Color.FromArgb(255, 0, 200, 255), Width = 2 };
    private QtMaType _type = QtMaType.Jma;
    private int _period = 20;
    private bool _showWarmup;
    private Q.AbstractBase _ind;

    public QtMovingAverage()
    {
        DataSeries[0] = _ma;
        _ind = QtFactory.Create(_type, _period);
    }

    [Display(Name = "Tipo", GroupName = "Cálculo", Order = 10)]
    public QtMaType Type { get => _type; set { _type = value; RecalculateValues(); } }

    [Display(Name = "Período", GroupName = "Cálculo", Order = 20)]
    [Range(1, 10000)]
    public int Period { get => _period; set { _period = Math.Max(1, value); RecalculateValues(); } }

    [Display(Name = "Fuente", GroupName = "Cálculo", Order = 30)]
    public DataSeriesType Source { get => SourceType; set { SourceType = value; RecalculateValues(); } }

    [Display(Name = "Dibujar durante el calentamiento", GroupName = "Cálculo", Order = 40)]
    public bool ShowWarmup { get => _showWarmup; set { _showWarmup = value; RecalculateValues(); } }

    protected override void ResetState() => _ind = QtFactory.Create(_type, _period);

    protected override void Step(int bar, decimal value, bool isNew)
    {
        var r = _ind.Update(new Q.TValue(TimeOf(bar), (double)value), isNew);
        // como TradingView: la media se dibuja desde que hay tantas velas como su período
        _ma[bar] = _showWarmup || _ind.IsHot || bar + 1 >= _period ? ToDec(r.Value) : 0m;
    }
}

/// <summary>Tres medias en un solo indicador. Por defecto EMA 9 / EMA 60 / WMA 200.</summary>
[DisplayName("QuanTAlib · Medias 9/60/200")]
[Category(QtCategories.QuanTAlib)]
[Description("Tres medias de QuanTAlib en un indicador. Por defecto EMA 9 (amarilla), EMA 60 (violeta) y WMA 200 (azul); cada una con su tipo y período.")]
public class QtMaRibbon : QtIndicator
{
    private readonly ValueDataSeries _fast = new("Fast", "Rápida") { Color = Color.FromArgb(255, 255, 214, 0), Width = 1 };
    private readonly ValueDataSeries _mid = new("Mid", "Media") { Color = Color.FromArgb(255, 170, 90, 255), Width = 2 };
    private readonly ValueDataSeries _slow = new("Slow", "Lenta") { Color = Color.FromArgb(255, 40, 120, 255), Width = 2 };
    private QtMaType _fastType = QtMaType.Ema, _midType = QtMaType.Ema, _slowType = QtMaType.Wma;
    private int _fastPeriod = 9, _midPeriod = 60, _slowPeriod = 200;
    private bool _showWarmup;
    private Q.AbstractBase _f, _m, _s;

    public QtMaRibbon()
    {
        DataSeries[0] = _fast;
        DataSeries.Add(_mid);
        DataSeries.Add(_slow);
        (_f, _m, _s) = Build();
    }

    [Display(Name = "Rápida: tipo", GroupName = "Rápida", Order = 10)]
    public QtMaType FastType { get => _fastType; set { _fastType = value; RecalculateValues(); } }

    [Display(Name = "Rápida: período", GroupName = "Rápida", Order = 11)]
    [Range(1, 10000)]
    public int FastPeriod { get => _fastPeriod; set { _fastPeriod = Math.Max(1, value); RecalculateValues(); } }

    [Display(Name = "Media: tipo", GroupName = "Media", Order = 20)]
    public QtMaType MidType { get => _midType; set { _midType = value; RecalculateValues(); } }

    [Display(Name = "Media: período", GroupName = "Media", Order = 21)]
    [Range(1, 10000)]
    public int MidPeriod { get => _midPeriod; set { _midPeriod = Math.Max(1, value); RecalculateValues(); } }

    [Display(Name = "Lenta: tipo", GroupName = "Lenta", Order = 30)]
    public QtMaType SlowType { get => _slowType; set { _slowType = value; RecalculateValues(); } }

    [Display(Name = "Lenta: período", GroupName = "Lenta", Order = 31)]
    [Range(1, 10000)]
    public int SlowPeriod { get => _slowPeriod; set { _slowPeriod = Math.Max(1, value); RecalculateValues(); } }

    [Display(Name = "Fuente", GroupName = "Cálculo", Order = 40)]
    public DataSeriesType Source { get => SourceType; set { SourceType = value; RecalculateValues(); } }

    [Display(Name = "Dibujar durante el calentamiento", GroupName = "Cálculo", Order = 41)]
    public bool ShowWarmup { get => _showWarmup; set { _showWarmup = value; RecalculateValues(); } }

    private (Q.AbstractBase, Q.AbstractBase, Q.AbstractBase) Build() =>
        (QtFactory.Create(_fastType, _fastPeriod), QtFactory.Create(_midType, _midPeriod), QtFactory.Create(_slowType, _slowPeriod));

    protected override void ResetState() => (_f, _m, _s) = Build();

    protected override void Step(int bar, decimal value, bool isNew)
    {
        var tv = new Q.TValue(TimeOf(bar), (double)value);
        Put(_fast, _f, bar, tv, isNew, _fastPeriod);
        Put(_mid, _m, bar, tv, isNew, _midPeriod);
        Put(_slow, _s, bar, tv, isNew, _slowPeriod);
    }

    private void Put(ValueDataSeries s, Q.AbstractBase ind, int bar, Q.TValue tv, bool isNew, int period)
    {
        var r = ind.Update(tv, isNew);
        // como TradingView: cada media se dibuja desde que hay tantas velas como su período
        s[bar] = _showWarmup || ind.IsHot || bar + 1 >= period ? ToDec(r.Value) : 0m;
    }
}

/// <summary>
/// Niveles Camarilla de la sesión anterior con la fórmula estándar de TradingView
/// (H1..H4 = C ± rango × 1.1/12, 1.1/6, 1.1/4, 1.1/2) más H5/H6 y L5/L6.
/// QuanTAlib trae su propio Pivotcam con otras constantes (1.0833/12 … 1.5/12) que no coinciden con TradingView,
/// por eso los niveles se calculan acá.
/// </summary>
/// <summary>Qué define el "día" para la Camarilla.</summary>
public enum CamarillaSession
{
    [Display(Name = "CME (18:00 Nueva York)")] Cme,
    [Display(Name = "Sesión del gráfico")] Chart,
    [Display(Name = "Personalizada")] Custom,
}

[DisplayName("QuanTAlib · Camarilla (sesión)")]
[Category(QtCategories.QuanTAlib)]
[Description("Camarilla de la sesión anterior con la fórmula de TradingView: H1–H6 y L1–L6 (H2/L2 y PP ocultos por defecto). Por defecto el día empieza a las 18:00 de Nueva York (CME), como TradingView en futuros.")]
public class QtCamarilla : QtIndicator
{
    private static readonly Color Res = Color.FromArgb(220, 240, 82, 90);
    private static readonly Color Sup = Color.FromArgb(220, 38, 194, 129);
    private static readonly Color Ext = Color.FromArgb(220, 255, 170, 0);

    private readonly ValueDataSeries _pp = new("PP", "PP") { Color = Color.FromArgb(200, 200, 200, 200), LineDashStyle = LineDashStyle.Dot, Visible = false };
    private readonly ValueDataSeries _h1 = new("H1", "H1") { Color = Res, LineDashStyle = LineDashStyle.Dot };
    private readonly ValueDataSeries _h2 = new("H2", "H2") { Color = Res, LineDashStyle = LineDashStyle.Dot, Visible = false };
    private readonly ValueDataSeries _h3 = new("H3", "H3") { Color = Res, Width = 2 };
    private readonly ValueDataSeries _h4 = new("H4", "H4") { Color = Res, Width = 2, LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _h5 = new("H5", "H5") { Color = Ext, LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _h6 = new("H6", "H6") { Color = Ext, LineDashStyle = LineDashStyle.Dot };
    private readonly ValueDataSeries _l1 = new("L1", "L1") { Color = Sup, LineDashStyle = LineDashStyle.Dot };
    private readonly ValueDataSeries _l2 = new("L2", "L2") { Color = Sup, LineDashStyle = LineDashStyle.Dot, Visible = false };
    private readonly ValueDataSeries _l3 = new("L3", "L3") { Color = Sup, Width = 2 };
    private readonly ValueDataSeries _l4 = new("L4", "L4") { Color = Sup, Width = 2, LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _l5 = new("L5", "L5") { Color = Ext, LineDashStyle = LineDashStyle.Dash };
    private readonly ValueDataSeries _l6 = new("L6", "L6") { Color = Ext, LineDashStyle = LineDashStyle.Dot };

    private CamarillaSession _mode = CamarillaSession.Cme;
    private string _tzId = "America/New_York";
    private decimal _startHour = 18;
    private SessionSettings _session = Build(CamarillaSession.Cme, "America/New_York", 18);

    // sesión en curso y sesión anterior (H, L, C)
    private decimal _curH, _curL, _curC;
    private decimal _prevH, _prevL, _prevC;
    private bool _hasPrev;
    private int _sessionStart;
    private CamarillaLevels _levels;

    // para corregir la última vela (isNew = false) sin acumular dos veces
    private (decimal H, decimal L, decimal C, decimal pH, decimal pL, decimal pC, bool hasPrev, int start, CamarillaLevels lv) _snap;

    public QtCamarilla() : base(true)
    {
        DenyToChangePanel = true;
        DataSeries[0] = _h3;
        foreach (var s in All.Where(s => s != _h3)) DataSeries.Add(s);
    }

    [Display(Name = "Día de trading", GroupName = "Sesión", Order = 10)]
    public CamarillaSession SessionMode { get => _mode; set { _mode = value; Rebuild(); } }

    [Display(Name = "Zona horaria (personalizada)", GroupName = "Sesión", Order = 20)]
    public string TimeZoneId { get => _tzId; set { _tzId = string.IsNullOrWhiteSpace(value) ? "America/New_York" : value.Trim(); Rebuild(); } }

    [Display(Name = "Hora de inicio (personalizada)", GroupName = "Sesión", Order = 30)]
    [Range(0, 23.99)]
    public decimal StartHour { get => _startHour; set { _startHour = Math.Clamp(value, 0, 23.99m); Rebuild(); } }

    private void Rebuild()
    {
        _session = Build(_mode, _tzId, _startHour);
        RecalculateValues();
    }

    private static SessionSettings Build(CamarillaSession mode, string tzId, decimal startHour)
    {
        if (mode == CamarillaSession.Chart) return SessionSettings.CryptoUtc; // no se usa: manda el gráfico
        string id = mode == CamarillaSession.Cme ? "America/New_York" : tzId;
        var tz = TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : TimeZoneInfo.Utc;
        var start = mode == CamarillaSession.Cme ? TimeSpan.FromHours(18) : TimeSpan.FromHours((double)startHour);
        return new SessionSettings { TimeZone = tz, Start = start };
    }

    private bool StartsSession(int bar)
    {
        if (bar == 0) return true;
        if (_mode == CamarillaSession.Chart) return IsNewSession(bar);
        return _session.IsNewSession(GetCandle(bar - 1).Time, GetCandle(bar).Time);
    }

    private IEnumerable<ValueDataSeries> All => new[] { _pp, _h1, _h2, _h3, _h4, _h5, _h6, _l1, _l2, _l3, _l4, _l5, _l6 };

    protected override void ResetState()
    {
        _curH = _curL = _curC = 0;
        _prevH = _prevL = _prevC = 0;
        _hasPrev = false;
        _sessionStart = 0;
        _levels = default;
    }

    protected override void Step(int bar, decimal value, bool isNew)
    {
        if (isNew) _snap = (_curH, _curL, _curC, _prevH, _prevL, _prevC, _hasPrev, _sessionStart, _levels);
        else (_curH, _curL, _curC, _prevH, _prevL, _prevC, _hasPrev, _sessionStart, _levels) = _snap;

        var c = GetCandle(bar);
        bool newSession = StartsSession(bar);
        if (newSession)
        {
            if (bar > 0)
            {
                _prevH = _curH;
                _prevL = _curL;
                _prevC = _curC;
                _hasPrev = true;
                _levels = CamarillaLevels.From(_prevH, _prevL, _prevC);
                foreach (var s in All) s.SetPointOfEndLine(bar - 1);
            }

            _sessionStart = bar;
            _curH = c.High;
            _curL = c.Low;
        }
        else
        {
            _curH = Math.Max(_curH, c.High);
            _curL = Math.Min(_curL, c.Low);
        }

        _curC = c.Close;

        if (!_hasPrev) return;
        var lv = _levels;
        _pp[bar] = lv.PP;
        _h1[bar] = lv.H1; _h2[bar] = lv.H2; _h3[bar] = lv.H3; _h4[bar] = lv.H4; _h5[bar] = lv.H5; _h6[bar] = lv.H6;
        _l1[bar] = lv.L1; _l2[bar] = lv.L2; _l3[bar] = lv.L3; _l4[bar] = lv.L4; _l5[bar] = lv.L5; _l6[bar] = lv.L6;
    }
}

/// <summary>Fórmula Camarilla de TradingView, más H5/H6 y L5/L6 de los scripts habituales.</summary>
public readonly record struct CamarillaLevels(
    decimal PP,
    decimal H1, decimal H2, decimal H3, decimal H4, decimal H5, decimal H6,
    decimal L1, decimal L2, decimal L3, decimal L4, decimal L5, decimal L6)
{
    public static CamarillaLevels From(decimal h, decimal l, decimal c)
    {
        decimal r = h - l;
        decimal h4 = c + r * 1.1m / 2m;
        decimal h3 = c + r * 1.1m / 4m;
        decimal h5 = l != 0 ? h / l * c : h4;
        decimal h6 = h5 + 1.168m * (h5 - h4);
        return new CamarillaLevels(
            PP: (h + l + c) / 3m,
            H1: c + r * 1.1m / 12m,
            H2: c + r * 1.1m / 6m,
            H3: h3,
            H4: h4,
            H5: h5,
            H6: h6,
            L1: c - r * 1.1m / 12m,
            L2: c - r * 1.1m / 6m,
            L3: c - r * 1.1m / 4m,
            L4: c - r * 1.1m / 2m,
            L5: c - (h5 - c),
            L6: c - (h6 - c));
    }
}
