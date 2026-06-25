# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AmuseAI is a local AI generation platform supporting image, video, audio, and text pipelines via NVIDIA CUDA and AMD ROCm backends. The primary target is Windows (WPF, .NET 10.0, x64-only), with a cross-platform Linux port (`Amuse.UI.Linux`) built on Avalonia that calls Python directly.

## Build Commands

```bash
# Full solution build
dotnet build Amuse.sln -c Release -p:Platform=x64

# Build specific project
dotnet build Amuse.App/Amuse.App.csproj -c Release -p:Platform=x64

# Build Linux UI (cross-platform, no Platform flag needed)
dotnet build Amuse.UI.Linux/Amuse.UI.Linux.csproj

# Run Linux UI
dotnet run --project Amuse.UI.Linux/Amuse.UI.Linux.csproj

# Publish installer build
dotnet publish Amuse.App/Amuse.App.csproj -c Release_Installer -p:Platform=x64

# Debug build (uses local TensorStack project references)
dotnet build Amuse.sln -c Debug -p:Platform=x64
```

**No automated test suite exists in this repository.** Manual testing requires running the app with a capable GPU.

Build configurations:
- `Debug` — links against local TensorStack project references (not NuGet packages); requires a sibling TensorStack checkout
- `Release` — links against TensorStack NuGet packages
- `Release_Installer` — same as Release but sets app data path to `%LOCALAPPDATA%\Amuse`; dev mode stores data in the binary output directory

Version is defined globally in `Directory.Build.props` (`Version`, `PackageVersion`).

## Architecture

### Multi-Process Design (Windows)

The Windows app runs as three processes communicating via named pipes:

```
Amuse.App (WPF, main process)
    │
    ├── AmuseHost.PyTorch.exe  (subprocess — PyTorch/HuggingFace pipelines)
    └── AmuseHost.Onnx.exe     (subprocess — ONNX Runtime pipelines)
```

Each subprocess connection uses **three named pipes**: `CommandChannel`, `PipelineChannel`, `ProgressChannel`. The IPC protocol is defined in `Amuse.Common/`: `PipelineClient.cs` (main-app side), `PipelineServer.cs` (host side), `ProcessHandler.cs` (lifecycle).

Host processes are spawned on demand by `PyTorchDiffusion.cs` or `OnnxDiffusion.cs` in `Amuse.App/Backend/`, both implementing `IDiffusionRuntime` (`Amuse.App/Backend/IDiffusionRuntime.cs`).

### Project Breakdown

| Project | Role |
|---|---|
| `Amuse.App` | WPF UI (Windows), services, settings, backend runtime selection |
| `Amuse.UI.Linux` | Avalonia UI (Linux/cross-platform), standalone Python backend via `Backend/generate.py` |
| `Amuse.Common` | Shared IPC types, enums, message/option models |
| `Amuse.Host.PyTorch` | Standalone server wrapping PyTorch via CSnakes (embedded Python) |
| `Amuse.Host.Onnx` | Standalone server wrapping ONNX Runtime |

### Linux UI Layer (Amuse.UI.Linux)

Uses **Avalonia** (not WPF) and **CommunityToolkit.Mvvm** (source-generators-based). Active development focus.

**Navigation**: `MainWindowViewModel` holds `CurrentView` (a `View` enum value) and `ActiveContent` (the instantiated control). Navigation calls `ViewFactory.Create(view)` which switch-expresses the enum to a concrete view. `ViewManager` (in `Views/View.cs`) tracks the last-visited view per `ViewCategory` (Image/Video/Audio/Settings).

**View hierarchy**: Each pipeline has a matching `*View.axaml` + `*View.axaml.cs` and a `*ViewModel.cs`. ViewModels extend `ObservableObject`; `[ObservableProperty]` generates backing fields; `[RelayCommand]` generates `ICommand` properties.

