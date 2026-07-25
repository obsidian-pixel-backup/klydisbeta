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
using Klydis.App.Services;

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

        // Apply the persisted theme before any window is shown, so there is no
        // flash of the default palette.
        ServiceProvider.GetRequiredService<ThemeService>().LoadAndApplyPersistedTheme();

        try
        {
            // Initialize required core services
            var modelRegistry = ServiceProvider.GetRequiredService<ModelRegistry>();
            var modelDiscovery = ServiceProvider.GetRequiredService<ModelDiscoveryService>();
            
            modelDiscovery.ModelDiscovered += (path) => { _ = modelRegistry.SyncWithDiskAsync(); };
            modelDiscovery.ModelDeleted += (path) => { _ = modelRegistry.SyncWithDiskAsync(); };

            await modelRegistry.LoadAsync();
            await modelRegistry.SyncWithDiskAsync();

            var messageStore = ServiceProvider.GetRequiredService<Klydis.Core.Memory.MessageStore>();
            await messageStore.InitializeAsync();

            var skillLibraryManager = ServiceProvider.GetRequiredService<Klydis.Core.Skills.SkillLibraryManager>();
            await skillLibraryManager.InitializeAsync();
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
        services.AddSingleton<INativeResourceDisposer, NativeResourceDisposer>();
        services.AddSingleton<SpeculativeDecodingService>();
        services.AddSingleton<InferenceEngine>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<InferenceEngine>>();
            var disposer = sp.GetRequiredService<INativeResourceDisposer>();
            var engine = new InferenceEngine(logger, disposer);
            engine.SpeculativeDecodingService = sp.GetRequiredService<SpeculativeDecodingService>();
            return engine;
        });
        services.AddSingleton<Klydis.Core.Chat.IInferenceEngine>(sp => sp.GetRequiredService<InferenceEngine>());
        services.AddSingleton<ModelRegistry>();
        services.AddSingleton<ModelDiscoveryService>();
        services.AddSingleton<ModelQuantizerService>();
        services.AddSingleton<System.Net.Http.HttpClient>();
        services.AddSingleton<HuggingFaceClient>();
        services.AddSingleton<MessageStore>();
        services.AddSingleton<ContextOrchestrator>();
        services.AddSingleton<ModelMessageQueue>();
        services.AddSingleton<Klydis.Core.Chat.CamoufoxManager>();
        services.AddSingleton<Klydis.Core.Chat.StealthBrowserService>();
        services.AddSingleton<ChatEngine>();
        services.AddSingleton<ToolExecutor>();
        services.AddSingleton<PromptTemplateEngine>();
        services.AddSingleton<ModelPool>();
        services.AddSingleton<GpuProfiler>();
        services.AddSingleton<SystemProfiler>();
        services.AddSingleton<OffloadStrategy>();
        services.AddSingleton<Klydis.Core.Skills.SkillLibraryManager>();
        services.AddSingleton<Klydis.Core.Skills.DynamicSkillSelector>();
        services.AddSingleton<ThemeService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<ModelLibraryViewModel>();
        services.AddTransient<SkillLibraryViewModel>();
        services.AddTransient<SystemMonitorViewModel>();
        services.AddTransient<SettingsViewModel>();
        
        // Views
        services.AddTransient<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (ServiceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
        Environment.Exit(0);
    }
}
