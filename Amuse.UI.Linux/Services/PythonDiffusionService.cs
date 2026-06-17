using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Amuse.UI.Linux.Services
{
    public class PythonDiffusionService
    {
        private static readonly string[] PythonCandidates =
            { "python3", "python", "/usr/bin/python3", "/usr/local/bin/python3" };

        private Process _activeProcess;
        private readonly object _processLock = new object();

        public static string FindPython()
        {
            foreach (var candidate in PythonCandidates)
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    });
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0) return candidate;
                }
                catch { }
            }
            return null;
        }

        public static string GetScriptPath()
        {
            // Look next to the binary, then relative to source tree
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backend", "generate.py"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Backend", "generate.py"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return Path.GetFullPath(c);
            return candidates[0];
        }

        public async Task<(List<string> missing, string error)> CheckDependenciesAsync()
        {
            var python = FindPython();
            if (python == null)
                return (null, "Python 3 not found. Install Python 3 to continue.");

            var check = await RunPythonAsync(python,
                "-c \"import diffusers,torch,transformers,accelerate,PIL; print('ok')\"",
                CancellationToken.None);
            if (check.stdout.Trim() == "ok")
                return (new List<string>(), null);

            var missing = new List<string>();
            foreach (var pkg in new[] { ("diffusers", "diffusers"), ("torch", "torch"), ("transformers", "transformers"), ("accelerate", "accelerate"), ("PIL", "Pillow") })
            {
                var r = await RunPythonAsync(python, $"-c \"import {pkg.Item1}\"", CancellationToken.None);
                if (r.exitCode != 0) missing.Add(pkg.Item2);
            }
            return (missing, null);
        }

        public async Task InstallDependenciesAsync(IProgress<string> progress, CancellationToken ct)
        {
            var python = FindPython();
            if (python == null) throw new Exception("Python not found");

            var packages = "torch torchvision --index-url https://download.pytorch.org/whl/cpu diffusers transformers accelerate Pillow";
            progress.Report("Installing PyTorch (CPU) and diffusers…");

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"-m pip install --upgrade {packages}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            _ = Task.Run(async () =>
            {
                string line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    progress.Report(line);
            }, ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new Exception($"pip install failed (exit {proc.ExitCode})");
        }

        public async Task GenerateAsync(
            GenerationRequest request,
            IProgress<GenerationProgress> progress,
            Action<int> onSeedGenerated,
            Action<string> onComplete,
            CancellationToken ct)
        {
            var python = FindPython() ?? throw new Exception("Python 3 not found");
            var script = GetScriptPath();
            if (!File.Exists(script))
                throw new FileNotFoundException($"Backend script not found: {script}");

            var outputPath = Path.Combine(Path.GetTempPath(), $"amuse_{Guid.NewGuid():N}.png");

            var config = JsonSerializer.Serialize(new
            {
                model_id        = request.ModelId,
                prompt          = request.Prompt,
                negative_prompt = request.NegativePrompt,
                width           = request.Width,
                height          = request.Height,
                steps           = request.Steps,
                guidance_scale  = request.GuidanceScale,
                seed            = request.Seed,
                scheduler       = request.Scheduler,
                is_xl           = request.IsXL,
                output_path     = outputPath,
            });

            var psi = new ProcessStartInfo
            {
                FileName               = python,
                Arguments              = $"\"{script}\"",
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            // Force CPU-only: AMD Cezanne iGPU (Vega) segfaults ROCm 7.2 at tensor init.
            // Use empty string — ROCm does not accept "-1" like CUDA does.
            psi.EnvironmentVariables["CUDA_VISIBLE_DEVICES"]  = "";
            psi.EnvironmentVariables["HIP_VISIBLE_DEVICES"]   = "";
            psi.EnvironmentVariables["ROCR_VISIBLE_DEVICES"]  = "";

            // Kill any process left over from a previous run
            KillActiveProcess();

            var proc = Process.Start(psi);
            lock (_processLock) { _activeProcess = proc; }

            // Kill the subprocess when the cancellation token fires
            using var killOnCancel = ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            // Capture stderr in background (unlinked from ct so we can read it after cancel)
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await proc.StandardInput.WriteLineAsync(config);
                proc.StandardInput.Close();

                string line;
                string resultPath = null;

                while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var type = root.GetProperty("type").GetString();

                        switch (type)
                        {
                            case "progress":
                                progress.Report(new GenerationProgress
                                {
                                    Step    = root.TryGetProperty("step",    out var s) ? s.GetInt32()  : 0,
                                    Total   = root.TryGetProperty("total",   out var t) ? t.GetInt32()  : request.Steps,
                                    Message = root.TryGetProperty("message", out var m) ? m.GetString() : "",
                                });
                                break;

                            case "seed":
                                if (root.TryGetProperty("value", out var sv))
                                    onSeedGenerated?.Invoke(sv.GetInt32());
                                break;

                            case "info":
                                if (root.TryGetProperty("message", out var im))
                                    progress.Report(new GenerationProgress { Message = im.GetString() });
                                break;

                            case "missing_deps":
                                throw new Exception("Missing Python dependencies. Install them in Settings.");

                            case "complete":
                                if (root.TryGetProperty("output_path", out var op))
                                    resultPath = op.GetString();
                                break;

                            case "error":
                                var msg = root.TryGetProperty("message", out var em) ? em.GetString() : "Unknown error";
                                throw new Exception(msg);
                        }
                    }
                    catch (JsonException) { /* non-JSON line, ignore */ }
                }

                ct.ThrowIfCancellationRequested();

                await proc.WaitForExitAsync(CancellationToken.None);

                if (proc.ExitCode != 0 && resultPath == null)
                {
                    var stderr = await stderrTask;
                    throw new Exception($"Generation failed (exit {proc.ExitCode}):\n{stderr}");
                }

                if (resultPath != null && File.Exists(resultPath))
                    onComplete?.Invoke(resultPath);
                else
                    throw new Exception("Generation completed but output file was not found.");
            }
            finally
            {
                lock (_processLock)
                {
                    if (ReferenceEquals(_activeProcess, proc))
                        _activeProcess = null;
                }
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
            }
        }

        private void KillActiveProcess()
        {
            lock (_processLock)
            {
                if (_activeProcess == null) return;
                try { _activeProcess.Kill(entireProcessTree: true); } catch { }
                try { _activeProcess.Dispose(); } catch { }
                _activeProcess = null;
            }
        }

        private static async Task<(int exitCode, string stdout)> RunPythonAsync(
            string python, string args, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = python,
                    Arguments              = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                };
                using var p = Process.Start(psi);
                var stdout = await p.StandardOutput.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                return (p.ExitCode, stdout);
            }
            catch { return (-1, ""); }
        }
    }
}
