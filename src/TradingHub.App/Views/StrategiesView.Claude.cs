using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

/// <summary>Panel del asistente (Claude Code o Codex) dentro de la pestaña Código: pedís estrategias y ves el código en el editor.</summary>
internal sealed partial class StrategiesView
{
    private IAiBridge _claude = new ClaudeCodeBridge();
    private readonly TextBlock _aiTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 14 };
    private string _workingText = "";
    private string EngineName => AiEngines.Name(_claude.Engine);
    private readonly StackPanel _chat = new() { Spacing = 10, Margin = new Thickness(14, 12) };
    private readonly ScrollViewer _chatScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 64,
        MaxHeight = 160,
        Watermark = L.T("Pedile a Claude una estrategia nueva o un cambio en la abierta…"),
    };
    private readonly TextBlock _claudeStatus = new() { Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button _sendButton = null!;
    private Button _stopButton2 = null!;
    private SelectableTextBlock? _currentReply;
    private Dictionary<string, DateTime> _filesBefore = new();
    private Dictionary<string, DateTime> _indicatorsBefore = new();
    private DateTime _requestStartedUtc;
    private bool _gotReplyText;
    private DateTime _loadedFileTime;
    private readonly ToggleButton _labToggle = new();
    private readonly TextBlock _claudeHint = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private HashSet<string> _reportsBefore = new();

    // conversación actual (se guarda en el historial) y ventana aparte del chat
    private ChatConversation _conv = new();
    private Grid? _claudeGrid;
    private GridSplitter? _claudeSplit;
    private Control? _claudePanel;
    private Window? _claudeWindow;
    private Button? _claudeDetach;

    private Control BuildClaudePanel()
    {
        var newChat = Ui.IconButton(MaterialIconKind.ChatPlusOutline, L.T("Nueva conversación"), () =>
        {
            if (_claude.IsRunning) return;
            _claude.NewConversation();
            _conv = new ChatConversation { Engine = _claude.Engine };
            _chat.Children.Clear();
            ShowSuggestions();
        }, 17);
        var history = new Button { Content = Ui.Icon(MaterialIconKind.History, 17) }.Classes("icon");
        ToolTip.SetTip(history, L.T("Historial de conversaciones (se pueden retomar donde quedaron)"));
        history.Click += (_, _) => ShowHistoryMenu(history);
        _claudeDetach = Ui.IconButton(MaterialIconKind.OpenInNew, L.T("Sacar el chat a una ventana aparte"), ToggleClaudeWindow, 17);
        var title = Ui.V(1, Ui.H(8, Ui.Icon(MaterialIconKind.Creation, 18, Ui.Accent), _aiTitle), _claudeStatus);
        // elegir asistente (Claude o Codex), modelo y razonamiento
        var engineButton = new Button { Content = Ui.Icon(MaterialIconKind.TuneVariant, 17) }.Classes("icon");
        ToolTip.SetTip(engineButton, L.T("Asistente y modelo (Claude Code o Codex)"));
        engineButton.Click += (_, _) => ShowEngineMenu(engineButton);
        // laboratorio: Claude corre backtests, optimizaciones y walk-forward por su cuenta
        _labToggle.Content = Ui.H(6, Ui.Icon(MaterialIconKind.FlaskOutline, 15), new TextBlock { Text = L.T("Laboratorio"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _labToggle.Padding = new Thickness(8, 4);
        ToolTip.SetTip(_labToggle, L.T("Modo laboratorio: Claude puede bajar datos, correr backtests, optimizaciones, walk-forward y Monte Carlo por su cuenta, decidir si la estrategia es ganadora o perdedora y dejar un reporte en la pestaña Reportes."));
        _labToggle.IsCheckedChanged += (_, _) => UpdateClaudeHint();
        var headRight = Ui.H(6, _labToggle, engineButton, history, newChat, _claudeDetach);
        var head = new DockPanel { Margin = new Thickness(14, 10, 10, 10) };
        DockPanel.SetDock(headRight, Dock.Right);
        head.Children.Add(headRight);
        head.Children.Add(title);

        _sendButton = Ui.Button(L.T("Enviar"), MaterialIconKind.Send, () => _ = SendToClaudeAsync(_prompt.Text ?? ""), "primary");
        ToolTip.SetTip(_sendButton, Keys.Label(L.T("Enter")));
        _stopButton2 = Ui.Button(L.T("Detener"), MaterialIconKind.Stop, () => _claude.Cancel());
        _stopButton2.IsVisible = false;
        _prompt.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(Keys.Command))
            {
                _ = SendToClaudeAsync(_prompt.Text ?? "");
                e.Handled = true;
            }
        };
        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var right = Ui.H(8, _stopButton2, _sendButton);
        DockPanel.SetDock(right, Dock.Right);
        actions.Children.Add(right);
        UpdateClaudeHint();
        actions.Children.Add(_claudeHint);
        var input = new Border { Child = Ui.V(0, _prompt, actions), Padding = new Thickness(12), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };

        _chatScroll.Content = _chat;
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(new Border { Child = head, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) }.Also(b => DockPanel.SetDock(b, Dock.Top)));
        DockPanel.SetDock(input, Dock.Bottom);
        root.Children.Add(input);
        root.Children.Add(_chatScroll);

        UseEngine(App.Services.Settings.AiEngine);
        ShowSuggestions();
        return new Border { Child = root, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, 0, 0) };
    }

    // ===================================================================== asistente: Claude Code o Codex

    private void OnAiFinished(ClaudeResult r) => _ = OnClaudeFinishedAsync(r);

    /// <summary>Usa este asistente en el chat (con el modelo y el razonamiento guardados en los ajustes).</summary>
    private void UseEngine(string engine)
    {
        engine = AiEngines.Normalize(engine);
        if (_claude.IsRunning) return;
        if (_claude.Engine != engine)
        {
            _claude.Text -= OnClaudeText;
            _claude.ToolUsed -= OnClaudeTool;
            _claude.ToolFinished -= ReloadIfChangedOnDisk;
            _claude.Finished -= OnAiFinished;
            _claude = AiEngines.Create(engine);
        }
        else
        {
            // ya suscripto (salvo la primera vez)
            _claude.Text -= OnClaudeText;
            _claude.ToolUsed -= OnClaudeTool;
            _claude.ToolFinished -= ReloadIfChangedOnDisk;
            _claude.Finished -= OnAiFinished;
        }

        _claude.Text += OnClaudeText;
        _claude.ToolUsed += OnClaudeTool;
        _claude.ToolFinished += ReloadIfChangedOnDisk;
        _claude.Finished += OnAiFinished;
        ApplyModel();
        _aiTitle.Text = EngineName;
        UpdateClaudeHint();
        _ = DetectClaudeAsync();
    }

    private void ApplyModel()
    {
        var s = App.Services.Settings;
        bool codex = _claude.Engine == AiEngines.Codex;
        _claude.ModelOverride = codex ? s.CodexModel : s.ClaudeModel;
        _claude.EffortOverride = codex ? s.CodexEffort : s.ClaudeEffort;
    }

    /// <summary>Menú del asistente: Claude Code o Codex, modelo (se puede escribir otro) y razonamiento.</summary>
    private void ShowEngineMenu(Control anchor)
    {
        var s = App.Services.Settings;
        string engine = _claude.Engine;
        var engines = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var e in AiEngines.All) engines.Items.Add(new ComboBoxItem { Content = AiEngines.Product(e), Tag = e });
        engines.SelectedItem = engines.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag! == engine);

        var models = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var custom = new TextBox { Watermark = L.T("Otro modelo (id exacto, opcional)") };
        var efforts = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var note = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        bool loading = false;

        string CurrentModel(string e) => e == AiEngines.Codex ? s.CodexModel : s.ClaudeModel;
        string CurrentEffort(string e) => e == AiEngines.Codex ? s.CodexEffort : s.ClaudeEffort;

        void Fill(string e)
        {
            loading = true;
            models.Items.Clear();
            foreach (var (id, name) in AiEngines.Models(e)) models.Items.Add(new ComboBoxItem { Content = L.T(name), Tag = id });
            var m = CurrentModel(e);
            var known = models.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == m);
            models.SelectedItem = known ?? models.Items[0];
            custom.Text = known == null ? m : "";
            efforts.Items.Clear();
            foreach (var (id, name) in AiEngines.Efforts(e)) efforts.Items.Add(new ComboBoxItem { Content = L.T(name), Tag = id });
            efforts.SelectedItem = efforts.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == CurrentEffort(e)) ?? efforts.Items[0];
            note.Text = e == AiEngines.Codex
                ? L.T("Codex usa tu cuenta de ChatGPT (o tu API key) con «codex login». Los nombres de los modelos de OpenAI cambian seguido: si uno deja de andar, elegí «Predeterminado» o escribí el id exacto.")
                : L.T("Claude Code usa tu suscripción de Claude. Opus 5.5 es el más capaz; Sonnet y Haiku son más rápidos.");
            loading = false;
        }

        void Save()
        {
            if (loading || engines.SelectedItem is not ComboBoxItem { Tag: string e }) return;
            string model = !string.IsNullOrWhiteSpace(custom.Text) ? custom.Text.Trim() : models.SelectedItem is ComboBoxItem { Tag: string id } ? id : "";
            string effort = efforts.SelectedItem is ComboBoxItem { Tag: string ef } ? ef : "";
            if (e == AiEngines.Codex)
            {
                s.CodexModel = model;
                s.CodexEffort = effort;
            }
            else
            {
                s.ClaudeModel = model;
                s.ClaudeEffort = effort;
            }

            s.Save();
            if (_claude.Engine == e) ApplyModel();
            _ = DetectClaudeAsync();
        }

        Fill(engine);
        engines.SelectionChanged += (_, _) =>
        {
            if (loading || engines.SelectedItem is not ComboBoxItem { Tag: string e } || e == _claude.Engine) return;
            if (_claude.IsRunning)
            {
                engines.SelectedItem = engines.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag! == _claude.Engine);
                return;
            }

            s.AiEngine = e;
            s.Save();
            Fill(e);
            UseEngine(e);
            // la sesión de un asistente no sigue en el otro: conversación nueva
            if (_conv.Messages.Count > 0)
            {
                _claude.NewConversation();
                _conv = new ChatConversation { Engine = e };
                _chat.Children.Clear();
                ShowSuggestions();
            }
        };
        models.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            loading = true;
            custom.Text = "";
            loading = false;
            Save();
        };
        custom.LostFocus += (_, _) => Save();
        custom.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Save();
        };
        efforts.SelectionChanged += (_, _) => Save();

        var login = Ui.Button(L.T("Iniciar sesión"), MaterialIconKind.Login, () => AiEngines.OpenLogin(_claude.Engine));
        login.Padding = new Thickness(10, 4);
        var content = Ui.V(8,
            new TextBlock { Text = L.T("Asistente"), Classes = { "small" }, FontWeight = FontWeight.SemiBold }, engines,
            new TextBlock { Text = L.T("Modelo"), Classes = { "small" }, FontWeight = FontWeight.SemiBold }, models, custom,
            new TextBlock { Text = L.T("Razonamiento"), Classes = { "small" }, FontWeight = FontWeight.SemiBold }, efforts,
            note, login);
        content.Width = 300;
        content.Margin = new Thickness(4);
        new Flyout { Content = content, Placement = PlacementMode.BottomEdgeAlignedRight }.ShowAt(anchor);
    }

    private void UpdateClaudeHint()
    {
        bool lab = _labToggle.IsChecked == true;
        bool codex = _claude.Engine == AiEngines.Codex;
        _claudeHint.Text = lab
            ? L.F("Laboratorio: {0} corre las pruebas por su cuenta (pueden tardar varios minutos) y guarda un reporte en Reportes.", EngineName)
            : codex
                ? L.T("Usa tu Codex (cuenta de ChatGPT). Trabaja solo en la carpeta de estrategias, sin red.")
                : L.T("Usa tu Claude Code (suscripción). Solo puede leer y editar archivos de la carpeta de estrategias.");
        _claudeHint.Foreground = lab ? Ui.Accent : null;
        _prompt.Watermark = lab ? L.T("Ej.: investigá y optimizá la estrategia abierta con 60 días de BTCUSDT y decime si es ganadora") : L.T("Pedile una estrategia o un cambio…");
    }

    private async Task DetectClaudeAsync()
    {
        var engine = _claude.Engine;
        var v = await AiEngines.VersionAsync(engine);
        if (engine != _claude.Engine) return; // cambió de asistente mientras se buscaba
        string model = _claude.ModelOverride is { Length: > 0 } m ? " · " + (AiEngines.Models(engine).FirstOrDefault(x => x.Id == m).Name is { } n ? L.T(n) : m) : "";
        _claudeStatus.Text = v == null
            ? L.F("{0} no está instalado", AiEngines.Product(engine))
            : engine == AiEngines.Codex
                ? L.F("Codex {0} · tu cuenta", CodexBridge.ShortVersion(v)) + model
                : L.F("Claude Code {0} · tu suscripción", v.Replace("(Claude Code)", "").Trim()) + model;
        _sendButton.IsEnabled = v != null;
        if (v == null)
            AddNote(MaterialIconKind.AlertCircleOutline, engine == AiEngines.Codex
                ? L.F("No encontré Codex CLI en esta {0}. Instalalo ({1}), iniciá sesión con «codex login» y volvé a elegir Codex.", Os.Device, AiEngines.InstallHint(engine))
                : L.F("No encontré Claude Code en esta {0}. Instalalo (claude.com/claude-code), iniciá sesión con tu cuenta y volvé a abrir Trading Hub.",Os.Device), Ui.Warn);
    }

    private void ShowSuggestions()
    {
        if (_chat.Children.Count > 0) return;
        var intro = new TextBlock
        {
            Text = L.T("Describí la estrategia que querés y Claude la escribe acá: vas a ver el código aparecer en el editor y Trading Hub lo compila al terminar."),
            Classes = { "muted", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        var chips = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (s, lab) in new[]
                 {
                     (L.T("Investigá y optimizá la estrategia abierta: backtest, optimización, walk-forward y Monte Carlo, y armá el reporte con el veredicto"), true),
                     (L.T("Revisá la cartera en vivo y proponé cambios de tamaño (subir a las que rinden, bajar a las que no)"), true),
                     (L.T("Creá una estrategia que opere la ruptura del rango de los primeros 30 minutos"), false),
                     (L.T("Explicame qué hace la estrategia abierta"), false),
                     (L.T("Agregale un filtro para operar solo entre las 10 y las 17"), false),
                     (L.T("Mejorá la gestión de riesgo con stop por ATR"), false),
                 })
        {
            var content = lab
                ? (Control)Ui.H(6, Ui.Icon(MaterialIconKind.FlaskOutline, 14, Ui.Accent), new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 300 })
                : new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var b = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6) }.Classes("ghost");
            b.Click += (_, _) =>
            {
                if (lab) _labToggle.IsChecked = true;
                _prompt.Text = s;
                _prompt.Focus();
            };
            chips.Children.Add(b);
        }

        _chat.Children.Add(Ui.V(10, intro, chips));
    }

    private void AddBubble(string text, bool user)
    {
        if (_chat.Children.Count == 1 && _chat.Children[0] is StackPanel { Children.Count: 2 } sp && sp.Children[1] is WrapPanel) _chat.Children.Clear();
        var tb = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        var bubble = new Border
        {
            Child = tb,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(10),
            Background = user ? Ui.Bg3 : Ui.Bg2,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Margin = user ? new Thickness(40, 0, 0, 0) : new Thickness(0),
        };
        _chat.Children.Add(bubble);
        if (!user) _currentReply = tb;
        ScrollChatToEnd();
    }

    private void AddNote(MaterialIconKind icon, string text, IBrush? color = null)
    {
        var row = Ui.H(6, Ui.Icon(icon, 14, color ?? Ui.Text2), new TextBlock { Text = text, Classes = { "small" }, Foreground = color ?? Ui.Text2, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
        _chat.Children.Add(row);
        ScrollChatToEnd();
    }

    private void ScrollChatToEnd() => Dispatcher.UIThread.Post(() => _chatScroll.ScrollToEnd(), DispatcherPriority.Background);

    private async Task SendToClaudeAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || _claude.IsRunning) return;
        if (_editor.IsDirty) Save();
        _prompt.Text = "";
        AddBubble(text, user: true);
        if (_conv.Messages.Count == 0)
        {
            _conv.Title = text.Length > 70 ? text[..70].TrimEnd() + "…" : text;
            _conv.Lab = _labToggle.IsChecked == true;
            _conv.Engine = _claude.Engine;
            _conv.Strategy = _current != null ? Path.GetFileName(_current.Path) : null;
        }

        _conv.Messages.Add(new ChatMessage { Role = "user", Text = text });
        ChatHistory.Save(_conv);
        _currentReply = null;
        _gotReplyText = false;
        _filesBefore = Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").ToDictionary(f => f, File.GetLastWriteTimeUtc);
        Directory.CreateDirectory(AppPaths.UserIndicators);
        _indicatorsBefore = Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        _requestStartedUtc = DateTime.UtcNow;
        _loadedFileTime = _current != null && File.Exists(_current.Path) ? File.GetLastWriteTimeUtc(_current.Path) : default;

        // contexto: archivo abierto y errores actuales
        var context = new List<string>();
        if (_current != null) context.Add(L.F("Archivo abierto en el editor: {0}",Path.GetFileName(_current.Path)));
        context.Add(L.F("Carpeta de indicadores (para indicadores, no estrategias): {0}",AppPaths.UserIndicators));
        var errors = _current?.LastCompile?.Diagnostics.Where(d => d.Severity == "error").Take(15).ToList();
        if (errors is { Count: > 0 }) context.Add(L.T("Errores de compilación actuales:\n") + string.Join('\n', errors.Select(d => L.F("- Línea {0}: {1} ({2})",d.Line,d.Message,d.Id))));
        string prompt = context.Count > 0 ? L.F("[Contexto de Trading Hub]\n{0}\n\n[Pedido]\n{1}",string.Join("\n", context),text) : text;

        _sendButton.IsVisible = false;
        _stopButton2.IsVisible = true;
        _editor.IsReadOnly = true;
        bool lab = _labToggle.IsChecked == true;
        _reportsBefore = Lab.ReportStore.List().Select(r => r.Path).ToHashSet();
        _workingText = L.F("{0} está trabajando…", EngineName);
        AddNote(MaterialIconKind.ProgressClock, _workingText);
        await _claude.SendAsync(prompt, AppPaths.Strategies, ClaudeCodeBridge.StrategySystemPrompt + ClaudeCodeBridge.IndicatorSystemPrompt + (lab ? ClaudeCodeBridge.LabSystemPrompt : ""), lab,
            new[] { AppPaths.UserIndicators, Directory.CreateDirectory(Lab.LabServer.CapturesDir).FullName });
    }

    /// <summary>Las capturas del gráfico que miró Claude en este pedido, en el chat (tocás una y se abre grande).</summary>
    private void ShowSnapshots()
    {
        var dir = Lab.LabServer.CapturesDir;
        if (!Directory.Exists(dir)) return;
        foreach (var f in new DirectoryInfo(dir).GetFiles("*.png").Where(f => f.LastWriteTimeUtc >= _requestStartedUtc).OrderBy(f => f.LastWriteTimeUtc).Take(4))
        {
            try
            {
                var bmp = new Avalonia.Media.Imaging.Bitmap(f.FullName);
                var img = new Image { Source = bmp, MaxWidth = 420, Stretch = Stretch.Uniform, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                var path = f.FullName;
                img.PointerReleased += (_, _) => TradingHub.App.Windows.Shell.Open(path);
                ToolTip.SetTip(img, L.T("Lo que miró Claude (tocá para verlo grande)"));
                _chat.Children.Add(Ui.V(6, Ui.H(8, Ui.Icon(MaterialIconKind.ChartBoxOutline, 15, Ui.Accent), new TextBlock { Text = L.T("Lo que miró del gráfico"), Classes = { "small" } }),
                    new Border { Child = img, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left }));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }
    }

    /// <summary>Indicadores que Claude creó o cambió en este pedido: los compila y deja el resultado en el chat.</summary>
    private void ReportIndicators()
    {
        if (!Directory.Exists(AppPaths.UserIndicators)) return;
        var changed = Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories)
            .Where(f => !_indicatorsBefore.TryGetValue(f, out var t) || File.GetLastWriteTimeUtc(f) > t).ToList();
        if (changed.Count == 0 || _svc.Scripts is not { } scripts) return;
        var res = scripts.CompileAll();
        string names = string.Join(", ", changed.Select(Path.GetFileNameWithoutExtension));
        var errors = res?.Diagnostics.Where(d => d.Severity == "error" && changed.Any(f => string.IsNullOrEmpty(d.File) || Path.GetFullPath(d.File) == Path.GetFullPath(f))).ToList() ?? new();
        var open = Ui.Button(L.T("Abrir en el editor de indicadores"), MaterialIconKind.CodeBraces, () =>
        {
            if (TopLevel.GetTopLevel(this) is Window w) new Windows.CodeEditorWindow(_svc, Windows.CodeKind.Indicator).Show(w);
        });
        open.Padding = new Thickness(10, 4);
        if (res?.Success == true)
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CheckCircle, 15, Ui.Up),
                new TextBlock { Text = L.F("Indicador {0} compilado: ya está en el gráfico en Indicadores → «Mis indicadores»",names), Classes = { "small" }, Foreground = Ui.Up, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), open));
        else
        {
            string detail = string.Join("\n", (errors.Count > 0 ? errors : res?.Diagnostics.Where(d => d.Severity == "error").ToList() ?? new()).Take(12)
                .Select(d => L.F("- {0} línea {1}: {2} ({3})",Path.GetFileName(d.File),d.Line,d.Message,d.Id)));
            var fix = Ui.Button(L.T("Pedirle que los arregle"), MaterialIconKind.AutoFix, () => _ = SendToClaudeAsync(L.F("Corregí los errores de compilación del indicador {0} (en la carpeta de indicadores):\n{1}",names,detail)), "primary");
            fix.Padding = new Thickness(10, 4);
            int count = errors.Count > 0 ? errors.Count : res?.Diagnostics.Count(d => d.Severity == "error") ?? 0;
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CloseCircle, 15, Ui.Down),
                new TextBlock { Text = L.F("El indicador {0} tiene {1} error(es) de compilación",names,count), Classes = { "small" }, Foreground = Ui.Down, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), Ui.H(8, fix, open)));
        }
    }

    private void OnClaudeText(string chunk)
    {
        if (_conv.Messages.LastOrDefault() is { Role: "assistant" } last && _gotReplyText) last.Text += chunk;
        else _conv.Messages.Add(new ChatMessage { Role = "assistant", Text = chunk });
        _gotReplyText = true;
        RemoveWorkingNote();
        if (_currentReply == null) AddBubble(chunk, user: false);
        else _currentReply.Text += chunk;
        ScrollChatToEnd();
    }

    private void OnClaudeTool(string tool, string? file)
    {
        RemoveWorkingNote();
        _currentReply = null; // lo que escriba después va en otra burbuja
        if (tool.StartsWith("mcp__tradinghub__", StringComparison.Ordinal))
        {
            var (labIcon, labVerb) = tool["mcp__tradinghub__".Length..] switch
            {
                "list_strategies" => (MaterialIconKind.RobotOutline, L.T("Revisando las estrategias")),
                "compile_strategy" => (MaterialIconKind.HammerWrench, L.T("Compilando")),
                "compile_indicator" => (MaterialIconKind.HammerWrench, L.T("Compilando el indicador")),
                "chart_snapshot" => (MaterialIconKind.ChartBoxOutline, L.T("Mirando el gráfico")),
                "list_indicators" => (MaterialIconKind.ChartBellCurveCumulative, L.T("Revisando tus indicadores")),
                "list_data" => (MaterialIconKind.DatabaseOutline, L.T("Mirando los datos guardados")),
                "download_data" => (MaterialIconKind.Download, L.T("Bajando datos")),
                "run_backtest" => (MaterialIconKind.PlayOutline, "Backtest"),
                "run_optimization" => (MaterialIconKind.Tune, L.T("Optimizando parámetros")),
                "run_walk_forward" => (MaterialIconKind.ChartTimelineVariant, L.T("Walk-forward")),
                "list_runs" => (MaterialIconKind.History, L.T("Revisando corridas anteriores")),
                "analyze_portfolio" => (MaterialIconKind.ChartTimelineVariant, L.T("Analizando el portafolio")),
                "suggest_portfolio" => (MaterialIconKind.ScaleBalance, L.T("Buscando estrategias no correlacionadas")),
                "live_portfolio" => (MaterialIconKind.Pulse, L.T("Revisando la cartera en vivo")),
                "propose_sizing" => (MaterialIconKind.ScaleBalance, L.T("Dejando propuestas de tamaño (las aprobás vos)")),
                "save_report" => (MaterialIconKind.FileDocumentOutline, L.T("Guardando el reporte")),
                var other => (MaterialIconKind.FlaskOutline, other),
            };
            AddNote(labIcon, string.IsNullOrEmpty(file) ? labVerb : $"{labVerb} · {file}", Ui.Accent);
            return;
        }

        string name = file != null ? Path.GetFileName(file) : "";
        var (icon, verb) = tool switch
        {
            "Write" => (MaterialIconKind.FilePlusOutline, L.T("Escribiendo")),
            "Edit" or "MultiEdit" => (MaterialIconKind.FileEditOutline, L.T("Editando")),
            "Read" => (MaterialIconKind.FileEyeOutline, L.T("Leyendo")),
            "Delete" => (MaterialIconKind.FileRemoveOutline, L.T("Borrando")),
            "Bash" => (MaterialIconKind.Console, L.T("Ejecutando")),
            "WebSearch" => (MaterialIconKind.Web, L.T("Buscando en la web")),
            _ => (MaterialIconKind.FolderSearchOutline, L.T("Buscando")),
        };
        if (tool is "Bash" or "WebSearch") name = file ?? "";
        AddNote(icon, $"{verb} {name}".Trim());
    }

    private void RemoveWorkingNote()
    {
        var note = _chat.Children.OfType<StackPanel>().LastOrDefault(p => p.Children.Count == 2 && p.Children[1] is TextBlock tb && tb.Text == _workingText);
        if (note != null) _chat.Children.Remove(note);
    }

    /// <summary>Si Claude cambió el archivo abierto, se muestra al instante en el editor.</summary>
    private void ReloadIfChangedOnDisk()
    {
        if (_current == null || !File.Exists(_current.Path)) return;
        var t = File.GetLastWriteTimeUtc(_current.Path);
        if (t == _loadedFileTime) return;
        _loadedFileTime = t;
        _editor.Text = File.ReadAllText(_current.Path);
        UpdateFileInfo();
    }

    private async Task OnClaudeFinishedAsync(ClaudeResult r)
    {
        _conv.SessionId = _claude.SessionId ?? _conv.SessionId;
        if (r.IsError) _conv.Messages.Add(new ChatMessage { Role = "note", Text = r.NeedsLogin ? L.F("{0} pidió iniciar sesión.", AiEngines.Product(_claude.Engine)) : r.Text.Length > 400 ? r.Text[..400] + "…" : r.Text });
        else if (!_gotReplyText && !string.IsNullOrWhiteSpace(r.Text)) _conv.Messages.Add(new ChatMessage { Role = "assistant", Text = r.Text });
        ChatHistory.Save(_conv);
        RemoveWorkingNote();
        _sendButton.IsVisible = true;
        _stopButton2.IsVisible = false;
        _editor.IsReadOnly = false;
        if (r.IsError)
        {
            if (r.NeedsLogin)
            {
                var engine = _claude.Engine;
                bool codex = engine == AiEngines.Codex;
                var login = Ui.Button(codex ? L.T("Iniciar sesión en Codex") : L.T("Iniciar sesión en Claude Code"), MaterialIconKind.Login, () => AiEngines.OpenLogin(engine), "primary");
                var card = Ui.Card(Ui.V(8,
                    new TextBlock { Text = codex ? L.T("Codex necesita que inicies sesión") : L.T("Claude Code necesita que inicies sesión"), FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = (Os.IsWindows ? L.T("Se abre una ventana de comandos") : L.T("Se abre la Terminal")) + (codex
                        ? L.T(" con «codex login»: elegí «Sign in with ChatGPT» (tu suscripción) o tu API key y seguí los pasos en el navegador. Después volvé acá y repetí el pedido.")
                        : L.T(" con Claude Code: elegí tu cuenta de Claude (suscripción) y seguí los pasos en el navegador. Después volvé acá y repetí el pedido.")), Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap },
                    login), 12);
                _chat.Children.Add(card);
            }
            else
            {
                AddNote(MaterialIconKind.AlertCircleOutline, r.Text.Length > 400 ? r.Text[..400] + "…" : r.Text, Ui.Down);
            }

            ScrollChatToEnd();
            return;
        }

        if (!_gotReplyText && !string.IsNullOrWhiteSpace(r.Text)) AddBubble(r.Text, user: false);

        // ¿creó una estrategia nueva? se abre sola
        _catalog.Refresh();
        var created = Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Where(f => !_filesBefore.ContainsKey(f)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        var target = created != null ? _catalog.Files.FirstOrDefault(f => f.Path == created) : _current;
        if (target != null && !ReferenceEquals(target, _current)) await SelectAsync(target);
        else
        {
            ReloadIfChangedOnDisk();
            await CompileAsync(silent: true);
        }

        var res = _current?.LastCompile;
        int errors = res?.Diagnostics.Count(d => d.Severity == "error") ?? 0;
        if (res?.Success == true && _current?.Type != null)
        {
            var test = Ui.Button(L.T("Probar en backtest"), MaterialIconKind.PlayOutline, () =>
            {
                _tabs.Select("backtest");
                ShowTab("backtest");
                _ = RunBacktestAsync();
            });
            test.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CheckCircle, 15, Ui.Up), new TextBlock { Text = L.F("{0} compila sin errores",_current.DisplayName), Classes = { "small" }, Foreground = Ui.Up, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), test));
        }
        else if (errors > 0)
        {
            var fix = Ui.Button(L.T("Pedirle que los arregle"), MaterialIconKind.AutoFix, () => _ = SendToClaudeAsync(L.T("Corregí los errores de compilación del archivo abierto.")), "primary");
            fix.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CloseCircle, 15, Ui.Down), new TextBlock { Text = L.F("{0} error(es) de compilación",errors), Classes = { "small" }, Foreground = Ui.Down }), fix));
        }

        // ¿creó o cambió indicadores? se compilan y se avisa si quedaron en «Mis indicadores» o qué falla
        ReportIndicators();

        // lo que miró del gráfico: la misma imagen, para que veas de qué habla
        ShowSnapshots();

        // reportes nuevos del laboratorio
        foreach (var rep in Lab.ReportStore.List().Where(x => !_reportsBefore.Contains(x.Path)).Reverse())
        {
            var path = rep.Path;
            var open = Ui.Button(L.T("Ver reporte"), MaterialIconKind.FileDocumentOutline, () => OpenReport(path), "primary");
            open.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.FileChartOutline, 15, Ui.Accent), new TextBlock { Text = rep.Title, Classes = { "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 }, VerdictPill(rep.Verdict)), open));
            BuildStrategyMenu();
        }

        if (_labToggle.IsChecked == true) RefreshHistory();
        if (TradingHub.Strategies.LiveStore.LoadProposals().Any(p => p.Source == "claude" && p.IsPending && p.CreatedUtc > DateTime.UtcNow.AddMinutes(-30)))
        {
            var review = Ui.Button(L.T("Revisar y aprobar"), MaterialIconKind.ScaleBalance, () =>
            {
                if (TopLevel.GetTopLevel(this) is not TradingHub.App.Windows.MainWindow main) return;
                main.Navigate("live");
                (main.Page("live") as LiveView)?.Select("proposals");
            }, "primary");
            review.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.ScaleBalance, 15, Ui.Accent), new TextBlock { Text = L.T("Claude dejó cambios de tamaño pendientes de tu aprobación"), Classes = { "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 }), review));
        }
        if (r.Seconds > 0) AddNote(MaterialIconKind.TimerOutline, r.Seconds >= 120 ? $"{r.Seconds / 60:0.#} min" : $"{r.Seconds:0} s");
        ScrollChatToEnd();
    }

    // ===================================================================== historial de conversaciones

    private void ShowHistoryMenu(Control anchor)
    {
        var list = new StackPanel { Spacing = 2, Width = 360 };
        var convs = ChatHistory.List();
        if (convs.Count == 0)
            list.Children.Add(new TextBlock { Text = L.T("Todavía no hay conversaciones guardadas."), Classes = { "muted", "small" }, Margin = new Thickness(8) });
        var flyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 460 }, Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var c in convs.Take(60))
        {
            var conv = c;
            var title = new TextBlock { Text = string.IsNullOrWhiteSpace(conv.Title) ? L.T("(sin título)") : conv.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, FontWeight = ReferenceEquals(conv.Id, _conv.Id) || conv.Id == _conv.Id ? FontWeight.SemiBold : FontWeight.Normal };
            var meta = new TextBlock
            {
                Text = L.F("{0} · {1} mensaje(s){2}{3}",Ui.Ago(conv.UpdatedUtc),conv.Messages.Count(m => m.Role == "user"),(conv.Lab ? L.T(" · laboratorio") : ""),(conv.Strategy != null ? " · " + conv.Strategy : "")) + (AiEngines.Normalize(conv.Engine) == AiEngines.Codex ? " · Codex" : ""),
                Classes = { "faint", "small" },
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var open = new Button { Content = Ui.V(1, title, meta), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5) }.Classes("ghost");
            open.Click += (_, _) =>
            {
                flyout.Hide();
                LoadConversation(conv);
            };
            var delete = Ui.IconButton(MaterialIconKind.Close, L.T("Borrar esta conversación del historial"), () =>
            {
                ChatHistory.Delete(conv.Id);
                flyout.Hide();
                if (conv.Id == _conv.Id) _conv = new ChatConversation { Title = _conv.Title, Lab = _conv.Lab };
            }, 14);
            var row = new DockPanel();
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(open);
            list.Children.Add(row);
        }

        flyout.ShowAt(anchor);
    }

    /// <summary>Muestra una conversación guardada y la deja lista para seguirla (Claude retoma su sesión).</summary>
    private void LoadConversation(ChatConversation conv)
    {
        if (_claude.IsRunning) return;
        // la sesión se sigue con el asistente con que se empezó
        if (AiEngines.Normalize(conv.Engine) != _claude.Engine) UseEngine(conv.Engine);
        _conv = conv;
        _claude.Resume(conv.SessionId);
        _labToggle.IsChecked = conv.Lab;
        _chat.Children.Clear();
        foreach (var m in conv.Messages)
        {
            if (m.Role == "note") AddNote(MaterialIconKind.InformationOutline, m.Text);
            else AddBubble(m.Text, user: m.Role == "user");
        }

        _currentReply = null;
        AddNote(MaterialIconKind.History, conv.SessionId != null
            ? L.F("Conversación retomada con {0}: lo que escribas sigue desde acá, con todo lo que ya sabía.", EngineName)
            : L.T("Conversación guardada (sin sesión de Claude): lo que escribas empieza una sesión nueva."));
        ScrollChatToEnd();
    }

    // ===================================================================== chat en una ventana aparte

    private void ToggleClaudeWindow()
    {
        if (_claudeWindow != null) DockClaude();
        else DetachClaude();
    }

    private void DetachClaude()
    {
        if (_claudePanel == null || _claudeGrid == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var s = App.Services.Settings;
        _claudeGrid.Children.Remove(_claudePanel);
        _claudeGrid.ColumnDefinitions[2].Width = new GridLength(0);
        if (_claudeSplit != null) _claudeSplit.IsVisible = false;
        var w = new Window
        {
            Title = L.T("Claude · estrategias"),
            Width = Math.Clamp(s.ClaudeWindowWidth, 320, 1600),
            Height = Math.Clamp(s.ClaudeWindowHeight, 360, 2400),
            MinWidth = 320,
            MinHeight = 360,
            Background = Ui.Bg1,
            Content = _claudePanel,
        };
        var pos = new PixelPoint(s.ClaudeWindowX, s.ClaudeWindowY);
        if (s.ClaudeWindowX >= 0 && w.Screens.All.Any(sc => sc.WorkingArea.Contains(pos)))
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = pos;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        w.Closing += (_, _) =>
        {
            s.ClaudeWindowX = w.Position.X;
            s.ClaudeWindowY = w.Position.Y;
            if (w.Width > 100) s.ClaudeWindowWidth = w.Width;
            if (w.Height > 100) s.ClaudeWindowHeight = w.Height;
            s.Save();
        };
        w.Closed += (_, _) => DockClaude();
        _claudeWindow = w;
        if (_claudeDetach != null)
        {
            _claudeDetach.Content = Ui.Icon(MaterialIconKind.DockRight, 17);
            ToolTip.SetTip(_claudeDetach, L.T("Volver a poner el chat al lado del editor"));
        }

        w.Show(owner);
    }

    private void DockClaude()
    {
        var w = _claudeWindow;
        if (w == null || _claudePanel == null || _claudeGrid == null) return;
        _claudeWindow = null;
        w.Content = null;
        if (_claudePanel.Parent == null)
        {
            Grid.SetColumn(_claudePanel, 2);
            _claudeGrid.Children.Add(_claudePanel);
        }

        _claudeGrid.ColumnDefinitions[2].Width = new GridLength(400);
        if (_claudeSplit != null) _claudeSplit.IsVisible = true;
        if (_claudeDetach != null)
        {
            _claudeDetach.Content = Ui.Icon(MaterialIconKind.OpenInNew, 17);
            ToolTip.SetTip(_claudeDetach, L.T("Sacar el chat a una ventana aparte"));
        }

        if (w.IsVisible) w.Close();
    }

    /// <summary>Le pide al laboratorio que analice la cartera en vivo y proponga tamaños (los aprobás en En vivo → Propuestas).</summary>
    internal void AskClaudeForSizing()
    {
        _labToggle.IsChecked = true;
        _tabs.Select("code");
        ShowTab("code");
        _ = SendToClaudeAsync(L.T("Analizá la cartera en vivo con live_portfolio (resultado, operaciones, drawdown y racha de cada estrategia) y, si corresponde, proponé cambios de tamaño con propose_sizing explicando el motivo de cada uno. No apliques nada: yo apruebo cada cambio en Trading Hub (En vivo → Propuestas)."));
    }
}
