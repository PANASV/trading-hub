using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Un TWS / IB Gateway de mentira: saludo, START_API, historia, tick-by-tick y profundidad (versión 187).</summary>
internal sealed class FakeIbGateway : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private NetworkStream? _stream;
    private readonly object _writeLock = new();

    public FakeIbGateway()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Lo que pidió el cliente (campos de cada mensaje).</summary>
    public ConcurrentQueue<string[]> Received { get; } = new();

    /// <summary>Velas de 1 minuto que tiene «IB» (por contrato «MNQ 202612»).</summary>
    public Func<string, DateTime, int, List<(long Time, double O, double H, double L, double C, decimal V)>> Bars { get; set; } = (_, _, _) => new();

    /// <summary>Si responde a la profundidad con un error (cuenta sin la profundidad de CME).</summary>
    public bool NoDepth { get; set; }

    public int? TradesReqId { get; private set; }
    public int? QuotesReqId { get; private set; }
    public int? DepthReqId { get; private set; }

    private async Task AcceptAsync()
    {
        try
        {
            using var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
            _stream = tcp.GetStream();
            var api = new byte[4];
            await _stream.ReadExactlyAsync(api, _cts.Token);
            Assert.Equal("API\0", Encoding.ASCII.GetString(api));
            var versions = await ReadAsync();
            Assert.Equal("v100..187", versions[0]);
            Write("187", "20261102 21:05:14 China Standard Time");
            while (!_cts.IsCancellationRequested)
            {
                var f = await ReadAsync();
                Received.Enqueue(f);
                Handle(f);
            }
        }
        catch (Exception)
        {
            // cerrado
        }
    }

    private void Handle(string[] f)
    {
        switch (f[0])
        {
            case "71": // START_API
                Write("15", "1", "U1234567");
                Write("9", "1", "1");
                Write("4", "2", "-1", "2104", "Market data farm connection is OK:usfarm", "");
                break;
            case "20": // reqHistoricalData
            {
                int reqId = int.Parse(f[1], CultureInfo.InvariantCulture);
                string contract = $"{f[3]} {f[5]}";
                var end = DateTime.ParseExact(f[15], "yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                string duration = f[17];
                int seconds = duration.EndsWith(" S") ? int.Parse(duration[..^2], CultureInfo.InvariantCulture) : int.Parse(duration[..^2], CultureInfo.InvariantCulture) * 86400;
                var bars = Bars(contract, end, seconds);
                if (bars.Count == 0)
                {
                    Write("4", "2", f[1], "162", "Historical Market Data Service error message:HMDS query returned no data: MNQZ6@CME Trades", "");
                    break;
                }

                var fields = new List<string> { "17", reqId.ToString(CultureInfo.InvariantCulture), "20261101 00:00:00", "20261102 00:00:00", bars.Count.ToString(CultureInfo.InvariantCulture) };
                foreach (var b in bars)
                    fields.AddRange(new[] { b.Time.ToString(CultureInfo.InvariantCulture), F(b.O), F(b.H), F(b.L), F(b.C), b.V.ToString(CultureInfo.InvariantCulture), F(b.C), "10" });
                Write(fields.ToArray());
                break;
            }
            case "97": // reqTickByTickData
                if (f[14] == "AllLast") TradesReqId = int.Parse(f[1], CultureInfo.InvariantCulture);
                else QuotesReqId = int.Parse(f[1], CultureInfo.InvariantCulture);
                break;
            case "10": // reqMktDepth
                DepthReqId = int.Parse(f[2], CultureInfo.InvariantCulture);
                if (NoDepth) Write("4", "2", f[2], "10092", "Deep market data is not supported for this combination of security type/exchange", "");
                break;
        }
    }

    private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public void Write(params string[] fields)
    {
        var payload = Encoding.UTF8.GetBytes(string.Concat(fields.Select(x => x + "\0")));
        var len = new byte[] { (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length };
        lock (_writeLock)
        {
            _stream!.Write(len);
            _stream.Write(payload);
        }
    }

    private async Task<string[]> ReadAsync()
    {
        var len = new byte[4];
        await _stream!.ReadExactlyAsync(len, _cts.Token);
        int n = (len[0] << 24) | (len[1] << 16) | (len[2] << 8) | len[3];
        var body = new byte[n];
        await _stream.ReadExactlyAsync(body, _cts.Token);
        var text = Encoding.UTF8.GetString(body);
        return (text.EndsWith('\0') ? text[..^1] : text).Split('\0');
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _stream?.Dispose();
    }
}

public class IbFeedTests
{
    private static readonly Instrument Mnq = IbBarImport.InstrumentFor("MNQ");

    private sealed class Sink : IMarketDataSink
    {
        public readonly ConcurrentQueue<Trade> Trades = new();
        public volatile DepthUpdate? Depth;
        public readonly ConcurrentQueue<(FeedStatus Status, string? Message)> Statuses = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            foreach (var t in trades) Trades.Enqueue(t);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth = update;
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => Statuses.Enqueue((status, message));
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(20);
        Assert.True(condition(), "no pasó a tiempo");
    }

    [Fact]
    public void Arma_los_pedidos_como_la_TWS_API_187()
    {
        var c = new IbFeed.Contract("MNQ", "CME", 2026, 12, false);
        var hist = IbFeed.HistoricalRequest(7, c, new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc), "86400 S", "1 min").Select(IbClient.Field).ToArray();
        Assert.Equal(new[] { "20", "7", "0", "MNQ", "FUT", "202612", "0", "", "", "CME", "", "USD", "", "", "0", "20261102-13:00:00", "1 min", "86400 S", "0", "TRADES", "2", "0", "" }, hist);
        var tbt = IbFeed.TickByTickRequest(8, c, "AllLast").Select(IbClient.Field).ToArray();
        Assert.Equal(new[] { "97", "8", "0", "MNQ", "FUT", "202612", "0", "", "", "CME", "", "USD", "", "", "AllLast", "0", "0" }, tbt);
        var depth = IbFeed.DepthRequest(9, c, 10).Select(IbClient.Field).ToArray();
        Assert.Equal(new[] { "10", "5", "9", "0", "MNQ", "FUT", "202612", "0", "", "", "CME", "", "USD", "", "", "10", "0", "" }, depth);
        Assert.Equal("a\0b\0\0", Encoding.UTF8.GetString(IbClient.Encode("a", "b", null)));
    }

    [Fact]
    public void Contrato_vigente_y_lado_agresor()
    {
        var c = IbFeed.ContractOn(Mnq, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("202612", c.MonthCode);
        Assert.False(c.IncludeExpired);
        // el lunes de la semana del vencimiento de diciembre (14/12/2026) pasa a marzo
        Assert.Equal("202703", IbFeed.ContractOn(Mnq, new DateTime(2026, 12, 14, 12, 0, 0, DateTimeKind.Utc)).MonthCode);
        Assert.True(IbFeed.ContractOn(Mnq, new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)).IncludeExpired);
        Assert.Throws<NotSupportedException>(() => IbFeed.ContractOn(IbBarImport.InstrumentFor("CL"), DateTime.UtcNow));

        Assert.Equal(TradeSide.Buy, IbFeed.Aggressor(100.25m, 100m, 100.25m, 100m, TradeSide.None));
        Assert.Equal(TradeSide.Sell, IbFeed.Aggressor(100m, 100m, 100.25m, 100.25m, TradeSide.Buy));
        Assert.Equal(TradeSide.Buy, IbFeed.Aggressor(100.5m, 0, 0, 100.25m, TradeSide.Sell)); // sin bid/ask: sube
        Assert.Equal(TradeSide.Sell, IbFeed.Aggressor(100.25m, 0, 0, 100.25m, TradeSide.Sell)); // igual: repite
    }

    [Fact]
    public void Lee_velas_y_errores_de_la_version_187()
    {
        var bars = IbFeed.ParseHistorical(new[] { "17", "5", "a", "b", "2", "1793602800", "28500.25", "28510", "28490.5", "28505.75", "1234", "28501.1", "321", "1793602860", "28505.75", "28506", "28500", "28501", "77", "28503", "50" });
        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateTime(2026, 11, 2, 7, 0, 0, DateTimeKind.Utc), bars[0].OpenUtc);
        Assert.Equal(28500.25m, bars[0].Open);
        Assert.Equal(1234m, bars[0].Volume);
        Assert.Equal(77m, bars[1].Volume);
        var (id, code, text) = IbClient.Error(new[] { "4", "2", "12", "354", "Requested market data is not subscribed.\\u00e9", "" });
        Assert.Equal((12, 354, "Requested market data is not subscribed.é"), (id, code, text));
    }

    [Fact]
    public async Task Baja_velas_de_1_minuto_por_dia_y_contrato()
    {
        using var gw = new FakeIbGateway();
        var start = new DateTime(2026, 10, 29, 22, 0, 0, DateTimeKind.Utc);
        // una vela por minuto durante 2 días, solo del MNQ de diciembre
        gw.Bars = (contract, end, seconds) =>
        {
            Assert.Equal("MNQ 202612", contract);
            var list = new List<(long, double, double, double, double, decimal)>();
            for (var t = end.AddSeconds(-seconds); t < end; t = t.AddMinutes(1))
                if (t >= start && t < start.AddDays(2) && t.DayOfWeek != DayOfWeek.Saturday)
                    list.Add((new DateTimeOffset(t).ToUnixTimeSeconds(), 28000, 28001, 27999, 28000.5, 3));
            return list;
        };
        using var feed = new IbFeed(() => new IbSettings("127.0.0.1", gw.Port, 17));
        var klines = new List<Kline>();
        await feed.LoadKlinesAsync(Mnq, start, start.AddDays(2), 1, 0, b =>
        {
            klines.AddRange(b);
            return true;
        }, null, CancellationToken.None);
        Assert.Equal(2 * 1440 - 22 * 60, klines.Count); // el sábado 31/10 (UTC, hasta las 22:00) no tiene velas
        Assert.True(klines.Zip(klines.Skip(1)).All(p => p.First.OpenTimeMs < p.Second.OpenTimeMs));
        // de a un día: dos pedidos, y un día sin datos (código 162) no es un error
        Assert.Equal(2, gw.Received.Count(f => f[0] == "20"));

        // las últimas horas, como operaciones (cuatro por vela)
        var trades = new List<Trade>();
        await feed.LoadTradesAsync(Mnq, start, start.AddHours(1), null, b =>
        {
            trades.AddRange(b);
            return true;
        }, null, CancellationToken.None);
        Assert.Equal(60 * 4, trades.Count);
        Assert.Equal(60 * 3m, trades.Sum(t => t.Volume));
    }

    [Fact]
    public async Task Sin_IB_Gateway_la_historia_queda_con_lo_guardado()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        using var feed = new IbFeed(() => new IbSettings("127.0.0.1", port, 17));
        int batches = 0;
        await feed.LoadKlinesAsync(Mnq, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow, 1, 0, _ => ++batches > 0, null, CancellationToken.None);
        await feed.LoadTradesAsync(Mnq, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, null, _ => ++batches > 0, null, CancellationToken.None);
        Assert.Equal(0, batches);
        Assert.Null(await feed.LastTradeTimeAsync(Mnq, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => feed.ClientAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task En_vivo_operaciones_con_lado_y_libro(bool noDepth)
    {
        using var gw = new FakeIbGateway { NoDepth = noDepth };
        using var feed = new IbFeed(() => new IbSettings("127.0.0.1", gw.Port, 17));
        var sink = new Sink();
        using var sub = feed.Subscribe(Mnq, sink);
        await Until(() => gw.TradesReqId != null && gw.QuotesReqId != null && gw.DepthReqId != null);
        await Until(() => sink.Statuses.Any(s => s.Status == FeedStatus.Connected));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string q = gw.QuotesReqId!.Value.ToString(CultureInfo.InvariantCulture), t = gw.TradesReqId!.Value.ToString(CultureInfo.InvariantCulture);
        gw.Write("99", q, "3", now.ToString(CultureInfo.InvariantCulture), "28545.5", "28545.75", "7", "9", "0");
        if (!noDepth)
        {
            string d = gw.DepthReqId!.Value.ToString(CultureInfo.InvariantCulture);
            gw.Write("12", "1", d, "0", "0", "1", "28545.5", "7"); // bid
            gw.Write("12", "1", d, "1", "0", "1", "28545.25", "12");
            gw.Write("12", "1", d, "0", "0", "0", "28545.75", "9"); // ask
            gw.Write("12", "1", d, "0", "1", "1", "28545.5", "4"); // cambia el bid
        }

        gw.Write("99", t, "2", now.ToString(CultureInfo.InvariantCulture), "28545.75", "2", "0", "CME", "");
        gw.Write("99", t, "2", now.ToString(CultureInfo.InvariantCulture), "28545.5", "1", "0", "CME", "");
        await Until(() => sink.Trades.Count == 2);
        var trades = sink.Trades.ToArray();
        Assert.Equal((28545.75m, 2m, TradeSide.Buy), (trades[0].Price, trades[0].Volume, trades[0].Side));
        Assert.Equal((28545.5m, 1m, TradeSide.Sell), (trades[1].Price, trades[1].Volume, trades[1].Side));
        Assert.True(trades[1].Id > trades[0].Id && trades[1].Time >= trades[0].Time);

        await Until(() => sink.Depth != null && (noDepth || sink.Depth.Snapshot.Bids.Length == 2));
        var book = sink.Depth!.Snapshot;
        Assert.Equal(Mnq.ToTicks(28545.5m), book.BestBidTicks);
        Assert.Equal(Mnq.ToTicks(28545.75m), book.BestAskTicks);
        Assert.Equal(noDepth ? 7m : 4m, book.BestBidSize);
        Assert.Equal(9m, book.BestAskSize);
        Assert.DoesNotContain(sink.Statuses, s => s.Status == FeedStatus.Error);

        // el contrato de diciembre, todo el horario
        var req = gw.Received.First(f => f[0] == "97");
        Assert.Equal(("MNQ", "FUT", "202612", "CME"), (req[3], req[4], req[5], req[9]));
    }

    [Fact]
    public async Task Sin_suscripcion_de_CME_lo_dice()
    {
        using var gw = new FakeIbGateway();
        using var feed = new IbFeed(() => new IbSettings("127.0.0.1", gw.Port, 17));
        var sink = new Sink();
        using var sub = feed.Subscribe(Mnq, sink);
        await Until(() => gw.TradesReqId != null);
        gw.Write("4", "2", gw.TradesReqId!.Value.ToString(CultureInfo.InvariantCulture), "10089", "Requested market data requires additional subscription for API.", "");
        await Until(() => sink.Statuses.Any(s => s.Status == FeedStatus.Error));
        Assert.Contains(sink.Statuses, s => s.Status == FeedStatus.Error && s.Message!.Contains("CME", StringComparison.Ordinal));
    }
    [Fact]
    public async Task El_grafico_junta_lo_importado_lo_que_falta_y_lo_en_vivo()
    {
        var dir = Directory.CreateTempSubdirectory("th-ib-live-");
        try
        {
            var root = Path.Combine(dir.FullName, "Ticks");
            var now = DateTime.UtcNow;
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            // importado: de hace 3 días a hace 1 día (velas de 1 minuto como las de IB Gateway)
            var imported = new List<Trade>();
            var importedFrom = minute.AddDays(-3);
            var importedTo = minute.AddDays(-1);
            var klines = new List<Kline>();
            for (var t = importedFrom; t < importedTo; t = t.AddMinutes(1))
                klines.Add(new Kline(new DateTimeOffset(t).ToUnixTimeMilliseconds(), 28000, 28002, 27999, 28001, 5, 0, false));
            KlineHistory.Synthesize(klines, Mnq, 1, new[] { (importedFrom, importedTo) }, imported);
            foreach (var g in imported.GroupBy(t => DateOnly.FromDateTime(t.Time)))
                HistoricalData.WriteDay(root, Mnq, g.Key, g.ToList(), replace: true);

            // IB tiene el último día (lo que falta)
            using var gw = new FakeIbGateway
            {
                Bars = (_, end, seconds) =>
                {
                    var list = new List<(long, double, double, double, double, decimal)>();
                    for (var t = end.AddSeconds(-seconds); t < end; t = t.AddMinutes(1))
                        if (t >= importedTo && t < minute) list.Add((new DateTimeOffset(t).ToUnixTimeSeconds(), 28100, 28102, 28099, 28101, 2));
                    return list;
                },
            };
            using var feed = new IbFeed(() => new IbSettings("127.0.0.1", gw.Port, 17), root);
            var data = new InstrumentData(Mnq, feed) { RecordingRoot = root, RecordLive = false };
            data.Start(now.AddHours(-48));
            await Until(() =>
            {
                data.Pump();
                return !data.IsLoadingHistory;
            }, 30000);
            Assert.Null(data.HistoryError);
            // las 24 h importadas que entran en las 48 h y las 24 h que faltaban, de IB
            var times = Enumerable.Range(0, data.Trades.Count).Select(i => data.Trades.GetTime(i)).ToList();
            Assert.True(times[0] <= now.AddHours(-47.9), $"primera {times[0]:O}");
            Assert.True(times[^1] >= minute.AddMinutes(-2), $"última {times[^1]:O} · pedidos: " + string.Join(" | ", gw.Received.Where(f => f[0] == "20").Select(f => f[15] + " " + f[17])));
            // en las uniones (disco → velas de IB → últimas horas) puede faltar el minuto partido, como con los demás feeds
            var gaps = times.Zip(times.Skip(1)).Where(p => (p.Second - p.First) > TimeSpan.FromMinutes(3)).Take(5).ToList();
            Assert.True(gaps.Count == 0, $"{times.Count} ops, {times[0]:O} → {times[^1]:O}; huecos: " + string.Join("; ", gaps.Select(g => $"{g.First:O} → {g.Second:O}")) + " · pedidos: " + string.Join(" | ", gw.Received.Where(f => f[0] == "20").Select(f => f[15] + " " + f[17])));
            Assert.Equal(28101m, data.LastPrice);

            // en vivo
            await Until(() => gw.TradesReqId != null);
            gw.Write("99", gw.TradesReqId!.Value.ToString(CultureInfo.InvariantCulture), "2", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "28110.25", "3", "0", "CME", "");
            await Until(() =>
            {
                data.Pump();
                return data.LastPrice == 28110.25m;
            });
            data.Stop();
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
