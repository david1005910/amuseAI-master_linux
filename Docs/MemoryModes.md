## Memory Modes

Memory modes control **how models are placed across GPUs and CPU memory** during inference. They are designed to simplify setup while offering fine-grained control when needed.

| Mode | Description |
|------|-------------|
| **Auto** | Automatically selects the best memory strategy for the selected device(s). |
| **Balanced** | Spreads the model across available GPUs and system RAM to maximize capacity while maintaining stability. |
| **Low** | CPU Offload: Only loads the specific sub-module currently being executed into VRAM. Extremely slow, but runs on almost any GPU. |
| **Medium** | Model Offload: Keeps the main model on the CPU and swaps it to the GPU only when needed. Much faster than "Low" but saves significant VRAM. |
| **High** | GPU Offload: Loads all models directly into GPU VRAM. Provides the fastest possible generation speeds but requires the most memory. |

> **Tip:**
> If you’re unsure which mode to use, start with **Auto** — it handles most cases well.