using Avalonia.Controls;
using Avalonia.Input;

namespace TradingHub.App.Services;

/// <summary>Una acción que se puede disparar con un atajo de teclado.</summary>
public sealed record HotkeyDef(string Id, string Group, string Name, string? DefaultGesture = null);

/// <summary>
/// Atajos de teclado configurables (Ajustes → Atajos de teclado). Los de operar vienen sin asignar a
/// propósito: cada uno elige los suyos. En cuentas reales siguen pidiendo confirmación si está activada.
/// </summary>
public static class Hotkeys
{
    public static readonly HotkeyDef[] All =
    {
        new("buy-market", L.T("Operar"), L.T("Comprar a mercado")),
        new("sell-market", L.T("Operar"), L.T("Vender a mercado")),
        new("buy-bid", L.T("Operar"), L.T("Comprar límite en el bid")),
        new("sell-ask", L.T("Operar"), L.T("Vender límite en el ask")),
        new("flatten", L.T("Operar"), L.T("Cerrar la posición")),
        new("reverse", L.T("Operar"), L.T("Dar vuelta la posición")),
        new("cancel-all", L.T("Operar"), L.T("Cancelar todas las órdenes")),
        new("close-25", L.T("Operar"), L.T("Cerrar el 25 %")),
        new("close-50", L.T("Operar"), L.T("Cerrar el 50 %")),
        new("breakeven", L.T("Operar"), L.T("Stop a breakeven")),
        new("brackets", L.T("Operar"), L.T("Poner stop y objetivos de la plantilla")),
        new("qty-next", L.T("Operar"), L.T("Cantidad: siguiente preset")),
        new("qty-prev", L.T("Operar"), L.T("Cantidad: preset anterior")),

        new("period-m1", L.T("Gráfico"), L.T("Período 1 minuto"), "Alt+D1"),
        new("period-m5", L.T("Gráfico"), L.T("Período 5 minutos"), "Alt+D2"),
        new("period-m15", L.T("Gráfico"), L.T("Período 15 minutos"), "Alt+D3"),
        new("period-h1", L.T("Gráfico"), L.T("Período 1 hora"), "Alt+D4"),
        new("period-h4", L.T("Gráfico"), L.T("Período 4 horas"), "Alt+D5"),
        new("period-d1", L.T("Gráfico"), L.T("Período diario"), "Alt+D6"),
        new("style-candles", L.T("Gráfico"), L.T("Estilo velas")),
        new("style-footprint", L.T("Gráfico"), L.T("Estilo footprint")),
        new("style-heikin", L.T("Gráfico"), L.T("Estilo Heikin Ashi")),
        new("autoscale", L.T("Gráfico"), L.T("Escala automática")),
        new("go-last", L.T("Gráfico"), L.T("Ir al último precio")),
        new("tool-hline", L.T("Gráfico"), L.T("Dibujar línea horizontal"), "Alt+H"),
        new("tool-cursor", L.T("Gráfico"), L.T("Cursor (soltar herramienta)")),

        new("layout-1", L.T("Ventanas"), L.T("Diseño de un gráfico")),
        new("layout-4", L.T("Ventanas"), L.T("Diseño de cuatro gráficos")),
        new("next-tab", L.T("Ventanas"), L.T("Pestaña siguiente"), "Ctrl+Tab"),
        new("prev-tab", L.T("Ventanas"), L.T("Pestaña anterior"), "Ctrl+Shift+Tab"),
        new("detach", L.T("Ventanas"), L.T("Sacar el gráfico a una ventana")),
        new("toggle-dom", L.T("Ventanas"), L.T("Mostrar u ocultar el DOM")),
        new("toggle-trader", L.T("Ventanas"), L.T("Mostrar u ocultar el Chart Trader del gráfico"), "Alt+T"),
        new("toggle-panel", L.T("Ventanas"), L.T("Mostrar u ocultar el panel derecho")),
    };

    public static string? GestureOf(AppSettings s, string id) =>
        s.Hotkeys.TryGetValue(id, out var g) ? (string.IsNullOrEmpty(g) ? null : g) : All.FirstOrDefault(h => h.Id == id)?.DefaultGesture;

    /// <summary>Texto lindo para mostrar un atajo (⌥1, ⇧B, F5… en la Mac; Alt+1, Shift+B, F5… en Windows).</summary>
    public static string Pretty(string? gesture) => Pretty(gesture, mac: !Os.IsWindows);

    internal static string Pretty(string? gesture, bool mac)
    {
        if (string.IsNullOrEmpty(gesture)) return "—";
        try
        {
            var g = KeyGesture.Parse(gesture);
            string key = g.Key switch
            {
                >= Key.D0 and <= Key.D9 => ((int)(g.Key - Key.D0)).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(g.Key - Key.NumPad0),
                Key.Space => L.T("Espacio"),
                Key.Return => mac ? "↩" : "Enter",
                Key.Tab => "Tab",
                Key.Up => "↑",
                Key.Down => "↓",
                Key.Left => "←",
                Key.Right => "→",
                _ => g.Key.ToString(),
            };
            return Keys.Format(g.KeyModifiers, key, mac);
        }
        catch
        {
            return gesture;
        }
    }

    /// <summary>Atajo a partir de una tecla presionada (null si es solo un modificador).</summary>
    public static string? FromKey(Key key, KeyModifiers mods)
    {
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return null;
        return new KeyGesture(key, mods).ToString();
    }

    /// <summary>Acción que corresponde a la tecla, o null. No interfiere con lo que se escribe en un campo de texto.</summary>
    public static string? Match(AppSettings s, KeyEventArgs e, TopLevel? top)
    {
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        bool typing = top?.FocusManager?.GetFocusedElement() is TextBox;
        bool strongModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        bool functionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (typing && !strongModifier && !functionKey) return null;
        foreach (var h in All)
        {
            var g = GestureOf(s, h.Id);
            if (g == null) continue;
            try
            {
                if (KeyGesture.Parse(g).Matches(e)) return h.Id;
            }
            catch
            {
                // atajo mal guardado: se ignora
            }
        }

        return null;
    }
}
