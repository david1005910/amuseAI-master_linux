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
    public partial class TextToVideoViewModel : ObservableObject
    {
        private readonly PythonDiffusionService _service = new();
        private CancellationTokenSource _cts;

        // ── Inputs ──────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private string _prompt = "";
        [ObservableProperty] private int    _steps     = 25;
        [ObservableProperty] private int    _numFrames = 16;
        [ObservableProperty] private int    _fps       = 8;
        [ObservableProperty] private string _selectedModel = "damo-vilab/text-to-video-ms-1.7b";

        // ── State ────────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private bool   _isGenerating;
        [ObservableProperty] private int    _progressValue;
        [ObservableProperty] private int    _progressMax = 1;
        [ObservableProperty] private string _statusText  = "Ready";
        [ObservableProperty] private string _timeElapsed = "";
        [ObservableProperty] private Bitmap _previewFrame;
        [ObservableProperty] private string _lastOutputPath;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private bool   _hasDeps = false;
        [ObservableProperty] private string _depsStatus  = "Checking dependencies…";
        [ObservableProperty] private bool   _isInstalling;
        [ObservableProperty] private string _installLog  = "";

        public ObservableCollection<VideoModelPreset> ModelPresets { get; } = new()
        {
            new("ModelScope 1.7B (~7GB)", "damo-vilab/text-to-video-ms-1.7b"),
            new("Custom (enter model ID below)", ""),
        };

        public ObservableCollection<int> FrameOptions { get; } = new() { 8, 16, 24, 32 };
        public ObservableCollection<int> FpsOptions   { get; } = new() { 4, 8, 12, 16 };

        public TextToVideoViewModel()
        {
            _ = CheckDepsAsync();
        }

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
            var sw = Stopwatch.StartNew();

            try
            {
                var outputPath = Path.Combine(Path.GetTempPath(),
                    $"amuse_video_{Guid.NewGuid():N}.gif");

                var request = new GenerationRequest
                {
                    Mode       = "video",
                    ModelId    = SelectedModel,
                    Prompt     = Prompt,
                    Steps      = Steps,
                    NumFrames  = NumFrames,
                    Fps        = Fps,
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
                    seed => { },
                    path =>
                    {
                        LastOutputPath = path;
                        var preview = path.Replace(".gif", "_preview.png");
                        if (File.Exists(preview))
                            LoadPreview(preview);
                        StatusText = $"Done! {NumFrames} frames";
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
                IsGenerating = false;
                TimeElapsed  = $"{sw.Elapsed.TotalSeconds:F1}s";
            }
        }

        private bool CanGenerate() =>
            !IsGenerating && HasDeps && !string.IsNullOrWhiteSpace(Prompt);

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void OpenVideo()
        {
            if (LastOutputPath == null || !File.Exists(LastOutputPath)) return;
            try { Process.Start(new ProcessStartInfo("xdg-open", LastOutputPath) { UseShellExecute = false }); }
            catch { }
        }

        [RelayCommand]
        private void SaveVideo()
        {
            if (LastOutputPath == null || !File.Exists(LastOutputPath)) return;
            var dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                $"amuse_{DateTime.Now:yyyyMMdd_HHmmss}.gif");
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(LastOutputPath, dest, overwrite: true);
            StatusText = $"Saved to {dest}";
        }

        [RelayCommand]
        private async Task InstallDepsAsync()
        {
            IsInstalling = true;
            InstallLog   = "";
            var log = new Progress<string>(s => InstallLog += s + "\n");
            try
            {
                await _service.InstallDependenciesAsync(log, CancellationToken.None);
                await CheckDepsAsync();
            }
            catch (Exception ex) { InstallLog += $"\nError: {ex.Message}"; }
            finally { IsInstalling = false; }
        }

        [RelayCommand]
        private void SelectModel(VideoModelPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.ModelId)) return;
            SelectedModel = preset.ModelId;
        }

        private async Task CheckDepsAsync()
        {
            DepsStatus = "Checking Python dependencies…";
            var (missing, error) = await _service.CheckDependenciesAsync();
            if (error != null)      { DepsStatus = error;                              HasDeps = false; }
            else if (missing.Count > 0) { DepsStatus = $"Missing: {string.Join(", ", missing)}"; HasDeps = false; }
            else                    { DepsStatus = "All dependencies installed";        HasDeps = true; }
            GenerateCommand.NotifyCanExecuteChanged();
        }

        private void LoadPreview(string path)
        {
            try
            {
                PreviewFrame?.Dispose();
                PreviewFrame = new Bitmap(path);
            }
            catch { }
        }
    }

    public record VideoModelPreset(string Label, string ModelId);
}
