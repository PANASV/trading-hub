using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>
/// Lanzar una estrategia en vivo: cuál, en qué instrumento y cuenta, con qué velas y parámetros (los de su backtest),
/// con qué riesgo (tamaño, pérdida máxima del día, drawdown máximo) y en qué portafolio.
/// </summary>
internal sealed class LiveLaunchWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly ComboBox _strategy = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Elegí una estrategia" };
    private readonly InstrumentPicker _picker;
    private readonly ComboBox _account = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Elegí primero el instrumento" };
    private readonly ComboBox _period = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _session = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly ParameterEditor _params = new();
    private readonly MarketConditionPicker _conditions = new();
    private readonly NumericUpDown _size = Num(0.25m, 0.01m, 100, "0.##", 1);
    private readonly NumericUpDown _dailyLoss = Num(10, 0, 100_000_000, "0.##", 0);
    private readonly NumericUpDown _maxDd = Num(10, 0, 100_000_000, "0.##", 0);
    private readonly CheckBox _closeOnLimit = new() { Content = "Al tocar un límite, cerrar también su posición", IsChecked = true };
    private readonly ComboBox _portfolio = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _warning = new() { Foreground = Ui.Warn, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly Button _start;
    private string? _profile;
    private Instrument? _instrument;
    private InstrumentData? _data;
    private Dictionary<string, object?> _values = new();
    private string? _timeZoneId;
    private TimeSpan _sessionStart;

    public LiveLaunchWindow(AppServices svc, Action<string> status, string? strategyId = null, IReadOnlyDictionary<string, object?>? parameters = null, string? portfolioId = null)
    {
        _svc = svc;
        _status = status;
        Title = "Nueva estrategia en vivo";
        Width = 600;
        Height = 840;
        MinWidth = 500;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        var catalog = svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        foreach (var f in catalog.Files.OrderBy(f => f.DisplayName))
            _strategy.Items.Add(new ComboBoxItem { Content = f.DisplayName + (f.LastCompile?.Success == false ? " (con errores)" : ""), Tag = f });
        foreach (var p in new[] { "M1", "M2", "M3", "M5", "M10", "M15", "M30", "H1", "H4" }) _period.Items.Add(p);
        _period.SelectedItem = "M5";

        _picker = new InstrumentPicker(svc) { HorizontalAlignment = HorizontalAlignment.Stretch };
        _picker.Picked += (profileId, inst, _) =>
        {
            _profile = profileId;
            _instrument = inst;
            _picker.SetCurrent(profileId, inst);
            _data = _svc.Open(profileId, inst);
            Ui.SyncAccounts(_account, _svc.AccountsFor(_data).ToList(), "sim", Ui.Down);
            if (_account.SelectedItem == null && _account.Items.Count > 0) _account.SelectedIndex = 0;
            UpdateWarning();
        };
        _account.SelectionChanged += (_, _) => UpdateWarning();
        _strategy.SelectionChanged += (_, _) => _ = LoadStrategyAsync(null);

        _portfolio.Items.Add(new ComboBoxItem { Content = "Ninguno (va al automático)", Tag = null });
        foreach (var d in LiveStore.LoadPortfolios().Where(d => !d.Auto)) _portfolio.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        _portfolio.SelectedItem = _portfolio.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == portfolioId) ?? _portfolio.Items[0];

        _start = Ui.Button("Iniciar en vivo", MaterialIconKind.Play, () => _ = StartAsync(), "primary");
        var cancel = Ui.Button("Cancelar", MaterialIconKind.Close, Close);

        var riskGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*") };
        void Put(Control c, int col)
        {
            Grid.SetColumn(c, col);
            riskGrid.Children.Add(c);
        }

        Put(Ui.Field("Tamaño (×)", _size, "multiplica sus órdenes"), 0);
        Put(Ui.Field("Pérdida máx. del día", _dailyLoss, "0 = sin límite"), 2);
        Put(Ui.Field("Drawdown máximo", _maxDd, "0 = sin límite"), 4);

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(Ui.V(4, Ui.H2("Nueva estrategia en vivo"),
            Note("Recibe el mercado en tiempo real y opera en la cuenta elegida. Antes de empezar arma sus indicadores con la historia (sin operar). Probala primero en la cuenta simulada.")));
        body.Children.Add(Section("Qué y dónde", Ui.Field("Estrategia", _strategy), Ui.Field("Instrumento", _picker), Ui.Field("Cuenta", _account), _warning,
            Ui.Field("Velas", _period), _session));
        body.Children.Add(Section("Parámetros", _params, Note("Vienen de lo último que configuraste en su Backtest.")));
        body.Children.Add(Section("Condición de mercado", _conditions,
            Note("Solo abre operaciones cuando el mercado está en lo que elijas (tendencia de las últimas 24 horas y volatilidad del último mes, con velas ya cerradas). Después lo cambiás desde En vivo sin reiniciarla.")));
        body.Children.Add(Section("Riesgo", riskGrid, _closeOnLimit,
            Note("Pérdida del día: al tocarla no abre más hasta la próxima sesión. Drawdown: la detiene. Las salidas siempre cierran justo lo que abrió, aunque cambies el tamaño en el medio.")));
        body.Children.Add(Section("Portafolio", _portfolio));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 10, 22, 16) };
        actions.Children.Add(cancel);
        actions.Children.Add(_start);
        var bottom = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };

        if (strategyId != null)
        {
            var item = _strategy.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is StrategyFile f && f.Id == strategyId);
            if (item != null)
            {
                _strategy.SelectedItem = item;
                _ = LoadStrategyAsync(parameters);
            }
        }
        else _params.Load(null, new Dictionary<string, object?>());
    }

    private static NumericUpDown Num(decimal increment, decimal min, decimal max, string format, decimal value) => new()
    {
        Increment = increment,
        Minimum = min,
        Maximum = max,
        Value = value,
        FormatString = format,
        NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private void UpdateWarning()
    {
        bool real = (_account.SelectedItem as ComboBoxItem)?.Tag is AccountOption { IsReal: true };
        _warning.IsVisible = real;
        _warning.Text = "Cuenta REAL: la estrategia va a mandar órdenes con dinero de verdad, sin confirmar cada una.";
    }

    /// <summary>Carga parámetros, velas y sesión de su último backtest (o los de la clase).</summary>
    private async Task LoadStrategyAsync(IReadOnlyDictionary<string, object?>? overrides)
    {
        if (_strategy.SelectedItem is not ComboBoxItem { Tag: StrategyFile file }) return;
        if (file.Type == null) await Task.Run(() => _svc.Strategies.Compile(file));
        if (file.Type == null)
        {
            _params.Load(null, new Dictionary<string, object?>());
            _status($"«{file.FileName}» tiene errores: arreglala en Backtesting.");
            return;
        }

        var saved = LiveCenter.SavedBacktest(file, _svc.Settings.TimeZoneId);
        var values = saved.Parameters;
        _timeZoneId = saved.TimeZoneId;
        _sessionStart = saved.SessionStart;
        if (saved.Period is { } sp)
        {
            if (!_period.Items.Cast<string>().Contains(sp)) _period.Items.Add(sp);
            _period.SelectedItem = sp;
        }

        if (overrides != null)
            foreach (var (k, v) in overrides)
                if (values.ContainsKey(k)) values[k] = v;
        _values = values;
        _params.Load(file.Type, values, hidden: LiveStrategyWindow.ContextParameters);
        var probe = file.Type != null ? (Strategy)Activator.CreateInstance(file.Type)! : null;
        probe?.SetParameters(values);
        _conditions.Load(probe?.RegimeFilter ?? RegimeFilter.All, probe?.MinVolatilityRank ?? 0, probe?.MaxVolatilityRank ?? 100);
        _session.Text = $"Sesión: {(_timeZoneId ?? "UTC")} desde las {_sessionStart:hh\\:mm} (la del backtest; define el «día» para la pérdida máxima del día).";
    }

    /// <summary>Los parámetros con la condición de mercado elegida.</summary>
    private Dictionary<string, object?> WithConditions(Dictionary<string, object?> values)
    {
        var (filter, min, max) = _conditions.Value;
        values[nameof(Strategy.RegimeFilter)] = (int)filter;
        values[nameof(Strategy.MinVolatilityRank)] = min;
        values[nameof(Strategy.MaxVolatilityRank)] = max;
        return values;
    }

    private async Task StartAsync()
    {
        if (_strategy.SelectedItem is not ComboBoxItem { Tag: StrategyFile file })
        {
            _status("Elegí una estrategia.");
            return;
        }

        if (_profile == null || _instrument == null || _data == null)
        {
            _picker.Open();
            return;
        }

        if (_account.SelectedItem is not ComboBoxItem { Tag: AccountOption acc })
        {
            _status("Elegí una cuenta.");
            return;
        }

        if (!ChartPeriod.TryParse(_period.SelectedItem as string ?? "M5", out var period))
        {
            _status("Período inválido.");
            return;
        }

        if (!_conditions.IsValid)
        {
            _status("Elegí al menos una condición de mercado y una volatilidad.");
            return;
        }

        if (acc.IsReal && !await ConfirmDialog.ShowAsync(this, "Ejecutar en cuenta REAL",
                $"«{file.DisplayName}» va a operar {_instrument.Symbol} en {acc.Name} con órdenes reales y sin confirmación individual. La detenés desde En vivo.",
                "Ejecutar con dinero real", danger: true))
            return;

        _start.IsEnabled = false;
        try
        {
            var (runner, message) = await _svc.Live.LaunchAsync(new LiveLaunch
            {
                File = file,
                ProfileId = _profile,
                Instrument = _instrument,
                AccountId = acc.Id,
                Period = period,
                Parameters = WithConditions(_params.Read(_values)),
                TimeZoneId = _timeZoneId,
                SessionStart = _sessionStart,
                Size = _size.Value ?? 1,
                MaxDailyLoss = _dailyLoss.Value ?? 0,
                MaxDrawdown = _maxDd.Value ?? 0,
                CloseOnLimit = _closeOnLimit.IsChecked == true,
                PortfolioId = (_portfolio.SelectedItem as ComboBoxItem)?.Tag as string,
            });
            _status(message);
            if (runner == null) return;
            if (runner.PortfolioId != null) _svc.Live.Assign(runner.Key, runner.PortfolioId);
            Close(runner.Key);
        }
        finally
        {
            _start.IsEnabled = true;
        }
    }
}
