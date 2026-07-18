using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Klydis.Core.Hardware;
using Klydis.Core.Models;
using Klydis.Core.Inference;
using Klydis.Core.Chat;
using Klydis.Core.Memory;
using Klydis.App.ViewModels;

namespace Klydis.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Gets the application-wide service provider.
    /// </summary>
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    public App()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(@"E:\DEVELOPER PROJECTS\klydisbeta\hard_log.txt", "INIT ERROR: " + ex.ToString());
            Environment.Exit(1);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        this.DispatcherUnhandledException += (s, args) =>
        {
            System.IO.File.WriteAllText("fatal_error.txt", args.Exception.ToString());
            args.Handled = true;
            Current.Shutdown();
        };

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        ServiceProvider = serviceCollection.BuildServiceProvider();

        try
        {
            // Initialize required core services
            var modelRegistry = ServiceProvider.GetRequiredService<ModelRegistry>();
            await modelRegistry.LoadAsync();
            await modelRegistry.SyncWithDiskAsync();

            var messageStore = ServiceProvider.GetRequiredService<Klydis.Core.Memory.MessageStore>();
            await messageStore.InitializeAsync();
        }
        catch (Exception ex)
        {
            var logger = ServiceProvider.GetRequiredService<ILogger<App>>();
            logger.LogError(ex, "Failed to initialize core services on startup.");
        }

        try
        {
            Console.WriteLine("1. Starting OnStartup");
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            Console.WriteLine("2. Resolved MainWindow");
            Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
            Console.WriteLine("3. Shown MainWindow");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.ToString());
            Current.Shutdown();
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(configure =>
        {
            configure.AddConsole();
            configure.SetMinimumLevel(LogLevel.Debug);
        });

        // Core Services
        services.AddSingleton<InferenceEngine>();
        services.AddSingleton<Klydis.Core.Chat.IInferenceEngine>(sp => sp.GetRequiredService<InferenceEngine>());
        services.AddSingleton<ModelRegistry>();
        services.AddSingleton<ModelDiscoveryService>();
        services.AddSingleton<HuggingFaceClient>();
        services.AddSingleton<MessageStore>();
        services.AddSingleton<ContextOrchestrator>();
        services.AddSingleton<ChatEngine>();
        services.AddSingleton<ToolExecutor>();
        services.AddSingleton<PromptTemplateEngine>();
        services.AddSingleton<ModelPool>();
        services.AddSingleton<GpuProfiler>();
        services.AddSingleton<SystemProfiler>();
        services.AddSingleton<OffloadStrategy>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<ModelLibraryViewModel>();
        services.AddTransient<SystemMonitorViewModel>();
        services.AddTransient<SettingsViewModel>();
        
        // Views
        services.AddTransient<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
        Environment.Exit(0);
    }
}
