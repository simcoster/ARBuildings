"""SAM 2 video extract, track, and export helpers."""
from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path

import cv2
import numpy as np
import torch
from PIL import Image

ROOT = Path(__file__).resolve().parent
VIDEO_DIR = ROOT / "Videos"
WORK_ROOT = ROOT / "batch_work"
EXPORT_ROOT = ROOT / "exports"
WEIGHTS = ROOT / "models" / "sam2.1_b.pt"


def list_videos() -> list[Path]:
    found: dict[str, Path] = {}
    if not VIDEO_DIR.exists():
        return []
    for path in sorted(VIDEO_DIR.glob("*.mp4")):
        found[path.name] = path
    return list(found.values())


def ffmpeg_bin() -> str | None:
    return shutil.which("ffmpeg")


def work_dir(video: Path, stride: int) -> Path:
    return WORK_ROOT / f"video_{video.stem}_s{int(stride)}"


def extract_frames(video: Path, stride: int) -> list[tuple[int, Path]]:
    stride = max(1, int(stride))
    out = work_dir(video, stride)
    meta_path = out / "meta.json"
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        if meta.get("stride") == stride and meta.get("video") == video.name:
            frames = sorted(out.glob("f*.jpg"))
            if frames:
                return [(int(p.stem[1:]), p) for p in frames]
    out.mkdir(parents=True, exist_ok=True)
    for old in out.glob("f*.jpg"):
        old.unlink()
    cap = cv2.VideoCapture(str(video))
    if not cap.isOpened():
        raise RuntimeError(f"cannot open {video}")
    written: list[tuple[int, Path]] = []
    idx = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        if idx % stride == 0:
            path = out / f"f{idx:06d}.jpg"
            cv2.imwrite(str(path), frame, [int(cv2.IMWRITE_JPEG_QUALITY), 95])
            written.append((idx, path))
        idx += 1
    cap.release()
    meta_path.write_text(
        json.dumps({"video": video.name, "stride": stride, "count": len(written)}, indent=2),
        encoding="utf-8",
    )
    return written


def mask_path_for(jpg: Path) -> Path:
    return jpg.with_suffix(".png")


def load_mask_png(path: Path, shape: tuple[int, int]) -> np.ndarray | None:
    if not path.exists():
        return None
    arr = np.array(Image.open(path).convert("L"))
    h, w = shape
    if arr.shape != (h, w):
        arr = np.array(Image.fromarray(arr).resize((w, h), Image.Resampling.NEAREST))
    return arr


def save_mask_png(path: Path, mask: np.ndarray) -> None:
    Image.fromarray(mask.astype(np.uint8)).save(path)


def encode_clip(paths: list[Path], mp4: Path, fps: float = 12.0) -> int:
    if len(paths) < 2:
        raise RuntimeError("need at least 2 frames to track")
    tmp = mp4.parent / "_clip_in"
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True, exist_ok=True)
    for i, path in enumerate(paths):
        shutil.copyfile(path, tmp / f"{i:06d}.jpg")
    ff = ffmpeg_bin()
    mp4.parent.mkdir(parents=True, exist_ok=True)
    if ff:
        cmd = [
            ff,
            "-y",
            "-framerate",
            str(fps),
            "-i",
            str(tmp / "%06d.jpg"),
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            "-crf",
            "18",
            str(mp4),
        ]
        proc = subprocess.run(cmd, capture_output=True, text=True)
        if proc.returncode != 0:
            raise RuntimeError(proc.stderr[-800:] or "ffmpeg failed")
    else:
        first = cv2.imread(str(paths[0]))
        if first is None:
            raise RuntimeError(f"could not read {paths[0]}")
        h, w = first.shape[:2]
        vw = cv2.VideoWriter(str(mp4), cv2.VideoWriter_fourcc(*"MJPG"), fps, (w, h))
        if not vw.isOpened():
            raise RuntimeError("could not open video writer")
        for path in paths:
            im = cv2.imread(str(path))
            if im is None:
                continue
            if im.shape[0] != h or im.shape[1] != w:
                im = cv2.resize(im, (w, h))
            vw.write(im)
        vw.release()
    cap = cv2.VideoCapture(str(mp4))
    n = 0
    while True:
        ok, _ = cap.read()
        if not ok:
            break
        n += 1
    cap.release()
    if n != len(paths):
        raise RuntimeError(f"clip has {n} frames, expected {len(paths)}")
    return n


def _result_binary(result, hw: tuple[int, int]) -> np.ndarray | None:
    if result is None or result.masks is None:
        return None
    arr = result.masks.data.cpu().numpy()
    if arr.size == 0:
        return None
    binary = (arr.max(axis=0) > 0.5).astype(np.uint8) * 255
    h, w = hw
    if binary.shape != (h, w):
        binary = np.array(Image.fromarray(binary).resize((w, h), Image.Resampling.NEAREST))
    return binary


