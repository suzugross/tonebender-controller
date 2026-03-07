using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ToneBenderController.Services;
using ToneBenderController.ViewModels;
using ToneBenderController.Views;

namespace ToneBenderController;

public partial class App : Application
{
    private IServiceProvider? _services;

    public static IServiceProvider Services
        => ((App)Current)._services
           ?? throw new InvalidOperationException("ServiceProvider is not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // --- Services (Singleton) ---
        services.AddSingleton<IDiskService, DiskService>();
        services.AddSingleton<IPowerShellService, PowerShellService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IAutopilotService, AutopilotService>();

        // --- ViewModels (Singleton) ---
        services.AddSingleton<UsbCreationViewModel>();
        services.AddSingleton<WinPeBuildViewModel>();
        services.AddSingleton<ToneBenderConfigViewModel>();
        services.AddSingleton<MainViewModel>();

        // --- Views (Transient) ---
        services.AddTransient<MainWindow>();
    }
}
