using System.Collections.Concurrent;
using System.Globalization;
using TradingHub.Data;

namespace TradingHub.Strategies;

/// <summary>Cómo se buscan los parámetros en cada ventana del walk-forward.</summary>
public enum SearchMode
{
    /// <summary>Grilla completa si es chica (el mapa entero, para ver la meseta); bayesiana si es grande.</summary>
    Auto,

    /// <summary>Todas las combinaciones.</summary>
    Grid,

    /// <summary>Optimización bayesiana (TPE): prueba primero donde es más probable que haya algo bueno.</summary>
    Bayesian,
}

/// <summary>Un camino fuera de muestra de la validación cruzada combinatoria purgada (CPCV).</summary>
public sealed class CpcvPath
{
    public int Number { get; set; }
    public decimal Net { get; set; }
    public double Sharpe { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int Days { get; set; }
}

/// <summary>Los valores posibles de cada parámetro que se optimiza (por índice) y cómo se arma cada combinación.</summary>
public sealed class ParameterSpace
{
    private readonly List<(string Name, Type Type, double[] Values)> _dims = new();
    private readonly Dictionary<int, string[]> _choices = new();

    public ParameterSpace(IReadOnlyList<ParameterRange> ranges, Type strategyType)
    {
        var props = Strategy.Parameters(strategyType).ToDictionary(p => p.Name);
        foreach (var r in ranges)
        {
            if (r.IsPeriod)
            {
                // el período de las velas: una dimensión más, cuyos valores son los lugares en la lista (de corto a largo)
                if (!r.Enabled || r.Choices.Count == 0) continue;
                _choices[_dims.Count] = r.Choices.ToArray();
                _dims.Add((r.Name, typeof(string), Enumerable.Range(0, r.Choices.Count).Select(i => (double)i).ToArray()));
            }
            else if (props.TryGetValue(r.Name, out var prop))
                _dims.Add((r.Name, prop.PropertyType, r.Values().ToArray()));
        }
    }

    /// <summary>Los períodos de las velas que se prueban (vacío si el período no se optimiza).</summary>
    public IReadOnlyList<string> Periods => _choices.Values.FirstOrDefault() ?? Array.Empty<string>();

    /// <summary>Para pruebas: dimensiones con estos tamaños (valores 0, 1, 2…).</summary>
    public ParameterSpace(params int[] sizes)
    {
        for (int k = 0; k < sizes.Length; k++) _dims.Add(($"p{k}", typeof(int), Enumerable.Range(0, sizes[k]).Select(v => (double)v).ToArray()));
    }

    public int Dimensions => _dims.Count;
    public int Length(int dim) => _dims[dim].Values.Length;
    public long Size => _dims.Aggregate(1L, (a, d) => a * Math.Max(1, d.Values.Length));

    public Dictionary<string, object?> Config(int[] idx)
    {
        var d = new Dictionary<string, object?>();
        for (int k = 0; k < _dims.Count; k++)
            d[_dims[k].Name] = _choices.TryGetValue(k, out var choices) ? choices[idx[k]] : Convert.ChangeType(_dims[k].Values[idx[k]], _dims[k].Type, CultureInfo.InvariantCulture);
        return d;
    }

    public static string Key(int[] idx) => string.Join(",", idx);

    /// <summary>Todas las combinaciones (la grilla).</summary>
    public IEnumerable<int[]> All()
    {
        var idx = new int[_dims.Count];
        while (true)
        {
            yield return (int[])idx.Clone();
            int j = 0;
            while (j < idx.Length)
            {
                if (++idx[j] < _dims[j].Values.Length) break;
                idx[j] = 0;
                j++;
            }

            if (j == idx.Length) yield break;
        }
    }

    /// <summary>Cuántos pasos de grilla las separan en el parámetro que más difiere (1 = vecinas).</summary>
    public static int Distance(int[] a, int[] b)
    {
        int d = 0;
        for (int k = 0; k < a.Length; k++) d = Math.Max(d, Math.Abs(a[k] - b[k]));
        return d;
    }
}

/// <summary>Una combinación corrida una sola vez sobre toda la historia.</summary>
internal sealed class ConfigRun
{
    public required int[] Index { get; init; }
    public required Dictionary<string, object?> Parameters { get; init; }

