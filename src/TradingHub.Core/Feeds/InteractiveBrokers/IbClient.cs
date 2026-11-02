using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace TradingHub.Feeds.InteractiveBrokers;

/// <summary>Error que manda TWS / IB Gateway (código y texto).</summary>
public sealed class IbException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Conexión con TWS / IB Gateway por el protocolo de la TWS API (mensajes con largo de 4 bytes y campos separados por
/// \0). Hace el saludo (versiones 100 a 187) y START_API, y queda leyendo en un hilo propio: cada mensaje llega por
/// <see cref="Message"/> con sus campos (el primero es el id del mensaje).
/// </summary>
public sealed class IbClient : IDisposable
{
    /// <summary>Versiones del protocolo que habla Trading Hub (187: sin protobuf ni campos nuevos de historia).</summary>
    public const int MinVersion = 100, MaxVersion = 187;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextReqId = 1000;

    private IbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public int ServerVersion { get; private set; }
    public string ConnectionTime { get; private set; } = "";

    /// <summary>Cuentas que maneja la sesión (managedAccounts).</summary>
    public string Accounts { get; private set; } = "";

    /// <summary>Un mensaje del servidor (campos; el primero es el id). Llega en el hilo de lectura.</summary>
    public event Action<string[]>? Message;

    /// <summary>Se cortó la conexión (con el motivo, o null si se cerró desde acá). Se completa una sola vez.</summary>
    public Task<string?> Closed => _closed.Task;

    public bool IsConnected => !_closed.Task.IsCompleted;

    public int NextRequestId() => Interlocked.Increment(ref _nextReqId);

    /// <summary>Conecta, saluda y espera nextValidId (la sesión ya acepta pedidos).</summary>
    public static async Task<IbClient> ConnectAsync(string host, int port, int clientId, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        IbClient? client = null;
        try
        {
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(host, port, t.Token).ConfigureAwait(false);
            }

            client = new IbClient(tcp);
            var hello = new List<byte>(Encoding.ASCII.GetBytes("API\0"));
            hello.AddRange(Frame(Encoding.ASCII.GetBytes($"v{MinVersion}..{MaxVersion}")));
            await client._stream.WriteAsync(hello.ToArray(), ct).ConfigureAwait(false);

            using var rt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rt.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await client.ReadMessageAsync(rt.Token).ConfigureAwait(false);
            client.ServerVersion = int.Parse(first[0], Inv);
            client.ConnectionTime = first.Length > 1 ? first[1] : "";

            // listo cuando llega nextValidId; un error de la sesión (por ejemplo, client id en uso) corta antes
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFirst(string[] f)
            {
                if (f.Length == 0) return;
                if (f[0] == "9") ready.TrySetResult(true);
                else if (f[0] == "15" && f.Length > 2) client.Accounts = f[2];
                else if (f[0] == "4" && Error(f) is { Id: -1, Code: var code, Text: var text } && code is 326 or 502 or 503 or 504 or 1100)
                    ready.TrySetException(new IbException(code, text));
            }

            client.Message += OnFirst;
            client.StartReading();
            client.Send(71, 2, clientId, ""); // START_API con optionalCapabilities vacío
            var done = await Task.WhenAny(ready.Task, client.Closed, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            client.Message -= OnFirst;
            if (done == ready.Task)
            {
                await ready.Task.ConfigureAwait(false); // lanza el error si lo hubo
                return client;
            }

            if (done == client.Closed) throw new IOException((await client.Closed.ConfigureAwait(false)) ?? "IB Gateway cerró la conexión");
            throw new TimeoutException("IB Gateway no respondió al iniciar la API");
        }
        catch
        {
            if (client != null) client.Dispose();
            else tcp.Dispose();
            throw;
        }
    }

    /// <summary>Error (id del pedido, código y texto) de un mensaje ERR_MSG (4) de la versión 187.</summary>
    public static (int Id, int Code, string Text) Error(string[] f)
    {
        var r = new IbReader(f, 1);
        r.ReadInt(); // versión del mensaje
        int id = r.ReadInt();
        int code = r.ReadInt();
        return (id, code, Unescape(r.ReadString()));
    }

    /// <summary>TWS manda los caracteres no ASCII como \uXXXX (ENCODE_MSG_ASCII7).</summary>
    internal static string Unescape(string s)
    {
        if (!s.Contains("\\u", StringComparison.Ordinal)) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length && s[i + 1] == 'u' && int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, Inv, out int c))
            {
                sb.Append((char)c);
                i += 5;
            }
            else sb.Append(s[i]);
        }

        return sb.ToString();
    }

    private void StartReading()
    {
        var thread = new Thread(() =>
        {
            string? reason = null;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var fields = ReadMessageAsync(_cts.Token).GetAwaiter().GetResult();
                    try
                    {
                        Message?.Invoke(fields);
                    }
                    catch (Exception)
                    {
                        // un error al procesar un mensaje no corta la conexión
                    }
                }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                reason = ex is EndOfStreamException ? "IB Gateway cerró la conexión" : ex.Message;
            }
            catch (Exception)
            {
                // cerrada desde acá
            }

            _closed.TrySetResult(reason);
        }) { IsBackground = true, Name = "ib-reader" };
        thread.Start();
    }

    private async Task<string[]> ReadMessageAsync(CancellationToken ct)
    {
        var len = new byte[4];
        await _stream.ReadExactlyAsync(len, ct).ConfigureAwait(false);
        int n = (len[0] << 24) | (len[1] << 16) | (len[2] << 8) | len[3];
        if (n < 0 || n > 64 * 1024 * 1024) throw new InvalidDataException($"Mensaje de IB inválido ({n} bytes)");
        var body = new byte[n];
        await _stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(body);
        if (text.EndsWith('\0')) text = text[..^1];
        return text.Split('\0');
    }

    private static byte[] Frame(byte[] payload)
    {
        var buf = new byte[payload.Length + 4];
        buf[0] = (byte)(payload.Length >> 24);
        buf[1] = (byte)(payload.Length >> 16);
        buf[2] = (byte)(payload.Length >> 8);
        buf[3] = (byte)payload.Length;
        payload.CopyTo(buf, 4);
        return buf;
    }

    /// <summary>Texto de un campo como lo espera TWS (bool 1/0, números con punto, null vacío).</summary>
    public static string Field(object? v) => v switch
    {
        null => "",
        bool b => b ? "1" : "0",
        double d => d.ToString("R", Inv),
        decimal m => m.ToString(Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    /// <summary>El mensaje armado (sin el largo): cada campo terminado en \0.</summary>
    public static byte[] Encode(params object?[] fields)
    {
        var sb = new StringBuilder();
        foreach (var f in fields) sb.Append(Field(f)).Append('\0');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public void Send(params object?[] fields)
    {
        var data = Frame(Encode(fields));
        lock (_sendLock)
        {
            try
            {
                _stream.Write(data);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                _closed.TrySetResult("No se pudo enviar a IB Gateway: " + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _closed.TrySetResult(null);
        try { _tcp.Dispose(); }
        catch { /* ya cerrada */ }
    }
}

/// <summary>Lee los campos de un mensaje de la TWS API en orden.</summary>
public sealed class IbReader(string[] fields, int start = 0)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private int _i = start;

    public bool HasMore => _i < fields.Length;

    public string ReadString() => _i < fields.Length ? fields[_i++] : "";

    public int ReadInt() => int.TryParse(ReadString(), NumberStyles.Integer, Inv, out var v) ? v : 0;

    public long ReadLong() => long.TryParse(ReadString(), NumberStyles.Integer, Inv, out var v) ? v : 0;

    public double ReadDouble() => double.TryParse(ReadString(), NumberStyles.Float, Inv, out var v) ? v : 0;

    /// <summary>Tamaños (Decimal de la API): vacío o el máximo de long = sin dato.</summary>
    public decimal ReadDecimal()
    {
        var s = ReadString();
        if (s.Length == 0 || s == "9223372036854775807" || !decimal.TryParse(s, NumberStyles.Float, Inv, out var v)) return 0;
        return v;
    }
}
