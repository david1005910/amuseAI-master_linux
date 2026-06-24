#!/usr/bin/env python3
"""Amuse Linux - Diffusion backend.
Reads a JSON config from stdin, outputs JSON-line progress to stdout.
Modes: image (default), video, audio, audio_to_text"""
import sys
import json
import os
import traceback
import random

# Must be set before torch import — AMD Cezanne iGPU (Vega) segfaults ROCm 7.2
# when HIP_VISIBLE_DEVICES="-1"; use empty string to suppress without crashing.
os.environ.setdefault("CUDA_VISIBLE_DEVICES", "")
os.environ.setdefault("HIP_VISIBLE_DEVICES", "")
os.environ.setdefault("ROCR_VISIBLE_DEVICES", "")


def emit(msg_type, **kwargs):
    print(json.dumps({"type": msg_type, **kwargs}), flush=True)


def check_deps():
    missing = []
    for pkg in ["diffusers", "torch", "transformers", "accelerate", "PIL"]:
        try:
            __import__(pkg)
        except ImportError:
            missing.append(pkg if pkg != "PIL" else "Pillow")
    return missing


def get_device():
    import torch
    cuda = os.environ.get("CUDA_VISIBLE_DEVICES", "unset")
    hip  = os.environ.get("HIP_VISIBLE_DEVICES",  "unset")
    if cuda in ("", "-1") or hip in ("", "-1"):
        emit("info", message="Using CPU (GPU disabled by environment)")
        return "cpu", torch.float32
    if torch.cuda.is_available():
        try:
            t = torch.tensor([1.0]).cuda()
            _ = (t * 2).cpu().item()
            name = torch.cuda.get_device_name(0)
            emit("info", message=f"GPU: {name}")
            return "cuda", torch.float16
        except Exception as e:
            emit("info", message=f"GPU test failed ({e}), falling back to CPU")
    emit("info", message="Using CPU (this will be slow)")
    return "cpu", torch.float32


# ── Image ─────────────────────────────────────────────────────────────────────

def generate_image(config):
    import torch
    from diffusers import (
        StableDiffusionPipeline,
        StableDiffusionXLPipeline,
        DPMSolverMultistepScheduler,
        EulerAncestralDiscreteScheduler,
        EulerDiscreteScheduler,
        DDIMScheduler,
        LMSDiscreteScheduler,
        PNDMScheduler,
    )

    model_id       = config.get("model_id", "runwayml/stable-diffusion-v1-5")
    prompt         = config.get("prompt", "")
    neg_prompt     = config.get("negative_prompt", "")
    width          = int(config.get("width", 512))
    height         = int(config.get("height", 512))
    steps          = int(config.get("steps", 20))
    guidance       = float(config.get("guidance_scale", 7.5))
    seed           = int(config.get("seed", -1))
    scheduler_name = config.get("scheduler", "DPMSolverMultistep")
    output_path    = config.get("output_path", "/tmp/amuse_output.png")
    is_xl          = config.get("is_xl", False)

    device, dtype = get_device()
    emit("progress", step=0, total=steps, message="Loading model…")

    pipeline_cls = StableDiffusionXLPipeline if is_xl else StableDiffusionPipeline
    kwargs = {"torch_dtype": dtype}
    if not is_xl:
        kwargs["safety_checker"] = None
        kwargs["requires_safety_checker"] = False

    pipe = pipeline_cls.from_pretrained(model_id, **kwargs)

    scheduler_map = {
        "DPMSolverMultistep": DPMSolverMultistepScheduler,
        "EulerA":             EulerAncestralDiscreteScheduler,
        "Euler":              EulerDiscreteScheduler,
        "DDIM":               DDIMScheduler,
        "LMS":                LMSDiscreteScheduler,
        "PNDM":               PNDMScheduler,
    }
    if scheduler_name in scheduler_map:
        pipe.scheduler = scheduler_map[scheduler_name].from_config(pipe.scheduler.config)

    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)

    generator = torch.Generator(device=device).manual_seed(seed)

    def on_step(pipe, step, timestep, cb_kwargs):
        emit("progress", step=step + 1, total=steps, message=f"Step {step+1}/{steps}")
        return cb_kwargs

    emit("progress", step=0, total=steps, message="Generating…")
    result = pipe(
        prompt=prompt,
        negative_prompt=neg_prompt or None,
        width=width,
        height=height,
        num_inference_steps=steps,
        guidance_scale=guidance,
        generator=generator,
        callback_on_step_end=on_step,
    )

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    result.images[0].save(output_path)
    emit("complete", output_path=output_path, seed=seed)