**Adding a new pipeline view** requires five coordinated changes:
1. Add the enum value to `View` and map it in `ViewManager.ViewCategoryMap` (`Views/View.cs`)
2. Create `ViewModels/MyNewViewModel.cs` (extend `ObservableObject`, use `[ObservableProperty]`/`[RelayCommand]`)
3. Create `Views/MyNewView.axaml` + `MyNewView.axaml.cs` with `DataContext = new MyNewViewModel()`
4. Add the case to `ViewFactory.Create()` (`Views/ViewFactory.cs`)
5. Add a handler function in `Backend/generate.py` and wire it in the `if mode ==` dispatch block

**ViewFactory quirk**: `View.TextToMusic` is aliased to `new TextToAudioView()` (shares the same view as `View.TextToAudio`). `View.Home` and `View.Recent` are not in ViewFactory and fall through to `StubView`.

### Linux Python IPC Protocol

`PythonDiffusionService` spawns `Backend/generate.py` as a subprocess. Communication is:
- **Input**: one JSON line written to `stdin`, then stdin is closed
- **Output**: JSON-line messages on `stdout` with a `type` field:
  - `{"type":"progress","step":N,"total":M,"message":"..."}` — progress update
  - `{"type":"seed","value":N}` — resolved seed (reported before generation)
  - `{"type":"info","message":"..."}` — informational status
  - `{"type":"complete","output_path":"..."}` — generation done
  - `{"type":"error","message":"..."}` — fatal error
  - `{"type":"missing_deps","packages":[...]}` — Python deps absent

The input JSON schema is defined in `Services/GenerationRequest.cs`. All fields are snake_cased when serialized. The `mode` field routes to the correct handler in `generate.py`:

| `mode` value | Handler in `generate.py` |
|---|---|
| `image` (default) | `generate_image` |
| `image_to_image` | `generate_image_to_image` |
| `image_edit` | `generate_image_edit` (InstructPix2Pix) |
| `inpaint` | `generate_inpaint` |
| `paint_to_image` | `generate_paint_to_image` |
| `video` | `generate_video` |
| `image_to_video` | `generate_image_to_video` (SVD) |
| `video_to_video` | `generate_video_to_video` |
| `frame_to_frame` | `generate_frame_to_frame` |
| `audio` | `generate_audio` |
| `audio_to_text` | `audio_to_text` (Whisper) |

`PythonDiffusionService` chooses the output file extension by mode: `.png` for image modes, `.gif` for video modes, `.wav` for audio, `.txt` for audio_to_text.

**GPU environment**: `CUDA_VISIBLE_DEVICES`, `HIP_VISIBLE_DEVICES`, and `ROCR_VISIBLE_DEVICES` are all set to `""` (empty string, not `"-1"`) before spawning the Python process. AMD iGPU (Cezanne/Vega) segfaults ROCm 7.2 at tensor init if `-1` is used.

### Linux Services

All Linux services are static classes or simple instantiated objects (no DI container):

- **`PythonDiffusionService`** — spawns `generate.py`, manages the subprocess lifecycle, parses JSON-line output. `GenerateAsync` is the primary entry point.
- **`AppSettingsService`** — singleton; persists `AppSettings` to `~/.config/AmuseAI/settings.json`. Fields: `OutputDirectory` (defaults to `~/.local/share/AmuseAI/gallery`), `PythonPath`, `GpuDevice`.
- **`ModelCatalogService`** — stores per-type model lists as JSON in `~/.local/share/AmuseAI/models/<type>.json`. Types: `diffusion`, `upscale`, `extract`. `ModelEntry` has a flexible `Fields` dictionary for pipeline-specific metadata.
- **`HistoryService`** — persists generation history (max 1000 entries) to `~/.local/share/AmuseAI/history.json`; copies output files into `AppSettings.OutputDirectory`.

### Windows UI Layer (Amuse.App)

- `App.xaml.cs` — DI host setup; all services registered as singletons; static `App.DirectoryBase/Data/Logs/Python` paths; single-instance mutex
- `MainWindow.xaml.cs` — navigation hub, hosts all feature views
- `Views/` — concrete views (one per pipeline type); view enum at `Views/View.cs`; `ViewBase` → `ViewBaseDiffusion` → concrete view
- `Controls/` — reusable XAML controls (DiffusionInputControl, CheckpointControl, LoraAdapterControl, SchedulerControl, etc.)
- `Dialogs/` — modal dialogs for model selection, environment management, wizards
- `Common/` — app-level model classes (`PipelineModel`, `DiffusionInputOptions`, `CheckpointModel`, etc.) — distinct from `Amuse.Common/`

