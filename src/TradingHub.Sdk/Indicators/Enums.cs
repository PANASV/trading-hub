using System.ComponentModel.DataAnnotations;

namespace TradingHub.Indicators
{
    /// <summary>Marca una propiedad como parámetro de cálculo.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ParameterAttribute : Attribute
    {
    }

    /// <summary>Cómo se dibuja una serie de valores.</summary>
    public enum VisualMode
    {
        [Display(Name = "Línea")] Line,
        [Display(Name = "Histograma")] Histogram,
        [Display(Name = "Hash")] Hash,
        [Display(Name = "Cruz")] Cross,
        [Display(Name = "Puntos")] Dots,
        [Display(Name = "Bloque")] Block,
        [Display(Name = "Cuadrado")] Square,
        [Display(Name = "Flecha arriba")] UpArrow,
        [Display(Name = "Flecha abajo")] DownArrow,
        [Display(Name = "Solo valor en eje")] OnlyValueOnAxis,
        [Display(Name = "Oculto")] Hide,
        [Display(Name = "Bandas")] Bands,
        [Display(Name = "Puntos (línea)")] Points,
        [Display(Name = "Niveles")] Levels,
        [Display(Name = "Escalones")] Steps,
    }

    public enum LineDashStyle
    {
        [Display(Name = "Sólida")] Solid,
        [Display(Name = "Guiones")] Dash,
        [Display(Name = "Puntos")] Dot,
        [Display(Name = "Guión-punto")] DashDot,
        [Display(Name = "Guión-punto-punto")] DashDotDot,
    }

    /// <summary>Valor de la vela que recibe OnCalculate como "value".</summary>
    public enum DataSeriesType
    {
        Open,
        High,
        Low,
        Close,
        HL2,
        HLC3,
        OHLC4,
        Volume,
        Delta,
        Ticks,
        Bid,
        Ask,
    }

    /// <summary>Lado de una operación (como lo reportan los indicadores).</summary>
    public enum TradeDirection
    {
        Between = 0,
        Buy = 1,
        Sell = 2,
    }

    public enum MarketDataType
    {
        Trade = 0,
        Bid = 1,
        Ask = 2,
    }

    public enum ChartVisualModes
    {
        Candles,
        Clusters,
        Line,
        Bars,
        Mountain,
        Heatmap,
    }

    public enum CumulativeTradeStates
    {
        Active,
        Finished,
    }
}
