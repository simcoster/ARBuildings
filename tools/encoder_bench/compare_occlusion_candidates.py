#!/usr/bin/env python
"""Run every occlusion candidate on one real frame and render what the app would draw.

This is a desk stand-in for SemanticOcclusion + NpuSegmenter, deliberately duplicating
their preprocessing and decode rather than approximating it: the input transpose, the
scalar normalisation, the NCHW/NHWC argmax and the min-max stretch are all the places a
model has already produced a believable wrong answer on this project. Getting it wrong
here costs seconds; getting it wrong on the phone costs a four-minute rebuild.

    python compare_occlusion_candidates.py --frame ../../tmp-occ-exp/cmp/seg_input.png
"""

from __future__ import annotations

import argparse
import os
import time

import numpy as np
import tensorflow as tf
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, "occ_models")

PASCAL = [
    "background", "aeroplane", "bicycle", "bird", "boat", "bottle", "bus", "car", "cat",
    "chair", "cow", "diningtable", "dog", "horse", "motorbike", "person", "pottedplant",
    "sheep", "sofa", "train", "tvmonitor",
]
CITYSCAPES = [
    "road", "sidewalk", "building", "wall", "fence", "pole", "traffic light",
    "traffic sign", "vegetation", "terrain", "sky", "person", "rider", "car", "truck",
    "bus", "train", "motorcycle", "bicycle",
]

# Mirrors SemanticOcclusion.ApplyModelDefaults. A single scalar stands in for the
# per-channel ImageNet mean and std, which shifts colour balance and no structure.
def normalisation(name: str) -> tuple[float, float]:
    n = name.lower()
    if "modnet" in n or "deeplab" in n or "coral" in n:
        return 127.5, 127.5
    if "isnet" in n or n.startswith("dis"):
        return 127.5, 255.0
    return 123.7, 58.4


def ramp(t: np.ndarray) -> np.ndarray:
    """Mirrors SemanticOcclusion.Ramp: blue -> green -> red, high is near / present."""
    t = np.clip(t, 0.0, 1.0)
    r = np.clip(1.5 - np.abs(4.0 * t - 3.0), 0, 1)
    g = np.clip(1.5 - np.abs(4.0 * t - 2.0), 0, 1)
    b = np.clip(1.5 - np.abs(4.0 * t - 1.0), 0, 1)
    return (np.stack([r, g, b], -1) * 255).astype(np.uint8)


def palette(k: int) -> np.ndarray:
    """A stable, high-contrast colour per class id."""
    rng = np.random.default_rng(7)
    p = rng.integers(60, 256, size=(k, 3), dtype=np.uint8)
    p[0] = (0, 0, 0)  # class 0 is background / road / stuff and is never painted
    return p