    /// <summary>Operaciones ordenadas por hora de entrada.</summary>
    public ClosedTrade[] Trades { get; init; } = Array.Empty<ClosedTrade>();

    /// <summary>Capital por hora: la hora del último punto, el último valor y el mínimo de esa hora.</summary>
    public long[] HourTimes { get; init; } = Array.Empty<long>();
    public float[] HourLast { get; init; } = Array.Empty<float>();
    public float[] HourMin { get; init; } = Array.Empty<float>();
    public string? Error { get; init; }
    public bool Ok => Error == null;

    /// <summary>Lo medido en cada ventana (desde, hasta).</summary>
    public ConcurrentDictionary<(long, long), PerformanceMetrics> Windows { get; } = new();
}

/// <summary>
/// Cada combinación se corre una sola vez sobre toda la historia, con los indicadores calientes y el contexto de mercado
/// como en vivo, y las ventanas se miden recortando ese resultado: el walk-forward y la validación cruzada no repiten
/// backtests. Una ventana solo usa lo que pasó dentro de ella (el motor nunca mira hacia adelante).
/// </summary>
internal sealed class ConfigCache
{
    private readonly ConcurrentDictionary<string, Lazy<ConfigRun>> _runs = new();
    private readonly Func<Strategy> _factory;
    private readonly Dictionary<string, object?> _base;
    private readonly TickData _data;
    private readonly BacktestSettings _settings;
    private readonly IReadOnlyList<(DateTime Time, decimal Close)>? _context;
    private readonly ParameterSpace _space;
    private readonly CancellationToken _ct;

    public ConfigCache(Func<Strategy> factory, Dictionary<string, object?> baseParameters, TickData data, BacktestSettings settings,
        IReadOnlyList<(DateTime Time, decimal Close)>? context, ParameterSpace space, CancellationToken ct)
    {
        _factory = factory;
        _base = baseParameters;
        _data = data;
        _settings = settings;
        _context = context;
        _space = space;
        _ct = ct;
        Crypto = data.Instrument.FeedId.StartsWith("binance", StringComparison.Ordinal) || data.Instrument.FeedId.StartsWith("bybit", StringComparison.Ordinal);
    }

    public bool Crypto { get; }
    public decimal Capital => _settings.InitialCapital;

    /// <summary>Combinaciones distintas que se corrieron.</summary>
    public int Count => _runs.Count;

    public IEnumerable<ConfigRun> Done => _runs.Values.Where(l => l.IsValueCreated).Select(l => l.Value);

