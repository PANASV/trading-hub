using System.Globalization;
using System.Text.RegularExpressions;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Importa velas de futuros de CME bajadas con IB Gateway (un CSV por día de trading, por ejemplo
/// <c>mnq1m_2026-10-30.csv</c> con <c>time,open,high,low,close,volume[,contract]</c> y <c>time</c> en segundos Unix UTC).
/// Las guarda como la fuente <see cref="FeedId"/> («ib»), que se trata como un futuro de CME: tick y valor del punto del
/// catálogo, sesión de las 18:00 de Nueva York y comisión de futuros en los backtests. Las velas se convierten en
/// operaciones sintéticas (cuatro por vela), así que sirven para gráficos y backtests de precio, no para flujo de órdenes.
/// </summary>
public static class IbBarImport
{
    /// <summary>Carpeta y fuente de la historia importada de IB.</summary>
    public const string FeedId = "ib";

    /// <summary>Velas leídas (las de volumen cero, minutos sin operaciones, no cuentan: no se guardan).</summary>
    public sealed record Result(string Symbol, int Files, long Bars, int Days, DateTime? From, DateTime? To, int BadLines, IReadOnlyList<string> Contracts);

    private static readonly Regex DatedFile = new(@"^(?<root>[A-Za-z][A-Za-z0-9]*)_(?<date>\d{4}-\d{2}-\d{2})\.csv$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// El símbolo de CME por el nombre de los archivos (mnq1m_… → MNQ), o null si no es un futuro conocido.
    /// </summary>
    public static string? SymbolFromFileName(string fileName)
    {
        var m = DatedFile.Match(Path.GetFileName(fileName));
        if (!m.Success) return null;
        string letters = m.Groups["root"].Value.ToUpperInvariant();
        // el nombre puede traer el período pegado (mnq1m → MNQ, m2k1m → M2K): se prueba de más largo a más corto
        for (int n = letters.Length; n >= 1; n--)
            if (FuturesCatalog.Find(letters[..n]) != null)
                return letters[..n];
        return null;
    }

    /// <summary>Los archivos de una carpeta con nombre «algo_AAAA-MM-DD.csv», en orden de fecha.</summary>
    public static List<string> FilesIn(string folder) =>
        Directory.EnumerateFiles(folder, "*.csv")
            .Where(f => DatedFile.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => DatedFile.Match(Path.GetFileName(f)).Groups["date"].Value, StringComparer.Ordinal)
            .ToList();

    /// <summary>Ficha de CME del símbolo (tick y valor del punto del catálogo).</summary>
    public static Instrument InstrumentFor(string symbol)
    {
        var spec = FuturesCatalog.Find(symbol) ?? throw new InvalidDataException($"{symbol} no es un futuro de CME conocido");
        return new Instrument { Symbol = spec.Root, FeedId = FeedId, Exchange = spec.Exchange, TickSize = spec.Tick, PointValue = spec.PointValue, VolumeStep = 1 };
    }

    /// <summary>
    /// Importa todos los CSV de la carpeta. Cada día UTC se reescribe completo, así que se puede volver a correr
    /// después de bajar días nuevos (o corregidos) sin duplicar nada.
    /// </summary>
    /// <param name="symbol">Símbolo de CME; null = se deduce del nombre de los archivos.</param>
    public static Result ImportFolder(string folder, string root, string? symbol = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"No existe la carpeta {folder}");
        var files = FilesIn(folder);
        if (files.Count == 0) throw new InvalidDataException("La carpeta no tiene archivos «símbolo_AAAA-MM-DD.csv»");
        symbol ??= SymbolFromFileName(files[0]) ?? throw new InvalidDataException($"No reconozco el futuro por el nombre {Path.GetFileName(files[0])}; indicá el símbolo (por ejemplo MNQ)");
        return Import(files, root, symbol, progress, ct);
    }

    public static Result Import(IReadOnlyList<string> files, string root, string symbol, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var inst = InstrumentFor(symbol);

        // 1) leer todo: un día de trading de CME (18:00 NY → 17:00 NY) cae en dos días UTC, así que se agrupa después
        var bars = new List<Kline>(files.Count * 1400);
        var contracts = new SortedSet<string>(StringComparer.Ordinal);
        int bad = 0;
        long stepMs = 0;
        for (int fi = 0; fi < files.Count; fi++)
        {
            ct.ThrowIfCancellationRequested();
            bad += ReadFile(files[fi], bars, contracts);
            progress?.Report(0.5 * (fi + 1) / files.Count);
        }

        if (bars.Count == 0) throw new InvalidDataException("No se leyó ninguna vela");
        bars.Sort((a, b) => a.OpenTimeMs.CompareTo(b.OpenTimeMs));

        // sin duplicados (dos archivos que se pisan): queda la última versión de cada minuto
        var unique = new List<Kline>(bars.Count);
        foreach (var k in bars)
        {
            if (unique.Count > 0 && unique[^1].OpenTimeMs == k.OpenTimeMs) unique[^1] = k;
            else unique.Add(k);
        }

        for (int i = 1; i < unique.Count; i++)
        {
            long d = unique[i].OpenTimeMs - unique[i - 1].OpenTimeMs;
            if (d > 0 && (stepMs == 0 || d < stepMs)) stepMs = d;
        }

        int minutes = (int)Math.Max(1, (stepMs == 0 ? 60_000 : stepMs) / 60_000);

        // 2) un archivo de historia por día UTC (como el resto de Trading Hub), reemplazando lo que hubiera
        var byDay = unique.GroupBy(k => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime)).ToList();
        var trades = new List<Trade>(4 * 1500);
        int days = 0;
        for (int i = 0; i < byDay.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var day = byDay[i].Key;
            var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            trades.Clear();
            KlineHistory.Synthesize(byDay[i].ToList(), inst, minutes, new[] { (start, start.AddDays(1)) }, trades);
            if (trades.Count == 0) continue;
            HistoricalData.WriteDay(root, inst, day, trades, replace: true);
            days++;
            progress?.Report(0.5 + 0.5 * (i + 1) / byDay.Count);
        }

        return new Result(inst.Symbol, files.Count, unique.Count, days,
            DateTimeOffset.FromUnixTimeMilliseconds(unique[0].OpenTimeMs).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(unique[^1].OpenTimeMs).UtcDateTime,
            bad, contracts.ToList());
    }

    /// <summary>Lee un CSV de velas; devuelve cuántas filas no se pudieron leer.</summary>
    private static int ReadFile(string path, List<Kline> output, ISet<string> contracts)
    {
        using var reader = new StreamReader(path);
        string? header = reader.ReadLine();
        if (header == null) return 0;
        var cols = header.Split(',').Select(c => c.Trim().ToLowerInvariant()).ToArray();
        int t = Array.IndexOf(cols, "time"), o = Array.IndexOf(cols, "open"), h = Array.IndexOf(cols, "high"),
            l = Array.IndexOf(cols, "low"), c = Array.IndexOf(cols, "close"), v = Array.IndexOf(cols, "volume"), k = Array.IndexOf(cols, "contract");
        if (t < 0 || o < 0 || h < 0 || l < 0 || c < 0)
            throw new InvalidDataException($"{Path.GetFileName(path)}: hacen falta las columnas time, open, high, low y close");

        int need = new[] { t, o, h, l, c }.Max();
        int bad = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length <= need)
            {
                bad++;
                continue;
            }

            if (!long.TryParse(f[t].Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)
                || !Num(f[o], out var op) || !Num(f[h], out var hi) || !Num(f[l], out var lo) || !Num(f[c], out var cl))
            {
                bad++;
                continue;
            }

            long ms = secs > 100_000_000_000 ? secs : secs * 1000;
            decimal vol = v >= 0 && v < f.Length && Num(f[v], out var vv) ? vv : 1;
            if (vol <= 0) continue; // minuto sin operaciones (IB lo rellena con el último precio): no hay vela
            if (k >= 0 && k < f.Length && f[k].Trim().Length > 0) contracts.Add(f[k].Trim());
            output.Add(new Kline(ms, op, hi, lo, cl, vol, 0, false));
        }

        return bad;
    }

    private static bool Num(string s, out decimal value) =>
        decimal.TryParse(s.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
