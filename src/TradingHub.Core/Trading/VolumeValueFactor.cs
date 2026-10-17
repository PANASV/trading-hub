namespace TradingHub.Indicators;

/// <summary>Factor volumen→dinero de un instrumento (y su moneda). Vive en el núcleo para que Valuation lo use.</summary>
public readonly struct VolumeValueFactor
{
    public VolumeValueFactor(decimal factor, global::TradingHub.Trading.MoneyCurrency? currency = null)
    {
        Factor = factor;
        Currency = currency;
    }

    public decimal Factor { get; }
    public global::TradingHub.Trading.MoneyCurrency? Currency { get; }

    public decimal Apply(decimal volume, decimal price) => volume * price * Factor;

    public decimal ToMoney(decimal volume, decimal price) => volume * price * Factor;

    public decimal ToVolume(decimal money, decimal price) => price * Factor == 0 ? 0 : money / (price * Factor);
}
