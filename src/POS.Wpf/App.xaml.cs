using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using POS.Application.Abstractions;
using POS.Infrastructure;
using POS.Wpf.Services;
using POS.Wpf.ViewModels;
using POS.Wpf.Windows;
using Serilog;

namespace POS.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.ToString(),
                "POS — unhandled error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show(ex.ToString(), "POS — fatal error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            StartPosHost(e);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Startup failed:\n\n" + ex,
                "POS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartPosHost(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .UseSerilog((context, _, _) =>
            {
                var logDir = Path.Combine(context.HostingEnvironment.ContentRootPath, "logs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "pos-.log");
                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(context.Configuration)
                    .WriteTo.Debug()
                    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                    .CreateLogger();
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<ICurrentDevice, CurrentDevice>();
                services.AddSingleton<ICurrentSession, CurrentSession>();
                services.AddSingleton<IReceiptPrinter, EscPosReceiptPrinter>();
                services.AddInfrastructure(context.Configuration, context.HostingEnvironment.ContentRootPath);
                services.AddHostedService<InvoiceSyncBackgroundService>();
                services.AddTransient<LoginViewModel>();
                services.AddTransient<LoginWindow>();
                services.AddTransient<MainViewModel>();
                services.AddTransient<MainWindow>();
                services.AddTransient<ProductManagementViewModel>();
                services.AddTransient<ProductManagementWindow>();
                services.AddTransient<CurrencySettingsViewModel>();
                services.AddTransient<CurrencySettingsWindow>();
                services.AddTransient<UserManagementViewModel>();
                services.AddTransient<UserManagementWindow>();
                services.AddTransient<DeviceManagementViewModel>();
                services.AddTransient<DeviceManagementWindow>();
            })
            .Build();

        _host.Services.ApplyPosDatabaseMigrations();

        var login = _host.Services.GetRequiredService<LoginWindow>();
        login.Topmost = true;
        if (login.ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        var main = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        main.Activate();
    }

    /// <summary>Swaps the application-level colour brushes to switch between warm light and dark theme.</summary>
    public static void SetTheme(bool dark)
    {
        var res = Current.Resources;
        if (dark)
        {
            // Warm dark (stone/slate palette)
            res["BgPrimaryBrush"]     = Brush("#1C1917");
            res["BgSecondaryBrush"]   = Brush("#292524");
            res["BgTertiaryBrush"]    = Brush("#231F1E");
            res["PanelBorderBrush"]   = Brush("#44403C");
            res["InputBorderBrush"]   = Brush("#57534E");
            res["TextPrimaryBrush"]   = Brush("#FAF9F8");
            res["TextSecondaryBrush"] = Brush("#A8A29E");
            res["TextTertiaryBrush"]  = Brush("#78716C");
            res["AccentLightBrush"]   = Brush("#052E16");
            res["AccentMidBrush"]     = Brush("#14532D");
            res["DangerBgBrush"]      = Brush("#450A0A");
            res["InfoBgBrush"]        = Brush("#1E3A5F");
            res["WarnBgBrush"]        = Brush("#451A03");
        }
        else
        {
            // Warm light (stone/neutral palette)
            res["BgPrimaryBrush"]     = Brush("#FFFFFF");
            res["BgSecondaryBrush"]   = Brush("#F5F5F4");
            res["BgTertiaryBrush"]    = Brush("#EDECEA");
            res["PanelBorderBrush"]   = Brush("#E7E5E4");
            res["InputBorderBrush"]   = Brush("#D6D3D1");
            res["TextPrimaryBrush"]   = Brush("#1C1917");
            res["TextSecondaryBrush"] = Brush("#78716C");
            res["TextTertiaryBrush"]  = Brush("#A8A29E");
            res["AccentLightBrush"]   = Brush("#F0FDF4");
            res["AccentMidBrush"]     = Brush("#DCFCE7");
            res["DangerBgBrush"]      = Brush("#FEF2F2");
            res["InfoBgBrush"]        = Brush("#DBEAFE");
            res["WarnBgBrush"]        = Brush("#FFFBEB");
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
