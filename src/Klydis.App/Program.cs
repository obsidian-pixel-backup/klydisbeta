using System;
using System.IO;
using System.Linq;
using LLama.Native;

namespace Klydis.App;

public class Program
{
    [STAThread]
    public static void Main()
    {
        // Configure process environment
        Environment.SetEnvironmentVariable("GGML_CUDA_FORCE_CUBLAS", "1");
        Environment.SetEnvironmentVariable("GGML_CUDA_DMMV_F16", "1");

        // Force LLamaSharp to load the CUDA backend globally before ANY native calls are made.
        // This ensures NVIDIA GPUs will properly utilize CUDA offloading instead of silently falling back to CPU.
        LLama.Native.NativeLibraryConfig.All.WithCuda();

        try
        {
            // Configure process PATH to include CUDA toolkit bin directories dynamically.
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            var envCudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            var cudaBasePath = !string.IsNullOrWhiteSpace(envCudaPath) && Directory.Exists(envCudaPath) 
                ? envCudaPath 
                : Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");

            if (Directory.Exists(cudaBasePath))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var pathParts = pathEnv.Split(Path.PathSeparator).ToList();
                
                var searchDirs = Directory.Exists(Path.Combine(cudaBasePath, "bin")) 
                    ? new[] { cudaBasePath } 
                    : Directory.GetDirectories(cudaBasePath);

                foreach (var versionDir in searchDirs)
                {
                    // Check both bin/ and bin/x64/ — CUDA 13+ uses bin/x64/
                    var binX64 = Path.Combine(versionDir, "bin", "x64");
                    var bin = Path.Combine(versionDir, "bin");
                    
                    foreach (var candidatePath in new[] { binX64, bin })
                    {
                        if (Directory.Exists(candidatePath) && !pathParts.Contains(candidatePath, StringComparer.OrdinalIgnoreCase))
                        {
                            pathParts.Insert(0, candidatePath);
                            try { File.AppendAllText("llama_native.log", $"[CUDA] Added to PATH: {candidatePath}{Environment.NewLine}"); } catch {}
                        }
                    }
                }
                
                Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, pathParts));
            }
            
            // Also check NVIDIA G-Assist / NVIDIA App path for CUDA 12 runtime DLLs
            var nvidiaGAssistPath = Path.Combine(programData, "NVIDIA Corporation", "NVIDIA App", "G-Assist");
            if (Directory.Exists(nvidiaGAssistPath))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!pathEnv.Split(Path.PathSeparator).Contains(nvidiaGAssistPath, StringComparer.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", nvidiaGAssistPath + Path.PathSeparator + pathEnv);
                }

                // Copy required CUDA 12 runtime DLLs to app root if missing
                var appRoot = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var cudaDll in new[] { "cudart64_12.dll", "cublasLt64_12.dll", "cublas64_12.dll", "nvJitLink_120_0.dll" })
                {
                    var srcDll = Path.Combine(nvidiaGAssistPath, cudaDll);
                    var destDll = Path.Combine(appRoot, cudaDll);
                    if (File.Exists(srcDll) && !File.Exists(destDll))
                    {
                        try { File.Copy(srcDll, destDll, overwrite: true); } catch { }
                    }
                }
            }
            
            // Also check the legacy NVIDIA CEF path (older NVIDIA App installs)
            var nvidiaCefPath = Path.Combine(programFiles, "NVIDIA Corporation", "NVIDIA app", "CEF");
            if (Directory.Exists(nvidiaCefPath))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!pathEnv.Split(Path.PathSeparator).Contains(nvidiaCefPath, StringComparer.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", nvidiaCefPath + Path.PathSeparator + pathEnv);
                }
            }

            // Sync updated native llama.dll binaries from %USERPROFILE%\.klydis\native\ if present.
            try
            {
                Klydis.Core.Inference.NativeEngineManager.EnsureDirectoriesExist();

                if (Klydis.Core.Inference.NativeEngineManager.HasCustomNativeEngine())
                {
                    int customCopied = Klydis.Core.Inference.NativeEngineManager.SyncCustomNativeEngine();
                    try { File.AppendAllText("llama_native.log", $"[CUSTOM_NATIVE] Synced {customCopied} updated native DLLs from .klydis\\native\\ to root and runtime folders.{Environment.NewLine}"); } catch {}
                }
                else
                {
                    // Fallback to bundled runtimes if offline and no custom engine exists
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var nativeDir = Path.Combine(baseDir, "runtimes", "win-x64", "native");
                    
                    var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    bool isCudaSupported = File.Exists(Path.Combine(system32, "nvcuda.dll"));
                    
                    string sourceSubFolder = "avx2";
                    if (isCudaSupported)
                    {
                        if (Directory.Exists(Path.Combine(nativeDir, "cuda13")))
                            sourceSubFolder = "cuda13";
                        else if (Directory.Exists(Path.Combine(nativeDir, "cuda12")))
                            sourceSubFolder = "cuda12";
                        else if (Directory.Exists(Path.Combine(nativeDir, "cuda11")))
                            sourceSubFolder = "cuda11";
                    }
                    
                    var sourcePath = Path.Combine(nativeDir, sourceSubFolder);
                    if (Directory.Exists(sourcePath))
                    {
                        foreach (var file in Directory.GetFiles(sourcePath))
                        {
                            var fileName = Path.GetFileName(file);
                            var destFile = Path.Combine(baseDir, fileName);
                            try
                            {
                                if (!File.Exists(destFile))
                                {
                                    File.Copy(file, destFile, true);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception nativeEx)
            {
                try { File.AppendAllText("llama_native.log", $"[NATIVE_SYNC] Error in native engine sync: {nativeEx.Message}{Environment.NewLine}"); } catch {}
            }
            
            // Configure LLamaSharp backend library preferences before anything else.
            // Prefer CUDA first, then Vulkan as fallback for non-NVIDIA GPUs.
            NativeLibraryConfig.All
                .WithCuda()
                .WithLogCallback((level, message) => {
                    try
                    {
                        File.AppendAllText("llama_native.log", $"[{level}] {message}{Environment.NewLine}");
                    }
                    catch { /* Ignore logging errors if file is locked */ }
                });

            Console.WriteLine("Program.Main started");
            var app = new App();
            Console.WriteLine("App instantiated");
            app.Run();
            Console.WriteLine("Run finished");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL ERROR: " + ex.ToString());
            try
            {
                File.AppendAllText("llama_native.log", $"FATAL ERROR: {ex}{Environment.NewLine}");
            }
            catch {}
        }
    }
}
