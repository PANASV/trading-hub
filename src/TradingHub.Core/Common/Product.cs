namespace TradingHub.Common;

/// <summary>El nombre de la app y sus carpetas.</summary>
public static class Product
{
    public const string Name = "Trading Hub";

    /// <summary>La carpeta de la app dentro de <paramref name="baseDir"/>.</summary>
    public static string Folder(string baseDir) => Path.Combine(baseDir, Name);

    /// <summary>Datos de la app (~/Library/Application Support/Trading Hub).</summary>
    public static string AppData => Folder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Carpeta visible del usuario (~/Documents/Trading Hub): estrategias e indicadores propios.</summary>
    public static string Documents => Folder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
}
