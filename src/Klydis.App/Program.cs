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
                        var cuda13Path = Path.Combine(nativeDir, "cuda13");
                        var cuda12Path = Path.Combine(nativeDir, "cuda12");

                        // If cuda13 directory is missing but cuda12 exists, replicate cuda12 to cuda13
                        // to satisfy LLamaSharp's native CUDA 13 loader resolution.
                        if (!Directory.Exists(cuda13Path) && Directory.Exists(cuda12Path))
                        {
                            try
                            {
                                Directory.CreateDirectory(cuda13Path);
                                foreach (var file in Directory.GetFiles(cuda12Path))
                                {
                                    File.Copy(file, Path.Combine(cuda13Path, Path.GetFileName(file)), overwrite: true);
                                }
                            }
                            catch {}
                        }

                        if (Directory.Exists(cuda13Path))
                            sourceSubFolder = "cuda13";
                        else if (Directory.Exists(cuda12Path))
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
                                File.Copy(file, destFile, overwrite: true);
                                try { File.AppendAllText("llama_native.log", $"[NATIVE_SYNC] Synced CUDA binary: {fileName}{Environment.NewLine}"); } catch {}
                            }
                            catch (Exception copyEx) 
                            {
                                try { File.AppendAllText("llama_native.log", $"[NATIVE_SYNC] Skip locked file {fileName}: {copyEx.Message}{Environment.NewLine}"); } catch {}
                            }
                        }
                    }
                }
            }
            catch (Exception nativeEx)
            {
                try { File.AppendAllText("llama_native.log", $"[NATIVE_SYNC] Error in native engine sync: {nativeEx.Message}{Environment.NewLine}"); } catch {}
            }
            
            // Configure LLamaSharp backend library preferences using process-wide idempotent guard.
            Klydis.Core.Inference.NativeEngineManager.EnsureNativeLibraryConfigured();

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
