namespace TradingHub.Market;

/// <summary>Lado agresor de una operación.</summary>
public enum TradeSide : byte
{
    /// <summary>Desconocido / entre bid y ask.</summary>
    None = 0,

    /// <summary>Comprador agresivo: pegó contra el ask (suma al volumen Ask).</summary>
    Buy = 1,

    /// <summary>Vendedor agresivo: pegó contra el bid (suma al volumen Bid).</summary>
    Sell = 2,
}

/// <summary>Una operación (tick) del tape.</summary>
public readonly struct Trade
{
    public readonly long Id;
    public readonly DateTime Time; // UTC
    public readonly decimal Price;
    public readonly decimal Volume;
    public readonly TradeSide Side;

    public Trade(long id, DateTime time, decimal price, decimal volume, TradeSide side)
    {
        Id = id;
        Time = time;
        Price = price;
        Volume = volume;
        Side = side;
    }

    public override string ToString() => $"{Time:HH:mm:ss.fff} {Side} {Volume} @ {Price} (#{Id})";
}
