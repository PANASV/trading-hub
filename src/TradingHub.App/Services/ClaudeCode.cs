using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace TradingHub.App.Services;

/// <summary>Resultado de un pedido a Claude Code.</summary>
public sealed record ClaudeResult(bool IsError, string Text, double CostUsd, double Seconds, bool NeedsLogin);

/// <summary>
/// Puente con Claude Code (la CLI <c>claude</c> instalada en la Mac, con la sesión de la suscripción
/// del usuario). Corre en modo no interactivo dentro de la carpeta de estrategias, con permiso solo
/// para leer y editar archivos de esa carpeta, y transmite la respuesta a medida que llega.
/// </summary>
public sealed class ClaudeCodeBridge
{
    private static string? _helpText;
    private static string? _helpCli;

    /// <summary>¿La CLI instalada conoce esta opción? (se lee una vez su --help)</summary>
    public static async Task<Func<string, bool>> SupportsAsync(string cli)
    {
        if (_helpText == null || _helpCli != cli)
        {
            string text = "";
            try
            {
                var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                psi.ArgumentList.Add("--help");
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var read = p.StandardOutput.ReadToEndAsync();
                if (await Task.WhenAny(read, Task.Delay(15000)) == read) text = await read;
                else p.Kill(true);
            }
            catch
            {
                // sin ayuda: solo las opciones básicas
            }

            _helpText = text;
            _helpCli = cli;
        }

        var help = _helpText;
        return flag => help.Contains(flag + " ", StringComparison.Ordinal) || help.Contains(flag + ",", StringComparison.Ordinal) || help.Contains(flag + "\n", StringComparison.Ordinal);
    }

    private Process? _proc;
    private bool _streamedText;

    /// <summary>Id de la conversación (para seguirla con --resume).</summary>
    public string? SessionId { get; private set; }

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>Texto de Claude a medida que lo escribe.</summary>
    public event Action<string>? Text;

    /// <summary>Claude usó una herramienta (leyó, creó o editó un archivo).</summary>
    public event Action<string, string?>? ToolUsed;

    /// <summary>Terminó una herramienta (por ejemplo, ya se escribió el archivo).</summary>
    public event Action? ToolFinished;

    public event Action<ClaudeResult>? Finished;

    public void NewConversation() => SessionId = null;

    /// <summary>Sigue una conversación guardada (el próximo mensaje va con --resume a esa sesión).</summary>
    public void Resume(string? sessionId) => SessionId = sessionId;

    private static string? _best;
    private static DateTime _bestAt;
    private static readonly Dictionary<string, (DateTime Stamp, Version? Version)> Versions = new();

    /// <summary>Modelo y esfuerzo con que trabaja Claude en Trading Hub (Opus 5.5 con esfuerzo extra).</summary>
    public static string Model { get; set; } = "claude-opus-5-5";
    public static string Effort { get; set; } = "xhigh";

