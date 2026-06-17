using Amuse.UI.Linux.Services;
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
    public partial class TextToAudioViewModel : ObservableObject
    {
        private readonly PythonDiffusionService _service = new();
        private CancellationTokenSource _cts;

        // ── Inputs ──────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private string _prompt = "";
        [ObservableProperty] private float  _duration     = 10f;
        [ObservableProperty] private string _selectedModel = "facebook/musicgen-small";

        // ── State ────────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private bool   _isGenerating;
        [ObservableProperty] private int    _progressValue;
        [ObservableProperty] private int    _progressMax = 1;
        [ObservableProperty] private string _statusText  = "Ready";
        [ObservableProperty] private string _timeElapsed = "";
        [ObservableProperty] private string _lastOutputPath;
        [ObservableProperty] private string _audioInfo;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
        private bool   _hasDeps = false;
        [ObservableProperty] private string _depsStatus  = "Checking dependencies…";
        [ObservableProperty] private bool   _isInstalling;
        [ObservableProperty] private string _installLog  = "";

        public ObservableCollection<AudioModelPreset> ModelPresets { get; } = new()
        {
            new("MusicGen Small (~600MB)",  "facebook/musicgen-small"),
            new("MusicGen Medium (~1.5GB)", "facebook/musicgen-medium"),
            new("AudioGen Medium (~1.5GB)", "facebook/audiogen-medium"),
            new("Custom (enter model ID below)", ""),
        };

        public TextToAudioViewModel()
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
            ProgressMax   = 2;
            StatusText    = "Starting…";
            AudioInfo     = null;
            var sw = Stopwatch.StartNew();

            try
            {
                var request = new GenerationRequest
                {
                    Mode      = "audio",
                    ModelId   = SelectedModel,
                    Prompt    = Prompt,
                    Duration  = Duration,
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
                        AudioInfo = $"WAV · {Duration:F0}s requested";
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
                IsGenerating = false;
                TimeElapsed  = $"{sw.Elapsed.TotalSeconds:F1}s";
            }
        }

        private bool CanGenerate() =>
            !IsGenerating && HasDeps && !string.IsNullOrWhiteSpace(Prompt);

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void PlayAudio()
        {
            if (LastOutputPath == null || !File.Exists(LastOutputPath)) return;
            try
            {
                // Try aplay (ALSA) first, fall back to xdg-open
                var aplay = new ProcessStartInfo("aplay", $"\"{LastOutputPath}\"")
                    { UseShellExecute = false };
                Process.Start(aplay);
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("xdg-open", LastOutputPath) { UseShellExecute = false }); }
                catch { }
            }
        }

        [RelayCommand]
        private void SaveAudio()
        {
            if (LastOutputPath == null || !File.Exists(LastOutputPath)) return;
            var musicDir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "Music");
            Directory.CreateDirectory(musicDir);
            var dest = Path.Combine(musicDir, $"amuse_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
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
        private void SelectModel(AudioModelPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.ModelId)) return;
            SelectedModel = preset.ModelId;
        }

        private async Task CheckDepsAsync()
        {
            DepsStatus = "Checking Python dependencies…";
            var (missing, error) = await _service.CheckDependenciesAsync();
            if (error != null)          { DepsStatus = error;                              HasDeps = false; }
            else if (missing.Count > 0) { DepsStatus = $"Missing: {string.Join(", ", missing)}"; HasDeps = false; }
            else                        { DepsStatus = "All dependencies installed";        HasDeps = true; }
            GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    public record AudioModelPreset(string Label, string ModelId);
}
