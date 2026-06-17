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
    public partial class AudioToTextViewModel : ObservableObject
    {
        private readonly PythonDiffusionService _service = new();
        private CancellationTokenSource _cts;

        // ── Inputs ──────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
        private string _selectedAudioPath;

        [ObservableProperty] private string _selectedModel    = "openai/whisper-base";
        [ObservableProperty] private string _selectedLanguage = "";   // empty = auto

        // ── State ────────────────────────────────────────────────
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
        private bool   _isTranscribing;
        [ObservableProperty] private int    _progressValue;
        [ObservableProperty] private int    _progressMax = 1;
        [ObservableProperty] private string _statusText  = "Ready — select an audio file to transcribe";
        [ObservableProperty] private string _timeElapsed = "";
        [ObservableProperty] private string _transcriptText;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
        private bool   _hasDeps = false;
        [ObservableProperty] private string _depsStatus = "Checking dependencies…";

        public ObservableCollection<WhisperModelPreset> ModelPresets { get; } = new()
        {
            new("Whisper Base (~74MB, fast)",   "openai/whisper-base"),
            new("Whisper Small (~244MB)",       "openai/whisper-small"),
            new("Whisper Medium (~769MB)",      "openai/whisper-medium"),
            new("Custom (enter model ID below)", ""),
        };

        public ObservableCollection<LanguageOption> Languages { get; } = new()
        {
            new("Auto-detect",  ""),
            new("English",      "english"),
            new("Korean",       "korean"),
            new("Japanese",     "japanese"),
            new("Chinese",      "chinese"),
            new("French",       "french"),
            new("German",       "german"),
            new("Spanish",      "spanish"),
        };

        public AudioToTextViewModel()
        {
            _ = CheckDepsAsync();
        }

        [RelayCommand(CanExecute = nameof(CanTranscribe))]
        private async Task TranscribeAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsTranscribing = true;
            ProgressValue  = 0;
            ProgressMax    = 2;
            TranscriptText = null;
            StatusText     = "Starting…";
            var sw = Stopwatch.StartNew();

            try
            {
                var outputPath = Path.Combine(Path.GetTempPath(),
                    $"amuse_transcript_{Guid.NewGuid():N}.txt");

                var request = new GenerationRequest
                {
                    Mode      = "audio_to_text",
                    ModelId   = SelectedModel,
                    AudioPath = SelectedAudioPath,
                    Language  = SelectedLanguage ?? "",
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
                        if (File.Exists(path))
                            TranscriptText = File.ReadAllText(path).Trim();
                        StatusText = "Transcription complete";
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
                IsTranscribing = false;
                TimeElapsed    = $"{sw.Elapsed.TotalSeconds:F1}s";
            }
        }

        private bool CanTranscribe() =>
            !IsTranscribing && HasDeps && !string.IsNullOrWhiteSpace(SelectedAudioPath);

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void CopyText()
        {
            if (string.IsNullOrEmpty(TranscriptText)) return;
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo("xclip", "-selection clipboard")
                    {
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                    }
                };
                proc.Start();
                proc.StandardInput.Write(TranscriptText);
                proc.StandardInput.Close();
                proc.WaitForExit(2000);
                StatusText = "Copied to clipboard";
            }
            catch { StatusText = "Copy failed (xclip not installed?)"; }
        }

        [RelayCommand]
        private void SaveTranscript()
        {
            if (string.IsNullOrEmpty(TranscriptText)) return;
            var dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                $"amuse_transcript_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.WriteAllText(dest, TranscriptText);
            StatusText = $"Saved to {dest}";
        }

        [RelayCommand]
        private void SelectModel(WhisperModelPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.ModelId)) return;
            SelectedModel = preset.ModelId;
        }

        private async Task CheckDepsAsync()
        {
            DepsStatus = "Checking Python dependencies…";
            var (missing, error) = await _service.CheckDependenciesAsync();
            if (error != null)          { DepsStatus = error;                                HasDeps = false; }
            else if (missing.Count > 0) { DepsStatus = $"Missing: {string.Join(", ", missing)}"; HasDeps = false; }
            else                        { DepsStatus = "All dependencies installed";           HasDeps = true; }
            TranscribeCommand.NotifyCanExecuteChanged();
        }
    }

    public record WhisperModelPreset(string Label, string ModelId);
    public record LanguageOption(string Label, string Code);
}
