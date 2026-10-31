using Avalonia.Input;

namespace TradingHub.App;

/// <summary>
/// Los nombres de las cosas del sistema en los textos de la interfaz: en la Mac quedan como siempre y en Windows se
/// usan los de Windows (la PC, el Administrador de credenciales…). Los textos largos eligen con <see cref="IsWindows"/>.
/// </summary>
internal static class Os
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();

    /// <summary>«Mac» o «PC» (para «esta Mac», «tu PC»…).</summary>
    public static string Device => IsWindows ? "PC" : "Mac";

    /// <summary>Dónde quedan las claves: «el Llavero» o «el Administrador de credenciales».</summary>
    public static string SecretStore => IsWindows ? L.T("el Administrador de credenciales") : L.T("el Llavero");

    /// <summary>Lo mismo, corto (para los campos de texto): «el Llavero» o «Windows».</summary>
    public static string SecretStoreShort => IsWindows ? "Windows" : L.T("el Llavero");
}

/// <summary>
/// Los atajos de teclado: la tecla de comando es ⌘ en la Mac y Ctrl en Windows, y cada sistema los escribe a su manera
/// (⇧⌘D o Ctrl+Shift+D).
/// </summary>
internal static class Keys
{
    /// <summary>La tecla de los atajos (⌘ en la Mac, Ctrl en Windows).</summary>
    public static KeyModifiers Command { get; } = Os.IsWindows ? KeyModifiers.Control : KeyModifiers.Meta;

    /// <summary>Un atajo con la tecla de comando, para mostrar: «⌘K» o «⇧⌘D» en la Mac; «Ctrl+K» o «Ctrl+Shift+D» en Windows.</summary>
    public static string Label(string key, bool shift = false) =>
        Format(Command | (shift ? KeyModifiers.Shift : KeyModifiers.None), key, mac: !Os.IsWindows);

    /// <summary>
    /// Modificadores y tecla como los escribe cada sistema: en la Mac con símbolos y en el orden de macOS (⌃⌥⇧⌘), en
    /// Windows con nombres separados por «+» (Ctrl+Alt+Shift+Win).
    /// </summary>
    public static string Format(KeyModifiers mods, string key, bool mac)
    {
        if (mac)
            return (mods.HasFlag(KeyModifiers.Control) ? "⌃" : "") + (mods.HasFlag(KeyModifiers.Alt) ? "⌥" : "") +
                   (mods.HasFlag(KeyModifiers.Shift) ? "⇧" : "") + (mods.HasFlag(KeyModifiers.Meta) ? "⌘" : "") + key;

        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }
}
