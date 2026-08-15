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
            Klydis.Core.Diagnostics.KlydisLog.AppendHardLog("INIT ERROR: " + ex + Environment.NewLine);
            Environment.Exit(1);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        this.DispatcherUnhandledException += (s, args) =>
        {
            Klydis.Core.Diagnostics.KlydisLog.AppendFatalError(args.Exception + Environment.NewLine);
            args.Handled = true;
            Current.Shutdown();
        };

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        ServiceProvider = serviceCollection.BuildServiceProvider();

        // Apply the persisted theme before any window is shown, so there is no
        // flash of the default palette.
        ServiceProvider.GetRequiredService<ThemeService>().LoadAndApplyPersistedTheme();

        // Show the splash IMMEDIATELY — the frontend is alive while the backend initializes
        // behind it. Previously every startup step (native engine sync, GitHub update check,
        // model library scan, DB / RAG / skills init) completed before ANY window appeared, so
        // a slow backend meant minutes of blank screen. StartupSequence reports each phase
        // into the splash, then we hand over to the main window.
        var splash = new Views.SplashWindow();
        Application.Current.MainWindow = splash;
        splash.Show();

        try
        {
            var startup = new Services.StartupSequence(ServiceProvider, splash);
            await startup.RunAsync();
        }
        catch (Exception ex)
        {
            var logger = ServiceProvider.GetRequiredService<ILogger<App>>();
            logger.LogError(ex, "Startup sequence failed.");
        }

        try
        {
            Console.WriteLine("1. Starting OnStartup");
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            Console.WriteLine("2. Resolved MainWindow");
            Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
            Console.WriteLine("3. Shown MainWindow");
            splash.Close();
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
        services.AddSingleton<ThemeService>();
        services.AddSingleton<INativeResourceDisposer, NativeResourceDisposer>();
        services.AddSingleton<SpeculativeDecodingService>();
        services.AddSingleton<InferenceEngine>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<InferenceEngine>>();
            var disposer = sp.GetRequiredService<INativeResourceDisposer>();
            var engine = new InferenceEngine(logger, disposer);
            engine.SpeculativeDecodingService = sp.GetRequiredService<SpeculativeDecodingService>();
            var themeService = sp.GetService<ThemeService>();
            if (themeService != null)
            {
                engine.UserContextLimit = (uint)themeService.UserContextLimit;
                engine.UserBatchSize = (uint)themeService.UserBatchSize;
                engine.UserUBatchSize = (uint)themeService.UserUBatchSize;
                engine.IsSpeculativeDecodingEnabled = themeService.IsSpeculativeDecodingEnabled;
                engine.SpeculativeDraftCount = themeService.SpeculativeDraftCount;
                engine.SelectedDraftModelPath = themeService.SelectedDraftModelPath;
            }
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
        services.AddSingleton<Klydis.Core.Learning.AdaptiveLearningService>();
        services.AddSingleton<Klydis.Core.Chat.CamoufoxManager>();
        services.AddSingleton<Klydis.Core.Chat.StealthBrowserService>();
        services.AddSingleton<ModelPool>();
        services.AddSingleton<GpuProfiler>();
        services.AddSingleton<SystemProfiler>();
        services.AddSingleton<OffloadStrategy>();
        services.AddSingleton<Klydis.Core.Skills.SkillLibraryManager>();
        services.AddSingleton<Klydis.Core.Skills.DynamicSkillSelector>();
        // RAG Services
        services.AddSingleton<Klydis.Core.RAG.IVectorEmbedder, Klydis.Core.RAG.LLamaVectorEmbedder>(sp =>
            new Klydis.Core.RAG.LLamaVectorEmbedder(dimension: 384, logger: sp.GetService<ILogger<Klydis.Core.RAG.LLamaVectorEmbedder>>()));
        services.AddSingleton<Klydis.Core.RAG.VectorStore>();
        services.AddSingleton<Klydis.Core.RAG.DocumentIngestionEngine>();
        services.AddSingleton<Klydis.Core.RAG.HybridRetriever>();

        services.AddSingleton<ToolExecutor>(sp =>
        {
            var toolExecutor = new ToolExecutor(
                sp.GetRequiredService<ILogger<ToolExecutor>>(),
                sp.GetRequiredService<MessageStore>(),
                sp.GetRequiredService<ContextOrchestrator>(),
                sp.GetService<ModelMessageQueue>(),
                sp.GetService<Klydis.Core.Skills.SkillLibraryManager>(),
                sp.GetService<Klydis.Core.Chat.StealthBrowserService>(),
                sp.GetService<Klydis.Core.RAG.VectorStore>(),
                sp.GetService<Klydis.Core.RAG.HybridRetriever>(),
                sp.GetService<Klydis.Core.RAG.DocumentIngestionEngine>(),
                sp.GetRequiredService<Klydis.Core.Learning.AdaptiveLearningService>()
            );
            // Default to Standard (approval gate for risky/flagged tools). AutoPilot mode
            // executes arbitrary PowerShell with no approval gate, which combined with
            // prompt-injection surface from RAG docs and crawled pages is unsafe as a default;
            // users who want the fully autonomous mode switch to it in the UI selector.
            toolExecutor.CurrentRiskLevel = RiskLevel.Standard;
            return toolExecutor;
        });
        services.AddSingleton<ChatEngine>(sp =>
        {
            var engine = new ChatEngine(
                sp.GetRequiredService<Klydis.Core.Chat.IInferenceEngine>(),
                sp.GetRequiredService<PromptTemplateEngine>(),
                sp.GetRequiredService<ToolExecutor>(),
                sp.GetRequiredService<MessageStore>(),
                sp.GetRequiredService<ContextOrchestrator>(),
                sp.GetRequiredService<ILogger<ChatEngine>>(),
                sp.GetService<ModelMessageQueue>(),
                sp.GetService<Klydis.Core.RAG.VectorStore>(),
                sp.GetRequiredService<Klydis.Core.Learning.AdaptiveLearningService>()
            );
            var themeService = sp.GetService<ThemeService>();
            if (themeService != null)
            {
                engine.SelectedPersonality = themeService.SelectedPersonality;
            }
            return engine;
        });
        services.AddSingleton<PromptTemplateEngine>();
        services.AddSingleton<Klydis.Core.Chat.GoalBudget>();
        services.AddTransient<Klydis.Core.Chat.GoalOrchestrator>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<ModelLibraryViewModel>();
        services.AddTransient<SkillLibraryViewModel>();
        services.AddTransient<SystemMonitorViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<RagViewModel>();
        
        // Views
        services.AddTransient<MainWindow>();
        services.AddTransient<Klydis.App.Views.RagView>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (ServiceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnExit disposal error: {ex.Message}");
        }

        base.OnExit(e);
        Environment.Exit(0);
    }
}