    /// <summary>
    /// Ruta de la CLI de Claude Code (o null si no está instalada). Si hay varias (la de la Terminal, la que trae la app
    /// de Claude…) usa la más nueva: los modelos nuevos (Opus 5.5) piden una versión reciente.
    /// </summary>
    public static string? FindCli()
    {
        var custom = Environment.GetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;
        lock (Versions)
        {
            if (_best != null && File.Exists(_best) && (DateTime.UtcNow - _bestAt).TotalMinutes < 10) return _best;
            var found = Candidates().Where(File.Exists).Distinct().ToList();
            _best = found.Select(p => (Path: p, Version: VersionOf(p))).OrderByDescending(x => x.Version ?? new Version(0, 0)).Select(x => x.Path).FirstOrDefault();
            _bestAt = DateTime.UtcNow;
            return _best;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            "/usr/local/bin/claude",
            "/opt/homebrew/bin/claude",
            Path.Combine(home, ".claude", "local", "claude"),
            Path.Combine(home, ".local", "bin", "claude"),
            Path.Combine(home, ".npm-global", "bin", "claude"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            list.Add(Path.Combine(dir, "claude"));
        // la que trae la app de Claude (se actualiza sola): …/claude-code/2.1.295/<id>/claude.app/Contents/MacOS/claude
        var bundled = Path.Combine(home, "Library", "Application Support", "Claude", "claude-code");
        try
        {
            if (Directory.Exists(bundled))
                foreach (var v in Directory.GetDirectories(bundled))
                    foreach (var h in Directory.GetDirectories(v))
                        list.Add(Path.Combine(h, "claude.app", "Contents", "MacOS", "claude"));
        }
        catch (Exception)
        {
            // sin permiso para mirar: se usan las otras
        }

        return list;
    }

    /// <summary>Versión de una CLI ("2.1.295 (Claude Code)" → 2.1.295), con caché por archivo.</summary>
    private static Version? VersionOf(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (Versions.TryGetValue(path, out var v) && v.Stamp == stamp) return v.Version;
            var psi = Prepare(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            Version? version = null;
            if (read.Wait(TimeSpan.FromSeconds(8)) && Version.TryParse(read.Result.Trim().Split(' ')[0], out var parsed)) version = parsed;
            else if (!p.HasExited) p.Kill(true);
            Versions[path] = (stamp, version);
            return version;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Versión instalada ("2.1.251 (Claude Code)") o null.</summary>
    public static async Task<string?> VersionAsync()
    {
        var cli = FindCli();
        if (cli == null) return null;
        try
        {
            var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Abre la Terminal con Claude Code para iniciar sesión (/login) con la suscripción.</summary>
    public static void OpenLoginTerminal()
    {
        var cli = FindCli() ?? "claude";
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"Terminal\" to do script \"'{cli}' /login\"");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tell application \"Terminal\" to activate");
            Process.Start(psi);
        }
        catch
        {
            // sin Terminal: el usuario puede escribir "claude" a mano
        }
    }

    internal static ProcessStartInfo Prepare(string cli, string workDir)
    {
        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // entorno limpio: que no herede credenciales temporales de otra sesión de Claude
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("CLAUDE", StringComparison.Ordinal) || key.StartsWith("ANTHROPIC", StringComparison.Ordinal))
                psi.Environment.Remove(key);
        psi.Environment["PATH"] = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin:" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["TERM"] = "dumb";
        return psi;
    }

    /// <summary>Herramientas del laboratorio (servidor MCP de Trading Hub).</summary>
    public static readonly string[] LabTools =
    {
        "list_strategies", "compile_strategy", "list_data", "download_data", "run_backtest", "run_optimization", "run_walk_forward", "list_runs",
        "analyze_portfolio", "suggest_portfolio", "live_portfolio", "propose_sizing", "save_report", "list_indicators", "compile_indicator", "chart_snapshot",
    };

    /// <summary>Config MCP que levanta Trading Hub en modo laboratorio (el mismo ejecutable con --mcp).</summary>
    internal static string WriteLabConfig()
    {
        // TRADINGHUB_MCP_COMMAND: otro ejecutable de Trading Hub (para pruebas); si no, este mismo
        var exe = Environment.GetEnvironmentVariable("TRADINGHUB_MCP_COMMAND") is { Length: > 0 } custom ? custom : Environment.ProcessPath ?? "TradingHub";
        var env = new Dictionary<string, string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) env["DOTNET_ROOT"] = root;
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["tradinghub"] = new Dictionary<string, object> { ["type"] = "stdio", ["command"] = exe, ["args"] = new[] { "--mcp" }, ["env"] = env },
            },
        };
        Directory.CreateDirectory(AppPaths.Root);
        var path = Path.Combine(AppPaths.Root, "claude-lab-mcp.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Manda un pedido. Los eventos llegan en el hilo de UI. Con <paramref name="lab"/> Claude puede correr pruebas.</summary>
    public async Task SendAsync(string prompt, string workDir, string systemPrompt, bool lab = false, IReadOnlyList<string>? extraDirs = null)
    {
        if (IsRunning) return;
        var cli = FindCli();
        if (cli == null)
        {
            Post(() => Finished?.Invoke(new ClaudeResult(true, "No encontré Claude Code en esta Mac. Instalalo desde claude.com/claude-code y volvé a intentar.", 0, 0, false)));
            return;
        }

        var psi = Prepare(cli, workDir);
        foreach (var a in new[]
                 {
                     "-p", prompt,
                     "--output-format", "stream-json",
                     "--verbose",
                     "--include-partial-messages",
                     "--append-system-prompt", systemPrompt,
                     "--tools", "Read,Write,Edit,MultiEdit,Glob,Grep",
                     "--permission-mode", "acceptEdits",
                     "--strict-mcp-config",
                 })
            psi.ArgumentList.Add(a);
        // Opus 5.5 con esfuerzo extra (las opciones que esta CLI no conoce se omiten)
        var supports = await SupportsAsync(cli);
        if (!string.IsNullOrWhiteSpace(Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(Model);
        }

        if (!string.IsNullOrWhiteSpace(Effort) && supports("--effort"))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(Effort);
        }

        // otras carpetas donde puede escribir (por ejemplo la de indicadores)
        foreach (var dir in extraDirs ?? Array.Empty<string>())
        {
            if (!Directory.Exists(dir) || !supports("--add-dir")) continue;
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(dir);
        }

        if (lab)
        {
            // laboratorio: Trading Hub como servidor MCP (backtest, optimización, walk-forward, reportes)
            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(WriteLabConfig());
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add(string.Join(",", LabTools.Select(t => "mcp__tradinghub__" + t)));
            // una optimización o un walk-forward pueden tardar varios minutos
            psi.Environment["MCP_TOOL_TIMEOUT"] = "3600000";
            psi.Environment["MCP_TIMEOUT"] = "60000";
        }

        if (SessionId != null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(SessionId);
        }

        var sw = Stopwatch.StartNew();
        var finalText = new StringBuilder();
        var stderr = new StringBuilder();
        bool isError = false, gotResult = false;
        double cost = 0;
        try
        {
            _proc = Process.Start(psi)!;
            _proc.StandardInput.Close();
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };
            _proc.BeginErrorReadLine();
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String) SessionId = sid.GetString();
                    switch (type)
                    {
                        case "stream_event":
                            HandleStreamEvent(root);
                            break;
                        case "assistant":
                            HandleAssistant(root);
                            break;
                        case "user":
                            Post(() => ToolFinished?.Invoke());
                            break;
                        case "result":
                            gotResult = true;
                            isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) finalText.Append(r.GetString());
                            if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDouble();
                            break;
                    }
                }
                catch (JsonException)
                {
                    // línea que no es JSON: se ignora
                }
            }

            await _proc.WaitForExitAsync();
            if (!gotResult)
            {
                isError = true;
                finalText.Append(stderr.Length > 0 ? stderr.ToString().Trim() : $"Claude Code terminó sin respuesta (código {_proc.ExitCode}).");
            }
        }
        catch (Exception ex)
        {
            isError = true;
            finalText.Append("No se pudo ejecutar Claude Code: " + ex.Message);
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }

        string text = finalText.ToString();
        bool needsLogin = isError && (text.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || text.Contains("/login", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase));
        var result = new ClaudeResult(isError, text, cost, sw.Elapsed.TotalSeconds, needsLogin);
        Post(() => Finished?.Invoke(result));
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev)) return;
        string evType = ev.TryGetProperty("type", out var et) ? et.GetString() ?? "" : "";
        if (evType == "message_start") _streamedText = false;
        if (evType != "content_block_delta" || !ev.TryGetProperty("delta", out var delta)) return;
        if (delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta" && delta.TryGetProperty("text", out var txt))
        {
            _streamedText = true;
            string s = txt.GetString() ?? "";
            Post(() => Text?.Invoke(s));
        }
    }

    private void HandleAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            string bt = block.TryGetProperty("type", out var b) ? b.GetString() ?? "" : "";
            if (bt == "text" && !_streamedText && block.TryGetProperty("text", out var txt))
            {
                string s = txt.GetString() ?? "";
                Post(() => Text?.Invoke(s));
            }
            else if (bt == "tool_use")
            {
                string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string? file = null;
                if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                {
                    if (input.TryGetProperty("file_path", out var fp)) file = fp.GetString();
                    else if (input.TryGetProperty("pattern", out var pat)) file = pat.GetString();
                    else if (name.StartsWith("mcp__", StringComparison.Ordinal))
                    {
                        // laboratorio: estrategia, instrumento y rango
                        string S(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        var parts = new[] { S("file"), S("symbol"), S("from").Length > 0 ? $"{S("from")} → {S("to")}" : "", S("title") }.Where(x => x.Length > 0);
                        file = string.Join(" · ", parts);
                    }
                }

                Post(() => ToolUsed?.Invoke(name, file));
            }
        }

        _streamedText = false;
    }

    public void Cancel()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // ya terminó
        }
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);

    /// <summary>Método de trabajo del laboratorio (se suma a las instrucciones de estrategias).</summary>
    public const string LabSystemPrompt = """

        MODO LABORATORIO. Tenés las herramientas mcp__tradinghub__* para evaluar estrategias por tu cuenta, sin pedirle nada al usuario:
        list_strategies, compile_strategy, list_data, download_data, run_backtest (con monte_carlo_runs), run_optimization,
        run_walk_forward, list_runs y save_report. Todo lo que corrés queda guardado en el historial de la estrategia en Trading Hub.

        Método (decidí vos los parámetros, rangos y datos; iterá hasta tener una conclusión firme):
        1. Revisá la estrategia (list_strategies / leé el archivo) y los datos (list_data). Si hay pocos días y el feed es
           binance-futures, binance-spot o bybit-linear, bajá más con download_data (apuntá a 30-90 días; cada día tarda ~5 s).
        2. Backtest base con los parámetros por defecto (comisión 0.04% y 1 tick de deslizamiento, salvo que el usuario diga otra cosa).
        3. Optimización con rangos razonables (menos de ~2000 combinaciones), métrica NetProfitOverDrawdown o Sharpe y
           min_trades ≥ 30. Mirá cuántas combinaciones dan ganancia (robustez), no solo la mejor.
        4. Walk-forward con los mismos rangos (ventana de optimización 3-4 veces la de prueba). Lo que vale es el resultado
           fuera de muestra: eficiencia, % de ventanas ganadoras, PF y drawdown fuera de muestra.
        5. Monte Carlo (monte_carlo_runs: 1000) sobre los mejores parámetros validados.
        6. Si la lógica tiene fallas claras, mejorá el código (editá el .cs), compilá con compile_strategy y repetí la evaluación.
           Compará siempre contra la versión anterior y no te quedes con cambios que solo mejoran dentro de muestra.
        7. Veredicto:
           - ganadora: fuera de muestra positiva, eficiencia walk-forward ≥ 0.5, PF fuera de muestra ≥ 1.2, ≥ 30 operaciones,
             drawdown p95 de Monte Carlo razonable para el capital y resultados estables entre ventanas.
           - prometedora: resultados positivos pero con poca muestra o algo de inestabilidad: hace falta más datos.
           - perdedora: pierde fuera de muestra o solo gana sobreajustada.
           - descartada: no compila, no opera o la idea no tiene sentido con estos datos.
        8. Portafolios: si hay varias estrategias (o la misma en varios instrumentos) con backtests, usá analyze_portfolio para
           medir la correlación de sus resultados diarios y suggest_portfolio para buscar combinaciones poco correlacionadas
           (correlación < 0.5) que mejoren el Sharpe y bajen el drawdown del conjunto.
        Cartera en vivo: si te piden revisar las estrategias que operan en vivo, usá live_portfolio (resultado, PF total y de
        las últimas 20 operaciones, drawdown actual y máximo de cada una) y proponé con propose_sizing: más tamaño (hasta ×3) a
        las que rinden de forma consistente, menos o pausa (0) a las que se degradan, siempre con un motivo concreto basado en
        los números. Con menos de ~10 operaciones no saques conclusiones. Nunca se aplica nada solo: el usuario aprueba cada
        cambio. En ese caso no hace falta save_report salvo que lo pidan.
        9. Terminá SIEMPRE con save_report (cuando evaluaste estrategias). Trading Hub arma solo el informe profesional con TODAS las métricas, tablas y gráficos a
           partir de las corridas (pasale backtest_run_id —el mejor validado, con Monte Carlo—, optimization_run_id y
           walk_forward_run_id; si evaluaste un portafolio, también «portfolio»). Vos escribís: verdict, summary (2 a 5 líneas),
           analysis (qué funciona y qué no, robustez según la sensibilidad de la optimización, eficiencia y estabilidad del
           walk-forward, Monte Carlo, comparación entre versiones del código), risks y next_steps, en Markdown y en español.
           Si cambiaste el código, explicalo en extra_markdown.
        No inventes resultados: todo número que escribas tiene que salir de una herramienta. En el chat, al final, contestá en
        2 a 4 líneas con el veredicto y los números clave (el detalle va en el informe).
        Ver el gráfico: con chart_snapshot corrés la estrategia en un rango y te queda una imagen del gráfico (velas, entradas y
        salidas, sus dibujos y líneas) que mirás con Read, más la lista de velas, operaciones, dibujos y registro con hora
        local. Usalo cuando el usuario te pregunta por algo que ve en su gráfico («marca la ruptura a las 7:15 pero entra a las
        8:45») o para revisar que lo que programaste hace lo que dice: mirá la vela exacta, los valores de sus líneas y la orden.
        """;

    /// <summary>Instrucciones para que Claude escriba indicadores propios (además de estrategias).</summary>
    public const string IndicatorSystemPrompt = """

        INDICADORES (cuando te piden un indicador y no una estrategia):
        - Van en la carpeta de indicadores del usuario (te paso la ruta en el contexto; tenés permiso para escribir ahí), un
          archivo .cs por indicador con la clase del mismo nombre. Se compilan solos al guardar y aparecen en el gráfico en
          Indicadores → «Mis indicadores». No los pongas en la carpeta de estrategias.
        - usings: System.ComponentModel, System.ComponentModel.DataAnnotations, System.Drawing (colores: Color.Orange,
          Color.FromArgb(a, r, g, b)), TradingHub.Indicators, TradingHub.Indicators.Drawing y, si dibujás vos, TradingHub.Rendering.Context y
          TradingHub.Rendering.Tools. System, System.Linq y System.Collections.Generic ya están.
        - `[DisplayName("Nombre visible")] [Category("Mis indicadores")] public class MiIndicador : Indicator` con constructor
          sin parámetros.
        - Series que se dibujan solas (campos): ValueDataSeries (línea o histograma:
          `new ValueDataSeries("Id", "Nombre") { Color = Color.Orange, Width = 2, VisualType = VisualMode.Line }`, también
          VisualMode.Histogram), RangeDataSeries (bandas: `_banda[bar] = new RangeValue { Upper = a, Lower = b }`) y
          PaintbarsDataSeries (pintar velas: `_velas[bar] = Color.Lime`). En el constructor: `DataSeries[0] = _serie;` (la
          principal) y `DataSeries.Add(_otra);` para las demás.
        - Panel: por defecto sobre el precio; para uno propio abajo (osciladores) `Panel = IndicatorDataProvider.NewPanel;`
          en el constructor.
        - Parámetros: propiedades públicas con `[Display(Name = "Período", GroupName = "Configuración")]` y `[Range(1, 1000)]`;
          en el set guardá el valor y llamá `RecalculateValues();`.
        - Cálculo: `protected override void OnCalculate(int bar, decimal value)` se llama para cada vela (bar = índice desde 0,
          value = cierre). `GetCandle(bar)` → Open, High, Low, Close, Volume, Ask, Bid, Delta, MaxDelta, MinDelta, Time.
          `CurrentBar` = cantidad de velas. Escribí `_serie[bar] = valor;` y leé las anteriores con `_serie[bar - 1]` (cuidá
          los índices negativos: al principio no hay suficientes velas).
        - Dibujo propio (opcional, por ejemplo rangos o etiquetas): en el constructor `EnableCustomDrawing = true;
          SubscribeToDrawingEvents(DrawingLayouts.Final);` y `protected override void OnRender(RenderContext context,
          DrawingLayouts layout)` con `ChartInfo.GetXByBar(bar)`, `ChartInfo.GetYByPrice(precio)`,
          `context.DrawLine(new RenderPen(color, 1), x1, y1, x2, y2)`, `context.FillRectangle(color, new Rectangle(x, y, w, h))`,
          `context.DrawString(texto, new RenderFont("Arial", 9), color, x, y)`. Velas visibles: FirstVisibleBarNumber y
          LastVisibleBarNumber.
        - Con las herramientas del laboratorio, compilalo con compile_indicator (y list_indicators para ver los que hay) y
          corregí hasta que compile. Sin laboratorio, Trading Hub lo compila al terminar y te pasa los errores.
        """;

    /// <summary>Instrucciones para que Claude escriba estrategias que compilen en Trading Hub.</summary>
    public const string StrategySystemPrompt = """
        Sos el asistente de estrategias de Trading Hub, una plataforma de trading de escritorio. Trabajás dentro de la
        carpeta de estrategias del usuario (el directorio actual). Respondé siempre en español rioplatense, breve.

        Cómo es una estrategia de Trading Hub:
        - Un archivo .cs por estrategia en el directorio actual. El nombre de la clase es igual al nombre del archivo.
        - `using TradingHub.Strategies;` (System, System.Linq y System.Collections.Generic ya están disponibles).
        - `[Strategy("Nombre visible", Description = "...")] public class MiEstrategia : Strategy { ... }` con constructor sin parámetros.
        - Parámetros editables: propiedades públicas con `[Parameter("Nombre")]`; para optimizarlas, además `[Optimize(mín, máx, paso)]`.
        - Ciclo: `protected override void OnInit()` (crear indicadores), `protected override void OnBar(Bar bar)` (al cerrar cada vela),
          opcional `OnTick(Trade trade)`, `OnOrderFilled(StrategyOrder order)`, `OnSessionStart(DateOnly session)`, `OnStop()`.
        - Datos: `Bars[0]` última vela cerrada, `Bars[1]` la anterior. `Bar`: Open, High, Low, Close, Volume, BuyVolume, SellVolume,
          Delta, MaxDelta, MinDelta, Trades, PocPrice, IsBullish, IsBearish, Range, Body, Typical, Time, CloseTime, Index.
          `bar.Levels` (precio → (Buy, Sell)) solo si en OnInit se pone `UseFootprint = true`.
          También: Price, Time (UTC), LocalTime, TickSize, CurrentBar, Instrument, IsBacktest.
        - Indicadores (crear en OnInit y guardar en campos): Sma(n), Ema(n), Rsi(n), Atr(n), Vwap(), Bollinger(n, k) (.Upper/.Lower/.Middle),
          Macd(12,26,9) (.Signal/.Histogram), Highest(n), Lowest(n), StdDev(n), CumulativeDelta(), Custom("nombre", bars => valor).
          Se leen como serie: `ind[0]` actual, `ind[1]` anterior, `ind.Value`, `ind.IsReady`. PriceSource: Close, Open, High, Low, Typical, Median, Volume, Delta.
        - Órdenes: EnterLong(), EnterShort() (dan vuelta si hace falta), Buy(cant), Sell(cant), BuyLimit(precio), SellLimit(precio),
          BuyStop(precio), SellStop(precio), ClosePosition(), CancelAll(), Cancel(orden), Modify(orden, precio).
          Gestión automática (OCO): propiedades StopLossTicks, TakeProfitTicks, TrailingStopTicks (se pueden fijar en el constructor).
          Quantity = cantidad por defecto.
        - Posición: Position (>0 largo, <0 corto), IsFlat, IsLong, IsShort, AveragePrice, OpenPnL, NetProfit, ActiveOrders.
        - Ayudas: CrossAbove(a, b), CrossBelow(a, b) (series o series contra un número), Ticks(n), Round(precio),
          TimeBetween(TimeSpan desde, TimeSpan hasta) (hora local de la sesión), Log("texto").
        - Precios y volúmenes son decimal (convertí double con (decimal) cuando haga falta).
        - Dibujos en el gráfico (se ven al ponerla en un gráfico, en la historia y en vivo). Tiempos UTC (bar.Time,
          Bars[n].Time); color "#RRGGBB", "#AARRGGBB" o un nombre ("verde", "rojo", "azul", "celeste", "amarillo", "naranja",
          "violeta", "rosa", "gris", "blanco"); sin color, el de la estrategia. Repetir el mismo id reemplaza el dibujo (sirve
          para estirar el rango del día vela a vela); usá ids distintos para cosas distintas, por ejemplo $"rango-{bar.Time:yyyyMMdd}".
            DrawBox(id, desde, precio1, hasta, precio2, color, fillOpacity: 15, text) → rectángulo (rangos, zonas)
            DrawLine(id, tiempo1, precio1, tiempo2, precio2, color, width, LineStyle.Solid|Dashed|Dotted)
            DrawHorizontalLine(id, precio, desde: null, color, width, style, text) → nivel hasta el borde derecho
            DrawArrowUp(id, tiempo, precio, color, text) / DrawArrowDown(...) → flecha con la punta en el precio
            DrawMarker(id, tiempo, precio, MarkerShape.Circle|Square|Diamond|Cross|ArrowUp|ArrowDown, color, text)
            DrawText(id, tiempo, precio, "texto", color)
            Plot("nombre", valor, color, width, style) → línea continua vela a vela (llamala en OnBar; null la corta)
            RemoveDrawing(id), ClearDrawings()
          Ejemplo: en OnBar, `Plot("Techo", _max[0], "verde"); Plot("Piso", _min[0], "rojo");` y en una ruptura
          `DrawArrowUp($"ruptura-{bar.Time:yyyyMMddHHmm}", bar.Time, bar.Close, "verde", "ruptura");`. Hay un tope de
          3.000 dibujos (se van los más viejos): no dibujes en cada vela algo que no lo necesita.
        - Contexto del mercado (con velas ya cerradas, sin mirar el futuro): MarketTrend (eficiencia de la tendencia de las
          últimas 24 h: 0 = rango puro, 1 = tendencia limpia; al azar ~0,2), Regime (MarketRegime.Range si ≤ 0,15, Mixed,
          Trend si ≥ 0,35, Unknown sin datos), MarketDirection (1 si en esas 24 h subió, -1 si bajó: una tendencia es
          alcista o bajista según eso), MarketChange (movimiento neto de 24 h, en %), MarketVolatility (desvío de los
          retornos por hora de 24 h, en %) y VolatilityRank (percentil de esa volatilidad en el último mes: 0 = la más baja,
          100 = la más alta). Pueden ser null. En vivo arrancan con el último mes de historia por hora.
        - Filtro de contexto (todas las estrategias lo traen, sin escribir código): parámetros RegimeFilter (All, RangeOnly,
          NoTrend, TrendOnly, NoRange, UpTrendOnly, DownTrendOnly, MixedOnly, RangeOrUpTrend, RangeOrDownTrend, MixedOrUpTrend,
          MixedOrDownTrend, NoDownTrend, NoUpTrend, NoMixed: cualquier combinación de rango, mixto y tendencia alcista o bajista),
          MinVolatilityRank y MaxVolatilityRank (0 a 100). Fuera de ese contexto la estrategia
          no abre ni aumenta posiciones (las salidas siempre pasan). Se pueden fijar como parámetros en el backtest, la
          optimización, el walk-forward y en vivo. ContextAllowsEntries dice si ahora deja entrar.

        Reglas:
        - Escribí código que compile en C# 13 sin paquetes externos. Nada de archivos, red, hilos ni Random sin semilla.
        - Comentarios cortos en español explicando la lógica.
        - Para crear una estrategia nueva creá un archivo nuevo con un nombre claro en PascalCase; para modificar, editá el archivo indicado.
        - Si te pasan errores de compilación, corregí el archivo.
        - Al terminar, explicá en 2 a 5 líneas qué hace la estrategia o qué cambiaste (sin pegar el código: el usuario lo ve en el editor).
        """;
}
