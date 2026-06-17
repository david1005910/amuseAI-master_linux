

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AmuseAI is a Windows WPF desktop application (.NET 10.0, C#, x64-only) serving as the flagship demo for the TensorStack SDK. It is a local AI generation platform supporting image, video, audio, and text pipelines via NVIDIA CUDA and AMD ROCm backends.

## Build Commands

```bash
# Full solution build
dotnet build Amuse.sln -c Release -p:Platform=x64

# Build specific project
dotnet build Amuse.App/Amuse.App.csproj -c Release -p:Platform=x64

# Publish installer build
dotnet publish Amuse.App/Amuse.App.csproj -c Release_Installer -p:Platform=x64

# Debug build (uses local TensorStack project references)
dotnet build Amuse.sln -c Debug -p:Platform=x64
```

**No automated test suite exists in this repository.** Manual testing requires running the WPF app on Windows with a capable GPU.

Build configurations:
- `Debug` — links against local TensorStack project references (not NuGet packages)
- `Release` — links against TensorStack NuGet packages
- `Release_Installer` — same as Release but sets app data path to `%LOCALAPPDATA%\Amuse`

Version is defined globally in `Directory.Build.props` (`Version`, `PackageVersion`).

## Architecture

### Multi-Process Design

The app runs as three processes communicating via named pipes:

```
Amuse.App (WPF, main process)
    │
    ├── AmuseHost.PyTorch.exe  (subprocess — PyTorch/HuggingFace pipelines)
    └── AmuseHost.Onnx.exe     (subprocess — ONNX Runtime pipelines)
```

Each subprocess connection uses **three named pipes**: `CommandChannel`, `PipelineChannel`, `ProgressChannel`. The IPC protocol is defined in `Amuse.Common/`: `PipelineClient.cs` (main-app side), `PipelineServer.cs` (host side), `ProcessHandler.cs` (lifecycle).

Host processes are spawned on demand by `PyTorchDiffusion.cs` or `OnnxDiffusion.cs` in `Amuse.App/Backend/`, both implementing `IDiffusionRuntime`.

### Project Breakdown

| Project | Role |
|---|---|
| `Amuse.App` | WPF UI, services, settings, backend runtime selection |
| `Amuse.Common` | Shared IPC types, enums, message/option models |
| `Amuse.Host.PyTorch` | Standalone server wrapping PyTorch via CSnakes (embedded Python) |
| `Amuse.Host.Onnx` | Standalone server wrapping ONNX Runtime |

### UI Layer (Amuse.App)

- `MainWindow.xaml.cs` — navigation hub, hosts all feature views
- `Views/` — 20+ feature views (one per pipeline type: TextToImage, ImageToVideo, etc.)
- `Controls/` — reusable XAML controls (DiffusionInputControl, CheckpointControl, LoraAdapterControl, SchedulerControl, etc.)
- `Dialogs/` — 12 modal dialogs for model selection, environment management, wizards

### Services Layer (Amuse.App/Services/)

Core business logic. Key services:

- **`DiffusionService`** — orchestrates all ML inference; dispatches to `IDiffusionRuntime`
- **`EnvironmentService`** — manages isolated Python venvs (vendor/device/pipeline-specific)
- **`HardwareService`** — GPU/CPU monitoring via WMI (~500ms refresh); NVIDIA and AMD detection
- **`ModelDownloadService`** — downloads models from HuggingFace
- **`HistoryService`** — persists generation results
- **`UpscaleService`**, **`ExtractService`**, **`InterpolationService`** — specialized pipelines
- **`AutomationManager`** — batch processing

### Configuration & Settings

- `Settings.default.json` (~9,800 lines) — master template with all environments, models, and defaults; copied to `Settings.json` on first run
- `Templates.json` — pipeline template definitions
- `SettingsManager.cs` — loads, saves, and migrates settings; `MigrationService` merges new defaults on upgrade
- `Settings.cs` — the root settings model (100+ properties)

### Python Environment Management

Documented in `Docs/Environments.md`. Amuse creates isolated Python venvs to prevent dependency conflicts. Environments are scoped by vendor (NVIDIA/AMD), device, and pipeline type. Lifecycle: create → install → load → rebuild/update as needed.

### Key Patterns

- **Backend abstraction**: `IDiffusionRuntime` interface lets the UI layer remain agnostic of PyTorch vs ONNX. Selection happens in `Amuse.App/Backend/` based on model type.
- **Enums are large**: `Amuse.Common/Enums.cs` is ~13,000 lines and is the single source of truth for pipeline types, schedulers, quantization modes, etc.
- **Nullable disabled** (`<Nullable>disable</Nullable>` in `Directory.Build.props`) — don't introduce nullable annotations in new code.
- **Implicit usings disabled** — always add explicit `using` statements.
- **WPF data binding** — views bind to service properties; avoid code-behind logic beyond event wiring.

## Supported AI Pipelines

**Image**: FLUX.1/FLUX.2/Chroma, StableDiffusion-XL/3, Kandinsky5, Qwen, Z-Image  
**Video**: LTX, Wan 2.2, CogVideoX, SkyReels-V2, Helios, Kandinsky5  
**Audio/Speech**: ACE-Step XL, Whisper, Supertonic  
**Formats**: safetensors, GGUF, ONNX; quantization via float8/int4 (quanto, bitsandbytes) — see `Docs/Quantization.md`
