using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>
/// Agente de riesgo de un portafolio en vivo: Claude (Opus, esfuerzo extra) mira cómo viene cada estrategia —resultado,
/// racha reciente, drawdown contra su historia, volatilidad del resultado diario y correlación con las demás— y reparte
/// el riesgo: tamaño de cada una y, si hace falta, su pérdida máxima del día y su drawdown máximo, dentro de los topes que
/// le pusiste. Por defecto deja una propuesta y vos aprobás cada cambio; en modo automático (solo si ninguna opera una
/// cuenta real) los aplica solo. Las bloqueadas (las que gestiona otro) no las toca.
/// </summary>
internal sealed class LiveRiskAgent
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly LiveCenter _center;
    private readonly Dictionary<string, string> _activity = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();

    public LiveRiskAgent(AppServices svc, LiveCenter center)
    {
        _svc = svc;
        _center = center;
    }

    /// <summary>Sube con cada cambio (para refrescar la vista).</summary>
    public int Version { get; private set; }

    public bool IsRunning(string portfolioId) => _running.ContainsKey(portfolioId);

    public bool AnyRunning => _running.Count > 0;

    /// <summary>Qué está haciendo con ese portafolio (o null).</summary>
    public string? Activity(string portfolioId) => _activity.GetValueOrDefault(portfolioId);

    public void Cancel(string portfolioId)
    {
        if (_running.TryGetValue(portfolioId, out var cts)) cts.Cancel();
    }

    private void Touch() => Version++;

    /// <summary>Revisa qué portafolios toca revisar (si hubo operaciones nuevas o pasó mucho tiempo).</summary>
    public void Tick()
    {
        if (!LiveStore.LoadPortfolios().Any(d => d.Agent.Enabled)) return;
        var entries = _center.Entries();
        foreach (var p in _center.Portfolios(entries))
        {
            var a = p.Def.Agent;
            if (!a.Enabled || IsRunning(p.Def.Id)) continue;
            if (!p.Members.Any(m => m.Running && !m.Locked)) continue;
            if (RunsToday(a) >= a.MaxRunsPerDay) continue;
            int trades = p.Members.Sum(m => m.Ledger.Trades.Count);
            var now = DateTime.UtcNow;
            bool due = a.LastRunUtc is not { } last
                ? trades >= a.MinNewTrades
                : (now - last).TotalMinutes >= Math.Max(10, a.EveryMinutes) &&
                  (trades - a.TradesAtLastRun >= a.MinNewTrades || (now - last).TotalHours >= Math.Max(1, a.ForceEveryHours));
            if (due) _ = RunAsync(p.Def.Id, manual: false);
        }
    }

    public static int RunsToday(LiveRiskAgentSettings a) => a.RunsUtc.Count(t => t.ToLocalTime().Date == DateTime.Now.Date);

    /// <summary>Corre el agente sobre un portafolio. Devuelve null si salió bien, o el motivo.</summary>
    public async Task<string?> RunAsync(string portfolioId, bool manual)
    {
        if (IsRunning(portfolioId)) return "Ya está revisando ese portafolio.";
        var p = _center.Portfolios().FirstOrDefault(x => x.Def.Id == portfolioId);
        if (p == null) return "No encuentro el portafolio.";
        var a = p.Def.Agent;
        var adjustable = p.Members.Where(m => !m.Locked && (m.Running || m.WaitingResume)).ToList();
        if (adjustable.Count == 0) return "No hay estrategias para ajustar.";
        if (!manual && RunsToday(a) >= a.MaxRunsPerDay) return "Llegó al tope de revisiones de hoy.";
        if (ClaudeCodeBridge.FindCli() == null) return "No encontré Claude Code en esta Mac.";

        var cts = new CancellationTokenSource();
        _running[portfolioId] = cts;
        _activity[portfolioId] = "Revisando el riesgo del portafolio…";
        Touch();
        try
        {
            var prompt = BuildPrompt(p, adjustable);
            var dir = Path.Combine(LiveStore.Root, "agente-riesgo", p.Def.Id);
            Directory.CreateDirectory(dir);
            var agent = new ClaudeAgent();
            var req = new ClaudeAgentRequest
            {
                Role = "live-risk",
                Prompt = prompt,
                SystemPrompt = SystemPrompt,
                WorkDir = dir,
                Tools = Array.Empty<string>(),
                LabTools = Array.Empty<string>(),
                PermissionMode = "dontAsk",
                Model = string.IsNullOrWhiteSpace(a.Model) ? ClaudeCodeBridge.Model : a.Model,
                Effort = string.IsNullOrWhiteSpace(a.Effort) ? ClaudeCodeBridge.Effort : a.Effort,
                MaxUsd = a.MaxUsdPerRun,
                Timeout = TimeSpan.FromMinutes(12),
            };
            var res = await agent.RunAsync(req, cts.Token);
            a.LastRunUtc = DateTime.UtcNow;
            a.TradesAtLastRun = p.Members.Sum(m => m.Ledger.Trades.Count);
            a.RunsUtc.Add(DateTime.UtcNow);
            a.RunsUtc.RemoveAll(t => t < DateTime.UtcNow.AddDays(-7));
            a.CostTotal += res.CostUsd;
            if (!res.Ok || res.Json is not { } json)
            {
                a.LastError = res.Error ?? "El agente no devolvió el bloque JSON pedido.";
                if ((res.Error ?? "").Contains("or newer is required", StringComparison.OrdinalIgnoreCase))
                    a.LastError = "Tu Claude Code es viejo para Opus 5.5: abrí la app de Claude (se actualiza sola) o corré «claude update».";
                Save(p.Def);
                _center.Notify($"⚠ Agente de riesgo de «{p.Def.Name}»: {a.LastError}");
                return a.LastError;
            }

            a.LastError = null;
            var proposal = Parse(json, p, adjustable);
            a.LastSummary = proposal.Summary;
            Save(p.Def);
            if (proposal.Items.Count == 0)
            {
                if (manual) _center.Notify($"Agente de riesgo de «{p.Def.Name}»: sin cambios. {Short(proposal.Summary, 160)}");
                return null;
            }

            bool auto = a.Mode == "auto" && !p.HasReal && adjustable.All(m => !m.Info.IsReal);
            if (auto)
            {
                foreach (var item in proposal.Items)
                {
                    var e = adjustable.First(m => m.Key == item.Key);
                    _center.ApplyRisk(e, item.ProposedSize, item.ProposedMaxDailyLoss ?? item.CurrentMaxDailyLoss, item.ProposedMaxDrawdown ?? item.CurrentMaxDrawdown,
                        e.Runner?.CloseOnLimit ?? e.Info.CloseOnLimit);
                    item.Status = "approved";
                }

                proposal.Source = "agente (automático)";
                LiveStore.AddProposal(proposal);
                _center.Notify($"El agente de riesgo ajustó {proposal.Items.Count} estrategia(s) de «{p.Def.Name}» (modo automático, cuentas simuladas).");
            }
            else
            {
                LiveStore.AddProposal(proposal);
                _center.Notify($"El agente de riesgo propone {proposal.Items.Count} cambio(s) en «{p.Def.Name}»: revisalos en En vivo → Portafolios.");
            }

            return null;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            a.LastError = ex.Message;
            Save(p.Def);
            return ex.Message;
        }
        finally
        {
            _running.Remove(portfolioId);
            _activity.Remove(portfolioId);
            cts.Dispose();
            Touch();
        }
    }

    private static void Save(LivePortfolioDef def)
    {
        var defs = LiveStore.LoadPortfolios();
        var stored = defs.FirstOrDefault(d => d.Id == def.Id);
        if (stored == null) defs.Add(def);
        else stored.Agent = def.Agent;
        LiveStore.SavePortfolios(defs);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string N(decimal v) => v.ToString("0.##", Inv);

    // ===================================================================== pedido

    internal const string SystemPrompt = """
        Sos el gestor de riesgo de un portafolio de estrategias algorítmicas que corren en vivo en Trading Hub (la
        plataforma de trading de un trader particular). No hay un humano en la conversación: decidí con criterio profesional y explicá.

        TU TRABAJO: repartir el riesgo entre las estrategias del portafolio de forma dinámica e inteligente, para que el
        portafolio gane lo más posible por unidad de riesgo sin pasarse del presupuesto. Herramientas que tenés: el
        TAMAÑO de cada estrategia (multiplicador de sus órdenes: 1 = como la programaron, 0 = no abre operaciones nuevas)
        y sus LÍMITES (pérdida máxima del día y drawdown máximo, en dinero; al tocarlos la estrategia frena o se detiene).

        Cómo pensás (como un risk manager de un fondo cuantitativo):
        - Muestra: con pocas operaciones (menos de ~20) los números son ruido. Cerca de 1× y cambios chicos. No persigas
          rachas cortas, ni para arriba ni para abajo.
        - Riesgo parejo: estrategias con resultado diario más volátil van con menos tamaño (pesos tipo inversa de la
          volatilidad), salvo que la evidencia de ventaja sea fuerte.
        - Correlación: si dos estrategias se mueven juntas (correlación del resultado diario > 0,6) son casi la misma
          apuesta: el tamaño conjunto tiene que bajar.
        - Degradación: drawdown actual cerca o por encima de su peor drawdown histórico, con profit factor reciente malo y
          muestra suficiente, es señal de que la ventaja se fue: bajá fuerte o poné 0 (pausar abrir) explicando por qué.
        - Presupuesto: la suma de las pérdidas máximas del día no debería pasar el riesgo diario del portafolio, y el
          drawdown del portafolio no debería acercarse al máximo permitido. Si se acerca, bajá el riesgo de todo.
        - Cambios graduales: no más de ±50 % del tamaño actual por revisión, salvo para frenar una que se degradó.
        - Contratos enteros: las órdenes van en contratos enteros. El tamaño multiplica la cantidad que programó la
          estrategia y te paso en qué contratos queda cada tamaño. En un futuro con micro (NQ→MNQ, ES→MES: 1 mini = 10
          micros) y «contrato inteligente» activo, una fracción del mini se ejecuta en micros con la misma exposición
          (×0,5 de 1 NQ = 5 MNQ), así que podés afinar de a décimas. Sin micro, todo lo que no llega a 1 contrato manda
          igual 1 (el mínimo): bajar de ahí no baja el riesgo; para reducir más, poné 0 y explicalo.
        - Respetá siempre los topes de tamaño que te pasan y las indicaciones del trader.
        - Nunca inventes números: usá solo los datos que te paso. Si no hace falta cambiar nada, no cambies nada.

        Respondé en español rioplatense, claro y concreto: 3 a 8 líneas de análisis y al final UN único bloque ```json:
        ```json
        {"summary":"resumen de 1 a 3 frases de cómo está el portafolio y qué hacés",
         "allocations":[{"key":"clave exacta de la estrategia","size":1.25,"max_daily_loss":250,"max_drawdown":1500,
                         "reason":"por qué, con los números que lo justifican"}]}
        ```
        Incluí en allocations solo las estrategias que cambian (tamaño o límites). max_daily_loss y max_drawdown son
        opcionales: ponelos solo si los cambiás (0 = sin límite). JSON válido, sin comentarios.
        [[ROL:live-risk]]
        """;

    private string BuildPrompt(LivePortfolioView p, List<LiveEntry> adjustable)
    {
        var a = p.Def.Agent;
        decimal capital = p.Def.Capital > 0 ? p.Def.Capital : 25_000m;
        var analysis = LiveCenter.Analyze(p);
        var sb = new StringBuilder();
        sb.AppendLine($"Portafolio «{p.Def.Name}» · capital de referencia {N(capital)} · fecha {DateTime.Now:yyyy-MM-dd HH:mm} (hora local).");
        sb.AppendLine($"Presupuesto: riesgo diario total {N(a.DailyRiskPercent)} % del capital ({N(capital * a.DailyRiskPercent / 100)}), drawdown máximo del portafolio {N(a.MaxDrawdownPercent)} % ({N(capital * a.MaxDrawdownPercent / 100)}).");
        sb.AppendLine($"Topes de tamaño por estrategia: entre {N(a.MinSize)} y {N(a.MaxSize)} (o 0 para que no abra operaciones nuevas).");
        if (!string.IsNullOrWhiteSpace(a.Instructions)) sb.AppendLine($"Indicaciones del trader: {a.Instructions.Trim()}");
        if (a.LastSummary.Length > 0 && a.LastRunUtc is { } last) sb.AppendLine($"Tu revisión anterior ({last.ToLocalTime():dd/MM HH:mm}): {a.LastSummary}");
        sb.AppendLine();
        var m = analysis.Metrics;
        sb.AppendLine($"PORTAFOLIO (operaciones cerradas): neto {N(m.NetProfit)} · {m.TotalTrades} operaciones · PF {N(Math.Min(m.ProfitFactor, 99))} · drawdown máx {N(m.MaxDrawdown)} · " +
                      $"Sharpe {m.SharpeRatio.ToString("0.00", Inv)} · abierto ahora {N(p.Members.Sum(x => x.OpenPnL))}");
        sb.AppendLine();
        sb.AppendLine("ESTRATEGIAS:");
        for (int i = 0; i < p.Members.Count; i++)
        {
            var e = p.Members[i];
            var stats = LiveStore.StatsOf(e.Info, e.Ledger, capital);
            var sm = stats.Metrics;
            bool canChange = adjustable.Contains(e);
            var r = e.Runner;
            decimal size = r?.SizeMultiplier ?? e.Info.Size;
            decimal dl = r?.MaxDailyLoss ?? e.Info.MaxDailyLoss, dd = r?.MaxDrawdown ?? e.Info.MaxDrawdown;
            double days = e.Ledger.Fills.Count > 0 ? (DateTime.UtcNow - e.Ledger.Fills[0].Time).TotalDays : 0;
            sb.AppendLine($"- key: {e.Key}");
            sb.AppendLine($"  {e.Info.Strategy} · {e.Info.Symbol} {e.Info.Period} · cuenta {e.Info.Account}{(e.Info.IsReal ? " (REAL)" : " (simulada)")} · {e.StateLabel}" +
                          $"{(canChange ? "" : $" · NO la podés cambiar ({(e.Locked ? "está bloqueada" : "está detenida")})")}");
            sb.AppendLine($"  tamaño ×{N(size)} · pérdida máx. del día {(dl > 0 ? N(dl) : "sin límite")} · drawdown máx. {(dd > 0 ? N(dd) : "sin límite")} · posición {N(e.Ledger.Position)} · abierto {N(e.OpenPnL)}");
            sb.AppendLine("  " + ContractLine(e, size));
            sb.AppendLine($"  {sm.TotalTrades} operaciones en {days:0.#} días · neto {N(stats.Net)} · PF {N(Math.Min(sm.ProfitFactor, 99))} · aciertos {sm.WinRate:0} % · " +
                          $"media/op {N(sm.TotalTrades > 0 ? e.Ledger.Trades.Average(t => t.NetPnl) : 0)} · ganancia media {N(sm.AverageWin)} · pérdida media {N(sm.AverageLoss)}");
            sb.AppendLine($"  drawdown actual {N(stats.CurrentDrawdown)} · peor drawdown {N(sm.MaxDrawdown)} · últimas {stats.RecentTrades}: PF {N(Math.Min(stats.RecentProfitFactor, 99))}, media {N(stats.RecentExpectancy)}" +
                          $"{(r != null ? $" · resultado de hoy {N(r.TodayPnL)}" : "")}");
            if (i < analysis.DailyPnl.Count)
            {
                var daily = analysis.DailyPnl[i];
                var vals = daily.Where(v => v != 0).ToList();
                double vol = vals.Count > 1 ? Math.Sqrt(vals.Sum(v => Math.Pow((double)v - vals.Average(x => (double)x), 2)) / (vals.Count - 1)) : 0;
                sb.AppendLine($"  resultado diario (últimos {Math.Min(20, daily.Length)} días): {string.Join(" ", daily.TakeLast(20).Select(N))} · volatilidad diaria {vol.ToString("0.##", Inv)}");
            }
        }

        if (p.Members.Count > 1 && analysis.Correlation.GetLength(0) == p.Members.Count)
        {
            sb.AppendLine();
            sb.AppendLine("CORRELACIÓN del resultado diario (Pearson):");
            for (int i = 0; i < p.Members.Count; i++)
                for (int j = i + 1; j < p.Members.Count; j++)
                    sb.AppendLine($"  {p.Members[i].Info.Strategy}·{p.Members[i].Info.Symbol} ↔ {p.Members[j].Info.Strategy}·{p.Members[j].Info.Symbol}: {analysis.Correlation[i, j].ToString("0.00", Inv)}");
        }

        sb.AppendLine();
        sb.AppendLine("Decidí el reparto de riesgo y devolvé el bloque JSON (solo con las que cambian).");
        return sb.ToString();
    }

    /// <summary>En qué contratos quedan las órdenes con el tamaño actual y con los de al lado (para que razone en contratos enteros).</summary>
    private string ContractLine(LiveEntry e, decimal size)
    {
        decimal qty = LiveCenter.QuantityOf(e.Info.Parameters);
        string Of(decimal s)
        {
            if (s <= 0) return "0 (no abre)";
            var (symbol, ratio) = _center.PreferredContract(e.Info.FeedId, e.Info.Symbol, qty, s);
            decimal units = Math.Floor(qty * s * ratio + 0.0000001m);
            return $"×{N(s)} = {N(Math.Max(1, units))} {symbol}{(units < 1 ? " (mínimo)" : "")}";
        }

        string micro = MarketInfo.IsFutures(e.Info.FeedId) && MarketInfo.MicroOf(e.Info.Symbol) is { } m
            ? _svc.Settings.SmartContracts ? $"tiene micro ({m}): las fracciones del mini van en micros" : $"tiene micro ({m}) pero el contrato inteligente está apagado"
            : "sin micro: menos de 1 contrato manda 1";
        var steps = new[] { size * 0.5m, size, size * 1.5m }.Select(v => Math.Round(v, 2)).Distinct();
        return $"contratos: la estrategia pide {N(qty)} {e.Info.Symbol} por orden · {micro} · {string.Join(" · ", steps.Select(Of))}";
    }

    /// <summary>Lee lo que devolvió el agente y lo deja como propuesta (dentro de los topes; solo las que puede tocar).</summary>
    internal static SizingProposal Parse(JsonElement json, LivePortfolioView p, List<LiveEntry> adjustable)
    {
        var a = p.Def.Agent;
        var proposal = new SizingProposal
        {
            Source = "agente",
            PortfolioId = p.Def.Id,
            PortfolioName = p.Def.Name,
            Summary = json.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? Short(s.GetString() ?? "", 600) : "",
        };
        if (!json.TryGetProperty("allocations", out var allocs) || allocs.ValueKind != JsonValueKind.Array) return proposal;
        foreach (var el in allocs.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            string key = el.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
            var e = adjustable.FirstOrDefault(x => x.Key == key);
            if (e == null) continue;
            decimal current = e.Runner?.SizeMultiplier ?? e.Info.Size;
            decimal curDl = e.Runner?.MaxDailyLoss ?? e.Info.MaxDailyLoss, curDd = e.Runner?.MaxDrawdown ?? e.Info.MaxDrawdown;
            decimal size = Num(el, "size") ?? current;
            size = size <= 0 ? 0 : Math.Clamp(Math.Round(size, 2), a.MinSize, a.MaxSize);
            decimal? dl = Num(el, "max_daily_loss") is { } d1 ? Math.Max(0, Math.Round(d1, 2)) : null;
            decimal? dd = Num(el, "max_drawdown") is { } d2 ? Math.Max(0, Math.Round(d2, 2)) : null;
            if (dl == curDl) dl = null;
            if (dd == curDd) dd = null;
            if (size == current && dl == null && dd == null) continue;
            proposal.Items.Add(new SizingItem
            {
                Key = key,
                Strategy = $"{e.Info.Strategy} · {e.Info.Symbol}",
                CurrentSize = current,
                ProposedSize = size,
                CurrentMaxDailyLoss = curDl,
                CurrentMaxDrawdown = curDd,
                ProposedMaxDailyLoss = dl,
                ProposedMaxDrawdown = dd,
                Reason = el.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? Short(r.GetString() ?? "", 500) : "",
            });
        }

        return proposal;
    }

    private static decimal? Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, Inv, out var ds)) return ds;
        return null;
    }
}
