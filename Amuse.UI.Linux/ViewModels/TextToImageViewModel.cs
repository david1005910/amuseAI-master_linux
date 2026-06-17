using Amuse.UI.Linux.Services;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Amuse.UI.Linux.ViewModels
{
    public partial class TextToImageViewModel : ObservableObject
    {
        private readonly PythonDiffusionService _service = new();
        private CancellationTokenSource _cts;

        // ── Inputs ──────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private string _prompt = "";
        [ObservableProperty] private string _negativePrompt = "";
        [ObservableProperty] private int    _width  = 512;
        [ObservableProperty] private int    _height = 512;
        [ObservableProperty] private int    _steps  = 20;
        [ObservableProperty] private float  _guidanceScale = 7.5f;
        [ObservableProperty] private int    _seed   = -1;
        [ObservableProperty] private string _selectedModel    = "runwayml/stable-diffusion-v1-5";
        [ObservableProperty] private string _selectedScheduler = "DPMSolverMultistep";

        // ── State ────────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private bool   _isGenerating;
        [ObservableProperty] private int    _progressValue;
        [ObservableProperty] private int    _progressMax = 1;
        [ObservableProperty] private string _statusText  = "Ready";
        [ObservableProperty] private string _timeElapsed = "";
        [ObservableProperty] private Bitmap _resultImage;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private bool   _hasDeps = false;
        [ObservableProperty] private string _depsStatus = "Checking dependencies…";
        [ObservableProperty] private bool   _isInstalling;
        [ObservableProperty] private string _installLog = "";
        [ObservableProperty] private string _lastOutputPath;

        // ── Static lists ─────────────────────────────────────────
        public ObservableCollection<string> SchedulerOptions { get; } = new()
        {
            "DPMSolverMultistep", "EulerA", "Euler", "DDIM", "LMS", "PNDM"
        };

        public ObservableCollection<ModelPreset> ModelPresets { get; } = new()
        {
            new("SD 1.5 (recommended, ~4GB)",  "runwayml/stable-diffusion-v1-5",        false, 512),
            new("SD 2.1 (~5GB)",               "stabilityai/stable-diffusion-2-1",       false, 768),
            new("SDXL Base (~7GB)",             "stabilityai/stable-diffusion-xl-base-1.0", true, 1024),
            new("Custom (enter model ID below)","",                                       false, 512),
        };

        public ObservableCollection<int> CommonSizes { get; } = new() { 256, 384, 512, 640, 768, 1024 };

        public TextToImageViewModel()
        {
            _ = CheckDepsAsync();
        }

        // ── Commands ─────────────────────────────────────────────
        [RelayCommand(CanExecute = nameof(CanGenerate))]
        private async Task GenerateAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsGenerating  = true;
            ProgressValue = 0;
            ProgressMax   = Steps;
            StatusText    = "Starting…";
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var preset = FindSelectedPreset();
                var request = new GenerationRequest
                {
                    ModelId        = SelectedModel,
                    Prompt         = Prompt,
                    NegativePrompt = NegativePrompt,
                    Width          = Width,
                    Height         = Height,
                    Steps          = Steps,
                    GuidanceScale  = GuidanceScale,
                    Seed           = Seed,
                    Scheduler      = SelectedScheduler,
                    IsXL           = preset?.IsXL ?? false,
                };

                var progress = new Progress<GenerationProgress>(p =>
                {
                    if (p.Total > 0) ProgressMax = p.Total;
                    ProgressValue = p.Step;
                    if (!string.IsNullOrEmpty(p.Message))
                        StatusText = p.Message;
                    TimeElapsed = $"{sw.Elapsed.TotalSeconds:F1}s";
                });

                await _service.GenerateAsync(
                    request, progress,
                    seed => Seed = seed,
                    path =>
                    {
                        LastOutputPath = path;
                        LoadResultImage(path);
                        StatusText = "Done!";
                    },
                    _cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
            finally
            {
                IsGenerating  = false;
                TimeElapsed   = $"{sw.Elapsed.TotalSeconds:F1}s";
            }
        }

        private bool CanGenerate() =>
            !IsGenerating && HasDeps && !string.IsNullOrWhiteSpace(Prompt);

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void RandomSeed()
        {
            Seed = new Random().Next(0, int.MaxValue);
        }

        [RelayCommand]
        private void ResetSeed() => Seed = -1;

        [RelayCommand]
        private void SaveImage()
        {
            if (LastOutputPath == null || !File.Exists(LastOutputPath)) return;
            var dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                $"amuse_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.Copy(LastOutputPath, dest, overwrite: true);
            StatusText = $"Saved to {dest}";
        }

        [RelayCommand]
        private async Task InstallDepsAsync()
        {
            IsInstalling = true;
            InstallLog   = "";
            var log = new Progress<string>(s =>
            {
                InstallLog += s + "\n";
            });
            try
            {
                await _service.InstallDependenciesAsync(log, CancellationToken.None);
                await CheckDepsAsync();
            }
            catch (Exception ex)
            {
                InstallLog += $"\nError: {ex.Message}";
            }
            finally { IsInstalling = false; }
        }

        [RelayCommand]
        private void SelectPreset(ModelPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.ModelId)) return;
            SelectedModel = preset.ModelId;
            if (preset.DefaultSize > 0)
            {
                Width  = preset.DefaultSize;
                Height = preset.DefaultSize;
            }
        }

        // ── Helpers ──────────────────────────────────────────────
        private async Task CheckDepsAsync()
        {
            DepsStatus = "Checking Python dependencies…";
            var (missing, error) = await _service.CheckDependenciesAsync();
            if (error != null)
            {
                DepsStatus = error;
                HasDeps    = false;
            }
            else if (missing.Count > 0)
            {
                DepsStatus = $"Missing: {string.Join(", ", missing)}";
                HasDeps    = false;
            }
            else
            {
                DepsStatus = "All dependencies installed";
                HasDeps    = true;
            }
            GenerateCommand.NotifyCanExecuteChanged();
        }

        private void LoadResultImage(string path)
        {
            try
            {
                ResultImage?.Dispose();
                ResultImage = new Bitmap(path);
            }
            catch { }
        }

        private ModelPreset FindSelectedPreset()
        {
            foreach (var p in ModelPresets)
                if (p.ModelId == SelectedModel) return p;
            return null;
        }

        partial void OnSelectedModelChanged(string value)
        {
            // Update IsXL flag / size hint from preset
            var preset = FindSelectedPreset();
            if (preset != null && preset.DefaultSize > 0 && Width == Height)
            {
                Width  = preset.DefaultSize;
                Height = preset.DefaultSize;
            }
        }
    }

    public record ModelPreset(string Label, string ModelId, bool IsXL, int DefaultSize);
}
