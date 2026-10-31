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

    public Strategy Create() => (Strategy)Activator.CreateInstance(Type ?? throw new InvalidOperationException(L.T("La estrategia no está compilada.")))!;
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
                var diag = new ScriptDiagnostic(file.Path, 1, 1, "error", "TH01", L.T("El archivo no tiene ninguna clase pública que herede de Strategy con un constructor sin parámetros."));
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
        if (className.Length == 0 || char.IsDigit(className[0])) className = L.T("Estrategia") + className;
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
        new(L.T("Cruce de medias"), L.T("Largo cuando la media rápida cruza arriba de la lenta y corto al revés. Ideal para empezar."), "CruceDeMedias", true, c => L.F("using TradingHub.Strategies;\n\n[Strategy(\"__NOMBRE__\", Description = \"Cruce de dos medias exponenciales con stop y objetivo en ticks.\")]\npublic class {0} : Strategy\n{{\n    [Parameter(\"Media rápida\"), Optimize(5, 30, 5)]\n    public int Rapida {{ get; set; }} = 10;\n\n    [Parameter(\"Media lenta\"), Optimize(20, 100, 10)]\n    public int Lenta {{ get; set; }} = 40;\n\n    private Ema _rapida = null!, _lenta = null!;\n\n    protected override void OnInit()\n    {{\n        _rapida = Ema(Rapida);\n        _lenta = Ema(Lenta);\n    }}\n\n    protected override void OnBar(Bar bar)\n    {{\n        if (!_lenta.IsReady) return;\n\n        if (CrossAbove(_rapida, _lenta)) EnterLong();\n        else if (CrossBelow(_rapida, _lenta)) EnterShort();\n    }}\n}}",c)),

        new(L.T("Ruptura de rango"), L.T("Compra la ruptura del máximo de N velas y vende la del mínimo, con stop por ATR."), "RupturaDeRango", true, c => L.F("using TradingHub.Strategies;\n\n[Strategy(\"__NOMBRE__\", Description = \"Ruptura de canal (Donchian) con salida por ATR.\")]\npublic class {0} : Strategy\n{{\n    [Parameter(\"Velas del canal\"), Optimize(10, 60, 10)]\n    public int Canal {{ get; set; }} = 20;\n\n    [Parameter(\"Múltiplo de ATR para el stop\"), Optimize(1, 4, 0.5)]\n    public double MultiploAtr {{ get; set; }} = 2;\n\n    private Highest _maximo = null!;\n    private Lowest _minimo = null!;\n    private Atr _atr = null!;\n    private decimal _stop;\n\n    protected override void OnInit()\n    {{\n        _maximo = Highest(Canal);\n        _minimo = Lowest(Canal);\n        _atr = Atr(14);\n    }}\n\n    protected override void OnBar(Bar bar)\n    {{\n        if (Bars.Count <= Canal || !_atr.IsReady) return;\n        decimal techo = _maximo[1], piso = _minimo[1];\n        decimal distancia = _atr.Value * (decimal)MultiploAtr;\n\n        if (IsFlat)\n        {{\n            if (bar.Close > techo) {{ EnterLong(); _stop = bar.Close - distancia; }}\n            else if (bar.Close < piso) {{ EnterShort(); _stop = bar.Close + distancia; }}\n        }}\n        else if (IsLong)\n        {{\n            _stop = Math.Max(_stop, bar.Close - distancia);\n            if (bar.Close < _stop) ClosePosition(\"Stop ATR\");\n        }}\n        else\n        {{\n            _stop = Math.Min(_stop, bar.Close + distancia);\n            if (bar.Close > _stop) ClosePosition(\"Stop ATR\");\n        }}\n    }}\n}}",c)),

        new(L.T("Absorción con delta"), L.T("Order flow: vela con delta muy negativo que igual cierra arriba (absorción de ventas) → compra, y al revés."), "AbsorcionConDelta", true, c => L.F("using TradingHub.Strategies;\n\n[Strategy(\"__NOMBRE__\", Description = \"Busca absorción: mucho volumen agresivo en contra que no logra mover el precio.\")]\npublic class {0} : Strategy\n{{\n    [Parameter(\"Velas para el promedio de volumen\")]\n    public int Promedio {{ get; set; }} = 20;\n\n    [Parameter(\"Volumen mínimo (x promedio)\"), Optimize(1.0, 3.0, 0.5)]\n    public double VolumenMinimo {{ get; set; }} = 1.5;\n\n    [Parameter(\"Delta mínimo (% del volumen)\"), Optimize(10, 40, 10)]\n    public int DeltaMinimo {{ get; set; }} = 20;\n\n    [Parameter(\"Velas máximas en la operación\")]\n    public int VelasMaximas {{ get; set; }} = 6;\n\n    private Sma _volumen = null!;\n    private int _barEntrada;\n\n    public {1}()\n    {{\n        StopLossTicks = 40;\n        TakeProfitTicks = 80;\n    }}\n\n    protected override void OnInit()\n    {{\n        UseFootprint = true; // volumen por precio en cada vela (bar.Levels, bar.PocPrice)\n        _volumen = Sma(Promedio, PriceSource.Volume);\n    }}\n\n    protected override void OnBar(Bar bar)\n    {{\n        if (!_volumen.IsReady) return;\n\n        if (!IsFlat && CurrentBar - _barEntrada >= VelasMaximas)\n        {{\n            ClosePosition(\"Tiempo\");\n            return;\n        }}\n\n        bool volumenAlto = bar.Volume >= _volumen[1] * (decimal)VolumenMinimo;\n        decimal deltaPct = bar.Volume > 0 ? 100 * bar.Delta / bar.Volume : 0;\n\n        // ventas agresivas fuertes pero la vela cierra arriba de su POC: alguien absorbió\n        if (IsFlat && volumenAlto && deltaPct <= -DeltaMinimo && bar.Close > bar.PocPrice && bar.IsBullish)\n        {{\n            EnterLong();\n            _barEntrada = CurrentBar;\n        }}\n        else if (IsFlat && volumenAlto && deltaPct >= DeltaMinimo && bar.Close < bar.PocPrice && bar.IsBearish)\n        {{\n            EnterShort();\n            _barEntrada = CurrentBar;\n        }}\n    }}\n}}",c,c)),

        new(L.T("Reversión a la VWAP"), L.T("Opera el regreso a la VWAP de la sesión cuando el precio se aleja demasiado, solo en un horario."), "ReversionVwap", true, c => L.F("using TradingHub.Strategies;\n\n[Strategy(\"__NOMBRE__\", Description = \"Vende extensiones por encima de la VWAP y compra por debajo; sale en la VWAP.\")]\npublic class {0} : Strategy\n{{\n    [Parameter(\"Distancia (x ATR)\"), Optimize(1.0, 3.0, 0.5)]\n    public double Distancia {{ get; set; }} = 2;\n\n    [Parameter(\"Desde (hora local)\")]\n    public int HoraDesde {{ get; set; }} = 10;\n\n    [Parameter(\"Hasta (hora local)\")]\n    public int HoraHasta {{ get; set; }} = 20;\n\n    private Vwap _vwap = null!;\n    private Atr _atr = null!;\n\n    public {1}()\n    {{\n        StopLossTicks = 60;\n    }}\n\n    protected override void OnInit()\n    {{\n        _vwap = Vwap();\n        _atr = Atr(20);\n    }}\n\n    protected override void OnBar(Bar bar)\n    {{\n        if (!_atr.IsReady) return;\n        bool horario = TimeBetween(TimeSpan.FromHours(HoraDesde), TimeSpan.FromHours(HoraHasta));\n        decimal banda = _atr.Value * (decimal)Distancia;\n\n        if (IsLong && bar.Close >= _vwap.Value) ClosePosition(\"VWAP\");\n        else if (IsShort && bar.Close <= _vwap.Value) ClosePosition(\"VWAP\");\n        else if (IsFlat && horario)\n        {{\n            if (bar.Close < _vwap.Value - banda) EnterLong();\n            else if (bar.Close > _vwap.Value + banda) EnterShort();\n        }}\n\n        // fuera de horario no se queda posicionado\n        if (!horario && !IsFlat) ClosePosition(\"Fin del horario\");\n    }}\n}}",c,c)),

        new(L.T("Vacía"), L.T("Esqueleto mínimo con los métodos disponibles."), "MiEstrategia", false, c => L.F("using TradingHub.Strategies;\n\n[Strategy(\"__NOMBRE__\", Description = \"\")]\npublic class {0} : Strategy\n{{\n    // [Parameter(\"Nombre\"), Optimize(mín, máx, paso)] para que aparezca en la optimización\n    [Parameter(\"Período\"), Optimize(10, 50, 10)]\n    public int Periodo {{ get; set; }} = 20;\n\n    private Sma _media = null!;\n\n    protected override void OnInit()\n    {{\n        // indicadores: Sma, Ema, Rsi, Atr, Vwap, Bollinger, Macd, Highest, Lowest, CumulativeDelta…\n        _media = Sma(Periodo);\n    }}\n\n    protected override void OnBar(Bar bar)\n    {{\n        // bar = vela recién cerrada (Open, High, Low, Close, Volume, BuyVolume, SellVolume, Delta…)\n        // órdenes: EnterLong(), EnterShort(), Buy(), Sell(), BuyLimit(precio), SellStop(precio), ClosePosition()\n        // stops automáticos: StopLossTicks, TakeProfitTicks, TrailingStopTicks\n    }}\n}}",c)),
    };
}
