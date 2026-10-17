namespace TradingHub.Market;

/// <summary>
/// Un instrumento operable (BTCUSDT en Binance Futures, ES en el simulador, etc.).
/// Los precios se manejan internamente como múltiplos enteros del tick ("ticks").
/// </summary>
public sealed class Instrument
{
    public required string Symbol { get; init; }

    /// <summary>Identificador del feed que lo provee (ej. "binance-futures").</summary>
    public required string FeedId { get; init; }

    /// <summary>Nombre legible del mercado (ej. "Binance Futures").</summary>
    public string Exchange { get; init; } = "";

    public required decimal TickSize { get; init; }

    /// <summary>Paso mínimo de volumen (0.001 BTC, 1 contrato, ...).</summary>
    public decimal VolumeStep { get; init; } = 1m;

    /// <summary>Valor monetario de 1 punto por 1 unidad de volumen (para PnL).</summary>
    public decimal PointValue { get; init; } = 1m;

    public string BaseAsset { get; init; } = "";
    public string QuoteAsset { get; init; } = "";
    public string Description { get; init; } = "";

    public string Key => $"{FeedId}:{Symbol}";

    public int PriceDecimals => DecimalPlaces(TickSize);
    public int VolumeDecimals => DecimalPlaces(VolumeStep);

    public long ToTicks(decimal price) => (long)Math.Round(price / TickSize, MidpointRounding.AwayFromZero);
    public decimal FromTicks(long ticks) => ticks * TickSize;

    public decimal RoundPrice(decimal price) => FromTicks(ToTicks(price));

    public string FormatPrice(decimal price) => price.ToString("F" + PriceDecimals, System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => Symbol;

    public static int DecimalPlaces(decimal value)
    {
        value = Math.Abs(value);
        if (value == 0) return 0;
        var normalized = value / 1.0000000000000000000000000000m; // quita ceros a la derecha
        var bits = decimal.GetBits(normalized);
        return (bits[3] >> 16) & 0xFF;
    }
}
