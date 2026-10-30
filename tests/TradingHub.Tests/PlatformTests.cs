using Avalonia.Input;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.Common;

namespace TradingHub.Tests;

/// <summary>Solo corre en Windows (en la Mac queda omitida).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Solo en Windows";
    }
}

/// <summary>
/// Solo en Windows y si se pide con la variable (en 1): escribe una credencial de prueba en el Administrador de
/// credenciales de verdad y la borra al terminar.
/// </summary>
public sealed class WindowsOptInFactAttribute : FactAttribute
{
    public WindowsOptInFactAttribute(string variable)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Solo en Windows";
        else if (Environment.GetEnvironmentVariable(variable) != "1") Skip = $"Poné {variable}=1 para correrla";
    }
}

/// <summary>
/// Lo que cambia entre la Mac y Windows en la interfaz: atajos, textos, fuentes y una sola copia abierta. Lo que no
/// depende del sistema se prueba en los dos; las llamadas a Windows, solo en Windows.
/// </summary>
public class PlatformTests
{
    [Fact]
    public void Shortcuts_UseMacSymbolsOnMacAndNamesWithPlusOnWindows()
    {
        Assert.Equal("⌘K", Keys.Format(KeyModifiers.Meta, "K", mac: true));
        Assert.Equal("⇧⌘D", Keys.Format(KeyModifiers.Meta | KeyModifiers.Shift, "D", mac: true));
        Assert.Equal("⌃⌥⇧⌘A", Keys.Format(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta, "A", mac: true));
        Assert.Equal("Ctrl+K", Keys.Format(KeyModifiers.Control, "K", mac: false));
        Assert.Equal("Ctrl+Shift+D", Keys.Format(KeyModifiers.Control | KeyModifiers.Shift, "D", mac: false));
        Assert.Equal("Ctrl+Alt+A", Keys.Format(KeyModifiers.Control | KeyModifiers.Alt, "A", mac: false));
        Assert.Equal("F5", Keys.Format(KeyModifiers.None, "F5", mac: false));
    }

    [Fact]
    public void CommandKey_IsCmdOnMacAndCtrlOnWindows()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? KeyModifiers.Control : KeyModifiers.Meta, Keys.Command);
        Assert.Equal(OperatingSystem.IsWindows() ? "Ctrl+Shift+D" : "⇧⌘D", Keys.Label("D", shift: true));
        Assert.Equal(OperatingSystem.IsWindows() ? "Ctrl+Enter" : "⌘Enter", Keys.Label("Enter"));
    }

    [Fact]
    public void Hotkeys_KeepMacSymbolsAndUseNamesOnWindows()
    {
        Assert.Equal("⌥1", Hotkeys.Pretty("Alt+D1", mac: true));
        Assert.Equal("Alt+1", Hotkeys.Pretty("Alt+D1", mac: false));
        Assert.Equal("⌃⇧Tab", Hotkeys.Pretty("Ctrl+Shift+Tab", mac: true));
        Assert.Equal("Ctrl+Shift+Tab", Hotkeys.Pretty("Ctrl+Shift+Tab", mac: false));
        Assert.Equal("↩", Hotkeys.Pretty("Enter", mac: true));
        Assert.Equal("Enter", Hotkeys.Pretty("Enter", mac: false));
        Assert.Equal("—", Hotkeys.Pretty(null, mac: false));
    }

    [Fact]
    public void Texts_NameTheThingsOfEachSystem()
    {
        bool windows = OperatingSystem.IsWindows();
        Assert.Equal(windows ? "PC" : "Mac", Os.Device);
        Assert.Equal(windows ? "el Administrador de credenciales" : "el Llavero", Os.SecretStore);
    }

    [Fact]
    public void MonoFont_IsMenloOnMacAndCascadiaOrConsolasOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("Cascadia Mono, Consolas", PlatformFonts.Mono);
            Assert.Equal("Consolas", PlatformFonts.MonoSkia);
            Assert.Equal("Segoe UI", PlatformFonts.SansSkia);
        }
        else
        {
            Assert.StartsWith("Menlo", PlatformFonts.Mono);
            Assert.Equal("Menlo", PlatformFonts.MonoSkia);
            Assert.Equal("Helvetica Neue", PlatformFonts.SansSkia);
        }

        Assert.Equal(PlatformFonts.MonoSkia, new TradingHub.Charting.ChartTheme().MonoFontFamily);
    }

    // ===================================================================== una sola copia abierta

    [Fact]
    public void SingleInstance_DependsOnlyOnTheDataFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "th-datos-a");
        var name = SingleInstance.NameFor(folder);
        Assert.StartsWith(@"Local\TradingHub-", name);
        Assert.Equal(name, SingleInstance.NameFor(folder + Path.DirectorySeparatorChar));
        Assert.Equal(name, SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "TH-DATOS-A")));
        Assert.NotEqual(name, SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "th-datos-b")));
    }
}