# ── Video ─────────────────────────────────────────────────────────────────────

def generate_video(config):
    import torch
    from diffusers import DiffusionPipeline

    model_id   = config.get("model_id", "damo-vilab/text-to-video-ms-1.7b")
    prompt     = config.get("prompt", "")
    num_frames = int(config.get("num_frames", 16))
    steps      = int(config.get("steps", 25))
    fps        = int(config.get("fps", 8))
    output_path = config.get("output_path", "/tmp/amuse_video.gif")

    device, dtype = get_device()
    emit("progress", step=0, total=steps, message="Loading video model…")

    pipe = DiffusionPipeline.from_pretrained(model_id, torch_dtype=dtype)
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    emit("progress", step=0, total=steps, message="Generating frames…")

    # TextToVideoSDPipeline (damo-vilab) uses the older callback/callback_steps API,
    # not callback_on_step_end. Use callback_steps=1 for per-step progress.
    call_kwargs = dict(
        prompt=prompt,
        num_frames=num_frames,
        num_inference_steps=steps,
    )
    import inspect
    sig = inspect.signature(pipe.__call__)
    if "callback_on_step_end" in sig.parameters:
        def on_step_new(p, step, timestep, cb_kwargs):
            emit("progress", step=step + 1, total=steps, message=f"Step {step+1}/{steps}")
            return cb_kwargs
        call_kwargs["callback_on_step_end"] = on_step_new
    elif "callback" in sig.parameters:
        def on_step_old(step, timestep, latents):
            emit("progress", step=step + 1, total=steps, message=f"Step {step+1}/{steps}")
        call_kwargs["callback"] = on_step_old
        call_kwargs["callback_steps"] = 1

    result = pipe(**call_kwargs)

    raw_frames = result.frames[0]  # PIL images or numpy arrays depending on diffusers version

    # Normalise to PIL Images
    from PIL import Image as PILImage
    import numpy as np
    pil_frames = []
    for f in raw_frames:
        if isinstance(f, PILImage.Image):
            pil_frames.append(f)
        else:
            arr = np.array(f)
            if arr.dtype != np.uint8:
                arr = (np.clip(arr, 0, 1) * 255).astype(np.uint8)
            pil_frames.append(PILImage.fromarray(arr))

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)

    # Save animated GIF using PIL
    frame_duration_ms = max(1, int(1000 / fps))
    pil_frames[0].save(
        output_path,
        save_all=True,
        append_images=pil_frames[1:],
        optimize=False,
        duration=frame_duration_ms,
        loop=0,
    )

    # First frame as static preview
    preview_path = output_path.replace(".gif", "_preview.png")
    pil_frames[0].save(preview_path)

    emit("complete", output_path=output_path, preview_path=preview_path,
         num_frames=len(pil_frames), fps=fps)


# ── Audio ─────────────────────────────────────────────────────────────────────

