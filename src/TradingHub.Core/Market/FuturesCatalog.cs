namespace TradingHub.Market;

/// <summary>Un futuro de CME Group: raíz, bolsa (como la pide Rithmic), tick, valor de un punto, nombre y rubro.</summary>
public sealed record FuturesProduct(string Root, string Exchange, decimal Tick, decimal PointValue, string Name, string Sector);

/// <summary>
/// Los futuros de CME Group que se operan por Rithmic y Tradovate: índices, energía, metales, monedas, tasas, granos,
/// ganadería y cripto. Precios como los manda la bolsa (los granos en centavos por bushel).
/// </summary>
public static class FuturesCatalog
{
    public static IReadOnlyList<FuturesProduct> All { get; } = new FuturesProduct[]
    {
        // índices
        new("ES", "CME", 0.25m, 50m, "E-mini S&P 500", "Índices"),
        new("MES", "CME", 0.25m, 5m, "Micro E-mini S&P 500", "Índices"),
        new("NQ", "CME", 0.25m, 20m, "E-mini Nasdaq-100", "Índices"),
        new("MNQ", "CME", 0.25m, 2m, "Micro E-mini Nasdaq-100", "Índices"),
        new("YM", "CBOT", 1m, 5m, "E-mini Dow", "Índices"),
        new("MYM", "CBOT", 1m, 0.5m, "Micro E-mini Dow", "Índices"),
        new("RTY", "CME", 0.1m, 50m, "E-mini Russell 2000", "Índices"),
        new("M2K", "CME", 0.1m, 5m, "Micro E-mini Russell 2000", "Índices"),
        new("EMD", "CME", 0.1m, 100m, "E-mini S&P MidCap 400", "Índices"),
        new("NKD", "CME", 5m, 5m, "Nikkei 225 en dólares", "Índices"),

        // energía
        new("CL", "NYMEX", 0.01m, 1000m, "Crudo WTI", "Energía"),
        new("MCL", "NYMEX", 0.01m, 100m, "Micro crudo WTI", "Energía"),
        new("QM", "NYMEX", 0.025m, 500m, "E-mini crudo WTI", "Energía"),
        new("BZ", "NYMEX", 0.01m, 1000m, "Crudo Brent", "Energía"),
        new("NG", "NYMEX", 0.001m, 10000m, "Gas natural Henry Hub", "Energía"),
        new("QG", "NYMEX", 0.005m, 2500m, "E-mini gas natural", "Energía"),
        new("HO", "NYMEX", 0.0001m, 42000m, "Gasoil (heating oil)", "Energía"),
        new("RB", "NYMEX", 0.0001m, 42000m, "Nafta RBOB", "Energía"),

        // metales
        new("GC", "COMEX", 0.1m, 100m, "Oro", "Metales"),
        new("MGC", "COMEX", 0.1m, 10m, "Micro oro", "Metales"),
        new("SI", "COMEX", 0.005m, 5000m, "Plata", "Metales"),
        new("SIL", "COMEX", 0.005m, 1000m, "Micro plata", "Metales"),
        new("HG", "COMEX", 0.0005m, 25000m, "Cobre", "Metales"),
        new("MHG", "COMEX", 0.0005m, 2500m, "Micro cobre", "Metales"),
        new("PL", "NYMEX", 0.1m, 50m, "Platino", "Metales"),
        new("PA", "NYMEX", 0.5m, 100m, "Paladio", "Metales"),

        // monedas
        new("6E", "CME", 0.00005m, 125000m, "Euro FX", "Monedas"),
        new("M6E", "CME", 0.0001m, 12500m, "Micro euro", "Monedas"),
        new("6B", "CME", 0.0001m, 62500m, "Libra esterlina", "Monedas"),
        new("M6B", "CME", 0.0001m, 6250m, "Micro libra", "Monedas"),
        new("6J", "CME", 0.0000005m, 12500000m, "Yen japonés", "Monedas"),
        new("6A", "CME", 0.00005m, 100000m, "Dólar australiano", "Monedas"),
        new("M6A", "CME", 0.0001m, 10000m, "Micro dólar australiano", "Monedas"),
        new("6C", "CME", 0.00005m, 100000m, "Dólar canadiense", "Monedas"),
        new("6S", "CME", 0.00005m, 125000m, "Franco suizo", "Monedas"),
        new("6N", "CME", 0.00005m, 100000m, "Dólar neozelandés", "Monedas"),
        new("6M", "CME", 0.00001m, 500000m, "Peso mexicano", "Monedas"),

        // tasas
        new("ZT", "CBOT", 0.00390625m, 2000m, "Bono del Tesoro 2 años", "Tasas"),
        new("ZF", "CBOT", 0.0078125m, 1000m, "Bono del Tesoro 5 años", "Tasas"),
        new("ZN", "CBOT", 0.015625m, 1000m, "Bono del Tesoro 10 años", "Tasas"),
        new("TN", "CBOT", 0.015625m, 1000m, "Ultra bono 10 años", "Tasas"),
        new("ZB", "CBOT", 0.03125m, 1000m, "Bono del Tesoro 30 años", "Tasas"),
        new("UB", "CBOT", 0.03125m, 1000m, "Ultra bono del Tesoro", "Tasas"),
        new("SR3", "CME", 0.0025m, 2500m, "SOFR a 3 meses", "Tasas"),

        // granos (centavos por bushel; harina en dólares por tonelada; aceite en centavos por libra)
        new("ZC", "CBOT", 0.25m, 50m, "Maíz", "Granos"),
        new("ZS", "CBOT", 0.25m, 50m, "Soja", "Granos"),
        new("ZW", "CBOT", 0.25m, 50m, "Trigo", "Granos"),
        new("KE", "CBOT", 0.25m, 50m, "Trigo duro de Kansas", "Granos"),
        new("ZM", "CBOT", 0.1m, 100m, "Harina de soja", "Granos"),
        new("ZL", "CBOT", 0.01m, 600m, "Aceite de soja", "Granos"),
        new("ZO", "CBOT", 0.25m, 50m, "Avena", "Granos"),

        // ganadería (centavos por libra)
        new("LE", "CME", 0.025m, 400m, "Ganado en pie", "Ganadería"),
        new("GF", "CME", 0.025m, 500m, "Ganado de engorde", "Ganadería"),
        new("HE", "CME", 0.025m, 400m, "Cerdo magro", "Ganadería"),

        // cripto de la CME
        new("BTC", "CME", 5m, 5m, "Bitcoin (CME)", "Cripto CME"),
        new("MBT", "CME", 5m, 0.1m, "Micro bitcoin (CME)", "Cripto CME"),
        new("ETH", "CME", 0.5m, 50m, "Ether (CME)", "Cripto CME"),
        new("MET", "CME", 0.5m, 0.1m, "Micro ether (CME)", "Cripto CME"),
    };

    private static readonly Dictionary<string, FuturesProduct> ByRoot = All.ToDictionary(p => p.Root, StringComparer.OrdinalIgnoreCase);

    public static FuturesProduct? Find(string root) => ByRoot.TryGetValue(root.Trim(), out var p) ? p : null;
}