    public ConfigRun Get(int[] idx) =>
        _runs.GetOrAdd(ParameterSpace.Key(idx), _ => new Lazy<ConfigRun>(() => Run(idx), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public bool Has(int[] idx) => _runs.TryGetValue(ParameterSpace.Key(idx), out var l) && l.IsValueCreated;

    /// <summary>Corre en paralelo las que falten.</summary>
    public void Ensure(IReadOnlyList<int[]> list, int maxParallel, Action? done)
    {
        var options = new ParallelOptions { CancellationToken = _ct, MaxDegreeOfParallelism = maxParallel };
        Parallel.ForEach(list, options, idx =>
        {
            bool had = Has(idx);
            Get(idx);
            if (!had) done?.Invoke();
        });
    }

    private ConfigRun Run(int[] idx)
    {
        var pars = _space.Config(idx);
        var s = _factory();
        s.SetParameters(_base);
        s.SetParameters(pars);
        var settings = Optimizer.SettingsFor(_settings, pars).Clone();
        settings.CloseAtEnd = true;
        var r = new BacktestEngine(s, _data, settings) { ContextHistory = _context }.Run(_ct);
        if (!r.Ok) return new ConfigRun { Index = idx, Parameters = pars, Error = r.Error };

        // capital por hora (último y mínimo): alcanza para el drawdown y los retornos diarios de cualquier ventana
        var times = new List<long>();
        var last = new List<float>();
        var min = new List<float>();
        long hour = long.MinValue;
        foreach (var e in r.Equity)
        {
            long h = e.Time.Ticks / TimeSpan.TicksPerHour;
            float v = (float)e.Equity;
            if (h != hour)
            {
                hour = h;
                times.Add(e.Time.Ticks);
                last.Add(v);
                min.Add(v);
                continue;
            }

            times[^1] = e.Time.Ticks;
            last[^1] = v;
            if (v < min[^1]) min[^1] = v;
        }

        return new ConfigRun
        {
            Index = idx,
            Parameters = pars,
            Trades = r.Trades.OrderBy(t => t.EntryTime).ToArray(),
            HourTimes = times.ToArray(),
            HourLast = last.ToArray(),
            HourMin = min.ToArray(),
        };
    }

    /// <summary>
    /// Cómo le fue a una combinación en [desde, hasta): las operaciones que entraron en ese rango y el capital de esas
    /// horas (llevado al capital inicial al empezar el rango).
    /// </summary>
    public PerformanceMetrics Measure(ConfigRun run, DateTime from, DateTime to) =>
        run.Windows.GetOrAdd((from.Ticks, to.Ticks), _ =>
        {
            var (trades, equity) = Slice(run, from, to, Capital);
            return PerformanceMetrics.Compute(trades, equity, Capital, Crypto, 0, 0);
        });

    /// <summary>Las operaciones y el capital de un rango, empezando en <paramref name="capital"/>.</summary>
    public (List<ClosedTrade> Trades, List<EquityPoint> Equity) Slice(ConfigRun run, DateTime from, DateTime to, decimal capital)
    {
        var trades = new List<ClosedTrade>();
        int i = LowerEntry(run.Trades, from);
        for (; i < run.Trades.Length && run.Trades[i].EntryTime < to; i++) trades.Add(run.Trades[i]);
        int a = Lower(run.HourTimes, from.Ticks), b = Lower(run.HourTimes, to.Ticks);
        double start = a > 0 ? run.HourLast[a - 1] : (double)_settings.InitialCapital;
        var equity = new List<EquityPoint>(Math.Max(0, (b - a) * 2));
        for (int k = a; k < b; k++)
        {
            var t = new DateTime(run.HourTimes[k], DateTimeKind.Utc);
            equity.Add(new EquityPoint(t, capital + (decimal)(run.HourMin[k] - start), 0));
            equity.Add(new EquityPoint(t, capital + (decimal)(run.HourLast[k] - start), 0));
        }

        return (trades, equity);
    }

    /// <summary>Resultado de cada día de <paramref name="days"/> (cambio del capital de punta a punta del día).</summary>
    public static double[] Daily(ConfigRun run, IReadOnlyList<DateOnly> days, double capital)
    {
        var pnl = new double[days.Count];
        double prev = capital;
        int h = 0;
        for (int d = 0; d < days.Count; d++)
        {
            long end = days[d].AddDays(1).ToDateTime(TimeOnly.MinValue).Ticks;
            double last = prev;
            while (h < run.HourTimes.Length && run.HourTimes[h] < end)
            {
                last = run.HourLast[h];
                h++;
            }

            pnl[d] = last - prev;
            prev = last;
        }

        return pnl;
    }

    /// <summary>Operaciones que entraron cada día.</summary>
    public static int[] TradesPerDay(ConfigRun run, IReadOnlyList<DateOnly> days)
    {
        var map = new Dictionary<DateOnly, int>();
        for (int i = 0; i < days.Count; i++) map[days[i]] = i;
        var n = new int[days.Count];
        foreach (var t in run.Trades)
            if (map.TryGetValue(DateOnly.FromDateTime(t.EntryTime), out int i))
                n[i]++;
        return n;
    }

    private static int LowerEntry(ClosedTrade[] trades, DateTime t)
    {
        int lo = 0, hi = trades.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (trades[mid].EntryTime < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static int Lower(long[] a, long t)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (a[mid] < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}

/// <summary>
/// Optimización bayesiana con TPE (Tree-structured Parzen Estimator, Bergstra y otros, 2011) sobre una grilla discreta:
/// arranca con puntos bien repartidos (hipercubo latino) y después propone donde la densidad de las combinaciones buenas
/// supera a la de las malas. Evalúa de a tandas para usar todos los núcleos.
/// </summary>
public static class TpeSearch
{
    /// <param name="evaluate">Devuelve el puntaje de cada combinación (−∞ si no sirve, por ejemplo por falta de operaciones).</param>
    public static List<(int[] Idx, double Score)> Run(ParameterSpace space, Func<IReadOnlyList<int[]>, double[]> evaluate, int budget, int seed, int batch,
        CancellationToken ct = default)
    {
        var rnd = new Random(seed);
        var seen = new HashSet<string>();
        var done = new List<(int[] Idx, double Score)>();
        budget = (int)Math.Min(budget, space.Size);
        batch = Math.Max(1, batch);

        void Take(List<int[]> points)
        {
            if (points.Count == 0) return;
            var scores = evaluate(points);
            for (int i = 0; i < points.Count; i++) done.Add((points[i], scores[i]));
        }

        int startup = (int)Math.Min(budget, Math.Clamp(budget / 4, Math.Min(8, budget), 30));
        Take(LatinHypercube(space, startup, rnd, seen));
        while (done.Count < budget && seen.Count < space.Size)
        {
            ct.ThrowIfCancellationRequested();
            var next = Propose(space, done, Math.Min(batch, budget - done.Count), rnd, seen);
            if (next.Count == 0) break;
            Take(next);
        }

        return done;
    }

    private static List<int[]> LatinHypercube(ParameterSpace space, int n, Random rnd, HashSet<string> seen)
    {
        var points = new List<int[]>();
        var perms = Enumerable.Range(0, space.Dimensions).Select(_ => Enumerable.Range(0, n).OrderBy(_ => rnd.Next()).ToArray()).ToArray();
        for (int i = 0; i < n; i++)
        {
            var idx = new int[space.Dimensions];
            for (int k = 0; k < space.Dimensions; k++)
                idx[k] = Math.Min(space.Length(k) - 1, (int)((perms[k][i] + rnd.NextDouble()) / n * space.Length(k)));
            if (seen.Add(ParameterSpace.Key(idx))) points.Add(idx);
        }

        // si el hipercubo repitió puntos (dimensiones chicas), se completa al azar
        for (int tries = 0; points.Count < n && tries < n * 20; tries++)
        {
            var idx = RandomPoint(space, rnd);
            if (seen.Add(ParameterSpace.Key(idx))) points.Add(idx);
        }

        return points;
    }

    private static int[] RandomPoint(ParameterSpace space, Random rnd)
    {
        var idx = new int[space.Dimensions];
        for (int k = 0; k < idx.Length; k++) idx[k] = rnd.Next(space.Length(k));
        return idx;
    }

    private static List<int[]> Propose(ParameterSpace space, List<(int[] Idx, double Score)> done, int want, Random rnd, HashSet<string> seen)
    {
        var finite = done.Where(d => double.IsFinite(d.Score)).OrderByDescending(d => d.Score).ToList();
        var result = new List<int[]>();
        if (finite.Count < 2)
        {
            // todavía no hay nada bueno que imitar: al azar
            for (int tries = 0; result.Count < want && tries < want * 50; tries++)
            {
                var idx = RandomPoint(space, rnd);
                if (seen.Add(ParameterSpace.Key(idx))) result.Add(idx);
            }

            return result;
        }

        int nGood = Math.Clamp((int)Math.Ceiling(0.2 * finite.Count), 1, 25);
        var good = finite.Take(nGood).Select(d => d.Idx).ToList();
        var bad = done.Except(finite.Take(nGood)).Select(d => d.Idx).ToList();
        int dims = space.Dimensions;
        var pg = new double[dims][];
        var pb = new double[dims][];
        for (int k = 0; k < dims; k++)
        {
            int len = space.Length(k);
            double h = Math.Max(0.75, len / 8.0);
            pg[k] = Parzen(good.Select(x => x[k]), len, h);
            pb[k] = Parzen(bad.Select(x => x[k]), len, h);
        }

        // muchas candidatas sacadas de la densidad de las buenas; se quedan las de mejor cociente buenas/malas
        var cands = new Dictionary<string, (int[] Idx, double Score)>();
        for (int c = 0; c < Math.Max(48, want * 24); c++)
        {
            var idx = new int[dims];
            double score = 0;
            for (int k = 0; k < dims; k++)
            {
                idx[k] = Sample(pg[k], rnd);
                score += Math.Log(pg[k][idx[k]] / pb[k][idx[k]]);
            }

            string key = ParameterSpace.Key(idx);
            if (!seen.Contains(key)) cands[key] = (idx, score);
        }

        foreach (var c in cands.Values.OrderByDescending(c => c.Score).Take(want))
            if (seen.Add(ParameterSpace.Key(c.Idx)))
                result.Add(c.Idx);
        for (int tries = 0; result.Count < want && tries < want * 50; tries++)
        {
            var idx = RandomPoint(space, rnd);
            if (seen.Add(ParameterSpace.Key(idx))) result.Add(idx);
        }

        return result;
    }

    /// <summary>Densidad de Parzen sobre los índices de un parámetro (con un piso para no descartar nada del todo).</summary>
    private static double[] Parzen(IEnumerable<int> points, int len, double h)
    {
        var p = new double[len];
        for (int v = 0; v < len; v++) p[v] = 1.0 / len;
        int n = 0;
        foreach (var x in points)
        {
            n++;
            for (int v = 0; v < len; v++) p[v] += Math.Exp(-0.5 * Math.Pow((v - x) / h, 2));
        }

        double sum = p.Sum();
        for (int v = 0; v < len; v++) p[v] /= sum;
        return p;
    }

    private static int Sample(double[] p, Random rnd)
    {
        double u = rnd.NextDouble(), acc = 0;
        for (int v = 0; v < p.Length; v++)
        {
            acc += p[v];
            if (u <= acc) return v;
        }

        return p.Length - 1;
    }
}

/// <summary>
/// La mejor meseta, no el mejor pico: el puntaje de cada combinación es el promedio con sus vecinas (un paso de grilla en
/// cada parámetro), achicado hacia la mediana cuando tiene pocas vecinas medidas. Una combinación que gana sola entre
/// vecinas que pierden es casi siempre suerte; una zona entera que gana, no.
/// </summary>
public static class RobustSelection
{
    public static int Select(IReadOnlyList<(int[] Idx, double Score)> rows)
    {
        var finite = rows.Where(r => double.IsFinite(r.Score)).Select(r => r.Score).OrderBy(s => s).ToList();
        if (finite.Count == 0) return -1;
        double median = finite[finite.Count / 2];
        double worst = finite[0] - (finite[^1] - finite[0]) * 0.1 - Math.Abs(finite[0]) * 0.1;
        int best = -1;
        double bestScore = double.NegativeInfinity, bestOwn = double.NegativeInfinity;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!double.IsFinite(rows[i].Score)) continue;
            double sum = 0;
            int n = 0;
            for (int j = 0; j < rows.Count; j++)
            {
                if (ParameterSpace.Distance(rows[i].Idx, rows[j].Idx) > 1) continue;
                sum += double.IsFinite(rows[j].Score) ? rows[j].Score : worst;
                n++;
            }

            double score = (sum + median) / (n + 1);
            if (score > bestScore || (score == bestScore && rows[i].Score > bestOwn))
            {
                best = i;
                bestScore = score;
                bestOwn = rows[i].Score;
            }
        }

        return best;
    }
}

/// <summary>Medidas de sobreoptimización sobre el resultado diario de todas las combinaciones probadas.</summary>
public static class Overfitting
{
    /// <summary>
    /// Probabilidad de sobreoptimización con validación cruzada combinatoriamente simétrica (Bailey, Borwein, López de
    /// Prado y Zhu, «The Probability of Backtest Overfitting», 2017): parte la historia en bloques y, para cada forma de
    /// elegir la mitad como «dentro de muestra», toma la combinación con mejor Sharpe ahí y mira dónde queda en la otra
    /// mitad. PBO = proporción de veces que queda por debajo de la mediana; Loss = proporción de veces que pierde plata.
    /// </summary>
    public static (double Pbo, double Loss, int Splits)? Pbo(IReadOnlyList<double[]> daily, int blocks = 10)
    {
        int n = daily.Count;
        if (n < 4) return null;
        int days = daily[0].Length;
        while (blocks >= 4 && days < blocks * 5) blocks -= 2;
        if (blocks < 4) return null;
        var bounds = Enumerable.Range(0, blocks + 1).Select(b => (int)((long)b * days / blocks)).ToArray();
        var stats = new (double Sum, double Sq, int N)[n, blocks];
        for (int c = 0; c < n; c++)
            for (int b = 0; b < blocks; b++)
                for (int d = bounds[b]; d < bounds[b + 1]; d++)
                {
                    double v = daily[c][d];
                    stats[c, b] = (stats[c, b].Sum + v, stats[c, b].Sq + v * v, stats[c, b].N + 1);
                }

        int half = blocks / 2, splits = 0, below = 0, losses = 0;
        var isPerf = new double[n];
        var osPerf = new double[n];
        var osNet = new double[n];
        foreach (var subset in Combinations(blocks, half))
        {
            var inSet = new bool[blocks];
            foreach (var b in subset) inSet[b] = true;
            for (int c = 0; c < n; c++)
            {
                double si = 0, qi = 0, so = 0, qo = 0;
                int ni = 0, no = 0;
                for (int b = 0; b < blocks; b++)
                {
                    var (s, q, k) = stats[c, b];
                    if (inSet[b])
                    {
                        si += s;
                        qi += q;
                        ni += k;
                    }
                    else
                    {
                        so += s;
                        qo += q;
                        no += k;
                    }
                }

                isPerf[c] = Sharpe(si, qi, ni);
                osPerf[c] = Sharpe(so, qo, no);
                osNet[c] = so;
            }

            int star = 0;
            for (int c = 1; c < n; c++)
                if (isPerf[c] > isPerf[star])
                    star = c;
            // rango relativo fuera de muestra (1 = la peor): ω = rango / (n + 1); λ = log(ω / (1 − ω)) ≤ 0 → mitad de abajo
            int rank = 1;
            for (int c = 0; c < n; c++)
                if (c != star && osPerf[c] < osPerf[star])
                    rank++;
            double w = rank / (n + 1.0);
            if (Math.Log(w / (1 - w)) <= 0) below++;
            if (osNet[star] <= 0) losses++;
            splits++;
        }

        return splits == 0 ? null : ((double)below / splits, (double)losses / splits, splits);
    }

    /// <summary>
    /// Validación cruzada combinatoria purgada (López de Prado, «Advances in Financial Machine Learning», 2018): parte la
    /// historia en grupos seguidos; para cada par de grupos de prueba elige la combinación (por meseta) con los demás días
    /// —sin los pegados a la prueba (purga y embargo)— y mide cómo le fue en esos dos grupos. Con eso se arman varios
    /// caminos fuera de muestra completos en vez de uno solo.
    /// </summary>
    /// <param name="index">Índice de grilla de cada combinación (para la meseta).</param>
    /// <param name="daily">Resultado diario de cada combinación.</param>
    /// <param name="tradesPerDay">Operaciones que entraron cada día, por combinación.</param>
    public static List<CpcvPath> Cpcv(IReadOnlyList<int[]> index, IReadOnlyList<double[]> daily, IReadOnlyList<int[]> tradesPerDay, int minTrades,
        double capital, bool crypto, int groups = 8, int embargo = 1)
    {
        var paths = new List<CpcvPath>();
        int n = daily.Count;
        if (n == 0) return paths;
        int days = daily[0].Length;
        while (groups >= 4 && days < groups * 5) groups--;
        if (groups < 4) return paths;
        var bounds = Enumerable.Range(0, groups + 1).Select(g => (int)((long)g * days / groups)).ToArray();
        var pieces = Enumerable.Range(0, groups).Select(_ => new List<double[]>()).ToArray();
        var rows = new (int[] Idx, double Score)[n];
        foreach (var pair in Combinations(groups, 2))
        {
            int a = pair[0], b = pair[1];
            // días de entrenamiento: los que no son de prueba ni están a menos de «embargo» días de un grupo de prueba
            var train = new bool[days];
            for (int d = 0; d < days; d++) train[d] = true;
            foreach (var g in new[] { a, b })
                for (int d = Math.Max(0, bounds[g] - embargo); d < Math.Min(days, bounds[g + 1] + embargo); d++)
                    train[d] = false;
            for (int c = 0; c < n; c++)
            {
                double s = 0, q = 0;
                int k = 0, trades = 0;
                for (int d = 0; d < days; d++)
                {
                    if (!train[d]) continue;
                    double v = daily[c][d];
                    s += v;
                    q += v * v;
                    k++;
                    trades += tradesPerDay[c][d];
                }

                rows[c] = (index[c], trades >= minTrades ? Sharpe(s, q, k) : double.NegativeInfinity);
            }

            int pick = RobustSelection.Select(rows);
            if (pick < 0) pick = 0;
            foreach (var g in new[] { a, b }) pieces[g].Add(daily[pick][bounds[g]..bounds[g + 1]]);
        }

        // cada grupo fue de prueba en (grupos − 1) pares: el camino j toma la j-ésima vez de cada grupo
        int count = pieces.Min(p => p.Count);
        double ann = Math.Sqrt(crypto ? 365 : 252);
        for (int j = 0; j < count; j++)
        {
            var series = pieces.SelectMany(p => p[j]).ToArray();
            double eq = 0, peak = 0, dd = 0, sum = 0, sq = 0;
            foreach (var v in series)
            {
                eq += v;
                peak = Math.Max(peak, eq);
                dd = Math.Max(dd, peak - eq);
                sum += v;
                sq += v * v;
            }

            double mean = series.Length > 0 ? sum / series.Length : 0;
            double sd = series.Length > 1 ? Math.Sqrt(Math.Max(0, (sq - series.Length * mean * mean) / (series.Length - 1))) : 0;
            paths.Add(new CpcvPath
            {
                Number = j + 1,
                Net = (decimal)Math.Round(sum, 2),
                Sharpe = sd > 0 ? mean / sd * ann : 0,
                MaxDrawdown = (decimal)Math.Round(dd, 2),
                Days = series.Length,
            });
        }

        return paths;
    }

    /// <summary>Sharpe (sin anualizar) de una suma de días: media sobre desvío.</summary>
    private static double Sharpe(double sum, double sq, int n)
    {
        if (n < 2) return n == 1 ? sum : 0;
        double mean = sum / n;
        double var = Math.Max(0, (sq - n * mean * mean) / (n - 1));
        return var > 0 ? mean / Math.Sqrt(var) : mean > 0 ? 1e6 : mean < 0 ? -1e6 : 0;
    }

    /// <summary>Todas las formas de elegir k de n (en orden).</summary>
    public static IEnumerable<int[]> Combinations(int n, int k)
    {
        var c = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])c.Clone();
            int i = k - 1;
            while (i >= 0 && c[i] == n - k + i) i--;
            if (i < 0) yield break;
            c[i]++;
            for (int j = i + 1; j < k; j++) c[j] = c[j - 1] + 1;
        }
    }
}
