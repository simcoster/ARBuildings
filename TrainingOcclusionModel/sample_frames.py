"""Sample N frames from Videos/*.mp4, allocated by clip duration."""
from __future__ import annotations

import math
from pathlib import Path

import cv2

ROOT = Path(__file__).resolve().parent
VIDEO_DIR = ROOT / "Videos"
OUT_DIR = ROOT / "frames"
TOTAL = 200
JPEG_QUALITY = 95


def probe(path: Path) -> dict:
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise RuntimeError(f"cannot open {path}")
    n = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    fps = float(cap.get(cv2.CAP_PROP_FPS)) or 30.0
    cap.release()
    return {"path": path, "frames": n, "fps": fps, "duration": n / fps}


def allocate(durations: list[float], total: int) -> list[int]:
    """Largest-remainder so shares sum to exactly `total`."""
    s = sum(durations)
    if s <= 0:
        raise RuntimeError("all videos have zero duration")
    raw = [d / s * total for d in durations]
    floors = [int(math.floor(x)) for x in raw]
    leftover = total - sum(floors)
    order = sorted(range(len(raw)), key=lambda i: raw[i] - floors[i], reverse=True)
    counts = floors[:]
    for i in order[:leftover]:
        counts[i] += 1
    return counts


def sample_indices(n_frames: int, k: int) -> list[int]:
    """k evenly spaced indices, midpoints of equal bins (avoids always hitting 0 / last)."""
    if k <= 0:
        return []
    if k >= n_frames:
        return list(range(n_frames))
    return [min(n_frames - 1, int((i + 0.5) * n_frames / k)) for i in range(k)]


def grab(path: Path, indices: list[int]) -> list[tuple[int, object]]:
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise RuntimeError(f"cannot open {path}")
    out = []
    want = set(indices)
    next_i = 0
    idx = 0
    while next_i < len(indices):
        ok, frame = cap.read()
        if not ok:
            break
        if idx in want:
            out.append((idx, frame.copy()))
            next_i += 1
            # skip duplicates in want if two bins land on the same frame
            while next_i < len(indices) and indices[next_i] == idx:
                next_i += 1
        idx += 1
    cap.release()
    return out


def main() -> None:
    videos = sorted(VIDEO_DIR.glob("*.mp4"))
    if not videos:
        raise SystemExit(f"no mp4 files in {VIDEO_DIR}")

    info = [probe(p) for p in videos]
    counts = allocate([v["duration"] for v in info], TOTAL)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for old in OUT_DIR.glob("*.jpg"):
        old.unlink()

    written = 0
    print(f"{'video':<24} {'dur':>7} {'frames':>7} {'share':>6}  indices")
    for i, (v, k) in enumerate(zip(info, counts)):
        idxs = sample_indices(v["frames"], k)
        grabbed = grab(v["path"], idxs)
        stem = v["path"].stem
        print(
            f"{v['path'].name:<24} {v['duration']:6.2f}s {v['frames']:7d} {k:6d}  {idxs}"
        )
        for j, (fi, frame) in enumerate(grabbed):
            name = f"{written:02d}_{stem}_f{fi:06d}.jpg"
            cv2.imwrite(
                str(OUT_DIR / name),
                frame,
                [int(cv2.IMWRITE_JPEG_QUALITY), JPEG_QUALITY],
            )
            written += 1
        if len(grabbed) != k:
            print(f"  warning: wanted {k}, got {len(grabbed)}")

    print(f"\nwrote {written} frames -> {OUT_DIR}")


if __name__ == "__main__":
    main()
