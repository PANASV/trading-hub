using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

internal enum CodeKind
{
    Indicator,
}

/// <summary>
/// Editor de indicadores propios (C#, API compatible con ATAS). Al guardar se recompilan solos y
/// aparecen en la biblioteca de indicadores de cada gráfico.
/// </summary>
internal sealed class CodeEditorWindow : Window
{
    private readonly AppServices _svc;
    private readonly ListBox _files = new() { Background = Brushes.Transparent };
    private readonly CodeEditor _editor = new();
    private readonly TextBlock _title = new() { Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 8, Height = 8 };
    private readonly TextBlock _state = new() { FontSize = 12 };
    private string? _path;

    public CodeEditorWindow(AppServices svc, CodeKind kind)
    {
        _svc = svc;
        Title = L.T("Indicadores propios — editor");
        Width = 1180;
        Height = 780;
        MinWidth = 820;
        MinHeight = 520;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window;
        Content = Build();
        _editor.SaveRequested += SaveAndCompile;
        _editor.CompileRequested += SaveAndCompile;
        _editor.Changed += () => _title.Text = Path.GetFileName(_path) + " •";
        if (_svc.Scripts != null) _svc.Scripts.Compiled += OnCompiled;
        Closed += (_, _) =>
        {
            if (_svc.Scripts != null) _svc.Scripts.Compiled -= OnCompiled;
            if (_editor.IsDirty) Save();
        };
        FillFiles();
        if (_svc.Scripts?.LastResult is { } last) OnCompiled(last);
    }

    private Control Build()
    {
        var add = Ui.Button(L.T("Nuevo"), MaterialIconKind.Plus, () => _ = NewAsync());
        var folder = Ui.IconButton(MaterialIconKind.FolderOpenOutline, Os.IsWindows ? L.T("Abrir la carpeta en el Explorador de archivos") : L.T("Abrir la carpeta en Finder"), () => Shell.OpenFolder(AppPaths.UserIndicators));
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var acts = Ui.H(4, folder, add);
        DockPanel.SetDock(acts, Dock.Right);
        head.Children.Add(acts);
        head.Children.Add(Ui.H3(L.T("Archivos")));
        _files.SelectionChanged += (_, _) =>
        {
            if (_files.SelectedItem is ListBoxItem { Tag: string p }) Open(p);
        };
        var left = new DockPanel { Width = 250, Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var note = new TextBlock
        {
            Text = L.T("API de indicadores de Trading Hub (Indicator, OnCalculate, DataSeries, OnRender…); si pegás código escrito con esta API en otra plataforma, se adapta solo. Al guardar se recompila y queda disponible en Indicadores."),
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(note, Dock.Bottom);
        left.Children.Add(note);
        left.Children.Add(Ui.Card(_files, 6));

        var save = Ui.Button(L.T("Guardar y compilar"), MaterialIconKind.Hammer, SaveAndCompile, "primary");
        ToolTip.SetTip(save, Keys.Label("S"));
        var bar = new DockPanel { Margin = new Thickness(16, 10) };
        var right = Ui.H(10, new Border { Child = Ui.H(7, _dot, _state), Classes = { "pill" } }, save);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_title);
        var top = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Bg1 };
        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(_editor);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(new Border { Child = left, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) }.Also(b => DockPanel.SetDock(b, Dock.Left)));
        root.Children.Add(main);
        return root;
    }

    private void FillFiles(string? select = null)
    {
        _files.Items.Clear();
        foreach (var f in Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).OrderBy(f => f))
        {
            var row = Ui.H(8, Ui.Icon(MaterialIconKind.LanguageCsharp, 16, Ui.Accent), new TextBlock { Text = Path.GetRelativePath(AppPaths.UserIndicators, f), TextTrimming = TextTrimming.CharacterEllipsis });
            _files.Items.Add(new ListBoxItem { Content = row, Tag = f });
        }

        _files.SelectedItem = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag! == (select ?? _path)) ?? _files.Items.OfType<ListBoxItem>().FirstOrDefault();
    }

    private void Open(string path)
    {
        if (_path == path) return;
        if (_editor.IsDirty) Save();
        _path = path;
        _editor.Text = File.ReadAllText(path);
        _title.Text = Path.GetFileName(path);
        if (_svc.Scripts?.LastResult is { } last) _editor.ShowDiagnostics(last.Diagnostics, path);
    }

    private void Save()
    {
        if (_path == null) return;
        File.WriteAllText(_path, _editor.Text);
        _editor.IsDirty = false;
        _title.Text = Path.GetFileName(_path);
    }

    private void SaveAndCompile()
    {
        Save();
        _dot.Fill = Ui.Warn;
        _state.Text = L.T("Compilando…");
        _svc.Scripts?.CompileAll();
    }

    private void OnCompiled(TradingHub.Scripting.ScriptCompileResult r)
    {
        int errors = r.Diagnostics.Count(d => d.Severity == "error");
        _dot.Fill = r.Success ? Ui.Up : Ui.Down;
        _state.Text = r.Success ? L.F("Compilado · {0:0} ms",r.Elapsed.TotalMilliseconds) : $"{errors} error(es)";
        if (_path != null) _editor.ShowDiagnostics(r.Diagnostics, _path);
    }

    private async Task NewAsync()
    {
        var name = await PromptDialog.ShowAsync(this, L.T("Nuevo indicador"), L.T("Nombre de la clase"), "MiIndicador", L.T("Crear"));
        if (name == null) return;
        string cls = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (cls.Length == 0 || char.IsDigit(cls[0])) cls = L.T("Indicador") + cls;
        var path = Path.Combine(AppPaths.UserIndicators, cls + ".cs");
        if (!File.Exists(path)) File.WriteAllText(path, IndicatorTemplate(cls, name));
        FillFiles(path);
    }

    private static string IndicatorTemplate(string cls, string display) => L.F("using System.ComponentModel;\nusing System.ComponentModel.DataAnnotations;\nusing TradingHub.Indicators;\nusing TradingHub.Indicators.Drawing;\n\n[DisplayName(\"{0}\")]\n[Category(\"Mis indicadores\")]\npublic class {1} : Indicator\n{{\n    private readonly ValueDataSeries _serie = new(\"Serie\", \"Valor\") {{ Color = System.Drawing.Color.Orange, Width = 2 }};\n\n    [Display(Name = \"Período\", GroupName = \"Configuración\")]\n    [Range(1, 1000)]\n    public int Periodo {{ get; set; }} = 20;\n\n    public {2}()\n    {{\n        DataSeries[0] = _serie;\n    }}\n\n    protected override void OnCalculate(int bar, decimal value)\n    {{\n        // media simple del cierre como ejemplo\n        if (bar < Periodo) return;\n        decimal suma = 0;\n        for (int i = 0; i < Periodo; i++) suma += GetCandle(bar - i).Close;\n        _serie[bar] = suma / Periodo;\n    }}\n}}",display,cls,cls);
}
