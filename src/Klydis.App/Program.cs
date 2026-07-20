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
        // Enable high-speed GGML CUDA Tensor Core decoding kernels and universal FlashAttention
        Environment.SetEnvironmentVariable("GGML_CUDA_FORCE_CUBLAS", "1");
        Environment.SetEnvironmentVariable("GGML_CUDA_FA_ALL_QUANTS", "1");
        Environment.SetEnvironmentVariable("GGML_CUDA_DMMV_F16", "1");

        // Force LLamaSharp to load the CUDA backend globally before ANY native calls are made.
        // This ensures NVIDIA GPUs will properly utilize CUDA offloading instead of silently falling back to CPU.
        LLama.Native.NativeLibraryConfig.All.WithCuda();

        try
        {
            // Configure process PATH to include CUDA toolkit bin directories.
            // The ggml-cuda.dll needs CUDA runtime DLLs (cublas64_XX.dll, cublasLt64_XX.dll, cudart64_XX.dll)
            // which are located in the CUDA toolkit installation. We scan for all installed versions
            // and add them to PATH so the DLL loader can find them.
            var cudaBasePath = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
            if (Directory.Exists(cudaBasePath))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var pathParts = pathEnv.Split(Path.PathSeparator).ToList();
                
                foreach (var versionDir in Directory.GetDirectories(cudaBasePath))
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
            var nvidiaGAssistPath = @"C:\ProgramData\NVIDIA Corporation\NVIDIA App\G-Assist";
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
            var nvidiaCefPath = @"C:\Program Files\NVIDIA Corporation\NVIDIA app\CEF";
            if (Directory.Exists(nvidiaCefPath))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!pathEnv.Split(Path.PathSeparator).Contains(nvidiaCefPath, StringComparer.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", nvidiaCefPath + Path.PathSeparator + pathEnv);
                }
            }

            // Copy the active backend's DLLs directly to the application execution root folder.
            // In llama.cpp (b1000+), dynamic backend libraries (like ggml-cuda.dll, ggml-cpu-*.dll)
            // MUST reside in the same folder as llama.dll / the main executable to be loaded successfully.
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var nativeDir = Path.Combine(baseDir, "runtimes", "win-x64", "native");
                
                // Determine CUDA support
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
                    var sourceFiles = Directory.GetFiles(sourcePath);
                    bool needsRootCopy = false;
                    foreach (var srcFile in sourceFiles)
                    {
                        var fileName = Path.GetFileName(srcFile);
                        var destFile = Path.Combine(baseDir, fileName);
                        if (!File.Exists(destFile) || new FileInfo(destFile).Length != new FileInfo(srcFile).Length)
                        {
                            needsRootCopy = true;
                            break;
                        }
                    }

                    if (needsRootCopy)
                    {
                        try { File.AppendAllText("llama_native.log", $"[ROOT_COPY] Deploying self-contained backend from {sourceSubFolder} to app root...{Environment.NewLine}"); } catch {}
                        int copiedCount = 0;
                        foreach (var file in sourceFiles)
                        {
                            var fileName = Path.GetFileName(file);
                            var destFile = Path.Combine(baseDir, fileName);
                            try
                            {
                                File.Copy(file, destFile, true);
                                copiedCount++;
                            }
                            catch (Exception copyEx)
                            {
                                // If file is locked/in-use, ignore as it means it's already loaded and working
                                try { File.AppendAllText("llama_native.log", $"[ROOT_COPY] Note: could not copy {fileName} ({copyEx.Message}){Environment.NewLine}"); } catch {}
                            }
                        }
                        try { File.AppendAllText("llama_native.log", $"[ROOT_COPY] Done. Copied {copiedCount} files to app root.{Environment.NewLine}"); } catch {}
                    }
                    else
                    {
                        try { File.AppendAllText("llama_native.log", $"[ROOT_COPY] Root files already complete. Skipping copy.{Environment.NewLine}"); } catch {}
                    }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText("llama_native.log", $"[ROOT_COPY] FATAL: Failed root copy: {ex}{Environment.NewLine}"); } catch {}
            }
            
            // Configure LLamaSharp backend library preferences before anything else.
            // Prefer CUDA first, then Vulkan as fallback for non-NVIDIA GPUs.
            NativeLibraryConfig.All
                .WithCuda()
                .WithVulkan()
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
