using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Klydis.Core.Inference;
using Klydis.Core.Models;
using Klydis.Core.Memory;
using Klydis.Core.Skills;
using Klydis.App.Views;

namespace Klydis.App.Services;

/// <summary>
/// Runs the app's startup phases behind the splash window: native llama.cpp engine
/// sync/auto-update, model library scan, message store, RAG index, and skill library.
/// Each phase is awaited on the dispatcher (yielding to the UI loop so the splash
/// renders and stays animated) and heavy work runs on the thread pool, so the
/// frontend is live immediately instead of waiting minutes for the backend.
/// </summary>
public sealed class StartupSequence
{
    private readonly IServiceProvider _services;
    private readonly ILogger<StartupSequence> _logger;
    private readonly SplashWindow _splash;

    public StartupSequence(IServiceProvider services, SplashWindow splash)
    {
        _services = services;
        _logger = services.GetRequiredService<ILogger<StartupSequence>>();
        _splash = splash;
    }

    public async Task RunAsync()
    {
        var phases = new (string Name, Func<Task> Work)[]
        {
            ("Preparing native engine", PrepareNativeEngineAsync),
            ("Restoring bundled models", RestoreBundledModelsAsync),
            ("Scanning model library", ScanModelLibraryAsync),
            ("Initializing message store", InitMessageStoreAsync),
            ("Initializing RAG index", InitVectorStoreAsync),
            ("Loading skill library", InitSkillsAsync),
            ("Finalizing startup", FinalizeAsync)
        };

        _splash.BeginPhases(Array.ConvertAll(phases, p => p.Name));

        for (int i = 0; i < phases.Length; i++)
        {
            _splash.SetActivePhase(i, $"Step {i + 1} of {phases.Length}: {phases[i].Name}…");
            try
            {
                await phases[i].Work();
            }
            catch (Exception ex)
            {
                // A failed phase must never strand the user on the splash: log and continue.
                _logger.LogError(ex, "Startup phase '{Name}' failed.", phases[i].Name);
            }
            _splash.MarkPhaseComplete(i);
        }

        _splash.Finish("Ready");
    }

    /// <summary>
    /// Syncs and auto-updates the native llama.cpp engine (previously done synchronously in
    /// Program.Main before any window existed — the main cause of the multi-minute blank
    /// startup). Runs fully off the UI thread; a first-run download can take minutes while the
    /// splash keeps the user informed. Restarting to activate an updated engine is handled here.
    /// </summary>
    private async Task PrepareNativeEngineAsync()
    {
        // Watchdog: the native-engine sync/update step must NEVER hang the splash. A slow
        // network or a multi-hundred-MB llama.cpp download used to leave the user staring at
        // "Preparing native engine…" indefinitely. Budget the whole step; if it does not
        // finish in time, cancel it and start with the current engine — the daily-throttled
        // update check simply retries on a later launch.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Task.Run(async () =>
        {
            // The status callback fires on the thread pool; marshal it onto the splash (UI thread).
            void SetStatus(string text) => _splash.Dispatcher.Invoke(() => _splash.SetStatus(text));

            try
            {
                NativeEngineManager.EnsureDirectoriesExist();

                // Handles both cases internally: fresh download when no custom engine exists,
                // daily-throttled release check when one is installed.
                bool needsRestart = await NativeEngineManager.TryAutoUpdateNativeEngineAsync(
                    logger: _logger, forceCheck: false, ct: cts.Token, statusCallback: SetStatus);

                if (needsRestart)
                {
                    _logger.LogInformation("Native engine updated; restarting to activate.");
                    NativeEngineManager.RestartApplication();
                    return;
                }

                // Deploy the (possibly just downloaded) engine into this build's output before
                // the LLamaSharp wrapper initializes.
                int synced = NativeEngineManager.SyncCustomNativeEngine();
                _logger.LogInformation("Native engine synced ({Count} DLLs).", synced);

                NativeEngineManager.EnsureNativeLibraryConfigured();
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Native engine sync/update exceeded the startup budget; continuing with the current engine. The update check retries on the next launch.");
                try { NativeEngineManager.SyncCustomNativeEngine(); } catch { }
                try { NativeEngineManager.EnsureNativeLibraryConfigured(); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Native engine sync/update failed; continuing with the bundled engine.");
                try { NativeEngineManager.EnsureNativeLibraryConfigured(); } catch { }
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reassembles split GGUF part files into the full model binary (e.g. Smeagle Q8_0, stored
    /// as ~47 parts because GitHub rejects single files over 100 MB) before the model library
    /// scan so the restored .gguf is discoverable. Idempotent: an existing valid model is left
    /// untouched (cheap header-only check, no multi-GB hashing on the startup path); a missing
    /// or corrupt model is rebuilt from its parts and verified against the manifest's pinned
    /// SHA-256 before use. Runs off the UI thread with a watchdog so a slow disk can never
    /// strand the splash.
    /// </summary>
    private async Task RestoreBundledModelsAsync()
    {
        var restorer = _services.GetRequiredService<SplitModelRestorer>();

        // Reassembling ~4.3 GiB from parts is disk-bound; on a slow disk this can take a
        // while, so budget the whole step. If it does not finish, the scan simply sees the
        // model as missing and the next launch retries the restore.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await Task.Run(() => restorer.RestoreAsync(cts.Token)).ConfigureAwait(false);
    }

    private async Task ScanModelLibraryAsync()
    {
        var modelRegistry = _services.GetRequiredService<ModelRegistry>();
        var modelDiscovery = _services.GetRequiredService<ModelDiscoveryService>();

        modelDiscovery.ModelDiscovered += path => { Klydis.Core.Diagnostics.FireAndForget.Observe(modelRegistry.SyncWithDiskAsync(), _logger, "ModelRegistry.SyncWithDiskAsync"); };
        modelDiscovery.ModelDeleted += path => { Klydis.Core.Diagnostics.FireAndForget.Observe(modelRegistry.SyncWithDiskAsync(), _logger, "ModelRegistry.SyncWithDiskAsync"); };

        await modelRegistry.LoadAsync();
        await modelRegistry.SyncWithDiskAsync();
    }

    private async Task InitMessageStoreAsync()
    {
        var messageStore = _services.GetRequiredService<MessageStore>();
        await messageStore.InitializeAsync();

        // C2/C3: repair broken chrome-navigator and weather-fetcher scripts on every launch.
        // CreateCustomToolAsync uses ON CONFLICT DO UPDATE so this is fully idempotent.
        await messageStore.RepairBrokenCustomToolsAsync();
    }

    private async Task InitVectorStoreAsync()
    {
        var vectorStore = _services.GetRequiredService<Klydis.Core.RAG.VectorStore>();
        await vectorStore.InitializeAsync();

        var orchestrator = _services.GetRequiredService<ContextOrchestrator>();
        orchestrator.HybridRetriever = _services.GetRequiredService<Klydis.Core.RAG.HybridRetriever>();
    }

    private async Task InitSkillsAsync()
    {
        var skillLibraryManager = _services.GetRequiredService<SkillLibraryManager>();
        await skillLibraryManager.InitializeAsync();
    }

    private Task FinalizeAsync() => Task.CompletedTask;
}