def generate_audio(config):
    import torch
    import numpy as np
    import wave

    model_id    = config.get("model_id", "facebook/musicgen-small")
    prompt      = config.get("prompt", "")
    duration    = float(config.get("duration", 10.0))
    output_path = config.get("output_path", "/tmp/amuse_audio.wav")

    emit("progress", step=0, total=2, message="Loading audio model…")

    # Use MusicgenForConditionalGeneration directly — AutoModelForTextToWaveform
    # triggers a diffusers model_index.json probe first, which 404s for musicgen.
    from transformers import AutoProcessor, MusicgenForConditionalGeneration

    processor = AutoProcessor.from_pretrained(model_id)
    model     = MusicgenForConditionalGeneration.from_pretrained(
        model_id, torch_dtype=torch.float32
    )
    model.eval()

    sampling_rate = model.config.audio_encoder.sampling_rate  # 32000 for musicgen

    # 1 token ≈ 640 samples at 32 kHz → tokens_per_sec ≈ 50
    tokens_per_sec = sampling_rate / 640
    max_new_tokens = max(50, int(duration * tokens_per_sec))

    emit("progress", step=1, total=2, message=f"Generating {duration:.0f}s of audio…")

    inputs = processor(text=[prompt], padding=True, return_tensors="pt")

    with torch.no_grad():
        audio_values = model.generate(**inputs, max_new_tokens=max_new_tokens)

    # audio_values: (batch, channels, samples), float32 in [-1, 1]
    audio = audio_values[0, 0].cpu().numpy()

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)

    audio_int16 = np.clip(audio * 32767, -32768, 32767).astype(np.int16)
    with wave.open(output_path, "w") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(sampling_rate)
        wf.writeframes(audio_int16.tobytes())

    actual_duration = len(audio_int16) / sampling_rate
    emit("complete", output_path=output_path,
         duration=round(actual_duration, 1), sampling_rate=sampling_rate)
    emit("progress", step=2, total=2, message="Done")


# ── Audio to Text (Whisper) ───────────────────────────────────────────────────

def audio_to_text(config):
    from transformers import pipeline as hf_pipeline

    model_id   = config.get("model_id", "openai/whisper-base")
    audio_path = config.get("audio_path", "")
    language   = config.get("language") or None   # None = auto-detect
    output_path = config.get("output_path", "/tmp/amuse_transcript.txt")

    if not audio_path or not os.path.exists(audio_path):
        raise FileNotFoundError(f"Audio file not found: {audio_path}")

    emit("progress", step=0, total=2, message="Loading Whisper model…")

    pipe = hf_pipeline(
        "automatic-speech-recognition",
        model=model_id,
        device="cpu",
        chunk_length_s=30,
    )

    emit("progress", step=1, total=2, message="Transcribing audio…")

    kwargs = {}
    if language:
        kwargs["generate_kwargs"] = {"language": language}

    result = pipe(audio_path, **kwargs)
    text = result["text"].strip()

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        f.write(text)

    emit("complete", output_path=output_path, text=text)
    emit("progress", step=2, total=2, message="Done")


# ── Image to Image ────────────────────────────────────────────────────────────

def generate_image_to_image(config):
    import torch
    from diffusers import StableDiffusionImg2ImgPipeline, StableDiffusionXLImg2ImgPipeline
    from PIL import Image as PILImage

    model_id         = config.get("model_id", "runwayml/stable-diffusion-v1-5")
    prompt           = config.get("prompt", "")
    neg_prompt       = config.get("negative_prompt", "")
    input_image_path = config.get("input_image_path", "")
    strength         = float(config.get("strength", 0.75))
    steps            = int(config.get("steps", 20))
    guidance         = float(config.get("guidance_scale", 7.5))
    seed             = int(config.get("seed", -1))
    output_path      = config.get("output_path", "/tmp/amuse_output.png")
    is_xl            = config.get("is_xl", False)

    if not input_image_path or not os.path.exists(input_image_path):
        emit("error", message=f"Input image not found: {input_image_path}")
        return

    device, dtype = get_device()
    emit("progress", step=0, total=steps, message="Loading img2img model…")

    init_image = PILImage.open(input_image_path).convert("RGB")
    pipeline_cls = StableDiffusionXLImg2ImgPipeline if is_xl else StableDiffusionImg2ImgPipeline
    kwargs = {"torch_dtype": dtype}
    if not is_xl:
        kwargs["safety_checker"] = None
        kwargs["requires_safety_checker"] = False

    pipe = pipeline_cls.from_pretrained(model_id, **kwargs)
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)
    generator = torch.Generator(device=device).manual_seed(seed)

    def on_step(pipe, step, timestep, cb_kwargs):
        emit("progress", step=step+1, total=steps, message=f"Step {step+1}/{steps}")
        return cb_kwargs

    emit("progress", step=0, total=steps, message="Generating…")
    result = pipe(
        prompt=prompt,
        negative_prompt=neg_prompt or None,
        image=init_image,
        strength=strength,
        num_inference_steps=steps,
        guidance_scale=guidance,
        generator=generator,
        callback_on_step_end=on_step,
    )

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    result.images[0].save(output_path)
    emit("complete", output_path=output_path, seed=seed)


