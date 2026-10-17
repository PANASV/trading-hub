using TradingHub.Scripting;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>Una estrategia en disco (un archivo .cs) y su última compilación.</summary>
public sealed class StrategyFile
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public Type? Type { get; set; }
    public ScriptCompileResult? LastCompile { get; set; }
    public DateTime CompiledFileTime { get; set; }
    public string DisplayName => Type != null ? Strategy.DisplayName(Type) : FileName;

    /// <summary>Id estable para el historial (el nombre de la clase).</summary>
    public string Id => Type?.FullName ?? FileName;

    public Strategy Create() => (Strategy)Activator.CreateInstance(Type ?? throw new InvalidOperationException("La estrategia no está compilada."))!;
}

/// <summary>Estrategias del usuario: archivos en ~/Documents/Trading Hub/Estrategias, compilados con Roslyn.</summary>
public sealed class StrategyCatalog
{
    private readonly ScriptCompiler _compiler = new();

    public StrategyCatalog()
    {
        _ = typeof(Strategy); // asegura que TradingHub.Strategies esté cargado para referenciarlo al compilar
        // TRADINGHUB_BACKTESTS_DIR: historial alternativo (pruebas)
        StrategyStore.Root = Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR") is { Length: > 0 } dir ? dir : AppPaths.Backtests;
        Directory.CreateDirectory(AppPaths.Strategies);
        if (!Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Any())
        {
            foreach (var t in StrategyTemplates.All.Where(t => t.Example))
                File.WriteAllText(System.IO.Path.Combine(AppPaths.Strategies, t.FileName + ".cs"), t.Code(t.FileName).Replace("__NOMBRE__", t.Name));
        }

        Refresh();
    }

    public List<StrategyFile> Files { get; } = new();

    public event Action? Changed;

    public void Refresh()
    {
        var existing = Files.ToDictionary(f => f.Path);
        Files.Clear();
        var extra = Enumerable.Empty<string>();
        foreach (var path in Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Concat(extra).OrderBy(p => p))
            Files.Add(existing.TryGetValue(path, out var f) ? f : new StrategyFile { Path = path });
        Changed?.Invoke();
    }

    /// <summary>Compila un archivo (si cambió desde la última vez o si se fuerza).</summary>
    public ScriptCompileResult Compile(StrategyFile file, bool force = false)
    {
        var stamp = File.GetLastWriteTimeUtc(file.Path);
        if (!force && file.LastCompile != null && stamp == file.CompiledFileTime) return file.LastCompile;
        var res = _compiler.Compile(new[] { file.Path }, "Estrategia_" + file.FileName);
        file.LastCompile = res;
        file.CompiledFileTime = stamp;
        if (res.Success && res.Assembly != null)
        {
            var types = res.Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(Strategy).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null).ToList();
            file.Type = types.FirstOrDefault();
            if (file.Type == null)
            {
                var diag = new ScriptDiagnostic(file.Path, 1, 1, "error", "TH01", "El archivo no tiene ninguna clase pública que herede de Strategy con un constructor sin parámetros.");
                file.LastCompile = new ScriptCompileResult { Success = false, Diagnostics = res.Diagnostics.Append(diag).ToList(), Elapsed = res.Elapsed };
            }
        }
        else
        {
            file.Type = null;
        }

        Changed?.Invoke();
        return file.LastCompile;
    }

    /// <summary>Crea un archivo nuevo desde una plantilla y devuelve su ruta.</summary>
    public StrategyFile Create(string name, StrategyTemplate template)
    {
        string className = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (className.Length == 0 || char.IsDigit(className[0])) className = "Estrategia" + className;
        var path = System.IO.Path.Combine(AppPaths.Strategies, className + ".cs");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(AppPaths.Strategies, $"{className}{i}.cs");
        className = System.IO.Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(path, template.Code(className).Replace("__NOMBRE__", name.Replace("\"", "'")));
        Refresh();
        return Files.First(f => f.Path == path);
    }

    public void Delete(StrategyFile file)
    {
        if (File.Exists(file.Path)) File.Delete(file.Path);
        Refresh();
    }
}

