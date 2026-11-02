using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Feeds.InteractiveBrokers;

/// <summary>Dónde está TWS / IB Gateway y con qué client id se conecta Trading Hub.</summary>
public sealed record IbSettings(string Host, int Port, int ClientId);

/// <summary>
/// Datos de Interactive Brokers por TWS / IB Gateway (futuros de índices de CME: MNQ, MES, M2K, NQ, ES…):
/// <list type="bullet">
/// <item>historia: lo guardado en disco (las velas importadas y lo grabado en vivo) y lo que falte, en velas de 1 minuto
/// pedidas a IB (con el contrato que operaba cada día);</item>
/// <item>en vivo: cada operación (tick-by-tick «AllLast») con el lado agresor deducido del bid/ask del momento
/// (tick-by-tick «BidAsk»), y el libro (DOM) de 10 niveles si la cuenta tiene la profundidad de CME; si no, el mejor
/// bid y ask.</item>
/// </list>
/// Hace falta la suscripción de datos de CME en tiempo real en la cuenta de IB.
/// </summary>
public sealed class IbFeed : IMarketDataFeed, IKlineHistory, ISessionAwareFeed
{
    public const string FeedId = IbBarImport.FeedId;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Func<IbSettings> _settings;
    private readonly string? _root;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Action<string[]>> _routes = new();
    private IbClient? _client;
    private bool _disposed;

    /// <param name="settings">Host, puerto y client id (se leen en cada conexión).</param>
    /// <param name="root">Carpeta de ticks (para listar los símbolos importados); null = solo los del catálogo.</param>
    public IbFeed(Func<IbSettings> settings, string? root = null)
    {
        _settings = settings;
        _root = root;
    }

    public string Id => FeedId;
    public string DisplayName => "Interactive Brokers";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public string CacheTag => "ib";

    /// <summary>Tiempo máximo de un pedido de historia.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // ===================================================================== conexión