# ── Image Edit (InstructPix2Pix) ──────────────────────────────────────────────

def generate_image_edit(config):
    import torch
    from diffusers import StableDiffusionInstructPix2PixPipeline
    from PIL import Image as PILImage

    model_id         = config.get("model_id", "timbrooks/instruct-pix2pix")
    prompt           = config.get("prompt", "")
    input_image_path = config.get("input_image_path", "")
    steps            = int(config.get("steps", 20))
    guidance         = float(config.get("guidance_scale", 7.5))
    image_guidance   = float(config.get("image_guidance_scale", 1.5))
    seed             = int(config.get("seed", -1))
    output_path      = config.get("output_path", "/tmp/amuse_output.png")

    if not input_image_path or not os.path.exists(input_image_path):
        emit("error", message=f"Input image not found: {input_image_path}")
        return

    device, dtype = get_device()
    emit("progress", step=0, total=steps, message="Loading InstructPix2Pix model…")

    init_image = PILImage.open(input_image_path).convert("RGB")
    pipe = StableDiffusionInstructPix2PixPipeline.from_pretrained(
        model_id, torch_dtype=dtype, safety_checker=None
    )
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)
    generator = torch.Generator(device=device).manual_seed(seed)

    def on_step(pipe, step, timestep, cb_kwargs):
        emit("progress", step=step+1, total=steps, message=f"Step {step+1}/{steps}")
        return cb_kwargs

    emit("progress", step=0, total=steps, message="Editing image…")
    result = pipe(
        prompt,
        image=init_image,
        num_inference_steps=steps,
        guidance_scale=guidance,
        image_guidance_scale=image_guidance,
        generator=generator,
        callback_on_step_end=on_step,
    )

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    result.images[0].save(output_path)
    emit("complete", output_path=output_path, seed=seed)


# ── Inpaint ───────────────────────────────────────────────────────────────────

def generate_inpaint(config):
    import torch
    from diffusers import StableDiffusionInpaintPipeline
    from PIL import Image as PILImage

    model_id         = config.get("model_id", "runwayml/stable-diffusion-inpainting")
    prompt           = config.get("prompt", "")
    neg_prompt       = config.get("negative_prompt", "")
    input_image_path = config.get("input_image_path", "")
    mask_image_path  = config.get("mask_image_path", "")
    steps            = int(config.get("steps", 20))
    guidance         = float(config.get("guidance_scale", 7.5))
    seed             = int(config.get("seed", -1))
    output_path      = config.get("output_path", "/tmp/amuse_output.png")

    if not input_image_path or not os.path.exists(input_image_path):
        emit("error", message=f"Input image not found: {input_image_path}")
        return
    if not mask_image_path or not os.path.exists(mask_image_path):
        emit("error", message=f"Mask image not found: {mask_image_path}")
        return

    device, dtype = get_device()
    emit("progress", step=0, total=steps, message="Loading inpainting model…")

    init_image = PILImage.open(input_image_path).convert("RGB").resize((512, 512))
    mask_image = PILImage.open(mask_image_path).convert("RGB").resize((512, 512))

    pipe = StableDiffusionInpaintPipeline.from_pretrained(
        model_id, torch_dtype=dtype, safety_checker=None
    )
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)
    generator = torch.Generator(device=device).manual_seed(seed)

    def on_step(pipe, step, timestep, cb_kwargs):
        emit("progress", step=step+1, total=steps, message=f"Step {step+1}/{steps}")
        return cb_kwargs

    emit("progress", step=0, total=steps, message="Inpainting…")
    result = pipe(
        prompt=prompt,
        negative_prompt=neg_prompt or None,
        image=init_image,
        mask_image=mask_image,
        num_inference_steps=steps,
        guidance_scale=guidance,
        generator=generator,
        callback_on_step_end=on_step,
    )

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    result.images[0].save(output_path)
    emit("complete", output_path=output_path, seed=seed)


