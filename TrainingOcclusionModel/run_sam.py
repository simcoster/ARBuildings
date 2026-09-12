"""Run local SAM 2.1 (GPU) on a walkthrough frame.

Uses the bounding box of an existing mask as the prompt. Writes a pink
overlay next to the other mask previews. Does not overwrite official
{stem}.png masks.
"""
from __future__ import annotations

import argparse
import time
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from ultralytics import SAM

ROOT = Path(__file__).resolve().parent
FRAMES_DIR = ROOT / "frames"
MASKS_DIR = ROOT / "masks"
WEIGHTS = ROOT / "models" / "sam2.1_b.pt"
OVERLAY_PINK = np.array([255.0, 0.0, 80.0])
OVERLAY_ALPHA = 0.65


def bbox_from_mask(mask: np.ndarray) -> list[int]:
    ys, xs = np.where(mask > 127)
    if xs.size == 0:
        raise SystemExit("existing mask is empty; need a box prompt")
    pad = 8
    h, w = mask.shape
    return [
        max(0, int(xs.min()) - pad),
        max(0, int(ys.min()) - pad),
        min(w - 1, int(xs.max()) + pad),
        min(h - 1, int(ys.max()) + pad),
    ]


def pink_overlay(frame: np.ndarray, binary: np.ndarray) -> np.ndarray:
    m = (binary > 0)[..., None].astype(np.float32)
    painted = frame.astype(np.float32) * (1.0 - OVERLAY_ALPHA) + OVERLAY_PINK * OVERLAY_ALPHA
    return np.clip(frame.astype(np.float32) * (1.0 - m) + painted * m, 0, 255).astype(np.uint8)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--stem", default="47_20260830_183455_f000003")
    parser.add_argument("--weights", default=str(WEIGHTS))
    args = parser.parse_args()

    frame_path = FRAMES_DIR / f"{args.stem}.jpg"
    mask_path = MASKS_DIR / f"{args.stem}.png"
    if not frame_path.exists():
        raise SystemExit(f"missing frame {frame_path}")
    if not mask_path.exists():
        raise SystemExit(f"missing prompt mask {mask_path}")
    if not Path(args.weights).exists():
        raise SystemExit(f"missing weights {args.weights}")

    if not torch.cuda.is_available():
        raise SystemExit("CUDA is not available; SAM would run on CPU")

    frame = np.array(Image.open(frame_path).convert("RGB"))
    prompt_mask = np.array(Image.open(mask_path).convert("L"))
    box = bbox_from_mask(prompt_mask)
    print("device", torch.cuda.get_device_name(0))
    print("weights", args.weights)
    print("frame", frame_path.name, frame.shape)
    print("prompt box", box)

    model = SAM(args.weights)
    t0 = time.perf_counter()
    results = model.predict(
        source=str(frame_path),
        bboxes=[box],
        device=0,
        verbose=True,
    )
    dt = time.perf_counter() - t0
    r = results[0]
    if r.masks is None:
        raise SystemExit("SAM returned no masks")
    sam = r.masks.data.cpu().numpy()
    binary = (sam.max(axis=0) > 0.5).astype(np.uint8) * 255
    if binary.shape != prompt_mask.shape:
        binary = np.array(
            Image.fromarray(binary).resize(
                (prompt_mask.shape[1], prompt_mask.shape[0]),
                Image.Resampling.NEAREST,
            )
        )
    overlay = pink_overlay(frame, binary)
    out_overlay = MASKS_DIR / f"{args.stem}_sam2_overlay.jpg"
    Image.fromarray(overlay).save(out_overlay, quality=85)
    print(f"sam white px {int(np.count_nonzero(binary))}  prompt white px {int(np.count_nonzero(prompt_mask))}")
    print(f"elapsed {dt:.1f}s  vram {torch.cuda.max_memory_allocated()/1024**3:.2f} GB")
    print("wrote", out_overlay)


if __name__ == "__main__":
    main()
