using System.Text.Json;
using System.Text.RegularExpressions;
using TradingHub.Common;

namespace TradingHub.Tests;

/// <summary>Idioma de la interfaz: diccionarios, formatos con valores y la cadena chino → inglés → español.</summary>
[Collection("Loc")]
public class LocalizationTests : IDisposable
{
    public void Dispose() => Loc.Init(Loc.Spanish); // las demás pruebas esperan textos en español

    private static Dictionary<string, string> Dictionary(string lang)
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream($"TradingHub.Localization.{lang}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
    }

    private static readonly Regex Hole = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})");

    [Theory]
    [InlineData("en")]
    [InlineData("zh")]
    public void Cada_traduccion_usa_solo_los_valores_del_original(string lang)
    {
        var bad = new List<string>();
        foreach (var (es, tr) in Dictionary(lang))
        {
            var src = Hole.Matches(es).Select(m => m.Value).ToHashSet();
            if (Hole.Matches(tr).Any(m => !src.Contains(m.Value))) bad.Add(es);
            if (src.Count == 0) continue;
            // y que se pueda formatear con tantos valores como el original
            var args = Enumerable.Range(0, 12).Select(i => (object)i).ToArray();
            try { _ = string.Format(tr, args); }
            catch (FormatException) { bad.Add(es); }
        }

        Assert.Empty(bad);
    }

    [Fact]
    public void Traduce_textos_y_formatos_y_si_falta_queda_en_espanol()
    {
        Loc.Init(Loc.English);
        Assert.Equal("Settings", Loc.T("Ajustes"));
        Assert.Equal("Buy MKT", Loc.T("Comprar MKT"));
        Assert.Equal("Deleted 3 day(s) of MNQ", Loc.F("Borrados {0} día(s) de {1}", 3, "MNQ"));
        Assert.Equal("texto que no existe", Loc.T("texto que no existe"));
        // los espacios de los bordes se conservan aunque el diccionario tenga el texto sin ellos
        Assert.Equal("  Settings ", Loc.T("  Ajustes "));
        Assert.Equal("en-US", Loc.Culture.Name);

        Loc.Init(Loc.Chinese);
        Assert.Equal("设置", Loc.T("Ajustes"));
        Assert.Equal("zh-CN", Loc.Culture.Name);

        Loc.Init(Loc.Spanish);
        Assert.Equal("Ajustes", Loc.T("Ajustes"));
        Assert.Equal("Borrados 3 día(s) de MNQ", Loc.F("Borrados {0} día(s) de {1}", 3, "MNQ"));
    }

    [Fact]
    public void Al_chino_le_falta_algo_y_sale_en_ingles()
    {
        Loc.Use(Loc.Chinese, new Dictionary<string, string> { ["Abrir"] = "Open" });
        Assert.Equal("Open", Loc.T("Abrir"));

        // con Init: lo que el chino no tiene viene del inglés
        var dir = Directory.CreateTempSubdirectory("th-loc-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "en.json"), "{\"Texto solo en inglés\": \"English only\"}");
            Loc.Init(Loc.Chinese, dir.FullName);
            Assert.Equal("English only", Loc.T("Texto solo en inglés"));
            Assert.Equal("设置", Loc.T("Ajustes"));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Una_traduccion_mal_escrita_no_rompe_la_interfaz()
    {
        Loc.Use(Loc.English, new Dictionary<string, string> { ["{0} días"] = "{0} days {1}" });
        Assert.Equal("5 días", Loc.F("{0} días", 5));
    }

    [Theory]
    [InlineData("(\n    \"zh-Hans-CN\",\n    \"en-CN\"\n)", "zh")]
    [InlineData("(\n    \"es-AR\"\n)", "es")]
    [InlineData("(\n    en,\n    ja\n)", "en")]
    [InlineData("", null)]
    public void Lee_el_idioma_de_la_Mac(string output, string? expected) => Assert.Equal(expected, Loc.FirstAppleLanguage(output));

    [Theory]
    [InlineData("es", "es")]
    [InlineData("en", "en")]
    [InlineData("zh", "zh")]
    [InlineData("EN", "en")]
    public void El_ajuste_elige_el_idioma(string setting, string expected) => Assert.Equal(expected, Loc.Resolve(setting));
}

/// <summary>Cambian el idioma de toda la app: no corren a la par de las demás pruebas.</summary>
[CollectionDefinition("Loc", DisableParallelization = true)]
public class LocCollection
{
}