/// <summary>Plantilla de estrategia.</summary>
public sealed record StrategyTemplate(string Name, string Description, string FileName, bool Example, Func<string, string> Code);

/// <summary>Plantillas incluidas (también se usan como ejemplos la primera vez).</summary>
public static class StrategyTemplates
{
    public static readonly StrategyTemplate[] All =
    {
        new("Cruce de medias", "Largo cuando la media rápida cruza arriba de la lenta y corto al revés. Ideal para empezar.", "CruceDeMedias", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Cruce de dos medias exponenciales con stop y objetivo en ticks.")]
            public class {{c}} : Strategy
            {
                [Parameter("Media rápida"), Optimize(5, 30, 5)]
                public int Rapida { get; set; } = 10;

                [Parameter("Media lenta"), Optimize(20, 100, 10)]
                public int Lenta { get; set; } = 40;

                private Ema _rapida = null!, _lenta = null!;

                protected override void OnInit()
                {
                    _rapida = Ema(Rapida);
                    _lenta = Ema(Lenta);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_lenta.IsReady) return;

                    if (CrossAbove(_rapida, _lenta)) EnterLong();
                    else if (CrossBelow(_rapida, _lenta)) EnterShort();
                }
            }
            """),

        new("Ruptura de rango", "Compra la ruptura del máximo de N velas y vende la del mínimo, con stop por ATR.", "RupturaDeRango", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Ruptura de canal (Donchian) con salida por ATR.")]
            public class {{c}} : Strategy
            {
                [Parameter("Velas del canal"), Optimize(10, 60, 10)]
                public int Canal { get; set; } = 20;

                [Parameter("Múltiplo de ATR para el stop"), Optimize(1, 4, 0.5)]
                public double MultiploAtr { get; set; } = 2;

                private Highest _maximo = null!;
                private Lowest _minimo = null!;
                private Atr _atr = null!;
                private decimal _stop;

                protected override void OnInit()
                {
                    _maximo = Highest(Canal);
                    _minimo = Lowest(Canal);
                    _atr = Atr(14);
                }

                protected override void OnBar(Bar bar)
                {
                    if (Bars.Count <= Canal || !_atr.IsReady) return;
                    decimal techo = _maximo[1], piso = _minimo[1];
                    decimal distancia = _atr.Value * (decimal)MultiploAtr;

                    if (IsFlat)
                    {
                        if (bar.Close > techo) { EnterLong(); _stop = bar.Close - distancia; }
                        else if (bar.Close < piso) { EnterShort(); _stop = bar.Close + distancia; }
                    }
                    else if (IsLong)
                    {
                        _stop = Math.Max(_stop, bar.Close - distancia);
                        if (bar.Close < _stop) ClosePosition("Stop ATR");
                    }
                    else
                    {
                        _stop = Math.Min(_stop, bar.Close + distancia);
                        if (bar.Close > _stop) ClosePosition("Stop ATR");
                    }
                }
            }
            """),

