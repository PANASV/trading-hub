using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>
/// Configuración de una estrategia puesta en un gráfico: solo visual o ejecutando en una cuenta, sus parámetros y
/// cómo le fue con la historia del gráfico.
/// </summary>
internal sealed class StrategyChartWindow : Window
{
    private static readonly CultureInfo Es = new("es-AR");
    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly ChartStrategy _cs;
    private readonly RadioButton _visual = new() { Content = "Solo visual: marca dónde entra y dónde sale, no opera", GroupName = "modo" };
    private readonly RadioButton _live = new() { Content = "Ejecutar en vivo con una cuenta", GroupName = "modo" };
    private readonly ComboBox _account = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _accountNote = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _params = new() { Spacing = 8 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly List<(PropertyInfo Prop, Func<object?> Read)> _editors = new();
    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StrategyFile? _file;

    public StrategyChartWindow(TradingView host, ChartTab chart, ChartStrategy cs)
    {
        _host = host;
        _chart = chart;
        _cs = cs;
        Title = $"Estrategia en el gráfico · {cs.Overlay.Name}";
        Width = 480;
        Height = 680;
        MinWidth = 420;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;
        _file = host.StrategyCatalog.Files.FirstOrDefault(f => f.Id == cs.StrategyId) ?? host.StrategyCatalog.Files.FirstOrDefault(f => f.FileName == cs.StrategyId);

        _visual.IsChecked = !cs.Live;
        _live.IsChecked = cs.Live;
        _visual.IsCheckedChanged += (_, _) => UpdateMode();
        var accounts = chart.Model != null ? host.AccountOptions(chart, chart.Model.Data) : new List<AccountOption>();
        Ui.SyncAccounts(_account, accounts, cs.AccountId);
        if (_account.SelectedItem == null && _account.Items.Count > 0) _account.SelectedIndex = 0;
        BuildParameters();

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(20, 18, 20, 18) };
        body.Children.Add(Ui.V(4, Ui.H2(cs.Overlay.Name), Note($"{chart.Symbol} · {chart.Model?.Period.DisplayName}. Marca sus entradas (flecha) y salidas (cruz con el resultado) en la historia del gráfico y en las nuevas."), _state));
        body.Children.Add(Section("Cómo corre", _visual, _live, Ui.Field("Cuenta", _account), _accountNote));
        body.Children.Add(Section("Parámetros", _params));
        body.Children.Add(Section("En la historia del gráfico", _summary));

        var apply = Ui.Button("Aplicar", MaterialIconKind.Check, Apply, "primary");
        var remove = Ui.Button("Quitar del gráfico", MaterialIconKind.DeleteOutline, () =>
        {
            host.RemoveStrategy(chart, cs);
            Close();
        }, "danger");
        var close = Ui.Button("Cerrar", MaterialIconKind.Close, Close);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(20, 10, 20, 16) };
        foreach (var b in new Control[] { apply, remove, close }) actions.Children.Add(b);

        var root = new DockPanel();
        var actionsBorder = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(actionsBorder, Dock.Bottom);
        root.Children.Add(actionsBorder);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        UpdateMode();
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private void UpdateMode()
    {
        bool live = _live.IsChecked == true;
        _account.IsEnabled = live;
        _accountNote.Text = live
            ? "Manda órdenes de verdad a esa cuenta (si es real, te pide confirmar). La detenés volviendo a «Solo visual» o quitándola; la posición abierta queda como está."
            : "Corre con una cuenta simulada propia: no toca tu cuenta simulada ni tu historial.";
    }