# ── Paint to Image ────────────────────────────────────────────────────────────

def generate_paint_to_image(config):
    import torch
    from diffusers import StableDiffusionImg2ImgPipeline
    from PIL import Image as PILImage

    model_id         = config.get("model_id", "runwayml/stable-diffusion-v1-5")
    prompt           = config.get("prompt", "")
    neg_prompt       = config.get("negative_prompt", "")
    input_image_path = config.get("input_image_path", "")
    strength         = float(config.get("strength", 0.9))
    steps            = int(config.get("steps", 20))
    guidance         = float(config.get("guidance_scale", 7.5))
    seed             = int(config.get("seed", -1))
    output_path      = config.get("output_path", "/tmp/amuse_output.png")

    if not input_image_path or not os.path.exists(input_image_path):
        emit("error", message=f"Input image not found: {input_image_path}")
        return

    device, dtype = get_device()
    emit("progress", step=0, total=steps, message="Loading model…")

    init_image = PILImage.open(input_image_path).convert("RGB")
    pipe = StableDiffusionImg2ImgPipeline.from_pretrained(
        model_id, torch_dtype=dtype, safety_checker=None, requires_safety_checker=False
    )
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)
    generator = torch.Generator(device=device).manual_seed(seed)

    def on_step(pipe, step, timestep, cb_kwargs):
        emit("progress", step=step+1, total=steps, message=f"Step {step+1}/{steps}")
        return cb_kwargs

    emit("progress", step=0, total=steps, message="Generating from paint…")
    result = pipe(
        prompt=prompt,
        negative_prompt=neg_prompt or None,
        image=init_image,
        strength=strength,
        num_inference_steps=steps,
        guidance_scale=guidance,
        generator=generator,
        callback_on_step_end=on_step,
    )

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    result.images[0].save(output_path)
    emit("complete", output_path=output_path, seed=seed)


# ── Image to Video (SVD) ──────────────────────────────────────────────────────

