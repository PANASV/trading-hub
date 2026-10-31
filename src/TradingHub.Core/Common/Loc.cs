using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace TradingHub.Common;

/// <summary>
/// Idioma de la interfaz. Los textos se escriben en español en el código y se traducen con un diccionario cuya clave es
/// el texto en español (como gettext): <c>L.T("Comprar")</c> o, con valores, <c>L.F("{0} días", n)</c>.
/// Si un texto no tiene traducción se muestra en español. Las traducciones vienen en los recursos de esta biblioteca
/// (<c>Localization/en.json</c>) y se pueden completar o corregir con un archivo propio en
/// <c>{datos de la app}/Languages/en.json</c>, que pisa lo que trae la app.
/// </summary>
public static class Loc
{
    public const string Spanish = "es";
    public const string English = "en";
    public const string Chinese = "zh";

    /// <summary>Valor de ajuste: el idioma del sistema (español o chino si el sistema está en ese idioma; si no, inglés).</summary>
    public const string Auto = "auto";

    private static volatile Dictionary<string, string>? _table;
    private static readonly ConcurrentDictionary<string, string> Missing = new();

    /// <summary>Idiomas que se pueden elegir, con su nombre en ese idioma.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = new[]
    {
        (Spanish, "Español"),
        (English, "English"),
        (Chinese, "简体中文"),
    };

    /// <summary>Idioma activo ("es", "en" o "zh").</summary>
    public static string Language { get; private set; } = Spanish;

    public static bool IsSpanish => Language == Spanish;

    /// <summary>Formato de números y fechas de la interfaz según el idioma (es-AR, en-US o zh-CN).</summary>
    public static CultureInfo Culture => Language switch
    {
        English => CultureInfo.GetCultureInfo("en-US"),
        Chinese => CultureInfo.GetCultureInfo("zh-CN"),
        _ => CultureInfo.GetCultureInfo("es-AR"),
    };

    /// <summary>Resuelve "auto" con el idioma del sistema.</summary>
    public static string Resolve(string? setting)
    {
        foreach (var (code, _) in Languages)
            if (string.Equals(setting, code, StringComparison.OrdinalIgnoreCase))
                return code;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            Spanish => Spanish,
            Chinese => Chinese,
            _ => English,
        };
    }

    /// <summary>
    /// Elige el idioma (al arrancar, antes de armar la interfaz). <paramref name="userFolder"/> es la carpeta con
    /// traducciones propias (opcional).
    /// </summary>
    public static void Init(string? setting, string? userFolder = null)
    {
        Language = Resolve(setting);
        if (Language == Spanish)
        {
            _table = null;
            return;
        }

        // lo que le falte al chino se muestra en inglés (y lo que le falte al inglés, en español)
        var chain = Language == English ? new[] { English } : new[] { English, Language };
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lang in chain)
        {
            Merge(table, typeof(Loc).Assembly.GetManifestResourceStream($"TradingHub.Localization.{lang}.json"));
            if (userFolder == null) continue;
            var own = Path.Combine(userFolder, lang + ".json");
            if (!File.Exists(own)) continue;
            try { Merge(table, File.OpenRead(own)); }
            catch (Exception) { /* un archivo propio roto no impide arrancar */ }
        }

        _table = table;
    }

    /// <summary>Usa este diccionario (pruebas).</summary>
    public static void Use(string language, IReadOnlyDictionary<string, string>? table)
    {
        Language = language;
        _table = table == null ? null : new Dictionary<string, string>(table, StringComparer.Ordinal);
    }

    private static void Merge(Dictionary<string, string> table, Stream? json)
    {
        if (json == null) return;
        using (json)
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (d == null) return;
            foreach (var (k, v) in d)
                if (!string.IsNullOrEmpty(v)) table[k] = v;
        }
    }

    /// <summary>Traduce un texto fijo.</summary>
    public static string T(string? spanish)
    {
        if (string.IsNullOrEmpty(spanish)) return spanish ?? "";
        var table = _table;
        if (table == null) return spanish;
        if (table.TryGetValue(spanish, out var t)) return t;

        // "Listo. " o " · Abrir": se traduce el texto sin los espacios de los bordes y se los vuelve a poner
        int a = 0, b = spanish.Length;
        while (a < b && char.IsWhiteSpace(spanish[a])) a++;
        while (b > a && char.IsWhiteSpace(spanish[b - 1])) b--;
        if ((a > 0 || b < spanish.Length) && b > a && table.TryGetValue(spanish[a..b], out t))
            return spanish[..a] + t + spanish[b..];

        if (spanish.Any(char.IsLetter)) Missing.TryAdd(spanish, spanish);
        return spanish;
    }

    /// <summary>Traduce un texto con valores (formato compuesto: "{0} días").</summary>
    public static string F(string spanishFormat, params object?[] args)
    {
        var format = T(spanishFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // una traducción mal escrita no rompe la interfaz: se usa el original
            return string.Format(CultureInfo.CurrentCulture, spanishFormat, args);
        }
    }

    /// <summary>Textos que se pidieron y no tienen traducción (para completar el diccionario).</summary>
    public static IReadOnlyCollection<string> MissingTexts => Missing.Keys.ToList();
}
