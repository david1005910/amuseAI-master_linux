using Amuse.UI.Linux.Services;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Amuse.UI.Linux.ViewModels
{
    public partial class ImageToImageViewModel : ObservableObject
    {
        private readonly PythonDiffusionService _service = new();
        private CancellationTokenSource _cts;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private string _prompt = "";
        [ObservableProperty] private string _negativePrompt = "";
        [ObservableProperty] private string _selectedModel = "runwayml/stable-diffusion-v1-5";
        [ObservableProperty] private int    _steps         = 20;
        [ObservableProperty] private float  _guidanceScale = 7.5f;
        [ObservableProperty] private float  _strength      = 0.75f;
        [ObservableProperty] private int    _seed          = -1;
        [ObservableProperty] private string _scheduler     = "DPMSolverMultistep";
        [ObservableProperty] private bool   _isXL          = false;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private string _inputImagePath = "";
        [ObservableProperty] private Bitmap _inputImagePreview;

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
        [ObservableProperty] private string _depsStatus  = "Checking dependencies…";
        [ObservableProperty] private bool   _isInstalling;
        [ObservableProperty] private string _installLog  = "";

        public ObservableCollection<ModelPreset> ModelPresets { get; } = new()
        {
            new("SD 1.5 (recommended, ~4GB)", "runwayml/stable-diffusion-v1-5", false, 512),
            new("SD 2.1",                     "stabilityai/stable-diffusion-2-1", false, 768),
            new("Custom (enter model ID below)", "", false, 512),
        };

        public string[] SchedulerOptions { get; } =
            { "DPMSolverMultistep", "EulerA", "Euler", "DDIM", "LMS", "PNDM" };

        public ImageToImageViewModel()
        {
            _ = CheckDepsAsync();
        }

        [RelayCommand(CanExecute = nameof(CanGenerate))]
        private async Task GenerateAsync()
        {
            _cts?.Cancel(); _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsGenerating = true; ProgressValue = 0; ProgressMax = Steps;
            StatusText = "Starting…"; ResultImage = null;
            var sw = Stopwatch.StartNew();

            try
            {
                var request = new GenerationRequest
                {
                    Mode           = "image_to_image",
                    ModelId        = SelectedModel,
                    Prompt         = Prompt,
                    NegativePrompt = NegativePrompt,
                    InputImagePath = InputImagePath,
                    Strength       = Strength,
                    Steps          = Steps,
                    GuidanceScale  = GuidanceScale,
                    Seed           = Seed,
                    Scheduler      = Scheduler,
                    IsXL           = IsXL,
                };

                var progress = new Progress<GenerationProgress>(p =>
                {
                    if (p.Total > 0) ProgressMax = p.Total;
                    ProgressValue = p.Step;
                    if (!string.IsNullOrEmpty(p.Message)) StatusText = p.Message;
                    TimeElapsed = $"{sw.Elapsed.TotalSeconds:F1}s";
                });

                await _service.GenerateAsync(request, progress,
                    seed => { Seed = seed; },
                    path => { ResultImage = new Bitmap(path); StatusText = "Done!"; },
                    _cts.Token);
            }
            catch (OperationCanceledException) { StatusText = "Cancelled"; }
            catch (Exception ex)               { StatusText = $"Error: {ex.Message}"; }
            finally
            {
                IsGenerating = false;
                TimeElapsed  = $"{sw.Elapsed.TotalSeconds:F1}s";
            }
        }

        private bool CanGenerate() =>
            !IsGenerating && HasDeps &&
            !string.IsNullOrWhiteSpace(Prompt) &&
            !string.IsNullOrWhiteSpace(InputImagePath);

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void RandomSeed() => Seed = new Random().Next(0, int.MaxValue);

        [RelayCommand]
        private void ResetSeed() => Seed = -1;

        [RelayCommand]
        private void SaveImage()
        {
            if (ResultImage == null) return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AmuseAI");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"img2img_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            ResultImage.Save(dest);
            StatusText = $"Saved to {dest}";
        }

        public void SetInputImage(string path)
        {
            InputImagePath = path;
            if (File.Exists(path))
            {
                try { InputImagePreview = new Bitmap(path); } catch { InputImagePreview = null; }
            }
        }

        [RelayCommand]
        private async Task InstallDepsAsync()
        {
            IsInstalling = true; InstallLog = "";
            var log = new Progress<string>(s => InstallLog += s + "\n");
            try
            {
                await _service.InstallDependenciesAsync(log, CancellationToken.None);
                await CheckDepsAsync();
            }
            catch (Exception ex) { InstallLog += $"\nError: {ex.Message}"; }
            finally { IsInstalling = false; }
        }

        private async Task CheckDepsAsync()
        {
            DepsStatus = "Checking Python dependencies…";
            var (missing, error) = await _service.CheckDependenciesAsync();
            if (error != null)          { DepsStatus = error;                                    HasDeps = false; }
            else if (missing.Count > 0) { DepsStatus = $"Missing: {string.Join(", ", missing)}"; HasDeps = false; }
            else                        { DepsStatus = "All dependencies installed";              HasDeps = true; }
            GenerateCommand.NotifyCanExecuteChanged();
        }
    }
}
