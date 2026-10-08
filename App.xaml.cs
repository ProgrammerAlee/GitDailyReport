using System.Windows;
using GitDailyReport.Services;
using GitDailyReport.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace GitDailyReport;

/// <summary>
/// Application entry point with Dependency Injection setup
/// </summary>
public partial class App : Application
{
    private readonly ServiceProvider _serviceProvider;

    public App()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton(_ => new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        });
        services.AddSingleton<IDeepseekService, DeepseekService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.Loaded += (_, _) =>
        {
            mainWindow.Activate();
        };
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var message = e.Exception.InnerException?.Message ?? e.Exception.Message;
        MessageBox.Show(
            "程序出现未处理的错误。\n\n" + message,
            "错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