def generate_image_to_video(config):
    import torch
    from diffusers import StableVideoDiffusionPipeline
    from PIL import Image as PILImage

    model_id          = config.get("model_id", "stabilityai/stable-video-diffusion-img2vid-xt")
    input_image_path  = config.get("input_image_path", "")
    num_frames        = int(config.get("num_frames", 25))
    fps               = int(config.get("fps", 7))
    motion_bucket_id  = int(config.get("motion_bucket_id", 127))
    noise_aug         = float(config.get("noise_aug_strength", 0.02))
    decode_chunk_size = int(config.get("decode_chunk_size", 8))
    seed              = int(config.get("seed", -1))
    output_path       = config.get("output_path", "/tmp/amuse_i2v.gif")

    if not input_image_path or not os.path.exists(input_image_path):
        emit("error", message=f"Input image not found: {input_image_path}")
        return

    device, dtype = get_device()
    # SVD requires float16; fall back to float32 on CPU
    if device == "cpu":
        dtype = torch.float32

    emit("progress", step=0, total=num_frames, message="Loading SVD model…")

    pipe = StableVideoDiffusionPipeline.from_pretrained(
        model_id, torch_dtype=dtype, variant="fp16" if dtype == torch.float16 else None
    )
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    image = PILImage.open(input_image_path).convert("RGB")
    # SVD expects 1024x576 (XT) or 1024x576 (base)
    image = image.resize((1024, 576))

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)
    generator = torch.Generator(device=device).manual_seed(seed)

    emit("progress", step=0, total=num_frames, message="Generating video frames…")

    frames_output = pipe(
        image,
        num_frames=num_frames,
        motion_bucket_id=motion_bucket_id,
        noise_aug_strength=noise_aug,
        decode_chunk_size=decode_chunk_size,
        generator=generator,
    )
    frames = frames_output.frames[0]

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)

    frame_duration_ms = max(1, int(1000 / fps))
    frames[0].save(
        output_path,
        save_all=True,
        append_images=frames[1:],
        optimize=False,
        duration=frame_duration_ms,
        loop=0,
    )

    preview_path = output_path.replace(".gif", "_preview.png")
    frames[0].save(preview_path)

    emit("complete", output_path=output_path, preview_path=preview_path,
         num_frames=len(frames), fps=fps)


# ── Video to Video ────────────────────────────────────────────────────────────

def generate_video_to_video(config):
    import torch
    from diffusers import DiffusionPipeline
    from PIL import Image as PILImage

    model_id         = config.get("model_id", "damo-vilab/text-to-video-ms-1.7b")
    input_video_path = config.get("input_video_path", "")
    prompt           = config.get("prompt", "")
    neg_prompt       = config.get("negative_prompt", "")
    steps            = int(config.get("steps", 25))
    strength         = float(config.get("strength", 0.7))
    guidance         = float(config.get("guidance_scale", 7.5))
    fps              = int(config.get("fps", 8))
    seed             = int(config.get("seed", -1))
    output_path      = config.get("output_path", "/tmp/amuse_v2v.gif")

    if not input_video_path or not os.path.exists(input_video_path):
        emit("error", message=f"Input video not found: {input_video_path}")
        return

    device, dtype = get_device()
    emit("progress", step=0, total=steps, message="Loading video model…")

    # Extract frames from input GIF/video using PIL
    source = PILImage.open(input_video_path)
    source_frames = []
    try:
        while True:
            source_frames.append(source.copy().convert("RGB"))
            source.seek(source.tell() + 1)
    except EOFError:
        pass
    if not source_frames:
        source_frames = [source.convert("RGB")]

    pipe = DiffusionPipeline.from_pretrained(model_id, torch_dtype=dtype)
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)
    generator = torch.Generator(device=device).manual_seed(seed)

    def on_step(pipe, step, timestep, cb_kwargs):
        emit("progress", step=step + 1, total=steps, message=f"Step {step+1}/{steps}")
        return cb_kwargs

    num_frames = len(source_frames)
    emit("progress", step=0, total=steps, message=f"Re-generating {num_frames} frames with new style…")

    result = pipe(
        prompt=prompt,
        negative_prompt=neg_prompt or None,
        num_frames=num_frames,
        num_inference_steps=steps,
        generator=generator,
        callback_on_step_end=on_step,
    )
    frames = result.frames[0]

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)

    frame_duration_ms = max(1, int(1000 / fps))
    frames[0].save(
        output_path,
        save_all=True,
        append_images=frames[1:],
        optimize=False,
        duration=frame_duration_ms,
        loop=0,
    )

    preview_path = output_path.replace(".gif", "_preview.png")
    frames[0].save(preview_path)

    emit("complete", output_path=output_path, preview_path=preview_path,
         num_frames=len(frames), fps=fps)


# ── Frame to Frame ────────────────────────────────────────────────────────────