def run(path: str, frame: Image.Image) -> dict:
    it = tf.lite.Interpreter(model_path=path, num_threads=4)
    it.allocate_tensors()
    inp, out = it.get_input_details()[0], it.get_output_details()[0]
    ish, osh = list(inp["shape"]), list(out["shape"])

    nchw_in = len(ish) == 4 and ish[1] <= 4 < ish[2]
    ih, iw = (ish[2], ish[3]) if nchw_in else (ish[1], ish[2])

    mean, scale = normalisation(os.path.basename(path))
    rgb = np.asarray(frame.resize((iw, ih), Image.BILINEAR), dtype=np.float32)
    x = (rgb - mean) / scale
    x = x.transpose(2, 0, 1)[None] if nchw_in else x[None]

    it.set_tensor(inp["index"], x.astype(inp["dtype"]))
    t0 = time.perf_counter()
    it.invoke()
    ms = (time.perf_counter() - t0) * 1e3
    y = it.get_tensor(out["index"])

    # Mirrors NpuSegmenter.describe: [1,C,H,W] with C>4 is channel-major logits.
    if len(osh) == 4 and osh[1] > 4 and osh[3] <= 64:
        logits, nchw_out = y[0], False                    # NHWC
    elif len(osh) == 4:
        logits, nchw_out = y[0].transpose(1, 2, 0), True   # NCHW -> HWC
    else:
        logits, nchw_out = y[0][..., None], False
    c = logits.shape[-1]

    # NpuSegmenter.scalarOutput gates on FLOAT32 as well as channel count: an already-argmaxed
    # label map is one channel too, and DeepLab exports it as INT64. Without the dtype gate a
    # perfectly good label map decodes as a depth ramp of class ids.
    if c > 1 or out["dtype"] != np.float32:
        labels = (logits.argmax(-1) if c > 1 else logits[..., 0]).astype(np.uint8)
        names = CITYSCAPES if c == 19 else PASCAL
        hist = [(names[i] if i < len(names) else str(i), int(n))
                for i, n in enumerate(np.bincount(labels.ravel(), minlength=21)) if n]
        hist.sort(key=lambda t: -t[1])
        pal = palette(max(c, 21))
        vis = pal[labels]
        vis[labels == 0] = 0
        return dict(kind=f"labels x{c}{' NCHW' if nchw_out else ''}", ms=ms, vis=vis,
                    note=", ".join(f"{n}={v}" for n, v in hist[:4]),
                    raw=f"{logits.min():.3f}..{logits.max():.3f}")

    # Mirrors NpuSegmenter.decodeScalar: a matte is absolute, a depth map is stretched.
    v = logits[..., 0].astype(np.float32)
    lo, hi = float(v.min()), float(v.max())
    absolute = -0.05 <= lo and hi <= 1.05
    t = np.clip(v, 0, 1) if absolute else (
        (v - lo) / (hi - lo) if hi - lo > 1e-9 else np.zeros_like(v))
    code = (t * 255 + 0.5).astype(np.uint8)
    vis = ramp(code / 255.0)
    vis[code < 8] = 0  # scalarFloor, so an empty matte reads as empty
    return dict(kind="alpha (absolute)" if absolute else "depth (stretched)", ms=ms,
                vis=vis, note=f"painted {100.0 * (code >= 8).mean():.1f}%",
                raw=f"{lo:.4f}..{hi:.4f}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--frame", required=True)
    ap.add_argument("--out", default=os.path.join(HERE, "occ_compare"))
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    frame = Image.open(args.frame).convert("RGB")
    side = 384
    base = frame.resize((side, side), Image.BILINEAR)

    names = sorted(f for f in os.listdir(MODELS) if f.endswith(".tflite"))
    cells = [("camera frame (network input)", np.asarray(base), "", "")]
    for n in names:
        print(f"[run ] {n}")
        try:
            r = run(os.path.join(MODELS, n), frame)
        except Exception as e:  # noqa: BLE001
            print(f"       FAILED {type(e).__name__}: {e}")
            cells.append((n, np.zeros((side, side, 3), np.uint8), "FAILED", str(e)[:60]))
            continue
        print(f"       {r['kind']:<22} {r['ms']:7.0f} ms  raw {r['raw']}  {r['note']}")

        # 0.55 tint over the frame, the same mix ARCoreBackgroundMasked.shader applies.
        over = np.asarray(Image.fromarray(r["vis"]).resize((side, side), Image.NEAREST))
        lit = over.sum(-1, keepdims=True) > 12
        comp = np.where(lit, (0.45 * np.asarray(base) + 0.55 * over), np.asarray(base))
        cells.append((n.replace(".tflite", ""), comp.astype(np.uint8),
                      f"{r['kind']}  {r['ms']:.0f} ms", f"raw {r['raw']}  {r['note']}"))
        Image.fromarray(comp.astype(np.uint8)).save(
            os.path.join(args.out, n.replace(".tflite", ".png")))

    cols, pad, hdr = 4, 8, 40
    rows = (len(cells) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * (side + pad) + pad,
                              rows * (side + hdr + pad) + pad), (18, 18, 20))
    d = ImageDraw.Draw(sheet)
    for i, (title, img, sub, sub2) in enumerate(cells):
        x = pad + (i % cols) * (side + pad)
        y = pad + (i // cols) * (side + hdr + pad)
        sheet.paste(Image.fromarray(img), (x, y))
        d.text((x + 2, y + side + 2), title[:52], fill=(255, 255, 255))
        d.text((x + 2, y + side + 14), sub[:60], fill=(150, 220, 150))
        d.text((x + 2, y + side + 26), sub2[:60], fill=(170, 170, 200))

    grid = os.path.join(args.out, "grid.png")
    sheet.save(grid)
    print(f"\n{grid}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