        new("Absorción con delta", "Order flow: vela con delta muy negativo que igual cierra arriba (absorción de ventas) → compra, y al revés.", "AbsorcionConDelta", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Busca absorción: mucho volumen agresivo en contra que no logra mover el precio.")]
            public class {{c}} : Strategy
            {
                [Parameter("Velas para el promedio de volumen")]
                public int Promedio { get; set; } = 20;

                [Parameter("Volumen mínimo (x promedio)"), Optimize(1.0, 3.0, 0.5)]
                public double VolumenMinimo { get; set; } = 1.5;

                [Parameter("Delta mínimo (% del volumen)"), Optimize(10, 40, 10)]
                public int DeltaMinimo { get; set; } = 20;

                [Parameter("Velas máximas en la operación")]
                public int VelasMaximas { get; set; } = 6;

                private Sma _volumen = null!;
                private int _barEntrada;

                public {{c}}()
                {
                    StopLossTicks = 40;
                    TakeProfitTicks = 80;
                }

                protected override void OnInit()
                {
                    UseFootprint = true; // volumen por precio en cada vela (bar.Levels, bar.PocPrice)
                    _volumen = Sma(Promedio, PriceSource.Volume);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_volumen.IsReady) return;

                    if (!IsFlat && CurrentBar - _barEntrada >= VelasMaximas)
                    {
                        ClosePosition("Tiempo");
                        return;
                    }

                    bool volumenAlto = bar.Volume >= _volumen[1] * (decimal)VolumenMinimo;
                    decimal deltaPct = bar.Volume > 0 ? 100 * bar.Delta / bar.Volume : 0;

                    // ventas agresivas fuertes pero la vela cierra arriba de su POC: alguien absorbió
                    if (IsFlat && volumenAlto && deltaPct <= -DeltaMinimo && bar.Close > bar.PocPrice && bar.IsBullish)
                    {
                        EnterLong();
                        _barEntrada = CurrentBar;
                    }
                    else if (IsFlat && volumenAlto && deltaPct >= DeltaMinimo && bar.Close < bar.PocPrice && bar.IsBearish)
                    {
                        EnterShort();
                        _barEntrada = CurrentBar;
                    }
                }
            }
            """),

        new("Reversión a la VWAP", "Opera el regreso a la VWAP de la sesión cuando el precio se aleja demasiado, solo en un horario.", "ReversionVwap", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Vende extensiones por encima de la VWAP y compra por debajo; sale en la VWAP.")]
            public class {{c}} : Strategy
            {
                [Parameter("Distancia (x ATR)"), Optimize(1.0, 3.0, 0.5)]
                public double Distancia { get; set; } = 2;

                [Parameter("Desde (hora local)")]
                public int HoraDesde { get; set; } = 10;

                [Parameter("Hasta (hora local)")]
                public int HoraHasta { get; set; } = 20;

                private Vwap _vwap = null!;
                private Atr _atr = null!;

                public {{c}}()
                {
                    StopLossTicks = 60;
                }

                protected override void OnInit()
                {
                    _vwap = Vwap();
                    _atr = Atr(20);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_atr.IsReady) return;
                    bool horario = TimeBetween(TimeSpan.FromHours(HoraDesde), TimeSpan.FromHours(HoraHasta));
                    decimal banda = _atr.Value * (decimal)Distancia;

                    if (IsLong && bar.Close >= _vwap.Value) ClosePosition("VWAP");
                    else if (IsShort && bar.Close <= _vwap.Value) ClosePosition("VWAP");
                    else if (IsFlat && horario)
                    {
                        if (bar.Close < _vwap.Value - banda) EnterLong();
                        else if (bar.Close > _vwap.Value + banda) EnterShort();
                    }

                    // fuera de horario no se queda posicionado
                    if (!horario && !IsFlat) ClosePosition("Fin del horario");
                }
            }
            """),

        new("Vacía", "Esqueleto mínimo con los métodos disponibles.", "MiEstrategia", false, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "")]
            public class {{c}} : Strategy
            {
                // [Parameter("Nombre"), Optimize(mín, máx, paso)] para que aparezca en la optimización
                [Parameter("Período"), Optimize(10, 50, 10)]
                public int Periodo { get; set; } = 20;

                private Sma _media = null!;

                protected override void OnInit()
                {
                    // indicadores: Sma, Ema, Rsi, Atr, Vwap, Bollinger, Macd, Highest, Lowest, CumulativeDelta…
                    _media = Sma(Periodo);
                }

                protected override void OnBar(Bar bar)
                {
                    // bar = vela recién cerrada (Open, High, Low, Close, Volume, BuyVolume, SellVolume, Delta…)
                    // órdenes: EnterLong(), EnterShort(), Buy(), Sell(), BuyLimit(precio), SellStop(precio), ClosePosition()
                    // stops automáticos: StopLossTicks, TakeProfitTicks, TrailingStopTicks
                }
            }
            """),
    };
}
