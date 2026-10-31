using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>Crear o editar un portafolio en vivo: nombre, capital de referencia y qué estrategias corren juntas.</summary>
internal sealed class LivePortfolioWindow : Window
{
    private readonly AppServices _svc;
    private readonly LivePortfolioDef _def;
    private readonly TextBox _name = new();
    private readonly NumericUpDown _capital = new()
    {
        Minimum = 100,
        Maximum = 1_000_000_000,
        Increment = 1000,
        FormatString = "#,##0",
        NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly List<(CheckBox Box, string Key)> _members = new();

    public LivePortfolioWindow(AppServices svc, LivePortfolioDef? def)
    {
        _svc = svc;
        _def = def ?? new LivePortfolioDef { Name = "Portafolio " + (LiveStore.LoadPortfolios().Count(d => !d.Auto) + 1) };
        bool isNew = def == null;
        Title = isNew ? L.T("Nuevo portafolio en vivo") : L.F("Portafolio · {0}",_def.Name);
        Width = 560;
        Height = 660;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;
        _name.Text = _def.Name;
        _name.IsEnabled = !_def.Auto;
        _capital.Value = _def.Capital;

        var list = new StackPanel { Spacing = 6 };
        var entries = svc.Live.Entries();
        var others = LiveStore.LoadPortfolios().Where(d => !d.Auto && d.Id != _def.Id).ToList();
        foreach (var e in entries.OrderByDescending(e => e.Running).ThenBy(e => e.Info.Strategy))
        {
            var other = others.FirstOrDefault(d => d.Members.Contains(e.Key));
            var box = new CheckBox
            {
                IsChecked = _def.Members.Contains(e.Key),
                IsEnabled = !_def.Auto,
                Content = Ui.V(1,
                    new TextBlock { Text = $"{e.Info.Strategy} · {e.Info.Symbol} {e.Info.Period}", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = $"{e.Info.Account} · {e.SourceLabel} · {e.StateLabel}{(other != null ? L.F(" · hoy en «{0}» (pasa a este)",other.Name) : "")}{LockedNote(e)}",
                        Classes = { "faint", "small" },
                        TextWrapping = TextWrapping.Wrap,
                    }),
            };
            _members.Add((box, e.Key));
            list.Children.Add(box);
        }

        if (_members.Count == 0) list.Children.Add(new TextBlock { Text = L.T("Todavía no hay estrategias en vivo para agregar. Lanzá una con «Nueva estrategia en vivo»."), Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });

        var body = new StackPanel { Spacing = 16, Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(Ui.H2(isNew ? L.T("Nuevo portafolio") : _def.Name));
        if (_def.Auto)
            body.Children.Add(new TextBlock { Text = L.T("El automático junta solo todas las estrategias que corren en vivo sin un portafolio tuyo. Podés cambiar su capital de referencia y su agente de riesgo."), Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Ui.Field(L.T("Nombre"), _name));
        body.Children.Add(Ui.Field(L.T("Capital de referencia"), _capital, L.T("Para los porcentajes, el drawdown y el Sharpe del portafolio (y el presupuesto de riesgo del agente).")));
        body.Children.Add(Ui.V(8, Ui.H3(L.T("Estrategias")), new TextBlock { Text = L.T("Una estrategia está en un solo portafolio tuyo a la vez. Para sumar una nueva, lanzala con «Nueva estrategia en vivo» eligiendo este portafolio."), Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap }, list));

        var save = Ui.Button(isNew ? L.T("Crear portafolio") : L.T("Guardar"), MaterialIconKind.Check, Save, "primary");
        var cancel = Ui.Button(L.T("Cancelar"), MaterialIconKind.Close, () => Close(null));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 10, 22, 16) };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        var bottom = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(null);
        };
    }

    private void Save()
    {
        if (!_def.Auto)
        {
            _def.Name = string.IsNullOrWhiteSpace(_name.Text) ? _def.Name : _name.Text.Trim();
            _def.Members = _members.Where(m => m.Box.IsChecked == true).Select(m => m.Key).ToList();
        }

        _def.Capital = _capital.Value ?? _def.Capital;
        _svc.Live.SavePortfolio(_def);
        Close(_def.Id);
    }

    private static string LockedNote(LiveEntry e) =>
        "";
}
