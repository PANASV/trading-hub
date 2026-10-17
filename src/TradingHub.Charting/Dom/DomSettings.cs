using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingHub.Charting.Dom;

public enum DomColumn
{
    [Display(Name = "Órdenes de compra")] BuyOrders,
    [Display(Name = "Bid (libro)")] Bid,
    [Display(Name = "Precio")] Price,
    [Display(Name = "Ask (libro)")] Ask,
    [Display(Name = "Órdenes de venta")] SellOrders,
    [Display(Name = "Volumen de la sesión")] Volume,
    [Display(Name = "Delta de la sesión")] Delta,
    [Display(Name = "Operado al bid")] TradedBid,
    [Display(Name = "Operado al ask")] TradedAsk,
}

/// <summary>Una columna del DOM: si se ve y cuánto ancho relativo ocupa.</summary>
public sealed class DomColumnSetting
{
    public DomColumn Column { get; set; }
    public bool Visible { get; set; } = true;
    public double Width { get; set; } = 1;
}

/// <summary>Todo lo configurable del DOM.</summary>
public sealed class DomSettings
{
    [Browsable(false)]
    public List<DomColumnSetting> Columns { get; set; } = DefaultColumns();

    [Display(Name = "Alto de fila (px)", GroupName = "Tamaño", Order = 10)]
    [Range(12, 40)]
    public int RowHeight { get; set; } = 18;

    [Display(Name = "Tamaño de letra", GroupName = "Tamaño", Order = 20)]
    [Range(8, 20)]
    public int FontSize { get; set; } = 12;

    [Display(Name = "Mostrar encabezados", GroupName = "Tamaño", Order = 30)]
    public bool ShowHeader { get; set; } = true;

    [Display(Name = "Centrar en el precio automáticamente", GroupName = "Comportamiento", Order = 10)]
    public bool AutoCenter { get; set; } = true;

    [Display(Name = "Doble clic para operar", GroupName = "Comportamiento", Order = 20, Description = "Evita mandar órdenes por un clic accidental.")]
    public bool DoubleClickToTrade { get; set; }

    [Display(Name = "Resaltar tamaños desde", GroupName = "Comportamiento", Order = 30, Description = "Tamaños del libro iguales o mayores se resaltan (0 = no).")]
    public decimal LargeSize { get; set; }

    [Display(Name = "Barras del libro", GroupName = "Comportamiento", Order = 40, Description = "Dibuja barras proporcionales al tamaño de cada nivel.")]
    public bool BookBars { get; set; } = true;

    [Display(Name = "Bid", GroupName = "Colores", Order = 10)]
    public Color BidColor { get; set; } = Color.FromArgb(255, 30, 110, 200);

    [Display(Name = "Ask", GroupName = "Colores", Order = 20)]
    public Color AskColor { get; set; } = Color.FromArgb(255, 200, 50, 60);

    [Display(Name = "Fondo debajo del precio", GroupName = "Colores", Order = 30)]
    public Color BidBackground { get; set; } = Color.FromArgb(255, 10, 22, 40);

    [Display(Name = "Fondo encima del precio", GroupName = "Colores", Order = 40)]
    public Color AskBackground { get; set; } = Color.FromArgb(255, 40, 14, 18);

    [Display(Name = "Fondo entre bid y ask", GroupName = "Colores", Order = 50)]
    public Color SpreadBackground { get; set; } = Color.FromArgb(255, 16, 18, 26);

    [Display(Name = "Último precio", GroupName = "Colores", Order = 60)]
    public Color LastPriceColor { get; set; } = Color.FromArgb(255, 242, 181, 58);

    [Display(Name = "Volumen comprador", GroupName = "Colores", Order = 70)]
    public Color BuyVolumeColor { get; set; } = Color.FromArgb(150, 38, 194, 129);

    [Display(Name = "Volumen vendedor", GroupName = "Colores", Order = 80)]
    public Color SellVolumeColor { get; set; } = Color.FromArgb(150, 240, 82, 90);

    [Display(Name = "Texto de precios", GroupName = "Colores", Order = 90)]
    public Color PriceText { get; set; } = Color.FromArgb(255, 160, 175, 200);

    public static List<DomColumnSetting> DefaultColumns() => new()
    {
        new() { Column = DomColumn.BuyOrders, Width = 0.55 },
        new() { Column = DomColumn.Bid, Width = 1 },
        new() { Column = DomColumn.Price, Width = 1.05 },
        new() { Column = DomColumn.Ask, Width = 1 },
        new() { Column = DomColumn.SellOrders, Width = 0.55 },
        new() { Column = DomColumn.Volume, Width = 1.25 },
        new() { Column = DomColumn.Delta, Width = 0.9, Visible = false },
        new() { Column = DomColumn.TradedBid, Width = 0.9, Visible = false },
        new() { Column = DomColumn.TradedAsk, Width = 0.9, Visible = false },
    };

    /// <summary>Completa columnas que falten (configuraciones guardadas con versiones anteriores).</summary>
    public void Normalize()
    {
        foreach (var d in DefaultColumns())
            if (Columns.All(c => c.Column != d.Column)) Columns.Add(d);
        Columns = Columns.GroupBy(c => c.Column).Select(g => g.First()).ToList();
        if (!Columns.Any(c => c.Visible)) Columns.First(c => c.Column == DomColumn.Price).Visible = true;
    }

    public DomSettings Clone()
    {
        var c = (DomSettings)MemberwiseClone();
        c.Columns = Columns.Select(x => new DomColumnSetting { Column = x.Column, Visible = x.Visible, Width = x.Width }).ToList();
        return c;
    }
}

/// <summary>Guarda colores como #AARRGGBB en JSON.</summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            if (s.StartsWith('#') && uint.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out var argb)) return Color.FromArgb((int)argb);
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            return Color.FromArgb(reader.GetInt32());
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // formato viejo: se ignora
            using var _ = JsonDocument.ParseValue(ref reader);
        }

        return Color.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"#{(uint)value.ToArgb():X8}");
}
