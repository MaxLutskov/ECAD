using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ECAD.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLog.Write("Необроблена помилка застосунку", eventArgs.ExceptionObject as Exception ?? new Exception(eventArgs.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Write("Необроблена помилка фонової задачі", eventArgs.Exception);
            eventArgs.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            AppLog.Write("Необроблена помилка інтерфейсу", eventArgs.Exception);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is MainWindow window)
                window.ReportError(eventArgs.Exception, "Помилка інтерфейсу", false);
            eventArgs.Handled = true;
        };

        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex)
        {
            AppLog.Write("Критична помилка запуску або завершення", ex);
            throw;
        }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