def generate_frame_to_frame(config):
    import torch
    from diffusers import (
        StableDiffusionImg2ImgPipeline,
        StableDiffusionXLImg2ImgPipeline,
    )
    from PIL import Image as PILImage

    model_id         = config.get("model_id", "runwayml/stable-diffusion-v1-5")
    input_video_path = config.get("input_video_path", "")
    prompt           = config.get("prompt", "")
    neg_prompt       = config.get("negative_prompt", "")
    steps            = int(config.get("steps", 20))
    strength         = float(config.get("strength", 0.6))
    guidance         = float(config.get("guidance_scale", 7.5))
    fps              = int(config.get("fps", 8))
    seed             = int(config.get("seed", -1))
    is_xl            = config.get("is_xl", False)
    output_path      = config.get("output_path", "/tmp/amuse_f2f.gif")

    if not input_video_path or not os.path.exists(input_video_path):
        emit("error", message=f"Input video not found: {input_video_path}")
        return

    device, dtype = get_device()
    emit("progress", step=0, total=2, message="Loading img2img model…")

    # Extract frames
    source = PILImage.open(input_video_path)
    source_frames = []
    try:
        while True:
            source_frames.append(source.copy().convert("RGB"))
            source.seek(source.tell() + 1)
    except EOFError:
        pass
    if not source_frames:
        source_frames = [source.convert("RGB")]

    pipeline_cls = StableDiffusionXLImg2ImgPipeline if is_xl else StableDiffusionImg2ImgPipeline
    kwargs = {"torch_dtype": dtype}
    if not is_xl:
        kwargs["safety_checker"] = None
        kwargs["requires_safety_checker"] = False

    pipe = pipeline_cls.from_pretrained(model_id, **kwargs)
    pipe = pipe.to(device)
    if device == "cpu":
        pipe.enable_attention_slicing()

    if seed < 0:
        seed = random.randint(0, 2**32 - 1)
    emit("seed", value=seed)
    generator = torch.Generator(device=device).manual_seed(seed)

    total = len(source_frames)
    result_frames = []

    for i, frame in enumerate(source_frames):
        emit("progress", step=i, total=total,
             message=f"Processing frame {i+1}/{total}…")
        result = pipe(
            prompt=prompt,
            negative_prompt=neg_prompt or None,
            image=frame,
            strength=strength,
            num_inference_steps=steps,
            guidance_scale=guidance,
            generator=generator,
        )
        result_frames.append(result.images[0])

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)

    frame_duration_ms = max(1, int(1000 / fps))
    result_frames[0].save(
        output_path,
        save_all=True,
        append_images=result_frames[1:],
        optimize=False,
        duration=frame_duration_ms,
        loop=0,
    )

    preview_path = output_path.replace(".gif", "_preview.png")
    result_frames[0].save(preview_path)

    emit("complete", output_path=output_path, preview_path=preview_path,
         num_frames=len(result_frames), fps=fps)


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    raw = sys.stdin.readline().strip()
    if not raw:
        emit("error", message="No input received")
        sys.exit(1)

    missing = check_deps()
    if missing:
        emit("missing_deps", packages=missing)
        sys.exit(2)

    try:
        config = json.loads(raw)
        mode = config.get("mode", "image")
        if mode == "video":
            generate_video(config)
        elif mode == "image_to_video":
            generate_image_to_video(config)
        elif mode == "video_to_video":
            generate_video_to_video(config)
        elif mode == "frame_to_frame":
            generate_frame_to_frame(config)
        elif mode == "audio":
            generate_audio(config)
        elif mode == "audio_to_text":
            audio_to_text(config)
        elif mode == "image_to_image":
            generate_image_to_image(config)
        elif mode == "image_edit":
            generate_image_edit(config)
        elif mode == "inpaint":
            generate_inpaint(config)
        elif mode == "paint_to_image":
            generate_paint_to_image(config)
        else:
            generate_image(config)
    except Exception as e:
        emit("error", message=str(e), traceback=traceback.format_exc())
        sys.exit(1)
