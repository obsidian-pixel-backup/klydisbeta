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

            // Dynamically copy cuda12 native libraries to cuda13 folder if the host system uses CUDA 13+
            // and LLamaSharp's loader tries to load from cuda13, but the NuGet package only came with cuda12.
            // Also copies ggml-cpu.dll from avx2/ since the CUDA loader requires it as a dependency.
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var nativeDir = Path.Combine(baseDir, "runtimes", "win-x64", "native");
                var cuda12Dir = Path.Combine(nativeDir, "cuda12");
                var cuda13Dir = Path.Combine(nativeDir, "cuda13");
                var avx2Dir = Path.Combine(nativeDir, "avx2");

                if (Directory.Exists(cuda12Dir))
                {
                    var cuda12Files = Directory.GetFiles(cuda12Dir);
                    
                    // Critical DLLs that MUST be present for CUDA backend to load successfully.
                    // If ANY of these are missing, the loader will silently fall back to CPU (avx2).
                    var criticalDlls = new[] { "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cuda.dll" };

                    bool needsRecopy = false;
                    if (Directory.Exists(cuda13Dir))
                    {
                        var cuda13Files = Directory.GetFiles(cuda13Dir);
                        
                        // Check completeness: file count AND presence of all critical DLLs
                        var existingFileNames = cuda13Files.Select(f => Path.GetFileName(f).ToLowerInvariant()).ToArray();
                        bool hasCriticalFiles = criticalDlls.All(dll => existingFileNames.Contains(dll.ToLowerInvariant()));
                        
                        if (cuda13Files.Length < cuda12Files.Length || !hasCriticalFiles)
                        {
                            try { File.AppendAllText("llama_native.log", $"[CUDA] cuda13/ incomplete ({cuda13Files.Length} files, critical={hasCriticalFiles}). Deleting and re-copying...{Environment.NewLine}"); } catch {}
                            Directory.Delete(cuda13Dir, true);
                            needsRecopy = true;
                        }
                    }
                    else
                    {
                        needsRecopy = true;
                    }

                    if (needsRecopy)
                    {
                        Directory.CreateDirectory(cuda13Dir);

                        // Copy ALL CUDA 12 DLLs (ggml-base, ggml-cuda, ggml, llama, mtmd)
                        int copiedCount = 0;
                        foreach (var file in cuda12Files)
                        {
                            var fileName = Path.GetFileName(file);
                            var destFile = Path.Combine(cuda13Dir, fileName);
                            File.Copy(file, destFile, true);
                            copiedCount++;
                        }
                        try { File.AppendAllText("llama_native.log", $"[CUDA] Copied {copiedCount} files from cuda12/ to cuda13/{Environment.NewLine}"); } catch {}

                        // Also copy ggml-cpu.dll from avx2 — the CUDA loader requires it as a dependency
                        if (Directory.Exists(avx2Dir))
                        {
                            var cpuDll = Path.Combine(avx2Dir, "ggml-cpu.dll");
                            if (File.Exists(cpuDll))
                            {
                                File.Copy(cpuDll, Path.Combine(cuda13Dir, "ggml-cpu.dll"), true);
                                try { File.AppendAllText("llama_native.log", $"[CUDA] Copied ggml-cpu.dll from avx2/ to cuda13/{Environment.NewLine}"); } catch {}
                            }
                        }

                        // Verify all critical files are now present
                        var finalFiles = Directory.GetFiles(cuda13Dir).Select(f => Path.GetFileName(f).ToLowerInvariant()).ToArray();
                        var missingCritical = criticalDlls.Where(dll => !finalFiles.Contains(dll.ToLowerInvariant())).ToArray();
                        if (missingCritical.Length > 0)
                        {
                            try { File.AppendAllText("llama_native.log", $"[CUDA] WARNING: Still missing critical DLLs after copy: {string.Join(", ", missingCritical)}{Environment.NewLine}"); } catch {}
                        }
                        else
                        {
                            try { File.AppendAllText("llama_native.log", $"[CUDA] cuda13/ ready with {finalFiles.Length} files. All critical DLLs present.{Environment.NewLine}"); } catch {}
                        }
                    }
                    else
                    {
                        try { File.AppendAllText("llama_native.log", $"[CUDA] cuda13/ already complete. Skipping copy.{Environment.NewLine}"); } catch {}
                    }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText("llama_native.log", $"[CUDA] FATAL: Failed to copy CUDA 12 -> 13: {ex}{Environment.NewLine}"); } catch {}
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
                
                string sourceSubFolder = isCudaSupported ? "cuda13" : "avx2";
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
            app.InitializeComponent();
            Console.WriteLine("InitializeComponent done");
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
