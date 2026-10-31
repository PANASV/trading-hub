using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>
/// Feed sin tiempo real que muestra en el gráfico la historia guardada en disco de una fuente que solo da historia
/// (las velas de 1 minuto importadas de IB Gateway). Los instrumentos son los símbolos que tienen días guardados.
/// Con <see cref="ISessionAwareFeed"/> el gráfico termina en el último dato guardado en vez de quedar vacío.
/// </summary>
public sealed class StoredHistoryFeed : IMarketDataFeed, ISessionAwareFeed
{
    private readonly string _root;
    private readonly Func<string, Instrument?> _instrumentFor;

    /// <param name="root">Carpeta de ticks ({raíz}/{feed}/{símbolo}/{día}.vtk).</param>
    /// <param name="feedId">Fuente guardada (por ejemplo «ib»).</param>
    /// <param name="displayName">Nombre para la interfaz.</param>
    /// <param name="instrumentFor">Arma el instrumento de un símbolo (null = se ignora el símbolo).</param>
    public StoredHistoryFeed(string root, string feedId, string displayName, Func<string, Instrument?> instrumentFor)
    {
        _root = root;
        Id = feedId;
        DisplayName = displayName;
        _instrumentFor = instrumentFor;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public bool SupportsHistory => true;
    public bool IsLive => false;

    /// <summary>Símbolos con días guardados de esta fuente.</summary>
    public IReadOnlyList<string> Symbols()
    {
        var dir = Path.Combine(_root, Id);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(dir)
            .Where(d => Directory.EnumerateFiles(d, "*.vtk").Any())
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>();
        foreach (var symbol in Symbols())
        {
            try
            {
                if (_instrumentFor(symbol) is { } inst) list.Add(inst);
            }
            catch (Exception)
            {
                // un símbolo que no se reconoce no impide mostrar los demás
            }
        }

        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }

    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var batch = new List<Trade>(10_000);
            foreach (var t in HistoricalData.Read(_root, Id, instrument.Symbol, fromUtc, toUtc))
            {
                ct.ThrowIfCancellationRequested();
                if (fromId.HasValue && t.Id < fromId.Value) continue;
                batch.Add(t);
                if (batch.Count < 10_000) continue;
                if (!onBatch(batch)) return;
                batch = new List<Trade>(10_000);
            }

            if (batch.Count > 0) onBatch(batch);
            progress?.Report(1);
        }, ct);
    }

    /// <summary>Hora del último dato guardado (el gráfico termina ahí).</summary>
    public Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct) =>
        Task.Run(() => LastTradeTime(instrument.Symbol), ct);

    public DateTime? LastTradeTime(string symbol)
    {
        var dir = Path.Combine(_root, Id, symbol);
        if (!Directory.Exists(dir)) return null;
        var last = Directory.EnumerateFiles(dir, "*.vtk").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(n => DateOnly.TryParseExact(n, "yyyy-MM-dd", out _)).Max(StringComparer.Ordinal);
        if (last == null) return null;
        var day = DateOnly.ParseExact(last, "yyyy-MM-dd");
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime? max = null;
        foreach (var t in HistoricalData.Read(_root, Id, symbol, from, from.AddDays(1)))
            if (max == null || t.Time > max) max = t.Time;
        return max;
    }

    /// <summary>No hay tiempo real: solo avisa que es historia guardada.</summary>
    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        sink.OnStatus(instrument, FeedStatus.Connected, TradingHub.Common.Loc.T("Historia guardada (sin tiempo real)"));
        return new Nothing();
    }

    public void Dispose()
    {
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
