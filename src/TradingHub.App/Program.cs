using Avalonia;

namespace TradingHub.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // idioma de la interfaz: antes de armar cualquier texto
        TradingHub.Common.Loc.Init(Services.AppSettings.Load().Language, Path.Combine(Services.AppPaths.Root, "Languages"));

        // modo laboratorio: Trading Hub como servidor MCP para Claude Code (sin ventanas)
        if (args.Contains("--mcp"))
        {
            Environment.ExitCode = Lab.LabServer.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // importar velas de IB Gateway sin abrir la ventana: TradingHub --import-ib <carpeta> [SÍMBOLO]
        int ib = Array.IndexOf(args, "--import-ib");
        if (ib >= 0)
        {
            Environment.ExitCode = ImportIb(args.Skip(ib + 1).ToArray());
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        // Windows: si ya hay una Trading Hub abierta con estos datos, se muestra esa y esta no arranca (en la Mac lo hace el sistema)
        if (!Services.SingleInstance.Claim(Services.AppPaths.Root)) return;

        WaitForDisplay();
        try
        {
            BuildAvaloniaApp().AfterSetup(_ =>
            {
                // un error en un control se registra y la app sigue andando (no se cierra en pleno trading)
                Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    CrashLog.Write(e.Exception);
                    e.Handled = true;
                };
            }).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            throw;
        }
    }

    private static int ImportIb(string[] rest)
    {
        if (rest.Length == 0)
        {
            Console.Error.WriteLine("Uso: TradingHub --import-ib <carpeta con los CSV> [SÍMBOLO, por ejemplo MNQ]");
            return 2;
        }

        try
        {
            string folder = rest[0].StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rest[0][2..])
                : rest[0];
            var r = TradingHub.Data.IbBarImport.ImportFolder(folder, Services.AppPaths.Ticks, rest.Length > 1 ? rest[1].ToUpperInvariant() : null);
            Console.WriteLine($"{r.Symbol}: {r.Bars:N0} velas de {r.Files} archivo(s) → {r.Days} día(s) {r.From:yyyy-MM-dd} … {r.To:yyyy-MM-dd} UTC" +
                              (r.Contracts.Count > 0 ? $" · {string.Join(", ", r.Contracts)}" : "") +
                              (r.BadLines > 0 ? $" · {r.BadLines} filas ilegibles" : ""));
            Console.WriteLine($"Guardado en {Path.Combine(Services.AppPaths.Ticks, TradingHub.Data.IbBarImport.FeedId, r.Symbol)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint[]? activeDisplays, out uint displayCount);

    /// <summary>
    /// Con la pantalla apagada o bloqueada macOS no tiene pantalla activa y Avalonia no puede arrancar el dibujo
    /// (error -6661): en vez de cerrarse, Trading Hub espera a que vuelva la pantalla y ahí abre. En Windows no hace falta.
    /// </summary>
    private static void WaitForDisplay()
    {
        if (!OperatingSystem.IsMacOS()) return;
        for (int i = 0; ; i++)
        {
            try
            {
                if (CGGetActiveDisplayList(0, null, out var count) != 0 || count > 0) return;
            }
            catch (Exception)
            {
                return; // ante la duda arranca como siempre
            }

            if (i == 0) CrashLog.Write(new InvalidOperationException("No hay pantalla activa (apagada o bloqueada): Trading Hub abre cuando vuelva"));
            Thread.Sleep(2000);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            // menús y desplegables dentro de la ventana: como ventanas aparte, a veces se perdía el primer clic
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .LogToTrace();
}

/// <summary>Guarda los errores inesperados en crash.log, en la carpeta de datos de la app.</summary>
internal static class CrashLog
{
    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Services.AppPaths.Root);
            File.AppendAllText(Path.Combine(Services.AppPaths.Root, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // nada que hacer
        }
    }
}
