using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;

namespace TradingHub.App.Controls;

/// <summary>
/// Botón compacto que muestra un ícono y el valor elegido, y abre un menú con las opciones.
/// Ocupa mucho menos que un ComboBox con texto largo.
/// </summary>
public sealed class ChoiceButton<T> : Button
{
    private readonly List<(T Value, string Label, MenuItem Item)> _options = new();
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.None };
    private readonly MenuFlyout _menu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly Func<T, string>? _buttonLabel;
    private T? _selected;

    /// <param name="buttonLabel">Texto corto para el botón (si no, usa el mismo del menú).</param>
    public ChoiceButton(MaterialIconKind icon, IEnumerable<(T Value, string Label)> options, string tip, Func<T, string>? buttonLabel = null)
    {
        _buttonLabel = buttonLabel;
        Content = Ui.H(7, Ui.Icon(icon, 16, Ui.Text1), _label, Ui.Icon(MaterialIconKind.ChevronDown, 14, Ui.Text2));
        ToolTip.SetTip(this, tip);

        // los ítems se crean una sola vez, antes de abrir el menú (si se cargan al abrir, el menú sale vacío)
        foreach (var (value, label) in options)
        {
            var item = new MenuItem { Header = label };
            var v = value;
            item.Click += (_, _) =>
            {
                Select(v);
                Changed?.Invoke(v);
            };
            _menu.Items.Add(item);
            _options.Add((value, label, item));
        }

        Flyout = _menu;
        if (_options.Count > 0) Select(_options[0].Value);
    }

    // usa el tema y los estilos de Button (fondo, relleno, hover y área de clic completa)
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>El usuario eligió una opción.</summary>
    public event Action<T>? Changed;

    public T? Selected => _selected;

    /// <summary>Muestra un texto en el botón sin cambiar la opción marcada (por ejemplo, un rango a medida).</summary>
    public void ShowText(string text) => _label.Text = text;

    public void Select(T value)
    {
        _selected = value;
        foreach (var (v, label, item) in _options)
        {
            bool current = EqualityComparer<T>.Default.Equals(v, value);
            item.Icon = current ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null;
            if (current) _label.Text = _buttonLabel?.Invoke(v) ?? label;
        }
    }
}