    private void BuildParameters()
    {
        _params.Children.Clear();
        _editors.Clear();
        if (_file?.Type == null)
        {
            _params.Children.Add(Note("La estrategia no está compilada: abrila en Estrategias."));
            return;
        }

        var current = _file.Create();
        current.SetParameters(_cs.Parameters);
        foreach (var p in Strategy.Parameters(_file.Type))
        {
            object? value = p.GetValue(current);
            Control editor;
            Func<object?> read;
            var t = p.PropertyType;
            if (t == typeof(bool))
            {
                var cb = new CheckBox { IsChecked = value is true };
                editor = cb;
                read = () => cb.IsChecked == true;
            }
            else if (t.IsEnum)
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var v in Enum.GetValues(t)) combo.Items.Add(new ComboBoxItem { Content = Controls.PropertyGrid.EnumDisplay(v), Tag = v });
                var selected = value is string str && Enum.TryParse(t, str, out var parsed) ? parsed : value;
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
                editor = combo;
                read = () => combo.SelectedItem is ComboBoxItem { Tag: { } tag } ? tag : value;
            }
            else if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) || t == typeof(float))
            {
                bool whole = t == typeof(int) || t == typeof(long);
                var num = new NumericUpDown
                {
                    Value = Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture),
                    Increment = whole ? 1 : 0.1m,
                    FormatString = whole ? "0" : "0.####",
                    NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Minimum = decimal.MinValue,
                    Maximum = decimal.MaxValue,
                };
                editor = num;
                read = () => Convert.ChangeType(num.Value ?? 0, t, CultureInfo.InvariantCulture);
            }
            else
            {
                var tb = new TextBox { Text = value?.ToString() ?? "" };
                editor = tb;
                read = () => tb.Text;
            }

            var attr = p.GetCustomAttribute<ParameterAttribute>();
            if (!string.IsNullOrWhiteSpace(attr?.Description)) ToolTip.SetTip(editor, attr.Description);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            g.Children.Add(new TextBlock { Text = Strategy.ParameterName(p), VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(editor, 1);
            g.Children.Add(editor);
            _params.Children.Add(g);
            _editors.Add((p, read));
        }

        if (_editors.Count == 0) _params.Children.Add(Note("Esta estrategia no tiene parámetros."));
    }

    private void Refresh()
    {
        _state.Text = (_cs.Overlay.Error != null ? "⚠ " + _cs.Overlay.Error + " · " : "") + "Estado: " + _cs.Overlay.Status;
        _state.Foreground = _cs.Overlay.Error != null ? Ui.Down : _cs.Overlay.Live ? Ui.Accent : Ui.Text1;
        var m = _cs.HistoryMetrics;
        int live = _cs.Runner?.Ledger?.Trades.Count ?? 0;
        _summary.Text = m == null || m.TotalTrades == 0
            ? _cs.Busy ? "Calculando…" : "Sin operaciones en la historia cargada (cargá más días desde el botón de historia del gráfico)."
            : $"{m.TotalTrades} operaciones · resultado {m.NetProfit.ToString("+#,##0.00;-#,##0.00", Es)} · {m.WinRate:0.#}% ganadoras · profit factor {(m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Es))} · drawdown {(-m.MaxDrawdown).ToString("#,##0.00", Es)}" +
              (live > 0 ? $"\nDesde que corre en vivo: {live} operación(es) · {(_cs.Runner!.Ledger.Realized).ToString("+#,##0.00;-#,##0.00", Es)}" : "") +
              "\n(Sin comisiones ni deslizamiento: es una referencia visual; para el detalle usá el backtest en Estrategias.)";
    }

    private void Apply()
    {
        if (_file?.Type != null)
        {
            var s = _file.Create();
            s.SetParameters(_cs.Parameters);
            foreach (var (prop, read) in _editors)
            {
                try
                {
                    prop.SetValue(s, read());
                }
                catch (Exception)
                {
                    // valor inválido: queda el anterior
                }
            }

            _cs.Parameters = s.GetParameters();
        }

        _cs.Live = _live.IsChecked == true;
        if (_cs.Live && _account.SelectedItem is ComboBoxItem { Tag: AccountOption a }) _cs.AccountId = a.Id;
        _cs.Paused = false;
        _ = _host.RunStrategyAsync(_chart, _cs);
        Refresh();
    }
}