def bbox_from_mask(mask: np.ndarray, pad: int = 12) -> list[int] | None:
    ys, xs = np.where(mask > 0)
    if xs.size == 0:
        return None
    h, w = mask.shape[:2]
    return [
        max(0, int(xs.min()) - pad),
        max(0, int(ys.min()) - pad),
        min(w - 1, int(xs.max()) + pad),
        min(h - 1, int(ys.max()) + pad),
    ]


def sample_mask_points(mask: np.ndarray, n_pos: int = 8) -> list[list[int]]:
    inner = cv2.erode((mask > 0).astype(np.uint8), np.ones((21, 21), np.uint8), 1)
    ys, xs = np.where(inner > 0)
    if xs.size == 0:
        ys, xs = np.where(mask > 0)
    if xs.size == 0:
        return []
    n = min(n_pos, int(xs.size))
    idx = np.linspace(0, xs.size - 1, n, dtype=int)
    return [[int(xs[i]), int(ys[i])] for i in idx]


def image_prompt_mask(image_model, jpg: Path, prompt: np.ndarray) -> np.ndarray | None:
    box = bbox_from_mask(prompt)
    points = sample_mask_points(prompt)
    if box is None or image_model is None:
        return None
    kwargs = dict(
        source=str(jpg),
        bboxes=[box],
        device=0,
        verbose=False,
        save=False,
    )
    if points:
        kwargs["points"] = [points]
        kwargs["labels"] = [[1] * len(points)]
    results = image_model.predict(**kwargs)
    return _result_binary(results[0], prompt.shape[:2])


def track_forward(
    paths: list[Path],
    prompt_mask: np.ndarray,
    weights: Path | None = None,
    image_model=None,
) -> tuple[list[np.ndarray | None], dict]:
    """Track prompt_mask from paths[0] through the rest.

    Index 0 is the prompt frame. Later frames prefer SAM2 video; empty
    frames are filled with image-SAM from the last good mask.
    """
    from ultralytics.models.sam import SAM2VideoPredictor

    if len(paths) < 2:
        raise RuntimeError("need at least 2 frames to track")
    weights = Path(weights) if weights else WEIGHTS
    work = paths[0].parent
    mp4 = work / "sam_track.mp4"
    encode_clip(paths, mp4)
    first = np.array(Image.open(paths[0]).convert("RGB"))
    h, w = first.shape[:2]
    mask = prompt_mask.astype(np.uint8)
    if mask.max() == 1:
        mask = mask * 255
    if mask.shape != (h, w):
        mask = np.array(Image.fromarray(mask).resize((w, h), Image.Resampling.NEAREST))
    box = bbox_from_mask(mask)
    pts = sample_mask_points(mask)
    if box is None:
        raise RuntimeError("prompt mask is empty")
    pred = SAM2VideoPredictor(
        overrides=dict(
            model=str(weights),
            conf=0.25,
            task="segment",
            mode="predict",
            imgsz=1024,
            save=False,
            verbose=False,
            device=0,
            retina_masks=True,
        )
    )
    pred.inference_state = {}
    pred.prompts = {}
    prompt = {"bboxes": [box]}
    if pts:
        prompt["points"] = [pts]
        prompt["labels"] = [[1] * len(pts)]
    pred.set_prompts(prompt)
    try:
        results = list(
            pred(
                source=str(mp4),
                stream=True,
                bboxes=[box],
                points=[pts] if pts else None,
                labels=[[1] * len(pts)] if pts else None,
            )
        )
    finally:
        del pred
        torch.cuda.empty_cache()
    print(
        f"SAM2 video: clip_frames={len(paths)} results={len(results)} "
        f"masks={[0 if r is None or r.masks is None else int(r.masks.data.numel()) for r in results[:8]]}",
        flush=True,
    )
    out: list[np.ndarray | None] = [mask]
    last = mask
    n_video = 0
    n_fallback = 0
    for j in range(1, len(paths)):
        binary = _result_binary(results[j], (h, w)) if j < len(results) else None
        if binary is not None and binary.any():
            n_video += 1
            last = binary
            out.append(binary)
            continue
        fb = image_prompt_mask(image_model, paths[j], last)
        if fb is not None and fb.any():
            n_fallback += 1
            last = fb
            out.append(fb)
        else:
            out.append(None)
    return out, {"video": n_video, "fallback": n_fallback, "results": len(results)}


def export_sequence(
    items: list[tuple[int, Path]],
    dest: Path,
    overlay_fn,
) -> Path:
    dest.mkdir(parents=True, exist_ok=True)
    n_mask = 0
    for src_idx, jpg in items:
        frame = np.array(Image.open(jpg).convert("RGB"))
        Image.fromarray(frame).save(dest / f"{src_idx:06d}.jpg", quality=95)
        png = mask_path_for(jpg)
        if png.exists():
            shutil.copyfile(png, dest / f"{src_idx:06d}.png")
            mask = np.array(Image.open(png).convert("L"))
            Image.fromarray(overlay_fn(frame, mask)).save(
                dest / f"{src_idx:06d}_overlay.jpg", quality=85
            )
            n_mask += 1
    (dest / "README.txt").write_text(
        f"{len(items)} frames, {n_mask} masks\n",
        encoding="utf-8",
    )
    return dest
