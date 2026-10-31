using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Feeds.Rithmic;

namespace TradingHub.App.Views;

/// <summary>
/// Conexiones: perfiles de datos y cuentas (Rithmic, Binance, Bybit, Interactive Brokers y simulador).
/// Las contraseñas y API secrets se guardan en el Llavero de macOS (en Windows, en el Administrador de credenciales), nunca
/// en archivos.
/// </summary>
internal sealed class ConnectionsView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly ListBox _list = new();
    private readonly ContentControl _detail = new();
    private readonly Dictionary<string, (bool Ok, string Message, DateTime When)> _results = new();
    private ConnectionProfile? _selected;
    private CancellationTokenSource? _testCts;

    public ConnectionsView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        Content = Build();
        FillList();
        _svc.Connections.Changed += FillList;
    }

    public void OnShown() => FillList();

    public void OnTimer()
    {
    }

    private static MaterialIconKind IconOf(ConnectionKind k) => k switch
    {
        ConnectionKind.Simulator => MaterialIconKind.FlaskOutline,
        ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => MaterialIconKind.Bitcoin,
        ConnectionKind.Bybit => MaterialIconKind.CurrencyUsd,
        ConnectionKind.Rithmic => MaterialIconKind.ChartTimelineVariant,
        ConnectionKind.AmpFutures => MaterialIconKind.ChartBellCurveCumulative,
        ConnectionKind.Tradovate => MaterialIconKind.ChartLineVariant,
        ConnectionKind.CTrader => MaterialIconKind.ChartAreaspline,
        ConnectionKind.Oanda => MaterialIconKind.Earth,
        ConnectionKind.Alpaca => MaterialIconKind.Finance,
        ConnectionKind.Databento => MaterialIconKind.DatabaseArrowDownOutline,
        _ => MaterialIconKind.Bank,
    };

    private Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };

        // ---- lista
        var add = new Button { Content = Ui.H(6, Ui.Icon(MaterialIconKind.Plus, 16), new TextBlock { Text = L.T("Nueva conexión") }), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var menu = new MenuFlyout();
        foreach (var kind in Enum.GetValues<ConnectionKind>())
        {
            // qué da cada una, para elegir sin tener que saberlo
            string? hint = kind switch
            {
                ConnectionKind.Tradovate => L.T("futuros: cuentas para operar y datos"),
                ConnectionKind.CTrader => L.T("CFDs y forex de IC Markets, Pepperstone, FxPro…"),
                ConnectionKind.Oanda => L.T("CFDs de índices, oro y forex (práctica gratis)"),
                ConnectionKind.Alpaca => L.T("acciones y ETFs de EE.UU. (SPY, QQQ) gratis"),
                ConnectionKind.Databento => L.T("historia de futuros de CME desde 2010"),
                _ => null,
            };
            object itemHeader = hint == null ? Ui.EnumName(kind) : Ui.V(1, new TextBlock { Text = Ui.EnumName(kind) }, new TextBlock { Text = hint, Classes = { "faint", "small" } });
            var mi = new MenuItem { Header = itemHeader, Icon = Ui.Icon(IconOf(kind), 16) };
            mi.Click += (_, _) => AddProfile(kind);
            menu.Items.Add(mi);
        }

        add.Flyout = menu;
        var header = Ui.V(4, Ui.H1(L.T("Conexiones")), Ui.Muted(L.T("Datos de mercado y cuentas para operar")));
        header.Margin = new Thickness(0, 0, 0, 16);
        var left = new DockPanel { Margin = new Thickness(20, 20, 12, 20) };
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);
        DockPanel.SetDock(add, Dock.Bottom);
        add.Margin = new Thickness(0, 12, 0, 0);
        left.Children.Add(add);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: ConnectionProfile p }) Select(p);
        };
        _list.Background = Brushes.Transparent;
        left.Children.Add(_list);
        grid.Children.Add(new Border { Child = left, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) });

        // ---- detalle
        _detail.Margin = new Thickness(28, 24, 28, 32);
        var scroll = new ScrollViewer { Content = _detail };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private void FillList()
    {
        var keep = _selected?.Id;
        _list.Items.Clear();
        foreach (var p in _svc.Connections.Profiles)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Fill = !p.Enabled ? Ui.Text2 : _results.TryGetValue(p.Id, out var r) ? (r.Ok ? Ui.Up : Ui.Down) : Ui.Line;
            ToolTip.SetTip(dot, !p.Enabled ? L.T("Desactivada") : _results.TryGetValue(p.Id, out var r2) ? r2.Message : L.T("Sin probar"));
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Ui.Bg3, Child = Ui.Icon(IconOf(p.Kind), 18, p.Enabled ? Ui.Accent : Ui.Text2) };
            string sub = Describe(p);
            var text = Ui.V(1, new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, Opacity = p.Enabled ? 1 : 0.6 }, new TextBlock { Text = sub, Classes = { "muted", "small" } });
            var row = new DockPanel { Margin = new Thickness(2, 4) };
            DockPanel.SetDock(icon, Dock.Left);
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(dot);
            row.Children.Add(text);
            _list.Items.Add(new ListBoxItem { Content = row, Tag = p });
        }

        var sel = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((ConnectionProfile)i.Tag!).Id == keep) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        _list.SelectedItem = sel;
    }

    /// <summary>Resumen de una conexión para la lista.</summary>
    private string Describe(ConnectionProfile p)
    {
        string prefix = p.Name == Ui.EnumName(p.Kind) ? "" : Ui.EnumName(p.Kind) + " · ";
        if (!p.Enabled) return prefix + L.T("desactivada");
        string detail = p.Kind switch
        {
            ConnectionKind.Simulator => L.T("mercado simulado"),
            ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => string.IsNullOrWhiteSpace(p.ApiKey)
                ? L.T("datos públicos · sin cuenta")
                : p.Testnet ? L.T("datos + cuenta testnet") : p.AllowRealOrders ? L.T("datos + cuenta REAL") : L.T("datos + cuenta real (solo lectura)"),
            ConnectionKind.Bybit => L.T("datos públicos"),
            ConnectionKind.Rithmic or ConnectionKind.AmpFutures => string.IsNullOrWhiteSpace(p.RithmicUser) ? L.T("sin usuario configurado") : $"{p.RithmicUser} · {p.RithmicSystem}",
            ConnectionKind.Tradovate => string.IsNullOrWhiteSpace(p.TradovateUser) ? L.T("sin usuario configurado") : $"{p.TradovateUser} · {(p.Testnet ? "demo" : p.AllowRealOrders ? L.T("REAL") : L.T("real (solo lectura)"))}",
            ConnectionKind.CTrader => string.IsNullOrWhiteSpace(p.CTraderClientId) ? L.T("sin aplicación configurada") : $"{(p.Testnet ? "demo" : "real")}{(p.CTraderLogin > 0 ? L.T(" · cuenta ") + p.CTraderLogin : "")}",
            ConnectionKind.Oanda => (p.Testnet ? L.T("práctica") : L.T("real")) + (string.IsNullOrWhiteSpace(p.OandaAccount) ? "" : " · " + p.OandaAccount),
            ConnectionKind.Alpaca => string.IsNullOrWhiteSpace(p.ApiKey) ? L.T("sin claves") : L.F("historia SIP · en vivo {0}",p.AlpacaLiveFeed.ToUpperInvariant()),
            ConnectionKind.Databento => L.T("historia de futuros de CME (pago por uso)"),
            _ => $"{p.IbHost}:{p.IbPort}",
        };
        return prefix + detail;
    }

    private void AddProfile(ConnectionKind kind)
    {
        int n = _svc.Connections.Profiles.Count(p => p.Kind == kind) + 1;
        var p = new ConnectionProfile { Kind = kind, Name = Ui.EnumName(kind) + (n > 1 ? $" {n}" : ""), Enabled = kind is not (ConnectionKind.Rithmic or ConnectionKind.AmpFutures) };
        if (kind is ConnectionKind.CTrader) p.Name = n > 1 ? $"cTrader {n}" : "cTrader";
        if (kind is ConnectionKind.Alpaca) p.Name = n > 1 ? $"Alpaca {n}" : "Alpaca";
        if (kind is ConnectionKind.Databento) p.Name = "Databento";
        // el primero de cada tipo usa el id de sus datos (así los archivos de ticks quedan con un nombre claro)
        string defaultId = ConnectionProfile.DefaultId(kind);
        if (kind is ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento
            && _svc.Connections.Profiles.All(x => x.Id != defaultId))
            p.Id = defaultId;
        if (kind == ConnectionKind.Simulator) p.HistoryHours = 6;
        if (kind == ConnectionKind.AmpFutures)
        {
            // AMP da cuentas de Rithmic: servidor de producción (Chicago) y el sistema de la demo por defecto
            p.RithmicUri = RithmicGateways.ChicagoUri;
            p.RithmicSystem = L.T("Rithmic Paper Trading");
        }
        _svc.Connections.Profiles.Add(p);
        _svc.Connections.Save();
        _selected = p;
        FillList();
    }

    // ===================================================================== detalle

    private void Select(ConnectionProfile p)
    {
        _selected = p;
        _detail.Content = BuildDetail(p);
    }

    private Control BuildDetail(ConnectionProfile p)
    {
        var name = new TextBox { Text = p.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, MinWidth = 170, MaxWidth = 300 };
        var enabled = new ToggleSwitch { IsChecked = p.Enabled, OnContent = L.T("Activada"), OffContent = L.T("Desactivada") };
        var history = new ComboBox { MinWidth = 160 };
        foreach (var (h, label) in new[] { (0.5, L.T("30 minutos")), (1.0, L.T("1 hora")), (1.5, L.T("1,5 horas")), (3.0, L.T("3 horas")), (6.0, L.T("6 horas")), (12.0, L.T("12 horas")), (24.0, L.T("1 día")), (72.0, L.T("3 días")), (168.0, L.T("7 días")) })
            history.Items.Add(new ComboBoxItem { Content = label, Tag = h });
        history.SelectedItem = history.Items.OfType<ComboBoxItem>().OrderBy(i => Math.Abs((double)i.Tag! - p.HistoryHours)).First();

        var resultIcon = new ContentControl();
        var resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var resultBox = new Border { Child = Ui.H(10, resultIcon, resultText), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), Background = Ui.Bg2, IsVisible = false };
        void ShowResult(bool ok, string msg, bool running = false)
        {
            resultBox.IsVisible = true;
            resultIcon.Content = running ? Ui.Icon(MaterialIconKind.ProgressClock, 18, Ui.Text1) : Ui.Icon(ok ? MaterialIconKind.CheckCircle : MaterialIconKind.CloseCircle, 18, ok ? Ui.Up : Ui.Down);
            resultText.Text = msg;
        }

        if (_results.TryGetValue(p.Id, out var last)) ShowResult(last.Ok, $"{last.Message}  ({Ui.Ago(last.When)})");

        // campos según el tipo (se escriben en el perfil al guardar)
        var apply = new List<Action>();
        var sections = new StackPanel { Spacing = 16 };

        var dataSection = Ui.V(14,
            Ui.H2(L.T("Datos de mercado")),
            Ui.Field(L.T("Historia al abrir un gráfico"), history, L.T("Lo grabado o descargado en Datos carga al instante; el resto se pide a la conexión.")));
        sections.Children.Add(Ui.Card(dataSection));
        apply.Add(() => p.HistoryHours = (double)((ComboBoxItem)history.SelectedItem!).Tag!);

        switch (p.Kind)
        {
            case ConnectionKind.Simulator:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, L.T("Mercado simulado con libro y operaciones realistas, ideal para probar sin conexión. No necesita configuración.")));
                break;
            case ConnectionKind.BinanceFutures:
            case ConnectionKind.BinanceSpot:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, L.T("Los datos de mercado de Binance son públicos: no hace falta cuenta para ver gráficos, DOM y cinta.")));
                sections.Children.Add(Ui.Card(BinanceAccount(p, apply)));
                break;
            case ConnectionKind.Bybit:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, L.T("Datos públicos de los perpetuos lineales (USDT) de Bybit. Operar con cuenta Bybit todavía no está disponible.")));
                break;
            case ConnectionKind.Rithmic:
            case ConnectionKind.AmpFutures:
                sections.Children.Add(Ui.Card(RithmicSection(p, apply)));
                break;
            case ConnectionKind.InteractiveBrokers:
                sections.Children.Add(Ui.Card(IbSection(p, apply)));
                break;
            case ConnectionKind.Tradovate:
                sections.Children.Add(Ui.Card(TradovateSection(p, apply)));
                break;
            case ConnectionKind.CTrader:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, L.T("Los CFDs no tienen operaciones reales: cada cotización (bid) cuenta como una operación de volumen 1. Sirve para precio, velas, replay y backtests; el footprint y el delta son aproximados.")));
                sections.Children.Add(Ui.Card(CTraderSection(p, apply)));
                break;
            case ConnectionKind.Oanda:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, L.T("Los CFDs no tienen operaciones reales: cada cotización (precio medio) cuenta como una operación de volumen 1. Sirve para precio, velas, replay y backtests; el footprint y el delta son aproximados.")));
                sections.Children.Add(Ui.Card(OandaSection(p, apply)));
                break;
            case ConnectionKind.Alpaca:
                sections.Children.Add(Ui.Card(AlpacaSection(p, apply)));
                break;
            case ConnectionKind.Databento:
                dataSection.IsVisible = false;
                sections.Children.Add(Ui.Card(DatabentoSection(p, apply)));
                break;
        }

        // ---- encabezado y acciones
        var test = Ui.Button(L.T("Probar conexión"), MaterialIconKind.LanConnect, () => { });
        var save = Ui.Button(L.T("Guardar"), MaterialIconKind.ContentSaveOutline, () => { }, "primary");
        var delete = Ui.Button(L.T("Eliminar"), MaterialIconKind.DeleteOutline, () => { }, "ghost");
        delete.IsVisible = _svc.Connections.Profiles.Count(x => x.Kind == p.Kind) > 1 || p.Kind is ConnectionKind.InteractiveBrokers or ConnectionKind.AmpFutures
            or ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento;

        void Save()
        {
            p.Name = string.IsNullOrWhiteSpace(name.Text) ? Ui.EnumName(p.Kind) : name.Text.Trim();
            p.Enabled = enabled.IsChecked == true;
            foreach (var a in apply) a();
            _svc.Connections.Save();
            _svc.Connections.Invalidate(p);
            InstrumentPicker.ClearCache(p.Id);
            _status(L.F("Conexión \"{0}\" guardada",p.Name));
        }

        save.Click += (_, _) => Save();
        test.Click += async (_, _) =>
        {
            Save();
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            test.IsEnabled = false;
            ShowResult(true, L.T("Probando…"), running: true);
            try
            {
                var (ok, msg) = await _svc.Connections.TestAsync(p, _testCts.Token);
                _results[p.Id] = (ok, msg, DateTime.UtcNow);
                ShowResult(ok, msg);
                FillList();
            }
            finally
            {
                test.IsEnabled = true;
            }
        };
        delete.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            if (!await ConfirmDialog.ShowAsync(w, L.T("Eliminar conexión"), L.F("Se borra \"{0}\" y sus secretos {1}. Los datos grabados no se tocan.",p.Name,(Os.IsWindows ? L.T("del Administrador de credenciales") : L.T("del Llavero"))), L.T("Eliminar"), danger: true)) return;
            _svc.Connections.ClearSecrets(p);
            _svc.Connections.Invalidate(p);
            _svc.Connections.Profiles.Remove(p);
            _svc.Connections.Save();
            _selected = null;
            FillList();
        };

        var kindBadge = new Border { Child = Ui.H(6, Ui.Icon(IconOf(p.Kind), 14, Ui.Accent), new TextBlock { Text = Ui.EnumName(p.Kind), FontSize = 12 }), Classes = { "pill" } };
        var titleRow = new DockPanel();
        // el interruptor va con las acciones: en ventanas angostas no se superpone con «Eliminar»
        enabled.VerticalAlignment = VerticalAlignment.Center;
        enabled.Margin = new Thickness(0, 0, 8, 0);
        var actions = Ui.H(8, enabled, delete, test, save);
        DockPanel.SetDock(actions, Dock.Right);
        titleRow.Children.Add(actions);
        var left = Ui.H(12, name, kindBadge);
        left.ClipToBounds = true;
        titleRow.Children.Add(left);

        return new StackPanel { Spacing = 16, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left, Children = { titleRow, resultBox, sections } };
    }

    private static Control Note(MaterialIconKind icon, string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var d = new DockPanel();
        var i = Ui.Icon(icon, 16, Ui.Text2);
        i.Margin = new Thickness(0, 0, 8, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(i, Dock.Left);
        d.Children.Add(i);
        d.Children.Add(t);
        return d;
    }

    private static Grid TwoColumns(params Control[] fields)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        int row = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (i % 2 == 0)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                if (i > 0) g.RowDefinitions.Add(new RowDefinition(new GridLength(14)));
                row = g.RowDefinitions.Count - 1;
            }

            Grid.SetRow(fields[i], row);
            Grid.SetColumn(fields[i], i % 2 == 0 ? 0 : 2);
            g.Children.Add(fields[i]);
        }

        return g;
    }

    private Control BinanceAccount(ConnectionProfile p, List<Action> apply)
    {
        var testnet = new ToggleSwitch { IsChecked = p.Testnet, OnContent = L.T("Testnet (dinero de prueba)"), OffContent = L.T("Cuenta real") };
        var key = new TextBox { Text = p.ApiKey, Watermark = L.T("API key") };
        var secret = new TextBox { PasswordChar = '•', Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? L.T("API secret") : L.F("•••••••• (guardado en {0})",Os.SecretStoreShort) };
        var allowReal = new CheckBox { Content = L.T("Permitir órdenes REALES desde Trading Hub"), IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = L.T("Pedir confirmación antes de cada orden real"), IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Increment = 0.001m, FormatString = "0.#####", Watermark = L.T("sin límite") };
        var warn = Note(MaterialIconKind.AlertOutline, L.T("Con una cuenta real las órdenes mueven dinero de verdad. Por defecto Trading Hub solo opera en testnet; activá las órdenes reales únicamente si sabés lo que hacés."));
        void UpdateEnabled()
        {
            bool real = testnet.IsChecked != true;
            allowReal.IsEnabled = real;
            confirm.IsEnabled = real && allowReal.IsChecked == true;
            maxQty.IsEnabled = real && allowReal.IsChecked == true;
            warn.IsVisible = real;
        }

        testnet.IsCheckedChanged += (_, _) => UpdateEnabled();
        allowReal.IsCheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        apply.Add(() =>
        {
            p.Testnet = testnet.IsChecked == true;
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        var help = new Button { Content = L.T("¿Cómo creo una API key?"), Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        help.Click += (_, _) => Shell.Open(testnet.IsChecked == true
            ? (p.Kind == ConnectionKind.BinanceFutures ? "https://testnet.binancefuture.com" : "https://testnet.binance.vision")
            : "https://www.binance.com/es/my/settings/api-management");

        return Ui.V(14,
            Ui.H2(L.T("Cuenta para operar")),
            Note(MaterialIconKind.ShieldKeyOutline, L.T("Creá una API key solo con permiso de trading (sin retiros). El secret se guarda en ") + (Os.IsWindows ? L.T("el Administrador de credenciales de Windows.") : L.T("el Llavero de macOS."))),
            testnet,
            TwoColumns(Ui.Field(L.T("API key"), key), Ui.Field(L.T("API secret"), secret)),
            help,
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field(L.T("Cantidad máxima por orden (0 = sin límite)"), maxQty),
            warn);
    }

    private static string RithmicCacheFile => Path.Combine(AppPaths.Root, "rithmic-servidores.json");

    private static List<RithmicServer> LoadRithmicCache()
    {
        try
        {
            if (File.Exists(RithmicCacheFile))
                return System.Text.Json.JsonSerializer.Deserialize<List<RithmicServer>>(File.ReadAllText(RithmicCacheFile)) ?? new();
        }
        catch
        {
            // caché dañada: se vuelve a pedir
        }

        return new List<RithmicServer>();
    }

    /// <summary>Rithmic como en ATAS: usuario, contraseña, un solo «Servidor» (sistema. región), cotizaciones agregadas y tiempo de espera.</summary>
    private Control RithmicSection(ConnectionProfile p, List<Action> apply)
    {
        var servers = LoadRithmicCache();
        var search = new TextBox { Watermark = L.T("Buscar servidor (paper, 01, Apex, Frankfurt, Sao Paulo…)") };
        var server = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 420, PlaceholderText = L.T("Elegí el servidor") };
        var info = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var refresh = Ui.IconButton(MaterialIconKind.Refresh, L.T("Volver a pedir la lista a todos los servidores"), () => { }, 16);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        string selSystem = p.RithmicSystem, selUri = p.RithmicUri;

        // servidor a mano (por si Rithmic agrega uno nuevo)
        var customUri = new TextBox { Text = p.RithmicUri, Watermark = "wss://servidor:443" };
        var customSystem = new TextBox { Text = p.RithmicSystem, Watermark = L.T("Sistema (ej. Rithmic 01)") };
        var customToggle = new CheckBox { Content = L.T("Usar un servidor que no está en la lista") };
        var customPanel = TwoColumns(Ui.Field(L.T("Dirección"), customUri), Ui.Field(L.T("Sistema"), customSystem));
        customPanel.IsVisible = false;
        customToggle.IsCheckedChanged += (_, _) => customPanel.IsVisible = customToggle.IsChecked == true;

        void Fill()
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = servers.Where(sv => words.All(w => sv.Display.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            int fastest = servers.Count > 0 ? servers.Min(x => x.LatencyMs) : 0;
            server.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var sv in list)
            {
                var item = new ComboBoxItem
                {
                    Tag = sv,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new DockPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = $"{sv.LatencyMs} ms", Classes = { "faint", "small" }, Foreground = sv.LatencyMs <= fastest * 1.3 ? Ui.Up : null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }.Also(t => DockPanel.SetDock(t, Dock.Right)),
                            new TextBlock { Text = sv.Display, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                };
                server.Items.Add(item);
                if (sv.System == selSystem && sv.Uri == selUri) selected = item;
            }

            if (selected == null && words.Length == 0)
            {
                // lo guardado no está en la lista (o todavía no llegó): se muestra igual
                var cur = new RithmicServer(selSystem, RithmicGateways.NameOf(selUri), selUri, 0);
                selected = new ComboBoxItem { Tag = cur, Content = new TextBlock { Text = cur.Display } };
                server.Items.Insert(0, selected);
            }

            server.SelectedItem = selected;
        }

        var aggNote = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        void UpdateAggregated()
        {
            var known = servers.FirstOrDefault(x => x.System == selSystem && x.Uri == selUri);
            aggNote.Text = known == null ? ""
                : known.Aggregated ? L.T("Este sistema ofrece cotizaciones agregadas.")
                : L.T("Este sistema no ofrece cotizaciones agregadas: se conecta sin ellas (pedirlas igual hace que Rithmic rechace el login).");
        }

        server.SelectionChanged += (_, _) =>
        {
            if (server.SelectedItem is ComboBoxItem { Tag: RithmicServer sv })
            {
                selSystem = sv.System;
                selUri = sv.Uri;
                UpdateAggregated();
            }
        };
        search.TextChanged += (_, _) => Fill();

        async Task DiscoverAsync()
        {
            info.Text = L.T("Consultando los servidores de Rithmic…");
            refresh.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var found = await RithmicDirectory.DiscoverAsync(cts.Token);
                if (found.Count > 0)
                {
                    servers = found;
                    try
                    {
                        Directory.CreateDirectory(AppPaths.Root);
                        File.WriteAllText(RithmicCacheFile, System.Text.Json.JsonSerializer.Serialize(found));
                    }
                    catch
                    {
                        // sin caché no pasa nada
                    }
                }

                Fill();
                UpdateAggregated();
                var best = servers.GroupBy(x => x.Gateway).Select(g => g.First()).OrderBy(x => x.LatencyMs).FirstOrDefault();
                info.Text = servers.Count == 0
                    ? L.T("No respondió ningún servidor (¿sin internet?).")
                    : L.F("{0} combinaciones en {1} servidores. Lo más rápido desde acá: {2} ({3} ms). Colo 75 solo funciona desde el datacenter de la CME.",servers.Count,servers.Select(x => x.Uri).Distinct().Count(),best?.Gateway,best?.LatencyMs);
            }
            catch (Exception ex)
            {
                info.Text = L.T("No se pudo consultar: ") + ex.Message;
            }
            finally
            {
                refresh.IsEnabled = true;
            }
        }

        refresh.Click += async (_, _) => await DiscoverAsync();
        var serverRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        serverRow.Children.Add(refresh);
        serverRow.Children.Add(server);

        var user = new TextBox { Text = p.RithmicUser, Watermark = L.T("usuario (Login)") };
        var pass = new TextBox { PasswordChar = '•', RevealPassword = false, Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? L.T("contraseña") : L.F("•••••••• (guardada en {0})",Os.SecretStoreShort) };
        var reveal = new ToggleButton { Content = Ui.Icon(MaterialIconKind.EyeOutline, 15), Padding = new Thickness(6, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(reveal, L.T("Mostrar lo que escribís"));
        reveal.IsCheckedChanged += (_, _) => pass.RevealPassword = reveal.IsChecked == true;
        var passRow = new DockPanel();
        DockPanel.SetDock(reveal, Dock.Right);
        reveal.Margin = new Thickness(6, 0, 0, 0);
        passRow.Children.Add(reveal);
        passRow.Children.Add(pass);

        var aggregated = new CheckBox { Content = L.T("Cotizaciones agregadas"), IsChecked = p.RithmicAggregatedQuotes };
        ToolTip.SetTip(aggregated, L.T("Rithmic junta las actualizaciones del libro que llegan en el mismo instante (menos mensajes, mismo precio). Igual que la opción de ATAS."));
        var timeout = new NumericUpDown { Value = p.RithmicTimeoutMs, Minimum = 2000, Maximum = 120000, Increment = 1000, FormatString = "0", Width = 140 };
        var app = new TextBox { Text = p.RithmicAppName, Watermark = "TradingHub" };
        var products = new TextBox { Text = string.Join(", ", p.RithmicProducts), Watermark = L.T("ES, NQ, MES, MNQ, CL…") };

        // órdenes: las cuentas de práctica operan siempre; las reales (Rithmic 01, prop firms) solo si se habilita acá
        var allowReal = new CheckBox { Content = L.T("Permitir órdenes en cuentas reales (Rithmic 01, prop firms…)"), IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = L.T("Confirmar cada orden real"), IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };

        apply.Add(() =>
        {
            if (customToggle.IsChecked == true && !string.IsNullOrWhiteSpace(customUri.Text))
            {
                p.RithmicUri = customUri.Text.Trim();
                if (!string.IsNullOrWhiteSpace(customSystem.Text)) p.RithmicSystem = customSystem.Text.Trim();
            }
            else
            {
                p.RithmicUri = selUri;
                p.RithmicSystem = selSystem;
            }

            p.RithmicUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.RithmicAggregatedQuotes = aggregated.IsChecked == true;
            p.RithmicTimeoutMs = (int)(timeout.Value ?? 10000);
            p.RithmicAppName = string.IsNullOrWhiteSpace(app.Text) ? "TradingHub" : app.Text.Trim();
            p.RithmicProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked == true;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        Fill();
        Dispatcher.UIThread.Post(() => _ = DiscoverAsync(), DispatcherPriority.Background);
        return Ui.V(14,
            Ui.H2(p.Kind == ConnectionKind.AmpFutures ? L.T("Cuenta AMP Futures") : L.T("Cuenta Rithmic")),
            Note(MaterialIconKind.InformationOutline, p.Kind == ConnectionKind.AmpFutures
                ? L.T("AMP Futures opera sobre Rithmic: AMP te manda usuario y contraseña de Rithmic. Para la demo elegí «Rithmic Paper Trading» y para la cuenta real «Rithmic 01», en el servidor más cercano. Trading Hub se conecta directo por R|Protocol (no hace falta tener R|Trader Pro abierto).")
                : L.T("Conexión directa por R|Protocol, la API oficial de Rithmic (no hace falta R|Trader Pro). Elegí el servidor como en ATAS: sistema (Paper Trading, Rithmic 01, tu prop firm) y región.")),
            Ui.H3(L.T("Conexión")),
            TwoColumns(Ui.Field(L.T("Usuario"), user), Ui.Field(L.T("Contraseña"), passRow)),
            Ui.Field(L.T("Servidor"), Ui.V(8, search, serverRow, info)),
            customToggle,
            customPanel,
            Ui.H3(L.T("Datos de mercado")),
            TwoColumns(Ui.V(4, aggregated, aggNote), Ui.Field(L.T("Tiempo de espera de conexión (ms)"), timeout)),
            Ui.H3(L.T("Operar")),
            Note(MaterialIconKind.ShieldCheckOutline, L.T("Tus cuentas de Rithmic aparecen en el Chart Trader («Cuenta») cuando la conexión está activa. Las de práctica (Paper Trading) operan directo; las reales necesitan esta autorización y piden confirmación.")),
            allowReal,
            TwoColumns(confirm, Ui.Field(L.T("Cantidad máxima por orden (0 = sin límite)"), maxQty)),
            Ui.H3(L.T("Avanzado")),
            TwoColumns(Ui.Field(L.T("Nombre de la aplicación"), app, L.T("Algunos sistemas exigen un nombre de app aprobado.")), Ui.Field(L.T("Productos para el buscador"), products, L.T("Se busca el contrato del mes de cada producto (ej. ES → ESZ6)."))));
    }

    /// <summary>Campo de secreto: no muestra lo guardado, solo avisa que está en el Llavero (o en Windows).</summary>
    private TextBox SecretBox(ConnectionProfile p, string? name, string watermark) =>
        new() { PasswordChar = '•', Watermark = string.IsNullOrEmpty(name == null ? _svc.Connections.GetSecret(p) : _svc.Connections.GetSecret(p, name)) ? watermark : L.F("•••••••• (guardado en {0})",Os.SecretStoreShort) };

    private static Button Link(string text, string url)
    {
        var b = new Button { Content = text, Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        b.Click += (_, _) => Shell.Open(url);
        ToolTip.SetTip(b, url);
        return b;
    }

    /// <summary>Tradovate: usuario, contraseña y API key (CID + secret); demo o real; productos.</summary>
    private Control TradovateSection(ConnectionProfile p, List<Action> apply)
    {
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = L.T("Demo (simulada de Tradovate)"), OffContent = L.T("Cuenta real") };
        var user = new TextBox { Text = p.TradovateUser, Watermark = L.T("usuario de Tradovate") };
        var pass = SecretBox(p, null, L.T("contraseña"));
        var cid = new TextBox { Text = p.TradovateCid, Watermark = L.T("CID (número)") };
        var sec = SecretBox(p, "apisecret", L.T("secret de la API key"));
        var products = new TextBox { Text = string.Join(", ", p.TradovateProducts), Watermark = L.T("ES, NQ, MES, MNQ, CL…") };
        var allowReal = new CheckBox { Content = L.T("Permitir órdenes en cuentas reales de Tradovate"), IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = L.T("Pedir confirmación antes de cada orden real"), IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };
        void Update()
        {
            allowReal.IsEnabled = demo.IsChecked != true;
            confirm.IsEnabled = demo.IsChecked != true && allowReal.IsChecked == true;
        }

        allowReal.IsCheckedChanged += (_, _) => Update();
        demo.IsCheckedChanged += (_, _) => Update();
        Update();
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.TradovateUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.TradovateCid = (cid.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sec.Text)) _svc.Connections.SetSecret(p, "apisecret", sec.Text.Trim());
            sec.Text = "";
            p.TradovateProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });
        return Ui.V(14,
            Ui.H2(L.T("Cuenta Tradovate")),
            Note(MaterialIconKind.InformationOutline, L.T("Conexión directa a la API oficial de Tradovate. Tus cuentas aparecen en el Chart Trader y en Cuenta, y se pueden operar desde cualquier gráfico de futuros de CME (también desde uno de Rithmic). Las demo operan directo; las reales necesitan la autorización de abajo.")),
            Note(MaterialIconKind.AlertOutline, L.T("Datos de mercado por API: Tradovate solo los da si tu cuenta tiene habilitados los datos de CME para API (una licencia aparte de la CME). Sin eso igual podés operar, con los gráficos de Rithmic u otra conexión.")),
            demo,
            TwoColumns(Ui.Field(L.T("Usuario"), user), Ui.Field(L.T("Contraseña"), pass)),
            TwoColumns(Ui.Field(L.T("CID de la API key"), cid), Ui.Field(L.T("Secret de la API key"), sec)),
            Ui.H(16, Link(L.T("¿Dónde saco la API key? (Settings → API Access)"), "https://trader.tradovate.com/"), Link(L.T("Documentación de la API"), "https://partner.tradovate.com")),
            Ui.Field(L.T("Productos para el buscador"), products, L.T("Se usa el contrato del mes de cada uno (ES → ESZ6).")),
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field(L.T("Cantidad máxima por orden (0 = sin límite)"), maxQty),
            Note(MaterialIconKind.ShieldCheckOutline, L.T("Tradovate deja una sola sesión de datos por usuario: si abrís Tradovate Trader con el mismo usuario, puede cortar la de Trading Hub (y al revés).")));
    }

    /// <summary>cTrader: aplicación de la Open API (Client ID + Secret) y token de la cuenta (OAuth o Playground).</summary>
    private Control CTraderSection(ConnectionProfile p, List<Action> apply)
    {
        const string redirect = "http://127.0.0.1:53682/ctrader";
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = L.T("Cuenta demo"), OffContent = L.T("Cuenta real") };
        var clientId = new TextBox { Text = p.CTraderClientId, Watermark = L.T("Client ID de tu aplicación") };
        var secret = SecretBox(p, null, L.T("Secret de tu aplicación"));
        var token = SecretBox(p, "token", L.T("token de acceso (o tocá «Conectar con cTrader»)"));
        var login = new NumericUpDown { Value = p.CTraderLogin, Minimum = 0, Maximum = 999999999999, FormatString = "0", Watermark = L.T("la primera"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var oauthStatus = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var connect = Ui.Button(L.T("Conectar con cTrader"), MaterialIconKind.Login, () => { }, "primary");
        connect.Click += async (_, _) =>
        {
            string id = (clientId.Text ?? "").Trim();
            string sec = !string.IsNullOrWhiteSpace(secret.Text) ? secret.Text.Trim() : _svc.Connections.GetSecret(p);
            if (id.Length == 0 || sec.Length == 0)
            {
                oauthStatus.Text = L.T("Primero completá el Client ID y el Secret de tu aplicación.");
                return;
            }

            connect.IsEnabled = false;
            oauthStatus.Text = L.T("Se abrió el navegador: entrá con tu cTrader ID y autorizá a Trading Hub. Esperando…");
            try
            {
                var (access, refresh) = await CTraderOAuthAsync(id, sec, redirect);
                p.CTraderClientId = id;
                _svc.Connections.SetSecret(p, sec);
                _svc.Connections.SetSecret(p, "token", access);
                if (refresh.Length > 0) _svc.Connections.SetSecret(p, "refresh", refresh);
                _svc.Connections.Save();
                _svc.Connections.Invalidate(p);
                token.Watermark = L.F("•••••••• (guardado en {0})",Os.SecretStoreShort);
                oauthStatus.Text = L.T("Listo: Trading Hub quedó autorizado. Tocá «Probar conexión».");
            }
            catch (Exception ex)
            {
                oauthStatus.Text = L.T("No se pudo autorizar: ") + ex.Message;
            }
            finally
            {
                connect.IsEnabled = true;
            }
        };
        var copy = Ui.Button(L.T("Copiar"), MaterialIconKind.ContentCopy, () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(redirect);
        }, "ghost");
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.CTraderClientId = (clientId.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, "token", token.Text.Trim());
            token.Text = "";
            p.CTraderLogin = (long)(login.Value ?? 0);
        });
        return Ui.V(14,
            Ui.H2(L.T("cTrader Open API")),
            Note(MaterialIconKind.InformationOutline, L.T("Gratis con cualquier broker que tenga cTrader (IC Markets, Pepperstone, FxPro, Fusion Markets…) y una cuenta demo: CFDs de índices (US100/NAS100, US500, GER40…), forex, oro y petróleo, con historia tick a tick de años.")),
            Ui.H3(L.T("1 · Tu aplicación (una sola vez)")),
            Note(MaterialIconKind.ShieldKeyOutline, L.T("Creá una aplicación en openapi.ctrader.com (entrás con tu cTrader ID) y agregá esta dirección de redirección. Después copiá acá el Client ID y el Secret.")),
            Ui.H(8, new TextBlock { Text = redirect, FontFamily = Ui.MonoFont, VerticalAlignment = VerticalAlignment.Center }, copy, Link(L.T("Abrir openapi.ctrader.com"), "https://openapi.ctrader.com/apps")),
            TwoColumns(Ui.Field(L.T("Client ID"), clientId), Ui.Field(L.T("Secret"), secret)),
            Ui.H3(L.T("2 · Autorizar tu cuenta")),
            Ui.H(10, connect, demo),
            oauthStatus,
            Ui.Field(L.T("O pegá un token de acceso (el del «Playground» de tu aplicación)"), token),
            Ui.Field(L.T("Número de cuenta (login de cTrader)"), login, L.T("Si el token tiene varias cuentas, la que querés usar. 0 = la primera demo/real.")));
    }

    /// <summary>OAuth de cTrader: abre el navegador y espera el código en la dirección local de redirección.</summary>
    private static async Task<(string Access, string Refresh)> CTraderOAuthAsync(string clientId, string secret, string redirect)
    {
        var uri = new Uri(redirect);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{uri.Port}/");
        listener.Start();
        Shell.Open(TradingHub.Feeds.CTrader.CTraderAuth.AuthorizeUrl(clientId, redirect));
        var wait = listener.GetContextAsync();
        if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMinutes(5))) != wait) throw new TimeoutException(L.T("no llegó la autorización (5 minutos)"));
        var ctx = await wait;
        string? code = ctx.Request.QueryString["code"];
        string? error = ctx.Request.QueryString["error"] ?? ctx.Request.QueryString["error_description"];
        var html = System.Text.Encoding.UTF8.GetBytes(code != null
            ? L.T("<html><body style='font-family:-apple-system;padding:40px'><h2>Listo</h2><p>Trading Hub quedó autorizado. Ya podés cerrar esta pestaña y volver a Trading Hub.</p></body></html>")
            : L.T("<html><body style='font-family:-apple-system;padding:40px'><h2>No se autorizó</h2><p>Volvé a Trading Hub e intentá de nuevo.</p></body></html>"));
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
        if (code == null) throw new InvalidOperationException(error ?? L.T("cTrader no mandó el código"));
        var (access, refresh, _) = await TradingHub.Feeds.CTrader.CTraderAuth.ExchangeAsync(clientId, secret, code, redirect, CancellationToken.None);
        return (access, refresh);
    }

    /// <summary>OANDA: token personal y cuenta de práctica (gratis) o real.</summary>
    private Control OandaSection(ConnectionProfile p, List<Action> apply)
    {
        var practice = new ToggleSwitch { IsChecked = p.Testnet, OnContent = L.T("Cuenta de práctica (gratis)"), OffContent = L.T("Cuenta real") };
        var token = SecretBox(p, null, L.T("token de la API (Manage API Access)"));
        var account = new TextBox { Text = p.OandaAccount, Watermark = L.T("101-001-… (vacío = la primera)") };
        apply.Add(() =>
        {
            p.Testnet = practice.IsChecked == true;
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, token.Text.Trim());
            token.Text = "";
            p.OandaAccount = (account.Text ?? "").Trim();
        });
        return Ui.V(14,
            Ui.H2(L.T("OANDA (API v20)")),
            Note(MaterialIconKind.InformationOutline, L.T("Gratis con una cuenta de práctica: CFDs de índices (NAS100_USD = Nasdaq 100, SPX500_USD = S&P 500, US30_USD, DE30_EUR…), oro, petróleo y forex, con años de historia en velas de 5 segundos.")),
            practice,
            TwoColumns(Ui.Field(L.T("Token"), token), Ui.Field(L.T("Cuenta"), account)),
            Ui.H(16, Link(L.T("Crear cuenta de práctica"), "https://www.oanda.com/demo-account/"), Link(L.T("Generar el token (Manage API Access)"), "https://www.oanda.com/demo-account/tpa/personal_token")),
            Note(MaterialIconKind.ShieldKeyOutline, L.F("El token se guarda en {0}. Trading Hub lo usa solo para datos (no manda órdenes a OANDA).",Os.SecretStore)));
    }

    /// <summary>Alpaca: claves de la cuenta de práctica (historia SIP desde 2016 gratis).</summary>
    private Control AlpacaSection(ConnectionProfile p, List<Action> apply)
    {
        var key = new TextBox { Text = p.ApiKey, Watermark = L.T("API key ID") };
        var secret = SecretBox(p, null, L.T("secret key"));
        var live = new ComboBox { MinWidth = 260 };
        live.Items.Add(new ComboBoxItem { Content = L.T("IEX (gratis, una bolsa)"), Tag = "iex" });
        live.Items.Add(new ComboBoxItem { Content = L.T("SIP (todas las bolsas, plan pago)"), Tag = "sip" });
        live.SelectedItem = live.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == p.AlpacaLiveFeed) ?? live.Items[0];
        apply.Add(() =>
        {
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AlpacaLiveFeed = (live.SelectedItem as ComboBoxItem)?.Tag as string ?? "iex";
        });
        return Ui.V(14,
            Ui.H2(L.T("Alpaca (acciones y ETFs de EE.UU.)")),
            Note(MaterialIconKind.InformationOutline, L.T("Con la cuenta de práctica (gratis, desde cualquier país) la historia tick a tick de TODAS las bolsas de EE.UU. desde 2016 es gratis (salvo los últimos 15 minutos). SPY = S&P 500, QQQ = Nasdaq 100, IWM = Russell 2000, DIA = Dow: operaciones reales para estudiar los índices.")),
            TwoColumns(Ui.Field(L.T("API key ID"), key), Ui.Field(L.T("Secret key"), secret)),
            Ui.Field(L.T("Datos en vivo"), live),
            Ui.H(16, Link(L.T("Crear cuenta de práctica"), "https://app.alpaca.markets/signup"), Link(L.T("Dónde están las claves"), "https://app.alpaca.markets/paper/dashboard/overview")),
            Note(MaterialIconKind.ShieldKeyOutline, L.T("Trading Hub usa las claves solo para datos (no manda órdenes a Alpaca).")));
    }

    /// <summary>Databento: API key (historia de futuros de CME desde 2010, pago por uso con créditos de regalo).</summary>
    private Control DatabentoSection(ConnectionProfile p, List<Action> apply)
    {
        var key = SecretBox(p, null, L.T("API key (db-…)"));
        apply.Add(() =>
        {
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
        });
        return Ui.V(14,
            Ui.H2(L.T("Databento")),
            Note(MaterialIconKind.InformationOutline, L.T("Historia tick a tick de los futuros de CME (NQ, ES, MNQ, MES, CL, GC…) desde 2010, con el lado agresor real de cada operación: lo que Rithmic no guarda. Es pago por uso; las cuentas nuevas traen US$ 125 de créditos gratis.")),
            Ui.Field(L.T("API key"), key),
            Ui.H(16, Link(L.T("Crear cuenta (con créditos de regalo)"), "https://databento.com/signup"), Link(L.T("Precios"), "https://databento.com/pricing")),
            Note(MaterialIconKind.ShieldCheckOutline, L.T("Se usa desde Datos → «Futuros de CME con Databento»: Trading Hub calcula el costo antes y solo baja si confirmás. Se guarda aparte («Databento» en Datos) y en los backtests elegís solo Databento, solo Rithmic o las dos combinadas (Rithmic primero y Databento completa lo que falta).")));
    }

    private Control IbSection(ConnectionProfile p, List<Action> apply)
    {
        var host = new TextBox { Text = p.IbHost };
        var port = new NumericUpDown { Value = p.IbPort, Minimum = 1, Maximum = 65535, FormatString = "0" };
        var client = new NumericUpDown { Value = p.IbClientId, Minimum = 0, Maximum = 999, FormatString = "0" };
        apply.Add(() =>
        {
            p.IbHost = string.IsNullOrWhiteSpace(host.Text) ? "127.0.0.1" : host.Text.Trim();
            p.IbPort = (int)(port.Value ?? 7497);
            p.IbClientId = (int)(client.Value ?? 17);
        });
        return Ui.V(14,
            Ui.H2(L.T("Interactive Brokers (TWS / IB Gateway)")),
            Note(MaterialIconKind.InformationOutline, L.T("Abrí TWS o IB Gateway y habilitá la API (Configure → API → Settings). Puertos habituales: 7497 TWS paper, 7496 TWS real, 4002 Gateway paper. Por ahora Trading Hub solo verifica la conexión.")),
            TwoColumns(Ui.Field(L.T("Host"), host), Ui.Field(L.T("Puerto"), port)),
            Ui.Field(L.T("Client ID"), client));
    }
}
