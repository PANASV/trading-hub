using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using TradingHub.App.Services;
using TradingHub.App.Windows;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.UiSnapshot")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.Tests")]

namespace TradingHub.App;

public sealed class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    public override void Initialize()
    {
        Name = TradingHub.Common.Product.Name;
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Styles.Add(new Material.Icons.Avalonia.MaterialIconStyles(null));
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://TradingHub/Styles/TradingHubTheme.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = new AppServices();
        TradingHub.App.Services.AppTheme.Apply(Services.Settings.ThemeMode);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow(Services);
            // Windows: cerrar la ventana principal (sin la bandeja del sistema) cierra la app, aunque quede otra ventana
            // abierta; si no, quedaría corriendo sin nada a la vista y sin Dock para volver
            if (OperatingSystem.IsWindows()) desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = main;
            desktop.ShutdownRequested += (_, _) => Services.Shutdown();
            // el ícono en la barra de menú de la Mac o en la bandeja de Windows (cerrar la ventana deja a Trading Hub corriendo ahí)
            var menuBar = TradingHub.App.Services.MenuBar.Install(this, Services, main);
            // Windows: abrir Trading Hub otra vez trae esta ventana (ver SingleInstance)
            SingleInstance.OnShowRequested(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (menuBar != null) menuBar.ShowMain();
                else
                {
                    if (!main.IsVisible) main.Show();
                    if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                    main.Activate();
                }
            }));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
