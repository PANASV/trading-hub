using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Windows;

namespace TradingHub.App.Views;

/// <summary>Ajustes generales de la plataforma.</summary>
internal sealed class SettingsView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;

    public SettingsView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        // el margen va en el contenido: con Padding en el ScrollViewer el final quedaba tapado y no se podía bajar
        var content = Build();
        content.Margin = new Thickness(28, 24, 28, 32);
        Content = new ScrollViewer { Content = content };
    }

    public void OnShown()
    {
    }

    private int _tick;

    public void OnTimer()
    {
        if (IsVisible && ++_tick % 8 == 0) RefreshSleep(); // ~2 s
    }

    private Control Build()
    {
        var s = _svc.Settings;

        // zona horaria: las habituales primero
        var tz = new ComboBox { MinWidth = 320 };
        var common = new[] { "America/Argentina/Buenos_Aires", "America/New_York", "America/Chicago", "Europe/London", "Europe/Madrid", "UTC" };
        tz.Items.Add(new ComboBoxItem { Content = L.F("Hora de esta {0} ({1})",Os.Device,TimeZoneInfo.Local.Id), Tag = "" });
        foreach (var id in common)
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z)) tz.Items.Add(new ComboBoxItem { Content = Label(z), Tag = z.Id });
        tz.Items.Add(new Separator());
        foreach (var z in TimeZoneInfo.GetSystemTimeZones().Where(z => !common.Contains(z.Id)).OrderBy(z => z.BaseUtcOffset).ThenBy(z => z.Id))
            tz.Items.Add(new ComboBoxItem { Content = Label(z), Tag = z.Id });
        tz.SelectedItem = tz.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == (s.TimeZoneId ?? "")) ?? tz.Items[0];
        tz.SelectionChanged += (_, _) =>
        {
            if (tz.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            s.TimeZoneId = id == "" ? null : id;
            s.Save();
            if (TopLevel.GetTopLevel(this) is MainWindow w)
            {
                foreach (var t in w.Trading.Tabs)
                {
                    if (t.Model == null) continue;
                    t.Model.Settings.TimeZoneId = s.TimeZoneId;
                    t.Model.Invalidate();
                }
            }

            _status(L.T("Zona horaria actualizada"));
        };

        var confirm = Switch(s.ConfirmRealOrders, v => s.ConfirmRealOrders = v);
        var sounds = Switch(s.SoundAlerts, v => s.SoundAlerts = v);
        var record = Switch(s.RecordLiveData, v =>
        {
            s.RecordLiveData = v;
            _svc.Hub.RecordLive = v;
        });

        var appearance = new Ui.Segmented<string>(new[] { (AppTheme.Dark, L.T("Oscuro"), (string?)L.T("Negro")), (AppTheme.Light, L.T("Claro"), L.T("Blanco")) });
        appearance.Select(AppTheme.IsLight ? AppTheme.Light : AppTheme.Dark);
        appearance.SelectionChanged += mode =>
        {
            s.ThemeMode = mode;
            s.Save();
            AppTheme.Apply(mode);
        };
        var general = Section(L.T("General"), MaterialIconKind.TuneVariant,
            Setting(L.T("Idioma"), L.T("Idioma de toda la interfaz. Se aplica al volver a abrir Trading Hub."), BuildLanguage()),
            Setting(L.T("Apariencia"), L.T("Negro o blanco en toda la plataforma (también con el botón de sol/luna arriba). Los colores propios de cada gráfico se mantienen."), appearance),
            Setting(L.T("Zona horaria de los gráficos"), L.T("Horas del eje de tiempo, la cinta y las sesiones."), tz),
            Setting(L.T("Sonidos de alerta"), L.T("Alertas de precio, Big Trades y avisos de indicadores."), sounds)
            );

        var trading = Section(L.T("Operar"), MaterialIconKind.SwapVertical,
            Setting(L.T("Confirmar órdenes reales"), L.T("Pide confirmación antes de enviar órdenes a cuentas reales (además de lo configurado en cada conexión)."), confirm),
            Setting(L.T("Cuenta simulada"), L.F("Saldo inicial {0:N2}. Las órdenes del simulador se llenan con la cola del libro y el tape real.",_svc.Portfolio.BeginBalance), new TextBlock()));

        var data = Section(L.T("Datos"), MaterialIconKind.DatabaseOutline,
            Setting(L.T("Grabar lo que llega en vivo"), L.T("Guarda cada operación recibida para reabrir gráficos al instante, hacer replay y backtests."), record),
            Setting(L.T("Carpeta de datos"), AppPaths.Root, Ui.Button(L.T("Abrir"), MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Root))),
            Setting(L.T("Indicadores propios (C#)"), AppPaths.UserIndicators, Ui.Button(L.T("Abrir"), MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.UserIndicators))),
            Setting(L.T("Estrategias (C#)"), AppPaths.Strategies, Ui.Button(L.T("Abrir"), MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Strategies))));

        var sleep = BuildSleep();
        var liveStart = BuildLiveStart();
        var hotkeys = BuildHotkeys();

        var logo = new Image { Source = AppIcon.Bitmap, Width = 56, Height = 56 };
        var about = Ui.Card(Ui.H(16, logo, Ui.V(4,
            new TextBlock { Text = TradingHub.Common.Product.Name, Classes = { "h1" } },
            Ui.Muted(L.F("Versión {0} · .NET {1} · Avalonia + SkiaSharp",typeof(App).Assembly.GetName().Version?.ToString(3),Environment.Version)),
            Ui.Faint(L.T("Plataforma personal de order flow: footprint, DOM, cinta, indicadores en C#, estrategias y backtesting.")),
            Creator())));

        return new StackPanel
        {
            Spacing = 18,
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { Ui.V(4, Ui.H1(L.T("Ajustes")), Ui.Muted(L.T("Preferencias generales de Trading Hub"))), general, trading, liveStart, sleep, hotkeys, data, about },
        };
    }

    // ===================================================================== idioma

    private Control BuildLanguage()
    {
        var s = _svc.Settings;
        var box = new ComboBox { MinWidth = 240 };
        box.Items.Add(new ComboBoxItem { Content = L.F("Automático (el del sistema: {0})", TradingHub.Common.Loc.Languages.First(x => x.Code == TradingHub.Common.Loc.Resolve(TradingHub.Common.Loc.Auto)).Name), Tag = TradingHub.Common.Loc.Auto });
        foreach (var (code, name) in TradingHub.Common.Loc.Languages) box.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == s.Language) ?? box.Items[0];
        var restart = Ui.Button(L.T("Reiniciar ahora"), MaterialIconKind.Restart, Relaunch);
        restart.IsVisible = false;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: string code }) return;
            s.Language = code;
            s.Save();
            restart.IsVisible = TradingHub.Common.Loc.Resolve(code) != TradingHub.Common.Loc.Language;
            _status(L.T("Idioma guardado: se aplica al volver a abrir Trading Hub"));
        };
        return Ui.H(8, box, restart);
    }

    /// <summary>Cierra Trading Hub y la vuelve a abrir (para cambiar el idioma).</summary>
    private static void Relaunch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            if (OperatingSystem.IsMacOS())
            {
                // …/Trading Hub.app/Contents/MacOS/TradingHub → se abre el paquete cuando esta ya se cerró
                var bundle = exe.Contains(".app/Contents/MacOS/", StringComparison.Ordinal) ? exe[..(exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal) + 4)] : null;
                var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(bundle != null ? "sleep 2; open -n \"$0\"" : "sleep 2; \"$0\" &");
                psi.ArgumentList.Add(bundle ?? exe);
                System.Diagnostics.Process.Start(psi);
            }
            else if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add($"timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"");
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception)
        {
            // si no se puede volver a abrir sola, se cierra igual y el usuario la abre
        }

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    // ===================================================================== estrategias en vivo al abrir

    private Control BuildLiveStart()
    {
        var s = _svc.Settings;
        var auto = Switch(s.AutoResumeLive, v => s.AutoResumeLive = v);
        var real = Switch(s.AutoResumeLiveReal, v => s.AutoResumeLiveReal = v);
        real.IsEnabled = s.AutoResumeLive;
        auto.IsCheckedChanged += (_, _) => real.IsEnabled = auto.IsChecked == true;
        var smart = Switch(s.SmartContracts, v => s.SmartContracts = v);
        var menuBar = Switch(s.KeepInMenuBar, v =>
        {
            s.KeepInMenuBar = v;
            Services.MenuBar.Current?.Refresh();
        });
        var stay = Os.IsWindows
            ? Setting(L.T("Seguir en la bandeja del sistema al cerrar la ventana"), L.T("Cerrar la ventana no cierra Trading Hub: queda abajo a la derecha, en la bandeja del sistema (junto al reloj; si no lo ves, tocá la flechita ^), con las estrategias en vivo trabajando. Tocando su ícono se abre un panel con las estrategias y las posiciones en tiempo real; con clic derecho, un menú. Para cerrarla del todo: Alt+F4 o «Salir de Trading Hub» en ese panel."), menuBar)
            : Setting(L.T("Seguir en la barra de menú al cerrar la ventana"), L.T("Cerrar la ventana no cierra Trading Hub: queda arriba a la derecha, en la barra de menú, con las estrategias en vivo trabajando. Tocando su ícono se abre un panel con las estrategias y las posiciones en tiempo real. Para cerrarla del todo: ⌘Q o «Salir de Trading Hub» en ese panel."), menuBar);
        return Section(L.T("Estrategias en vivo"), MaterialIconKind.PlayCircleOutline,
            stay,
            Setting(L.T("Reanudarlas solas al abrir Trading Hub"), L.F("Las que estaban corriendo cuando se cerró Trading Hub (o se apagó la {0}) vuelven a arrancar solas unos segundos después de abrir, con sus mismos parámetros, tamaño y límites; las pausadas vuelven pausadas. Apagado: esperan que las reanudes desde En vivo.",Os.Device), auto),
            Setting(L.T("También las de cuentas reales"), L.T("Apagado (recomendado): las que operan dinero real esperan que las reanudes vos. Encendido: arrancan solas como las demás."), real),
            Setting(L.T("Elegir el contrato solo (minis o micros)"), L.T("Si el tamaño de una estrategia de futuros pide una fracción del mini (por ejemplo 1 NQ × 0,5), las órdenes van al micro con la misma exposición (5 MNQ), mirando el mismo gráfico. Con minis enteros usa el mini. Con una posición abierta sigue en el mismo contrato y cambia cuando queda plana."), smart));
    }

    // ===================================================================== modo sin dormir

    private readonly TextBlock _sleepState = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    /// <summary>Quién lo hizo, con enlace a su perfil.</summary>
    private static Control Creator()
    {
        var handle = new TextBlock
        {
            Text = "@ralphtradesz",
            Foreground = Ui.Accent,
            FontWeight = FontWeight.SemiBold,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        handle.PointerPressed += (_, _) => Shell.OpenUrl("https://x.com/ralphtradesz");
        ToolTip.SetTip(handle, "x.com/ralphtradesz");
        return Ui.H(6, new TextBlock { Text = L.T("Creado por"), Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center }, handle);
    }

    private Control BuildSleep()
    {
        var s = _svc.Settings;
        var awake = Switch(s.KeepAwake, v => s.KeepAwake = v);
        if (!SleepGuard.SupportsLidClosed)
        {
            // Windows: la tapa cerrada la maneja la configuración de energía (Trading Hub no la puede evitar): se explica cómo
            var powerOptions = Ui.Button(L.T("Opciones de energía"), MaterialIconKind.Power, Shell.OpenWindowsPowerOptions);
            RefreshSleep();
            return Section(L.T("Modo sin dormir"), MaterialIconKind.SleepOff,
                Setting(L.T("Mantener la PC despierta"), L.T("Mientras haya estrategias en vivo o agentes de Claude trabajando, la PC no se suspende por inactividad (la pantalla sí se puede apagar). Sin contraseña."), awake),
                Setting(L.T("Con la tapa cerrada"), L.T("Al cerrar la tapa de una notebook, Windows hace lo que diga su configuración de energía (casi siempre, suspenderse) y Trading Hub no lo puede evitar. Para que siga funcionando así: Opciones de energía → «Elegir el comportamiento del cierre de la tapa» → «No hacer nada», mejor solo con el cargador conectado. Con la tapa cerrada la notebook no ventila igual: no la guardes en un bolso así."), powerOptions),
                _sleepState);
        }

        var power = Switch(s.LidClosedOnlyOnPower, v => s.LidClosedOnlyOnPower = v);
        var lid = Switch(s.KeepAwakeLidClosed, v =>
        {
            s.KeepAwakeLidClosed = v;
            _status(v ? L.T("Tapa cerrada: cuando haya algo corriendo, macOS te pide la contraseña una vez (se deshace solo al cerrar Trading Hub)") : L.T("Tapa cerrada desactivado: al cerrarla, la Mac se duerme como siempre"));
        });
        power.IsEnabled = s.KeepAwakeLidClosed;
        lid.IsCheckedChanged += (_, _) => power.IsEnabled = lid.IsChecked == true;
        RefreshSleep();
        return Section(L.T("Modo sin dormir"), MaterialIconKind.SleepOff,
            Setting(L.T("Mantener la Mac despierta"), L.T("Mientras haya estrategias en vivo o agentes de Claude trabajando, la Mac no se duerme por inactividad (la pantalla sí se puede apagar). Sin contraseña."), awake),
            Setting(L.T("Seguir con la tapa cerrada"), L.T("Al cerrar la tapa macOS duerme todo. Con esto activado, cuando haya algo corriendo Trading Hub te pide la contraseña de administrador una vez y la Mac sigue funcionando con la tapa cerrada mientras Trading Hub esté abierto. Se deshace solo al cerrar Trading Hub (aunque se cierre de golpe). Con la tapa cerrada la Mac no ventila igual: no la guardes en un bolso así."), lid),
            Setting(L.T("Solo con el cargador conectado"), L.T("Con la batería, la Mac se duerme al cerrar la tapa aunque esté activado (recomendado)."), power),
            _sleepState);
    }

    private void RefreshSleep()
    {
        var g = _svc.Sleep;
        string reason = _svc.AwakeReason() ?? L.T("no hay nada corriendo");
        _sleepState.Text = (g.Awake ? L.F("Ahora: despierta ({0}).",g.Reason) : L.F("Ahora: puede dormir ({0}).",reason)) +
                           (!SleepGuard.SupportsLidClosed ? "" : g.LidHelperRunning ? L.T(" Tapa cerrada: activo.") : _svc.Settings.KeepAwakeLidClosed ? L.T(" Tapa cerrada: se activa cuando haya algo corriendo.") : "") +
                           (g.LidError != null ? " " + g.LidError : "");
    }

    // ===================================================================== atajos de teclado

    private readonly Dictionary<string, Button> _hotkeyButtons = new();

    private Control BuildHotkeys()
    {
        var s = _svc.Settings;
        var list = new StackPanel { Spacing = 6 };
        foreach (var group in Hotkeys.All.GroupBy(h => h.Group))
        {
            list.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "h3" }, Margin = new Thickness(0, list.Children.Count > 0 ? 12 : 0, 0, 2) });
            foreach (var h in group)
            {
                var def = h;
                var key = new Button { MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 4) };
                ToolTip.SetTip(key, Os.IsWindows ? L.T("Hacé clic y presioná la combinación. Esc cancela · Retroceso lo deja sin asignar.") : L.T("Hacé clic y presioná la combinación. Esc cancela · ⌫ lo deja sin asignar."));
                _hotkeyButtons[def.Id] = key;
                bool capturing = false;
                key.Click += (_, _) =>
                {
                    capturing = true;
                    key.Content = L.T("Presioná una tecla…");
                    key.Classes.Add("primary");
                    key.Focus();
                };
                key.LostFocus += (_, _) =>
                {
                    if (!capturing) return;
                    capturing = false;
                    key.Classes.Remove("primary");
                    RefreshHotkey(def.Id);
                };
                key.AddHandler(KeyDownEvent, (_, e) =>
                {
                    if (!capturing) return;
                    e.Handled = true;
                    if (e.Key == Avalonia.Input.Key.Escape)
                    {
                        capturing = false;
                        key.Classes.Remove("primary");
                        RefreshHotkey(def.Id);
                        return;
                    }

                    if (e.Key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete)
                    {
                        s.Hotkeys[def.Id] = "";
                    }
                    else
                    {
                        var gesture = Hotkeys.FromKey(e.Key, e.KeyModifiers);
                        if (gesture == null) return; // solo un modificador: sigue esperando
                        // si otra acción ya usaba ese atajo, se le saca
                        foreach (var other in Hotkeys.All.Where(o => o.Id != def.Id && Hotkeys.GestureOf(s, o.Id) == gesture))
                        {
                            s.Hotkeys[other.Id] = "";
                            RefreshHotkey(other.Id);
                            _status(L.F("{0} estaba en «{1}»: se pasó a «{2}»",Hotkeys.Pretty(gesture),other.Name,def.Name));
                        }

                        s.Hotkeys[def.Id] = gesture;
                    }

                    capturing = false;
                    key.Classes.Remove("primary");
                    s.Save();
                    RefreshHotkey(def.Id);
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                RefreshHotkey(def.Id);

                var row = new DockPanel();
                DockPanel.SetDock(key, Dock.Right);
                row.Children.Add(key);
                row.Children.Add(new TextBlock { Text = def.Name, VerticalAlignment = VerticalAlignment.Center });
                list.Children.Add(row);
            }
        }

        var reset = Ui.Button(L.T("Restablecer los de fábrica"), MaterialIconKind.Restore, () =>
        {
            s.Hotkeys.Clear();
            s.Save();
            foreach (var h in Hotkeys.All) RefreshHotkey(h.Id);
            _status(L.T("Atajos restablecidos"));
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        var note = new TextBlock
        {
            Text = L.T("Funcionan en Trading y en las ventanas de gráfico aparte, sobre el gráfico activo. Los de operar vienen sin asignar; en cuentas reales siguen pidiendo confirmación si está activada."),
            Classes = { "muted", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        return Ui.Card(Ui.V(16, Ui.H(10, Ui.Icon(MaterialIconKind.KeyboardOutline, 18, Ui.Accent), Ui.H2(L.T("Atajos de teclado"))), note, list, reset), 20);
    }

    private void RefreshHotkey(string id)
    {
        if (_hotkeyButtons.TryGetValue(id, out var b)) b.Content = Hotkeys.Pretty(Hotkeys.GestureOf(_svc.Settings, id));
    }

    private static string Label(TimeZoneInfo z)
    {
        var off = z.BaseUtcOffset;
        return L.F("(UTC{0}{1:hh\\:mm}) {2}",(off < TimeSpan.Zero ? "-" : "+"),off,z.Id.Replace('_', ' '));
    }

    private ToggleSwitch Switch(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "" };
        t.IsCheckedChanged += (_, _) =>
        {
            set(t.IsChecked == true);
            _svc.Settings.Save();
        };
        return t;
    }

    private static Control Section(string title, MaterialIconKind icon, params Control[] rows)
    {
        var list = new StackPanel { Spacing = 0 };
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 14) }.Classes("divider"));
            list.Children.Add(rows[i]);
        }

        return Ui.Card(Ui.V(16, Ui.H(10, Ui.Icon(icon, 18, Ui.Accent), Ui.H2(title)), list), 20);
    }

    private static Control Setting(string title, string description, Control control)
    {
        var text = Ui.V(3, new TextBlock { Text = title, FontWeight = FontWeight.Medium }, new TextBlock { Text = description, Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(24, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(text);
        return row;
    }
}
