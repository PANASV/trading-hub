namespace TradingHub.Strategies;

/// <summary>Trazo de una línea o borde.</summary>
public enum LineStyle
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>Forma de una marca en el gráfico.</summary>
public enum MarkerShape
{
    ArrowUp,
    ArrowDown,
    Circle,
    Square,
    Diamond,
    Cross,
}

/// <summary>Qué es un dibujo de la estrategia.</summary>
public enum DrawingKind
{
    Line,
    Box,
    HorizontalLine,
    Marker,
    Text,
}

/// <summary>
/// Un dibujo de la estrategia en el gráfico (rango, nivel, flecha, texto…). Los tiempos son UTC (como Bar.Time) y los
/// colores, "#RRGGBB", "#AARRGGBB" o un nombre ("verde", "rojo", "azul", "amarillo", "naranja", "violeta", "gris",
/// "blanco", "celeste", "rosa"); sin color, el de la estrategia.
/// </summary>
public sealed class StrategyDrawing
{
    public required string Id { get; init; }
    public DrawingKind Kind { get; init; }
    public DateTime Time1 { get; init; }
    public decimal Price1 { get; init; }
    public DateTime Time2 { get; init; }
    public decimal Price2 { get; init; }
    public string? Color { get; init; }

    /// <summary>Relleno de los rectángulos: opacidad de 0 a 100.</summary>
    public int FillOpacity { get; init; }

    public int Width { get; init; } = 1;
    public LineStyle Style { get; init; }
    public MarkerShape Shape { get; init; }
    public string Text { get; init; } = "";

    /// <summary>La línea horizontal sigue hasta el borde derecho del gráfico.</summary>
    public bool ExtendRight { get; init; }
}

/// <summary>Una línea continua que la estrategia traza vela a vela (por ejemplo el techo y el piso de un canal).</summary>
public sealed class StrategyPlot
{
    public required string Name { get; init; }
    public string? Color { get; set; }
    public int Width { get; set; } = 1;
    public LineStyle Style { get; set; }

    /// <summary>Puntos en orden de tiempo; un valor null corta la línea.</summary>
    public List<(DateTime Time, decimal? Value)> Points { get; } = new();
}

/// <summary>
/// Lo que la estrategia dibuja: un dibujo por id (repetir el id lo reemplaza, sirve para estirar el rango de hoy vela a
/// vela) y líneas continuas por nombre. Tiene un tope para no trabar el gráfico: al pasarlo se van los más viejos.
/// </summary>
public sealed class StrategyCanvas
{
    public const int MaxDrawings = 3000, MaxPlots = 20, MaxPointsPerPlot = 300_000;

    private readonly Dictionary<string, StrategyDrawing> _drawings = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
    private readonly Dictionary<string, StrategyPlot> _plots = new();
    private readonly object _lock = new();

    /// <summary>Sube con cada cambio (para redibujar solo cuando hace falta).</summary>
    public int Version { get; private set; }

    public void Set(StrategyDrawing d)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(d.Id, out var node)) _order.Remove(node);
            _drawings[d.Id] = d;
            _nodes[d.Id] = _order.AddLast(d.Id);
            while (_drawings.Count > MaxDrawings && _order.First is { } first)
            {
                _drawings.Remove(first.Value);
                _nodes.Remove(first.Value);
                _order.RemoveFirst();
            }

            Version++;
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (!_drawings.Remove(id)) return;
            if (_nodes.Remove(id, out var node)) _order.Remove(node);
            Version++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _drawings.Clear();
            _nodes.Clear();
            _order.Clear();
            _plots.Clear();
            Version++;
        }
    }

    public void Plot(string name, DateTime time, decimal? value, string? color, int width, LineStyle style)
    {
        lock (_lock)
        {
            if (!_plots.TryGetValue(name, out var p))
            {
                if (_plots.Count >= MaxPlots) return;
                _plots[name] = p = new StrategyPlot { Name = name };
            }

            p.Color = color ?? p.Color;
            p.Width = width;
            p.Style = style;
            // la misma vela otra vez (por ejemplo, al recalcular): se reemplaza el valor
            if (p.Points.Count > 0 && p.Points[^1].Time >= time)
            {
                if (p.Points[^1].Time == time) p.Points[^1] = (time, value);
            }
            else p.Points.Add((time, value));
            if (p.Points.Count > MaxPointsPerPlot) p.Points.RemoveRange(0, p.Points.Count - MaxPointsPerPlot);
            Version++;
        }
    }

    /// <summary>Copia de los dibujos (para pintarlos desde otro hilo).</summary>
    public List<StrategyDrawing> Drawings()
    {
        lock (_lock) return _order.Select(id => _drawings[id]).ToList();
    }

    /// <summary>Copia de las líneas continuas.</summary>
    public List<(string Name, string? Color, int Width, LineStyle Style, List<(DateTime Time, decimal? Value)> Points)> Plots()
    {
        lock (_lock) return _plots.Values.Select(p => (p.Name, p.Color, p.Width, p.Style, p.Points.ToList())).ToList();
    }
}
