## Quantization

Amuse supports **automatic float8, int4 quantization** during model load to reduce VRAM usage.

### Supported Backends

Amuse supports two quantization backends:

1. **quanto**
   - Used in the default environments
   - Supports both **CUDA** and **ROCm**

2. **bits-and-bytes**
   - Used in the default environments
   - Supports **CUDA** only

### Key Notes

- Quantization is **automatic** and happens during model loading
- `float8` can reduce VRAM usage by **~30–40%**
- `int4` can reduce VRAM usage by **~40–60%**
- Inference may be **slightly slower** when quantization is enabled

> Quantization is best suited for memory-constrained systems where VRAM is more important than raw speed.


---


### GGUF Support
GGUF is a specialized model format designed for extreme efficiency. These models are pre-shrunk, meaning they take up less space on your disk and significantly less space in your VRAM.

* **How to use:** Select a **.gguf** file as your Model Checkpoint.
* **VRAM Saving:** **Significant.** This is the best method for running large models on low-VRAM cards.
* **Best for:** Users who are hitting "Out of Memory" errors with standard checkpoints.
* **Current Limitation:** GGUF models must be loaded as diffusers checkpoint files, transformers models are not yet supported;

For more detailed information on quantization and how these models work, visit the [Diffusers GGUF Quantization documentation](https://huggingface.co/docs/diffusers/en/quantization/gguf)