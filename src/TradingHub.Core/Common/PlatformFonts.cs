namespace TradingHub.Common;

/// <summary>
/// Fuentes del sistema según la plataforma: las de la Mac (Menlo, Helvetica Neue) no vienen en Windows, donde se usan
/// Cascadia Mono o Consolas y Segoe UI.
/// </summary>
public static class PlatformFonts
{
    /// <summary>Monoespaciada para la interfaz (lista con alternativas, la primera que esté instalada).</summary>
    public static string Mono { get; } = OperatingSystem.IsWindows() ? "Cascadia Mono, Consolas, monospace" : "Menlo, SF Mono, monospace";

    /// <summary>Monoespaciada para dibujar con Skia (una sola familia: Skia no toma alternativas).</summary>
    public static string MonoSkia { get; } = OperatingSystem.IsWindows() ? "Consolas" : "Menlo";

    /// <summary>Sans para dibujar con Skia (gráficos y reportes).</summary>
    public static string SansSkia { get; } = OperatingSystem.IsWindows() ? "Segoe UI" : "Helvetica Neue";
}