**View hierarchy**: `ViewControl` (TensorStack.WPF) → `ViewBase` → `ViewBaseDiffusion` → concrete views. `ViewBaseDiffusion` handles `LoadPipelineAsync`/`UnloadPipelineAsync`, progress from both C# and Python backends, and dispatches to `ExecuteAsync`/`ExecuteAutomationAsync` which subclasses must implement.

### Windows Services Layer (Amuse.App/Services/)

All registered as singletons in `App.xaml.cs`; retrieved via `App.GetService<T>()`.

- **`DiffusionService`** — orchestrates all ML inference; dispatches to `IDiffusionRuntime`
- **`EnvironmentService`** — manages isolated Python venvs (vendor/device/pipeline-specific)
- **`HardwareService`** — GPU/CPU monitoring via WMI (~500ms refresh); NVIDIA and AMD detection
- **`ModelDownloadService`** — downloads models from HuggingFace
- **`HistoryService`** — persists generation results
- **`MigrationService`** — merges new defaults from `Settings.default.json` on upgrade
- **`UpscaleService`**, **`ExtractService`**, **`InterpolationService`** — specialized pipelines
- **`AutomationManager`** — batch processing

### Configuration & Settings (Windows)

- `Settings.default.json` (~9,800 lines) — master template with all environments, models, and defaults; copied to `Settings.json` on first run
- `Templates.json` — pipeline template definitions
- `SettingsManager.cs` — loads, saves, and migrates settings
- `Settings.cs` — the root settings model (100+ properties)

### Python Environment Management (Windows)

Documented in `Docs/Environments.md`. Amuse creates isolated Python venvs scoped by vendor (NVIDIA/AMD), device, and pipeline type. Precedence: pipeline-specific > device-specific > vendor-specific. Lifecycle: create → install → load → rebuild/update as needed.

### Key Patterns

- **Backend abstraction** (Windows): `IDiffusionRuntime` interface lets the UI layer remain agnostic of PyTorch vs ONNX. `DiffusionService` holds the active runtime.
- **Enums are large**: `Amuse.Common/Enums.cs` is ~13,000 lines and is the single source of truth for pipeline types, schedulers, quantization modes, etc.
- **Nullable disabled** (`<Nullable>disable</Nullable>` in `Directory.Build.props`) — don't introduce nullable annotations in new code.
- **Implicit usings disabled** — always add explicit `using` statements.
- **WPF data binding**: views bind to service properties via `INotifyPropertyChanged`; `ServiceBase` provides `SetProperty`; avoid code-behind logic beyond event wiring.
- **Pipeline load/reload/update**: `IDiffusionRuntime` distinguishes `LoadAsync` (cold load), `ReloadAsync` (model changed, same env), and `UpdateAsync` (only options changed, no reload needed).
- **CommunityToolkit.Mvvm source generators** (Linux): `[ObservableProperty]` on a `private` field named `_camelCase` generates the public `PascalCase` property plus `OnXChanged` partial hook. `[RelayCommand]` on a `private` method generates `XCommand`. `[NotifyCanExecuteChangedFor]` wires command re-evaluation.

## Other Projects in This Repo

`Youtube_SubjectSerach/` — a standalone Python/HTML mini-tool for YouTube subject search (unrelated to the main AI generation app). It has its own `server.py` and `index.html`.

## Supported AI Pipelines

**Image**: FLUX.1/FLUX.2/Chroma, StableDiffusion-XL/3, Kandinsky5, Qwen, Z-Image  
**Video**: LTX, Wan 2.2, CogVideoX, SkyReels-V2, Helios, Kandinsky5  
**Audio/Speech**: ACE-Step XL, Whisper, Supertonic  
**Formats**: safetensors, GGUF, ONNX; quantization via float8/int4 (quanto, bitsandbytes) — see `Docs/Quantization.md`
