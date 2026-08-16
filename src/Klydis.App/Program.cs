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
        // Session banner (PID, versions, timestamp) so multi-session logs are separable and
        // a crash is distinguishable from a clean shutdown.
        Klydis.Core.Diagnostics.CrashLog.WriteSessionBanner();

        // NOTE: The legacy GGML_CUDA_FORCE_CUBLAS / GGML_CUDA_DMMV_F16 env vars were removed:
        // on modern llama.cpp builds they force the cuBLAS / f16 DMMV kernels, which are
        // measurably slower than the default native CUDA kernels on most models. Leave the
        // defaults alone unless benchmarking proves otherwise on your specific GPU.

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
                            Klydis.Core.Diagnostics.KlydisLog.AppendNativeLog($"[CUDA] Added to PATH: {candidatePath}{Environment.NewLine}");
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

            // NOTE: the native llama.cpp engine sync + auto-update no longer runs here — it
            // used to BLOCK before any window existed (the multi-minute blank startup). It now
            // runs in the background behind the splash window via StartupSequence, which reports
            // its progress step-by-step.

            Console.WriteLine("Program.Main started");
            var app = new App();
            Console.WriteLine("App instantiated");
            app.Run();
            Console.WriteLine("Run finished");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL ERROR: " + ex.ToString());
            Klydis.Core.Diagnostics.CrashLog.WriteFatal(ex, "Program.Main");
        }
    }
}
