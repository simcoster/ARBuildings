"""Registry of off-the-shelf image encoders, wrapped to one interface.

Every model here is used as-is from public weights — no fine-tuning. Each loads to
a module with the signature

    forward(pixel_values: [B, 3, H, W] float) -> [B, D] float

so the benchmark can treat them interchangeably. For the dual-tower models
(CLIP, SigLIP) only the *vision* tower is benchmarked; the text tower never runs
at inference time for an image-embedding workload, and the projection head that
follows the tower is a single matmul whose cost is far below measurement noise.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Sequence

import torch
import torch.nn as nn


@dataclass(frozen=True)
class ModelSpec:
    name: str
    backend: str  # "hf" | "timm" | "open_clip"
    model_id: str
    tier: str  # "server" | "mobile"
    notes: str = ""


# Ordered roughly cheapest-first within each tier.
SPECS: Sequence[ModelSpec] = (
    # --- mobile tier: plausible to run on the A35 itself -------------------
    ModelSpec(
        "mobilenetv4-s", "timm", "mobilenetv4_conv_small.e2400_r224_in1k", "mobile",
        "pure conv, the floor of the range; NNAPI/XNNPACK like it best",
    ),
    ModelSpec(
        "fastvit-t8", "timm", "fastvit_t8.apple_in1k", "mobile",
        "Apple's hybrid conv/attn, designed for phone latency",
    ),
    ModelSpec(
        "efficientvit-b1", "timm", "efficientvit_b1.r224_in1k", "mobile",
        "linear-attention ViT, good accuracy per ms on mobile GPUs",
    ),
    ModelSpec(
        "vit-tiny", "timm", "vit_tiny_patch16_224.augreg_in21k", "mobile",
        "plain ViT lower bound; useful as an architecture control vs ViT-B",
    ),
    ModelSpec(
        "mobileclip2-s0", "open_clip", "MobileCLIP2-S0:dfndr2b", "mobile",
        "CLIP-aligned embedding space at mobile cost; optional dep",
    ),
    # --- server tier: the phone/server split candidates --------------------
    ModelSpec(
        "dinov2-small", "hf", "facebook/dinov2-small", "server",
        "22M params but /14 patches = 257 tokens, so not as cheap as it looks",
    ),
    ModelSpec(
        "clip-vit-b32", "hf", "openai/clip-vit-base-patch32", "server",
        "the reference point everyone quotes; 50 tokens",
    ),
    ModelSpec(
        "clip-vit-b16", "hf", "openai/clip-vit-base-patch16", "server",
        "same params as b32, 4x the tokens — isolates token count from width",
    ),
    ModelSpec(
        "siglip2-base16", "hf", "google/siglip2-base-patch16-224", "server",
        "stronger representations at ViT-B/16 cost",
    ),
    ModelSpec(
        "dinov2-base", "hf", "facebook/dinov2-base", "server",
        "strongest structural features here; the expensive end",
    ),
)

REGISTRY = {s.name: s for s in SPECS}

DEFAULT_SET = [s.name for s in SPECS]


@dataclass
class LoadedModel:
    spec: ModelSpec
    module: nn.Module
    size: int
    mean: tuple[float, ...]
    std: tuple[float, ...]
    dim: int = 0
    param_millions: float = 0.0


class _HFVisionTower(nn.Module):
    """Pooled output from a transformers vision model."""

    def __init__(self, tower: nn.Module):
        super().__init__()
        self.tower = tower

    def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
        out = self.tower(pixel_values=pixel_values)
        pooled = getattr(out, "pooler_output", None)
        if pooled is None:
            # DINOv2 without a pooler, and any model that returns tokens only.
            pooled = out.last_hidden_state.mean(dim=1)
        return pooled


class _OpenClipVisual(nn.Module):
    def __init__(self, visual: nn.Module):
        super().__init__()
        self.visual = visual

    def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
        return self.visual(pixel_values)


def _processor_size(proc) -> int:
    """Square input edge from an image processor.

    transformers 5.x returns a SizeDict rather than a plain dict, and it is not a
    dict subclass — hence the attribute *and* mapping lookup.
    """
    # crop_size first: it is what the model actually consumes. DINOv2 resizes the
    # shortest edge to 256 then centre-crops to 224 — taking size gives 325 tokens
    # where the real config gives 257.
    for holder in (getattr(proc, "crop_size", None), getattr(proc, "size", None)):
        if holder is None:
            continue
        if isinstance(holder, int):
            return holder
        for key in ("shortest_edge", "height", "width"):
            val = getattr(holder, key, None)
            if val is None and isinstance(holder, dict):
                val = holder.get(key)
            if val:
                return int(val)
    raise ValueError(f"cannot determine input size from {proc.__class__.__name__}")


def _load_hf(spec: ModelSpec) -> LoadedModel:
    from transformers import AutoImageProcessor, AutoModel

    model = AutoModel.from_pretrained(spec.model_id)
    # CLIP/SigLIP load as dual-tower; keep the vision side only.
    tower = getattr(model, "vision_model", model)

    proc = AutoImageProcessor.from_pretrained(spec.model_id)

    return LoadedModel(
        spec=spec,
        module=_HFVisionTower(tower),
        size=_processor_size(proc),
        mean=tuple(proc.image_mean),
        std=tuple(proc.image_std),
    )


def _load_timm(spec: ModelSpec) -> LoadedModel:
    import timm
    from timm.data import resolve_model_data_config

    # num_classes=0 drops the classifier and returns pooled features.
    model = timm.create_model(spec.model_id, pretrained=True, num_classes=0)
    cfg = resolve_model_data_config(model)

    return LoadedModel(
        spec=spec,
        module=model,
        size=int(cfg["input_size"][-1]),
        mean=tuple(cfg["mean"]),
        std=tuple(cfg["std"]),
    )


def _load_open_clip(spec: ModelSpec) -> LoadedModel:
    import open_clip

    # "Name:pretrained_tag", e.g. "MobileCLIP2-S0:dfndr2b"
    arch, _, pretrained = spec.model_id.partition(":")
    model, _, preprocess = open_clip.create_model_and_transforms(
        arch, pretrained=pretrained or None)
    visual = model.visual

    mean, std = (0.5, 0.5, 0.5), (0.5, 0.5, 0.5)
    for t in getattr(preprocess, "transforms", []):
        if t.__class__.__name__ == "Normalize":
            mean, std = tuple(t.mean), tuple(t.std)
            break

    size = getattr(visual, "image_size", 224)
    if isinstance(size, (tuple, list)):
        size = size[0]

    return LoadedModel(
        spec=spec,
        module=_OpenClipVisual(visual),
        size=int(size),
        mean=mean,
        std=std,
    )


_LOADERS = {"hf": _load_hf, "timm": _load_timm, "open_clip": _load_open_clip}


def load(name: str) -> LoadedModel:
    """Load one model by registry name, in eval mode on CPU."""
    if name not in REGISTRY:
        raise KeyError(f"unknown model {name!r}; known: {', '.join(REGISTRY)}")
    spec = REGISTRY[name]

    loaded = _LOADERS[spec.backend](spec)
    loaded.module.eval().requires_grad_(False)

    loaded.param_millions = sum(p.numel() for p in loaded.module.parameters()) / 1e6

    # Probe the output width rather than hardcoding it per model.
    with torch.inference_mode():
        probe = loaded.module(torch.zeros(1, 3, loaded.size, loaded.size))
    loaded.dim = int(probe.shape[-1])

    return loaded
