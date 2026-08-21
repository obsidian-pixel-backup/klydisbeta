using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Klydis.Core.Hardware;
using Klydis.Core.Models;
using Klydis.Core.Inference;
using Klydis.Core.Chat;
using Klydis.Core.Memory;
using Klydis.Core.Capabilities;
using Klydis.Core.Capabilities.Bridge;
using Klydis.Core.Capabilities.Policy;
using Klydis.Core.Epistemic;
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
        // Register BEFORE any window or service starts: an unhandled exception on a
        // non-UI thread (threadpool continuation, Task.Run fault) never reaches
        // DispatcherUnhandledException — the 2026-08-16 native access violation died with
        // zero app-side trace for exactly this reason (WER caught it, the app didn't).
        RegisterGlobalExceptionHandlers();

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

    /// <summary>
    /// Captures every failure mode the process can die from and writes a full forensic dump
    /// (exception chain + stack traces + native log tail) to %LOCALAPPDATA%\Klydis\logs\fatal_error.txt.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        // Unhandled exceptions on non-UI threads (the crash class that killed the app on
        // 2026-08-16: an access violation escaping a detached async continuation). The process
        // still terminates, but the dump is written first.
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            Klydis.Core.Diagnostics.CrashLog.WriteFatal(
                args.ExceptionObject as Exception ?? new Exception("Non-Exception failure object: " + args.ExceptionObject),
                $"AppDomain.UnhandledException (IsTerminating={args.IsTerminating})");

        // Faulted fire-and-forget tasks surface here at GC time; log them and mark observed so
        // the runtime does not additionally kill the process for the unobserved fault.
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Klydis.Core.Diagnostics.CrashLog.WriteFatal(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };

        // UI-thread exceptions: log forensics and mark handled so the application stays alive.
        this.DispatcherUnhandledException += (s, args) =>
        {
            Klydis.Core.Diagnostics.CrashLog.WriteFatal(args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            // Durable mirror of ALL ILogger output (Debug and up) into the rotating app.log.
            configure.AddProvider(new Klydis.Core.Diagnostics.KlydisLogFileLoggerProvider());
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
        services.AddSingleton<Klydis.Core.Tasks.TaskManager>();
        // P0: the task workspace root is established ONCE at startup from the process working
        // directory (canonicalized). It is propagated to the runtime (whose workspace-boundary
        // validator then encloses task-mode file tools) and to the tool executor (whose
        // run_command default cwd and tool-output offload stay inside it). Previously
        // AgentRuntime.WorkspaceRoot was never assigned in production, so the validator's
        // null-root path left the boundary permissive.
        string? appWorkspaceRoot = null;
        try
        {
            appWorkspaceRoot = System.IO.Path.GetFullPath(Environment.CurrentDirectory);
        }
        catch (Exception ex)
        {
            // Unresolvable working directory — keep the root null (permissive) and surface it.
            System.Diagnostics.Debug.WriteLine($"Failed to resolve workspace root from the process working directory; workspace enforcement is DISABLED: {ex}");
        }
        services.AddSingleton<Klydis.Core.Tasks.ITaskEventBus, Klydis.Core.Tasks.TaskEventBus>();
        services.AddSingleton<Klydis.Core.Tasks.IWorkspaceVersionManager, Klydis.Core.Tasks.WorkspaceVersionManager>();
        services.AddSingleton<Klydis.Core.Tasks.ITaskWorkspaceManager>(sp => new Klydis.Core.Tasks.TaskWorkspaceManager(appWorkspaceRoot));
        services.AddSingleton<Klydis.Core.Processes.IProcessManager, Klydis.Core.Processes.ProcessManager>();
        services.AddSingleton<Klydis.Core.Processes.ITerminalSessionManager, Klydis.Core.Processes.TerminalSessionManager>();
        services.AddSingleton<Klydis.Core.Tasks.ICompletionEngine, Klydis.Core.Tasks.CompletionEngine>();
        services.AddSingleton<Klydis.Core.Tasks.IActionExecutor, Klydis.Core.Tasks.ActionExecutorAdapter>();
        services.AddSingleton<Klydis.Core.Memory.IContextAssemblyPipeline, Klydis.Core.Memory.ContextAssemblyPipeline>();
        services.AddSingleton<Klydis.Core.Tasks.IStateDeltaStagnationTracker, Klydis.Core.Tasks.StateDeltaStagnationTracker>();

        services.AddSingleton<Klydis.Core.Tasks.AgentRuntime>(sp =>
        {
            var runtime = new Klydis.Core.Tasks.AgentRuntime(
                sp.GetRequiredService<Klydis.Core.Tasks.TaskManager>(),
                sp.GetRequiredService<MessageStore>(),
                sp.GetRequiredService<Klydis.Core.Tasks.IActionExecutor>(),
                sp.GetRequiredService<Klydis.Core.Tasks.ICompletionEngine>(),
                sp.GetService<Klydis.Core.Skills.ISkillRouter>(),
                sp.GetService<ILogger<Klydis.Core.Tasks.AgentRuntime>>());
            // Unbounded system access: runtime.WorkspaceRoot is null so the Action Gate
            // allows the agent full system access to any directory on the machine.
            runtime.WorkspaceRoot = null;
            return runtime;
        });
        services.AddSingleton<Klydis.Core.Tasks.IAgentRuntime>(sp => sp.GetRequiredService<Klydis.Core.Tasks.AgentRuntime>());

        services.AddSingleton<Klydis.Core.Learning.AdaptiveLearningService>();
        services.AddSingleton<Klydis.Core.Chat.CamoufoxManager>();
        services.AddSingleton<Klydis.Core.Chat.StealthBrowserService>();
        services.AddSingleton<ModelPool>();
        services.AddSingleton<GpuProfiler>();
        services.AddSingleton<SystemProfiler>();
        services.AddSingleton<Klydis.Core.Skills.SkillLibraryManager>();
        services.AddSingleton<Klydis.Core.Skills.SkillIndex>();
        services.AddSingleton<Klydis.Core.Skills.SkillReranker>();
        services.AddSingleton<Klydis.Core.Skills.SkillLeaseManager>();
        services.AddSingleton<Klydis.Core.Skills.DynamicSkillSelector>(sp =>
            new Klydis.Core.Skills.DynamicSkillSelector(
                sp.GetRequiredService<Klydis.Core.Skills.SkillLibraryManager>(),
                sp.GetService<Klydis.Core.Skills.SkillIndex>(),
                sp.GetService<Klydis.Core.Skills.SkillReranker>(),
                sp.GetService<ILogger<Klydis.Core.Skills.DynamicSkillSelector>>()));
        services.AddSingleton<Klydis.Core.Skills.ISkillRouter>(sp => sp.GetRequiredService<Klydis.Core.Skills.DynamicSkillSelector>());
        // RAG Services
        services.AddSingleton<Klydis.Core.RAG.IVectorEmbedder, Klydis.Core.RAG.LLamaVectorEmbedder>(sp =>
            new Klydis.Core.RAG.LLamaVectorEmbedder(dimension: 384, logger: sp.GetService<ILogger<Klydis.Core.RAG.LLamaVectorEmbedder>>()));
        services.AddSingleton<Klydis.Core.RAG.VectorStore>();
        services.AddSingleton<Klydis.Core.RAG.DocumentIngestionEngine>();
        services.AddSingleton<Klydis.Core.RAG.HybridRetriever>();

        // Machine Capabilities & Epistemic Subsystem
        services.AddSingleton<ICapabilityRegistry>(sp =>
        {
            var systemProfiler = sp.GetService<SystemProfiler>();
            var gpuProfiler = sp.GetService<GpuProfiler>();
            var logger = sp.GetService<ILogger<CapabilityRegistry>>();
            return CapabilityBootstrapper.CreateDefaultRegistry(systemProfiler, gpuProfiler, logger);
        });
        services.AddSingleton<CapabilityGraph>(sp =>
        {
            var registry = sp.GetRequiredService<ICapabilityRegistry>();
            return CapabilityBootstrapper.CreateDefaultGraph(registry);
        });
        services.AddSingleton<FactLedger>(sp =>
        {
            var store = sp.GetRequiredService<MessageStore>();
            var logger = sp.GetService<ILogger<FactLedger>>();
            return new FactLedger(store, logger);
        });
        services.AddSingleton<IWorldModel, MachineWorldModel>(sp =>
        {
            var ledger = sp.GetRequiredService<FactLedger>();
            var logger = sp.GetService<ILogger<MachineWorldModel>>();
            return new MachineWorldModel(ledger, logger);
        });
        services.AddSingleton<IPolicyGate, CapabilityPolicyGate>(sp =>
        {
            var logger = sp.GetService<ILogger<CapabilityPolicyGate>>();
            return new CapabilityPolicyGate(AuthorityMode.LocalFullControl, logger);
        });
        services.AddSingleton<CapabilityToolBridge>(sp =>
        {
            var registry = sp.GetRequiredService<ICapabilityRegistry>();
            var worldModel = sp.GetRequiredService<IWorldModel>();
            var policyGate = sp.GetRequiredService<IPolicyGate>();
            var logger = sp.GetService<ILogger<CapabilityToolBridge>>();
            return new CapabilityToolBridge(registry, worldModel, policyGate, logger);
        });

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
                sp.GetRequiredService<Klydis.Core.Learning.AdaptiveLearningService>(),
                sp.GetService<Klydis.Core.Tasks.TaskManager>()
            );
            // Default to Standard (approval gate for risky/flagged tools). AutoPilot mode
            // executes arbitrary PowerShell with no approval gate, which combined with
            // prompt-injection surface from RAG docs and crawled pages is unsafe as a default;
            // users who want the fully autonomous mode switch to it in the UI selector.
            toolExecutor.CurrentRiskLevel = RiskLevel.Standard;
            // P0: same canonical workspace root as the runtime, so run_command defaults and
            // tool-output offload stay inside the project boundary.
            toolExecutor.WorkspaceRoot = appWorkspaceRoot;
            toolExecutor.CapabilityRegistry = sp.GetRequiredService<ICapabilityRegistry>() as CapabilityRegistry;
            toolExecutor.CapabilityToolBridge = sp.GetRequiredService<CapabilityToolBridge>();
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
                sp.GetRequiredService<Klydis.Core.Learning.AdaptiveLearningService>(),
                sp.GetService<Klydis.Core.Tasks.TaskManager>(),
                sp.GetService<Klydis.Core.Tasks.AgentRuntime>()
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
        // Marker so a clean exit is distinguishable from a crash in fatal_error.txt.
        Klydis.Core.Diagnostics.CrashLog.WriteShutdown();

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