    /// <summary>La conexión abierta (o una nueva). Todos los gráficos de IB comparten una.</summary>
    public async Task<IbClient> ClientAsync(CancellationToken ct)
    {
        var c = _client;
        if (c is { IsConnected: true }) return c;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IbFeed));
            c = _client;
            if (c is { IsConnected: true }) return c;
            c?.Dispose();
            var s = _settings();
            try
            {
                c = await IbClient.ConnectAsync(s.Host, s.Port, s.ClientId, ct).ConfigureAwait(false);
            }
            catch (IbException ex) when (ex.Code == 326)
            {
                throw new IbException(326, Loc.F("El client id {0} ya está en uso en IB Gateway: cambialo en Conexiones → Interactive Brokers.", s.ClientId));
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new IOException(Loc.F("IB Gateway no responde en {0}:{1}. ¿Está abierto, con la sesión iniciada y la API habilitada (Configure → API → Settings)?", s.Host, s.Port), ex);
            }

            c.Message += Route;
            _client = c;
            return c;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void Route(string[] f)
    {
        if (f.Length < 2) return;
        int reqId;
        switch (f[0])
        {
            case "4": // error
                reqId = IbClient.Error(f).Id;
                break;
            case "17": // historicalData
            case "99": // tickByTick
                int.TryParse(f[1], NumberStyles.Integer, Inv, out reqId);
                break;
            case "12": // updateMktDepth
            case "13": // updateMktDepthL2
                if (f.Length < 3) return;
                int.TryParse(f[2], NumberStyles.Integer, Inv, out reqId);
                break;
            default:
                return;
        }

        if (_routes.TryGetValue(reqId, out var handler)) handler(f);
    }

    // ===================================================================== contratos

    /// <summary>Futuros que se pueden ver: los de índices de CME (con rolls conocidos) y lo importado.</summary>
    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[] { "MNQ", "MES", "M2K", "MYM", "NQ", "ES", "RTY", "YM" }.AsEnumerable();
        if (_root != null && Directory.Exists(Path.Combine(_root, FeedId)))
            roots = roots.Concat(Directory.EnumerateDirectories(Path.Combine(_root, FeedId)).Select(Path.GetFileName).OfType<string>());
        foreach (var root in roots)
        {
            if (!seen.Add(root) || FuturesCatalog.Find(root) == null) continue;
            var inst = IbBarImport.InstrumentFor(root);
            list.Add(new Instrument
            {
                Symbol = inst.Symbol,
                FeedId = FeedId,
                Exchange = inst.Exchange,
                TickSize = inst.TickSize,
                PointValue = inst.PointValue,
                VolumeStep = 1,
                Description = FuturesRolls.Supports(root)
                    ? $"{FuturesCatalog.Find(root)!.Name} · IB ({FuturesRolls.On(root, DateTime.UtcNow).Symbol})"
                    : $"{FuturesCatalog.Find(root)!.Name} · IB",
            });
        }

        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }

    /// <summary>Contrato de IB (futuro de CME) para un año y mes.</summary>
    public sealed record Contract(string Symbol, string Exchange, int Year, int Month, bool IncludeExpired)
    {
        public string MonthCode => $"{Year:0000}{Month:00}";

        /// <summary>Campos del contrato como van en reqHistoricalData / reqTickByTickData (conId … tradingClass).</summary>
        public object?[] Fields(bool withPrimaryExchange = true) => withPrimaryExchange
            ? new object?[] { 0, Symbol, "FUT", MonthCode, 0d, "", "", Exchange, "", "USD", "", "" }
            : new object?[] { 0, Symbol, "FUT", MonthCode, 0d, "", "", Exchange, "USD", "", "" };
    }

    /// <summary>El contrato que opera en un instante (MNQ el 2/11/2026 → MNQ 202612).</summary>
    public static Contract ContractOn(Instrument inst, DateTime utc)
    {
        if (!FuturesRolls.Supports(inst.Symbol))
            throw new NotSupportedException(Loc.F("{0}: Trading Hub todavía no sabe qué contrato de IB usar (solo futuros de índices: MNQ, MES, M2K, NQ, ES…).", inst.Symbol));
        var (y, m) = FuturesRolls.ContractMonth(utc);
        bool expired = FuturesRolls.Expiry(y, m).AddDays(1) < DateTime.UtcNow;
        return new Contract(inst.Symbol, string.IsNullOrEmpty(inst.Exchange) ? "CME" : inst.Exchange, y, m, expired);
    }

    // ===================================================================== historia

    /// <summary>Una vela de IB.</summary>
    public readonly record struct Bar(DateTime OpenUtc, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    /// <summary>Mensaje reqHistoricalData (20) de la versión 187: velas de TRADES, todo el horario, fechas en segundos.</summary>
    public static object?[] HistoricalRequest(int reqId, Contract c, DateTime endUtc, string duration, string barSize)
    {
        var f = new List<object?> { 20, reqId };
        f.AddRange(c.Fields());
        f.Add(c.IncludeExpired);
        f.Add(endUtc.ToString("yyyyMMdd-HH:mm:ss", Inv));
        f.Add(barSize);
        f.Add(duration);
        f.Add(0); // useRTH: también fuera del horario regular
        f.Add("TRADES");
        f.Add(2); // formatDate: segundos desde 1970
        f.Add(false); // keepUpToDate
        f.Add(""); // chartOptions
        return f.ToArray();
    }

    /// <summary>Velas de un mensaje historicalData (17) de la versión 187.</summary>
    public static List<Bar> ParseHistorical(string[] f)
    {
        var r = new IbReader(f, 2); // id del mensaje y reqId
        r.ReadString(); // inicio
        r.ReadString(); // fin
        int n = r.ReadInt();
        var bars = new List<Bar>(Math.Max(0, n));
        for (int i = 0; i < n && r.HasMore; i++)
        {
            string date = r.ReadString();
            double o = r.ReadDouble(), h = r.ReadDouble(), l = r.ReadDouble(), c = r.ReadDouble();
            decimal vol = r.ReadDecimal();
            r.ReadDecimal(); // WAP
            r.ReadInt(); // cantidad de operaciones
            if (!long.TryParse(date, NumberStyles.Integer, Inv, out long secs)) continue;
            bars.Add(new Bar(DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime, (decimal)o, (decimal)h, (decimal)l, (decimal)c, vol));
        }

        return bars;
    }

    /// <summary>Velas de un contrato que terminan en <paramref name="endUtc"/> (vacío si IB no tiene datos en ese rango).</summary>
    public async Task<List<Bar>> BarsAsync(Contract c, DateTime endUtc, string duration, string barSize, CancellationToken ct)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        int reqId = client.NextRequestId();
        var tcs = new TaskCompletionSource<List<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _routes[reqId] = f =>
        {
            if (f[0] == "17") tcs.TrySetResult(ParseHistorical(f));
            else if (f[0] == "4")
            {
                var (_, code, text) = IbClient.Error(f);
                // 162 sin datos en el rango (fin de semana, feriado): no es un error
                if (code == 162 && text.Contains("returned no data", StringComparison.OrdinalIgnoreCase)) tcs.TrySetResult(new List<Bar>());
                else if (code is not (2100 or 2104 or 2106 or 2107 or 2108 or 2158)) tcs.TrySetException(new IbException(code, $"IB {code}: {text}"));
            }
        };
        try
        {
            client.Send(HistoricalRequest(reqId, c, endUtc, duration, barSize));
            var done = await Task.WhenAny(tcs.Task, client.Closed, Task.Delay(RequestTimeout, ct)).ConfigureAwait(false);
            if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (done == client.Closed) throw new IOException(await client.Closed.ConfigureAwait(false) ?? "IB Gateway cerró la conexión");
            client.Send(25, 1, reqId); // cancelHistoricalData
            throw new TimeoutException(Loc.T("IB Gateway no devolvió la historia a tiempo"));
        }
        finally
        {
            _routes.TryRemove(reqId, out _);
        }
    }

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    /// <summary>
    /// Velas de 1, 5 o 15 minutos de IB entre dos instantes: por tramos de contrato (rolls) y de a un día (1 minuto) o una
    /// semana. Solo velas completas.
    /// </summary>
    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        List<Bar> bars;
        try
        {
            bars = await RangeAsync(instrument, fromUtc, toUtc, minutes, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or IbException or NotSupportedException)
        {
            // todo o nada: un tramo a medias no se guarda en la caché de velas (y lo guardado en disco se muestra igual)
            return;
        }

        var klines = bars.Select(b => new Kline(new DateTimeOffset(b.OpenUtc).ToUnixTimeMilliseconds(), b.Open, b.High, b.Low, b.Close, b.Volume, 0, false)).ToList();
        if (klines.Count > 0) onBatch(klines);
        progress?.Report(1);
    }

    private async Task<List<Bar>> RangeAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new List<Bar>();
        if (toUtc <= fromUtc || !FuturesRolls.Supports(instrument.Symbol)) return result;
        var now = DateTime.UtcNow;
        if (toUtc > now) toUtc = now;
        string barSize = minutes switch { 1 => "1 min", 5 => "5 mins", 15 => "15 mins", _ => $"{minutes} mins" };
        var chunk = minutes == 1 ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
        double total = (toUtc - fromUtc).Ticks;
        foreach (var (_, segFrom, segTo) in FuturesRolls.Segments(instrument.Symbol, fromUtc, toUtc))
        {
            var contract = ContractOn(instrument, segFrom);
            for (var start = segFrom; start < segTo; start += chunk)
            {
                ct.ThrowIfCancellationRequested();
                var end = start + chunk < segTo ? start + chunk : segTo;
                var span = end - start;
                string duration = span <= TimeSpan.FromDays(1)
                    ? $"{Math.Max(60, (int)Math.Ceiling(span.TotalSeconds))} S"
                    : $"{(int)Math.Ceiling(span.TotalDays)} D";
                foreach (var b in await BarsAsync(contract, end, duration, barSize, ct).ConfigureAwait(false))
                    if (b.OpenUtc >= start && b.OpenUtc < end && b.OpenUtc.AddMinutes(minutes) <= now) result.Add(b);
                progress?.Report((end - fromUtc).Ticks / total);
            }
        }

        result.Sort((a, b) => a.OpenUtc.CompareTo(b.OpenUtc));
        return result;
    }

    /// <summary>
    /// Lo que falta de las últimas horas, como operaciones sintéticas de las velas de 1 minuto de IB (cuatro por vela,
    /// como lo importado). Si IB Gateway no está, no trae nada (el gráfico queda con lo guardado).
    /// </summary>
    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        List<Bar> bars;
        try
        {
            bars = await RangeAsync(instrument, fromUtc, toUtc, 1, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or IbException or NotSupportedException)
        {
            return;
        }

        var klines = bars.Select(b => new Kline(new DateTimeOffset(b.OpenUtc).ToUnixTimeMilliseconds(), b.Open, b.High, b.Low, b.Close, b.Volume, 0, false)).ToList();
        var trades = new List<Trade>(klines.Count * 4);
        KlineHistory.Synthesize(klines, instrument, 1, new[] { (fromUtc, toUtc) }, trades);
        if (trades.Count > 0) onBatch(trades);
        progress?.Report(1);
    }

    /// <summary>Hora de la última vela de IB (con el mercado cerrado, el gráfico termina ahí).</summary>
    public async Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct)
    {
        if (!FuturesRolls.Supports(instrument.Symbol)) return null;
        try
        {
            var now = DateTime.UtcNow;
            var bars = await BarsAsync(ContractOn(instrument, now), now, "3 D", "1 min", ct).ConfigureAwait(false);
            return bars.Count > 0 ? bars[^1].OpenUtc.AddMinutes(1) : null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or IbException)
        {
            return null;
        }
    }

    // ===================================================================== en vivo

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSub(this, instrument, sink);
        sub.Start();
        return sub;
    }

    /// <summary>reqTickByTickData (97) de la versión 187.</summary>
    public static object?[] TickByTickRequest(int reqId, Contract c, string tickType)
    {
        var f = new List<object?> { 97, reqId };
        f.AddRange(c.Fields());
        f.Add(tickType);
        f.Add(0); // numberOfTicks
        f.Add(false); // ignoreSize
        return f.ToArray();
    }

    /// <summary>reqMktDepth (10) de la versión 187.</summary>
    public static object?[] DepthRequest(int reqId, Contract c, int rows)
    {
        var f = new List<object?> { 10, 5, reqId };
        f.AddRange(c.Fields());
        f.Add(rows);
        f.Add(false); // isSmartDepth
        f.Add(""); // mktDepthOptions
        return f.ToArray();
    }

    /// <summary>
    /// Lado agresor de una operación: contra el ask compra, contra el bid vende; entre medio, por el movimiento respecto de
    /// la anterior (sube compra, baja vende, igual repite).
    /// </summary>
    public static TradeSide Aggressor(decimal price, decimal bid, decimal ask, decimal lastPrice, TradeSide lastSide)
    {
        if (ask > 0 && price >= ask) return TradeSide.Buy;
        if (bid > 0 && price <= bid) return TradeSide.Sell;
        if (lastPrice > 0 && price > lastPrice) return TradeSide.Buy;
        if (lastPrice > 0 && price < lastPrice) return TradeSide.Sell;
        return lastSide;
    }

    private sealed class LiveSub(IbFeed feed, Instrument instrument, IMarketDataSink sink) : IDisposable, IReconnectable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly OrderBookBuilder _book = new();
        private readonly List<(decimal Price, decimal Size)>[] _depth = { new(), new() }; // 0 ask, 1 bid (como IB)
        private readonly object _lock = new();
        private IbClient? _client;
        private decimal _bid, _ask, _last;
        private TradeSide _lastSide;
        private DateTime _lastTime;
        private long _id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        private bool _depthLive;

        public void Start() => _ = Task.Run(RunAsync);

        public void Reconnect()
        {
            // cortar la conexión compartida: todos los gráficos de IB se reconectan
            try { _client?.Dispose(); }
            catch { /* ya cerrada */ }
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                int tradesId = 0, quotesId = 0, depthId = 0;
                IbClient? client = null;
                try
                {
                    sink.OnStatus(instrument, attempt == 0 ? FeedStatus.Connecting : FeedStatus.Reconnecting, Loc.T("Conectando a IB Gateway…"));
                    client = await feed.ClientAsync(ct).ConfigureAwait(false);
                    _client = client;
                    var contract = ContractOn(instrument, DateTime.UtcNow);
                    tradesId = client.NextRequestId();
                    quotesId = client.NextRequestId();
                    depthId = client.NextRequestId();
                    _depthLive = false;
                    lock (_lock)
                    {
                        _depth[0].Clear();
                        _depth[1].Clear();
                    }

                    feed._routes[tradesId] = f => OnMessage(f, Channel.Trades);
                    feed._routes[quotesId] = f => OnMessage(f, Channel.Quotes);
                    feed._routes[depthId] = f => OnMessage(f, Channel.Depth);
                    client.Send(TickByTickRequest(tradesId, contract, "AllLast"));
                    client.Send(TickByTickRequest(quotesId, contract, "BidAsk"));
                    client.Send(DepthRequest(depthId, contract, 10));
                    string name = FuturesRolls.Symbol(instrument.Symbol, contract.Year, contract.Month);
                    sink.OnStatus(instrument, FeedStatus.Connected, Loc.F("IB Gateway · {0} en vivo", name));
                    attempt = 0;
                    await Task.WhenAny(client.Closed, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
                    sink.OnStatus(instrument, FeedStatus.Reconnecting, Loc.F("Se cortó IB Gateway ({0}); reconectando…", await client.Closed.ConfigureAwait(false) ?? "cerrada"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    sink.OnStatus(instrument, ex is NotSupportedException ? FeedStatus.Error : FeedStatus.Reconnecting, ex.Message);
                    if (ex is NotSupportedException) return;
                }
                finally
                {
                    feed._routes.TryRemove(tradesId, out _);
                    feed._routes.TryRemove(quotesId, out _);
                    feed._routes.TryRemove(depthId, out _);
                    if (ct.IsCancellationRequested && client is { IsConnected: true })
                    {
                        client.Send(98, tradesId);
                        client.Send(98, quotesId);
                        client.Send(11, 1, depthId, false);
                    }
                }

                attempt++;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private enum Channel
        {
            Trades,
            Quotes,
            Depth,
        }

        private void OnMessage(string[] f, Channel channel)
        {
            switch (f[0])
            {
                case "99":
                    OnTick(f);
                    break;
                case "12":
                case "13":
                    OnDepth(f);
                    break;
                case "4":
                {
                    var (_, code, text) = IbClient.Error(f);
                    if (code is >= 2100 and < 2200) return; // avisos de estado de las granjas de datos
                    if (channel == Channel.Depth)
                    {
                        // sin la profundidad de CME (10092, 2152, 354…): el DOM queda con el mejor bid y ask
                        _depthLive = false;
                        return;
                    }

                    // sin el bid/ask tick a tick igual se sabe el lado por el movimiento del precio
                    if (channel == Channel.Quotes) return;
                    sink.OnStatus(instrument, FeedStatus.Error, code switch
                    {
                        354 or 10089 or 10090 or 10167 or 10168 or 10189 =>
                            Loc.F("IB no da datos en vivo de {0}: falta la suscripción de datos de CME en tiempo real en la cuenta ({1}).", instrument.Symbol, text),
                        10190 => Loc.F("IB: se llegó al máximo de suscripciones tick a tick de la cuenta; cerrá otro gráfico de IB ({0}).", text),
                        200 => Loc.F("IB no encontró el contrato de {0} ({1}).", instrument.Symbol, text),
                        _ => $"IB {code}: {text}",
                    });
                    break;
                }
            }
        }

        private void OnTick(string[] f)
        {
            var r = new IbReader(f, 2);
            int type = r.ReadInt();
            long secs = r.ReadLong();
            var time = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
            if (type is 1 or 2)
            {
                decimal price = instrument.RoundPrice((decimal)r.ReadDouble());
                decimal size = r.ReadDecimal();
                if (size <= 0 || price <= 0) return;
                Trade trade;
                lock (_lock)
                {
                    // IB da la hora en segundos: con la hora local se ordenan las del mismo segundo
                    var now = DateTime.UtcNow;
                    var t = Math.Abs((now - time).TotalSeconds) < 2 ? now : time;
                    if (t < _lastTime) t = _lastTime;
                    _lastTime = t;
                    _lastSide = Aggressor(price, _bid, _ask, _last, _lastSide);
                    _last = price;
                    trade = new Trade(++_id, t, price, size, _lastSide);
                }

                sink.OnTrades(instrument, new[] { trade });
            }
            else if (type == 3)
            {
                decimal bid = (decimal)r.ReadDouble(), ask = (decimal)r.ReadDouble();
                decimal bidSize = r.ReadDecimal(), askSize = r.ReadDecimal();
                DepthUpdate? update = null;
                lock (_lock)
                {
                    _bid = bid;
                    _ask = ask;
                    if (!_depthLive && bid > 0 && ask > 0)
                    {
                        _book.Clear();
                        _book.Set(BookSide.Bid, instrument.ToTicks(bid), bidSize);
                        _book.Set(BookSide.Ask, instrument.ToTicks(ask), askSize);
                        update = _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true);
                    }
                }

                if (update != null) sink.OnDepth(instrument, update);
            }
        }

        /// <summary>updateMktDepth (12) / updateMktDepthL2 (13): insertar, cambiar o borrar la fila «position» de un lado.</summary>
        private void OnDepth(string[] f)
        {
            var r = new IbReader(f, 3); // id, versión, reqId
            int position = r.ReadInt();
            if (f[0] == "13") r.ReadString(); // market maker
            int op = r.ReadInt();
            int side = r.ReadInt(); // 0 ask, 1 bid
            decimal price = (decimal)r.ReadDouble();
            decimal size = r.ReadDecimal();
            if (side is not (0 or 1) || position < 0) return;
            DepthUpdate update;
            lock (_lock)
            {
                _depthLive = true;
                var rows = _depth[side];
                switch (op)
                {
                    case 0:
                        rows.Insert(Math.Min(position, rows.Count), (price, size));
                        break;
                    case 1:
                        if (position < rows.Count) rows[position] = (price, size);
                        else rows.Add((price, size));
                        break;
                    case 2:
                        if (position < rows.Count) rows.RemoveAt(position);
                        break;
                }

                _book.Clear();
                foreach (var (p, s) in _depth[1]) _book.Set(BookSide.Bid, instrument.ToTicks(p), s);
                foreach (var (p, s) in _depth[0]) _book.Set(BookSide.Ask, instrument.ToTicks(p), s);
                update = _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true);
            }

            sink.OnDepth(instrument, update);
        }

        public void Dispose()
        {
            _cts.Cancel();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
        _client = null;
    }
}
