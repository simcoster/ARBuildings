"""Local SAM 2.1 click-to-paint for synagogue occlusion masks.

SAM modes prompt the model. Brush modes stamp a circle on the mask
with no model call. Does not overwrite official {stem}.png files.
"""
from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path

import cv2
import gradio as gr
import numpy as np
import torch
from PIL import Image
from ultralytics import SAM

import sam_video as sv

ROOT = Path(__file__).resolve().parent
FRAMES_DIR = ROOT / "frames"
MASKS_DIR = ROOT / "masks"
WEIGHTS = ROOT / "models" / "sam2.1_b.pt"
OVERLAY_PINK = np.array([255.0, 0.0, 80.0], dtype=np.float32)
OVERLAY_ALPHA = 0.65

MODEL: SAM | None = None

SAM_PAINT = "SAM paint"
SAM_ERASE = "SAM erase"
BRUSH_PAINT = "Brush paint"
BRUSH_ERASE = "Brush erase"
VIEW_OVERLAY = "Overlay"
VIEW_MASK = "Mask"


def frame_stems() -> list[str]:
    paths = sorted(FRAMES_DIR.glob("*.jpg"), key=lambda p: int(p.name.split("_")[0]))
    return [p.stem for p in paths]


def load_frame(stem: str) -> np.ndarray:
    path = FRAMES_DIR / f"{stem}.jpg"
    if not path.exists():
        raise gr.Error(f"missing frame {path.name}")
    return np.array(Image.open(path).convert("RGB"))


def existing_masks(stem: str) -> list[Path]:
    names = [
        f"{stem}_sam2.png",
        f"{stem}_batch10.png",
        f"{stem}_norefine.png",
        f"{stem}.png",
    ]
    out = []
    seen = set()
    for name in names:
        path = MASKS_DIR / name
        if path.exists() and path.name not in seen:
            out.append(path)
            seen.add(path.name)
    for path in sorted(MASKS_DIR.glob(f"{stem}*.png")):
        if path.name not in seen and "_check_" not in path.name:
            out.append(path)
            seen.add(path.name)
    return out


def empty_state(stem: str) -> dict:
    frame = load_frame(stem)
    h, w = frame.shape[:2]
    return {
        "stem": stem,
        "frame": frame,
        "points": [],  # current SAM paint prompts (x, y, label)
        "markers": [],  # visible click markers (x, y, label) 1=paint 0=erase
        "show_markers": True,
        "view": VIEW_OVERLAY,
        "committed": np.zeros((h, w), dtype=bool),
        "current": np.zeros((h, w), dtype=np.uint8),
        "history": [],
        "status": "SAM click = model. Brush = paint/erase pixels. Load a batch mask to edit it.",
    }


def pink_overlay(frame: np.ndarray, binary: np.ndarray) -> np.ndarray:
    m = (binary > 0)[..., None].astype(np.float32)
    painted = frame.astype(np.float32) * (1.0 - OVERLAY_ALPHA) + OVERLAY_PINK * OVERLAY_ALPHA
    return np.clip(frame.astype(np.float32) * (1.0 - m) + painted * m, 0, 255).astype(np.uint8)


def mask_rgb(binary: np.ndarray) -> np.ndarray:
    return np.stack([binary, binary, binary], axis=-1)


def draw_points(overlay: np.ndarray, points: list[tuple[int, int, int]]) -> np.ndarray:
    out = overlay.copy()
    for x, y, label in points:
        color = (40, 220, 40) if label == 1 else (40, 40, 230)
        cv2.circle(out, (int(x), int(y)), 18, (0, 0, 0), 5, cv2.LINE_AA)
        cv2.circle(out, (int(x), int(y)), 18, color, 3, cv2.LINE_AA)
        cv2.circle(out, (int(x), int(y)), 5, (255, 255, 255), -1, cv2.LINE_AA)
    return out


def combined_mask(state: dict) -> np.ndarray:
    return np.where(state["committed"] | (state["current"] > 0), 255, 0).astype(np.uint8)


def render(state: dict) -> tuple[np.ndarray, str]:
    mask = combined_mask(state)
    if state.get("view") == VIEW_MASK:
        overlay = mask_rgb(mask)
    else:
        overlay = pink_overlay(state["frame"], mask)
    if state.get("show_markers", True):
        overlay = draw_points(overlay, state.get("markers") or [])
    white = int(np.count_nonzero(mask))
    n_pos = sum(1 for *_, lab in state.get("markers") or [] if lab == 1)
    n_neg = sum(1 for *_, lab in state.get("markers") or [] if lab != 1)
    vis = "on" if state.get("show_markers", True) else "off"
    view = "mask" if state.get("view") == VIEW_MASK else "overlay"
    status = (
        f"{state['stem']}  |  {view}  |  {white} white px  |  "
        f"markers {n_pos}/{n_neg} ({vis})  |  {state['status']}"
    )
    return overlay, status


def add_marker(state: dict, x: int, y: int, label: int) -> None:
    markers = state.setdefault("markers", [])
    markers.append((int(x), int(y), int(label)))
    if len(markers) > 200:
        state["markers"] = markers[-200:]


def push_history(state: dict) -> None:
    state["history"].append(
        (
            state["committed"].copy(),
            state["current"].copy(),
            list(state["points"]),
            list(state.get("markers") or []),
        )
    )
    if len(state["history"]) > 80:
        state["history"] = state["history"][-80:]


def frame_source(state: dict) -> str:
    if state.get("kind") == "video":
        return state["paths"][state["idx"]]
    return str(FRAMES_DIR / f"{state['stem']}.jpg")


def predict_sam(state: dict, points: list[list[int]], labels: list[int]) -> np.ndarray | None:
    assert MODEL is not None
    results = MODEL.predict(
        source=frame_source(state),
        points=[points],
        labels=[labels],
        device=0,
        verbose=False,
        save=False,
    )
    r = results[0]
    if r.masks is None:
        return None
    sam = r.masks.data.cpu().numpy()
    if sam.size == 0:
        return None
    binary = (sam.max(axis=0) > 0.5).astype(np.uint8) * 255
    h, w = state["frame"].shape[:2]
    if binary.shape != (h, w):
        binary = np.array(
            Image.fromarray(binary).resize((w, h), Image.Resampling.NEAREST)
        )
    return binary


def run_sam(state: dict) -> None:
    pts = state["points"]
    if not any(lab == 1 for *_, lab in pts):
        state["current"] = np.zeros(state["frame"].shape[:2], dtype=np.uint8)
        state["status"] = "Need at least one SAM paint click on the building."
        return
    points = [[int(x), int(y)] for x, y, _ in pts]
    labels = [int(lab) for *_, lab in pts]
    binary = predict_sam(state, points, labels)
    if binary is None:
        state["status"] = "SAM returned no mask for these clicks."
        return
    state["current"] = binary
    state["status"] = "SAM paint updated. Add region to keep it, or brush-edit."


def sam_erase_at(state: dict, x: int, y: int) -> None:
    binary = predict_sam(state, [[x, y]], [1])
    if binary is None:
        state["status"] = "SAM erase got no mask at that click."
        return
    region = binary > 0
    state["committed"] &= ~region
    state["current"][region] = 0
    add_marker(state, x, y, 0)
    state["status"] = f"SAM erase cut {int(region.sum())} px."


def stamp_stroke(state: dict, points: list, radius: int, paint: bool) -> None:
    if not points:
        return
    h, w = state["frame"].shape[:2]
    layer = np.zeros((h, w), dtype=np.uint8)
    r = max(1, int(radius))
    pts = []
    for x, y in points:
        pts.append(
            (
                int(np.clip(round(float(x)), 0, w - 1)),
                int(np.clip(round(float(y)), 0, h - 1)),
            )
        )
    for x, y in pts:
        cv2.circle(layer, (x, y), r, 255, -1, lineType=cv2.LINE_AA)
    if len(pts) >= 2:
        cv2.polylines(
            layer,
            [np.array(pts, dtype=np.int32)],
            isClosed=False,
            color=255,
            thickness=max(1, 2 * r),
            lineType=cv2.LINE_AA,
        )
    dab = layer > 0
    if paint:
        state["committed"] |= dab
        state["status"] = f"Brush paint stroke ({len(pts)} pts, r={r})."
    else:
        state["committed"] &= ~dab
        state["current"][dab] = 0
        state["status"] = f"Brush erase stroke ({len(pts)} pts, r={r})."


def apply_mask_png(state: dict, path: Path) -> None:
    arr = np.array(Image.open(path).convert("L"))
    h, w = state["frame"].shape[:2]
    if arr.shape != (h, w):
        arr = np.array(Image.fromarray(arr).resize((w, h), Image.Resampling.NEAREST))
    state["committed"] = arr > 127
    state["current"] = np.zeros((h, w), dtype=np.uint8)
    state["points"] = []
    state["markers"] = []
    state["status"] = f"Loaded {path.name}."


def on_load(stem: str, view: str = VIEW_OVERLAY) -> tuple[dict, np.ndarray, str]:
    state = empty_state(stem)
    state["view"] = view or VIEW_OVERLAY
    paths = existing_masks(stem)
    if paths:
        apply_mask_png(state, paths[0])
    overlay, status = render(state)
    return state, overlay, status


def on_click(
    state: dict, mode: str, radius: float, evt: gr.SelectData
) -> tuple[dict, np.ndarray, str]:
    if state is None:
        raise gr.Error("Load a frame first.")
    x, y = evt.index
    h, w = state["frame"].shape[:2]
    x = int(np.clip(round(x), 0, w - 1))
    y = int(np.clip(round(y), 0, h - 1))
    push_history(state)
    if str(mode).startswith("Brush"):
        stamp_stroke(state, [(x, y)], int(radius), paint=(mode == BRUSH_PAINT))
    elif mode == SAM_ERASE:
        sam_erase_at(state, x, y)
    else:
        state["points"].append((x, y, 1))
        add_marker(state, x, y, 1)
        run_sam(state)
    overlay, status = render(state)
    return state, overlay, status


def on_add_region(state: dict) -> tuple[dict, np.ndarray, str]:
    push_history(state)
    if state["current"].any():
        state["committed"] |= state["current"] > 0
        state["current"] = np.zeros_like(state["current"])
        state["points"] = []
        state["status"] = "Region saved. SAM-click the next piece, or brush-edit."
    else:
        state["status"] = "Nothing to add — SAM-click the building first."
    overlay, status = render(state)
    return state, overlay, status


def on_undo(state: dict) -> tuple[dict, np.ndarray, str]:
    if not state["history"]:
        state["status"] = "Nothing to undo."
    else:
        committed, current, points, *rest = state["history"].pop()
        state["committed"] = committed
        state["current"] = current
        state["points"] = points
        if rest:
            state["markers"] = rest[0]
        state["status"] = "Undid last edit."
    overlay, status = render(state)
    return state, overlay, status


def clear_video_mask_files(jpg: Path) -> bool:
    cleared = False
    png = sv.mask_path_for(jpg)
    if png.exists():
        png.unlink()
        cleared = True
    mj = jpg.with_suffix(".markers.json")
    if mj.exists():
        mj.unlink()
    return cleared


def reset_video_frame_memory(state: dict) -> None:
    show = state.get("show_markers", True)
    view = state.get("view", VIEW_OVERLAY)
    jpg = Path(state["paths"][state["idx"]])
    frame = np.array(Image.open(jpg).convert("RGB"))
    h, w = frame.shape[:2]
    state["frame"] = frame
    state["committed"] = np.zeros((h, w), dtype=bool)
    state["current"] = np.zeros((h, w), dtype=np.uint8)
    state["points"] = []
    state["markers"] = []
    state["history"] = []
    state["show_markers"] = show
    state["view"] = view


def on_reset(state: dict) -> tuple[dict, np.ndarray, str]:
    show = state.get("show_markers", True)
    view = state.get("view", VIEW_OVERLAY)
    if state.get("kind") == "video":
        jpg = Path(state["paths"][state["idx"]])
        reset_video_frame_memory(state)
        clear_video_mask_files(jpg)
        state["status"] = "Reset this video frame."
        overlay, status = render(state)
        return state, overlay, status
    state = empty_state(state["stem"])
    state["show_markers"] = show
    state["view"] = view
    overlay, status = render(state)
    return state, overlay, status


def on_video_reset_forward(state: dict, n: float):
    if not state or state.get("kind") != "video":
        raise gr.Error("Load a video first.")
    n = max(1, int(n))
    i = int(state["idx"])
    paths = state["paths"]
    end = min(len(paths), i + n)
    cleared = 0
    for j in range(i, end):
        if clear_video_mask_files(Path(paths[j])):
            cleared += 1
    reset_video_frame_memory(state)
    src0 = state["src_idx"][i]
    src1 = state["src_idx"][end - 1]
    state["status"] = (
        f"Cleared {cleared} masks over {end - i} frames "
        f"(extracted {i + 1}–{end}, video {src0}–{src1})."
    )
    return video_pack(state)


def on_load_mask(state: dict) -> tuple[dict, np.ndarray, str]:
    paths = existing_masks(state["stem"])
    if not paths:
        state["status"] = "No existing mask PNG for this frame."
        overlay, status = render(state)
        return state, overlay, status
    push_history(state)
    apply_mask_png(state, paths[0])
    overlay, status = render(state)
    return state, overlay, status


def on_toggle_markers(state: dict):
    if state is None:
        raise gr.Error("Load a frame first.")
    state["show_markers"] = not state.get("show_markers", True)
    overlay, status = render(state)
    label = "Hide markers" if state["show_markers"] else "Show markers"
    return state, overlay, status, gr.update(value=label)


def on_view_mode(state: dict, view: str):
    if state is None:
        raise gr.Error("Load a frame first.")
    state["view"] = view or VIEW_OVERLAY
    overlay, status = render(state)
    return state, overlay, status


def bbox_from_mask(mask: np.ndarray, pad: int = 12) -> list[int] | None:
    ys, xs = np.where(mask > 0)
    if xs.size == 0:
        return None
    h, w = mask.shape
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


def write_sam2(stem: str, frame: np.ndarray, binary: np.ndarray) -> None:
    MASKS_DIR.mkdir(parents=True, exist_ok=True)
    Image.fromarray(binary).save(MASKS_DIR / f"{stem}_sam2.png")
    Image.fromarray(pink_overlay(frame, binary)).save(
        MASKS_DIR / f"{stem}_sam2_overlay.jpg", quality=85
    )


def sam_prompt_mask(stem: str, prompt: np.ndarray) -> np.ndarray | None:
    assert MODEL is not None
    box = bbox_from_mask(prompt)
    points = sample_mask_points(prompt)
    if box is None or not points:
        return None
    results = MODEL.predict(
        source=str(FRAMES_DIR / f"{stem}.jpg"),
        bboxes=[box],
        points=[points],
        labels=[[1] * len(points)],
        device=0,
        verbose=False,
        save=False,
    )
    r = results[0]
    if r.masks is None:
        return None
    arr = r.masks.data.cpu().numpy()
    if arr.size == 0:
        return None
    binary = (arr.max(axis=0) > 0.5).astype(np.uint8) * 255
    frame = load_frame(stem)
    h, w = frame.shape[:2]
    if binary.shape != (h, w):
        binary = np.array(Image.fromarray(binary).resize((w, h), Image.Resampling.NEAREST))
    return binary


def on_propagate(state: dict, n_fwd: float) -> tuple[dict, np.ndarray, str]:
    mask = combined_mask(state)
    if not mask.any():
        state["status"] = "Need a mask first (load / SAM / brush)."
        overlay, status = render(state)
        return state, overlay, status
    stems = frame_stems()
    try:
        idx = stems.index(state["stem"])
    except ValueError:
        state["status"] = "Current frame is not in the walkthrough list."
        overlay, status = render(state)
        return state, overlay, status
    n_fwd = max(1, int(n_fwd))
    later = stems[idx + 1 : idx + 1 + n_fwd]
    if not later:
        state["status"] = "No later frames to track."
        overlay, status = render(state)
        return state, overlay, status
    write_sam2(state["stem"], state["frame"], mask)
    prompt = mask
    saved: list[str] = []
    try:
        for stem in later:
            binary = sam_prompt_mask(stem, prompt)
            if binary is None or not binary.any():
                break
            write_sam2(stem, load_frame(stem), binary)
            saved.append(stem.split("_")[0])
            prompt = binary
    except Exception as e:
        state["status"] = f"Propagate failed: {e}"
        overlay, status = render(state)
        return state, overlay, status
    if not saved:
        state["status"] = "SAM returned no mask on the next frame."
    else:
        state["status"] = (
            f"Propagated to {len(saved)} frames ({saved[0]}–{saved[-1]}) as *_sam2.png. "
            "Next loads them."
        )
    overlay, status = render(state)
    return state, overlay, status


def on_save(state: dict) -> tuple[dict, np.ndarray, str]:
    mask = combined_mask(state)
    if not mask.any():
        state["status"] = "Nothing to save."
        overlay, status = render(state)
        return state, overlay, status
    if state.get("kind") == "video":
        stash_video_frame(state)
        jpg = Path(state["paths"][state["idx"]])
        state["status"] = f"Saved {sv.mask_path_for(jpg).name}."
        overlay, status = render(state)
        return state, overlay, status
    stem = state["stem"]
    MASKS_DIR.mkdir(parents=True, exist_ok=True)
    png = MASKS_DIR / f"{stem}_sam2.png"
    overlay_path = MASKS_DIR / f"{stem}_sam2_overlay.jpg"
    Image.fromarray(mask).save(png)
    Image.fromarray(pink_overlay(state["frame"], mask)).save(overlay_path, quality=85)
    h, w = state["frame"].shape[:2]
    state["committed"] = mask > 0
    state["current"] = np.zeros((h, w), dtype=np.uint8)
    white = int(np.count_nonzero(mask))
    state["status"] = (
        f"Saved {png.name} ({white} px). Next/Prev will reload this file."
    )
    overlay, status = render(state)
    return state, overlay, status


def shift_stem(state: dict, delta: int) -> tuple[dict, np.ndarray, str, str]:
    stems = frame_stems()
    try:
        i = stems.index(state["stem"])
    except ValueError:
        i = 0
    stem = stems[(i + delta) % len(stems)]
    show = state.get("show_markers", True)
    view = state.get("view", VIEW_OVERLAY)
    state, overlay, status = on_load(stem, view)
    state["show_markers"] = show
    overlay, status = render(state)
    return state, overlay, status, stem


def stash_video_frame(state: dict) -> None:
    if state.get("kind") != "video" or not state.get("paths"):
        return
    jpg = Path(state["paths"][state["idx"]])
    mask = combined_mask(state)
    if mask.any():
        sv.save_mask_png(sv.mask_path_for(jpg), mask)
    jpg.with_suffix(".markers.json").write_text(
        json.dumps(state.get("markers") or []), encoding="utf-8"
    )


def video_slider_update(state: dict):
    n = max(1, len(state.get("paths") or []))
    idx = int(state.get("idx") or 0)
    src = (state.get("src_idx") or [0])[idx] if state.get("src_idx") else 0
    return gr.update(
        value=idx,
        maximum=max(0, n - 1),
        label=f"Extracted {idx + 1}/{n}  (video frame {src})",
    )


def video_pack(state: dict):
    overlay, status = render(state)
    return state, overlay, status, video_slider_update(state)


def load_video_idx(state: dict, idx: int) -> dict:
    stash_video_frame(state)
    paths = state["paths"]
    idx = int(np.clip(idx, 0, len(paths) - 1))
    show = state.get("show_markers", True)
    view = state.get("view", VIEW_OVERLAY)
    jpg = Path(paths[idx])
    src = state["src_idx"][idx]
    frame = np.array(Image.open(jpg).convert("RGB"))
    h, w = frame.shape[:2]
    loaded = sv.load_mask_png(sv.mask_path_for(jpg), (h, w))
    markers = []
    mp = jpg.with_suffix(".markers.json")
    if mp.exists():
        try:
            markers = json.loads(mp.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            markers = []
    state["idx"] = idx
    state["stem"] = f"{Path(state['video']).stem}_f{src:06d}"
    state["frame"] = frame
    state["points"] = []
    state["markers"] = markers
    state["history"] = []
    state["show_markers"] = show
    state["view"] = view
    state["current"] = np.zeros((h, w), dtype=np.uint8)
    if loaded is not None:
        state["committed"] = loaded > 127
        state["status"] = f"Frame {idx + 1}/{len(paths)}  src {src}."
    else:
        state["committed"] = np.zeros((h, w), dtype=bool)
        state["status"] = f"Frame {idx + 1}/{len(paths)}  src {src}  (no mask yet)."
    return state


def on_video_load(video_name: str, stride: float, view: str = VIEW_OVERLAY):
    videos = {p.name: p for p in sv.list_videos()}
    if video_name not in videos:
        raise gr.Error("Pick a video first.")
    stride = max(1, int(stride))
    items = sv.extract_frames(videos[video_name], stride)
    if not items:
        raise gr.Error("No frames extracted.")
    src0, jpg0 = items[0]
    frame = np.array(Image.open(jpg0).convert("RGB"))
    h, w = frame.shape[:2]
    loaded = sv.load_mask_png(sv.mask_path_for(jpg0), (h, w))
    state = {
        "kind": "video",
        "video": video_name,
        "paths": [str(p) for _, p in items],
        "src_idx": [i for i, _ in items],
        "idx": 0,
        "stem": f"{Path(video_name).stem}_f{src0:06d}",
        "frame": frame,
        "points": [],
        "markers": [],
        "show_markers": True,
        "view": view or VIEW_OVERLAY,
        "committed": loaded > 127 if loaded is not None else np.zeros((h, w), dtype=bool),
        "current": np.zeros((h, w), dtype=np.uint8),
        "history": [],
        "status": f"Loaded {video_name}: {len(items)} frames (stride {stride}). Place SAM clicks, then Track forward.",
    }
    return video_pack(state)


def on_video_goto(state: dict, idx: float):
    if not state or state.get("kind") != "video":
        raise gr.Error("Load a video first.")
    state = load_video_idx(state, int(idx))
    return video_pack(state)


def on_video_step(state: dict, edit_stride: float, delta: int):
    if not state or state.get("kind") != "video":
        raise gr.Error("Load a video first.")
    step = max(1, int(edit_stride))
    return on_video_goto(state, state["idx"] + delta * step)


def on_video_track(state: dict, n_fwd: float):
    if not state or state.get("kind") != "video":
        raise gr.Error("Load a video first.")
    mask = combined_mask(state)
    if not mask.any():
        state["status"] = "Need a mask on this frame first (SAM paint / brush / load)."
        return video_pack(state)
    stash_video_frame(state)
    n_fwd = max(1, int(n_fwd))
    i = state["idx"]
    paths = [Path(p) for p in state["paths"][i : i + 1 + n_fwd]]
    if len(paths) < 2:
        state["status"] = "No later extracted frames to track."
        return video_pack(state)
    try:
        tracked, stats = sv.track_forward(paths, mask, WEIGHTS, image_model=MODEL)
    except Exception as e:
        state["status"] = f"Video track failed: {e}"
        return video_pack(state)
    saved = 0
    for j, binary in enumerate(tracked):
        if j == 0 or binary is None or not np.any(binary):
            continue
        sv.save_mask_png(sv.mask_path_for(paths[j]), binary)
        saved += 1
    last_src = state["src_idx"][min(i + saved, len(state["src_idx"]) - 1)]
    state["status"] = (
        f"Tracked {saved}/{len(paths) - 1} frames through video frame {last_src} "
        f"(SAM2 video {stats['video']}, image fallback {stats['fallback']}). "
        "Next/slider loads them."
    )
    return video_pack(state)


def on_video_export(state: dict):
    if not state or state.get("kind") != "video":
        raise gr.Error("Load a video first.")
    stash_video_frame(state)
    items = [(src, Path(p)) for src, p in zip(state["src_idx"], state["paths"])]
    dest = sv.EXPORT_ROOT / f"{Path(state['video']).stem}_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
    sv.export_sequence(items, dest, pink_overlay)
    n_mask = sum(1 for _, p in items if sv.mask_path_for(p).exists())
    state["status"] = f"Exported {len(items)} frames, {n_mask} masks -> {dest}"
    return video_pack(state)


VIEW_CSS = """
#sam-viewer,
#sam-video-viewer {
  user-select: none;
}
#sam-viewer .image-container,
#sam-video-viewer .image-container {
  overflow: auto !important;
  height: 85vh !important;
  max-height: 85vh !important;
}
#sam-viewer img,
#sam-video-viewer img {
  cursor: crosshair;
  user-select: none;
  -webkit-user-drag: none;
  max-width: none !important;
  max-height: none !important;
  object-fit: unset !important;
  display: block;
}
"""

VIEW_JS = r"""
() => {
  const MIN = 1, MAX = 8;
  const IDS = ['sam-viewer', 'sam-video-viewer'];
  const scales = {};
  let panning = false;
  let panId = null;
  let lastX = 0, lastY = 0;

  const rootOf = (id) => document.getElementById(id);
  const boxOf = (id) => {
    const root = rootOf(id);
    if (!root) return null;
    return root.querySelector('.image-container') || root;
  };
  const imgOf = (id) => {
    const root = rootOf(id);
    return root ? root.querySelector('img') : null;
  };

  const apply = (id) => {
    const img = imgOf(id);
    const box = boxOf(id);
    if (!img || !box) return;
    const scale = scales[id] || 1;
    box.style.overflow = 'auto';
    box.style.maxHeight = '85vh';
    box.style.height = '85vh';
    img.style.maxWidth = 'none';
    img.style.maxHeight = 'none';
    img.style.display = 'block';
    const natW = img.naturalWidth || 1080;
    const natH = img.naturalHeight || 1920;
    const fitH = Math.max(200, box.clientHeight || 800);
    const fit = fitH / natH;
    img.style.width = (natW * fit * scale) + 'px';
    img.style.height = (natH * fit * scale) + 'px';
  };

  window.__samZoom = (dir, id) => {
    id = id || IDS.find((x) => rootOf(x));
    if (!id) return;
    const box = boxOf(id);
    if (!box) return;
    const prev = scales[id] || 1;
    if (dir === 0) {
      scales[id] = 1;
      apply(id);
      box.scrollLeft = 0;
      box.scrollTop = 0;
      return;
    }
    const next = Math.min(MAX, Math.max(MIN, prev * (dir > 0 ? 1.25 : 0.8)));
    const cx = box.clientWidth / 2;
    const cy = box.clientHeight / 2;
    const imgX = (box.scrollLeft + cx) / prev;
    const imgY = (box.scrollTop + cy) / prev;
    scales[id] = next;
    apply(id);
    box.scrollLeft = imgX * next - cx;
    box.scrollTop = imgY * next - cy;
  };

  const zoomAt = (e, id, factor) => {
    const box = boxOf(id);
    if (!box) return;
    const prev = scales[id] || 1;
    const next = Math.min(MAX, Math.max(MIN, prev * factor));
    if (next === prev) return;
    const br = box.getBoundingClientRect();
    const cx = e.clientX - br.left;
    const cy = e.clientY - br.top;
    const imgX = (box.scrollLeft + cx) / prev;
    const imgY = (box.scrollTop + cy) / prev;
    scales[id] = next;
    apply(id);
    box.scrollLeft = imgX * next - cx;
    box.scrollTop = imgY * next - cy;
  };

  const bindOne = (id) => {
    const root = rootOf(id);
    if (!root) return;
    apply(id);
    if (root.dataset.zoomBound === '1') return;
    root.dataset.zoomBound = '1';
    root.addEventListener('wheel', (e) => {
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault();
        zoomAt(e, id, e.deltaY < 0 ? 1.12 : 1 / 1.12);
      }
    }, { passive: false });
    root.addEventListener('pointerdown', (e) => {
      if (e.button === 1 || e.altKey) {
        panning = true;
        panId = id;
        lastX = e.clientX;
        lastY = e.clientY;
        try { root.setPointerCapture(e.pointerId); } catch (err) {}
        e.preventDefault();
      }
    });
    root.addEventListener('pointermove', (e) => {
      if (!panning || panId !== id) return;
      const box = boxOf(id);
      if (!box) return;
      box.scrollLeft -= (e.clientX - lastX);
      box.scrollTop -= (e.clientY - lastY);
      lastX = e.clientX;
      lastY = e.clientY;
    });
    const stop = () => { panning = false; panId = null; };
    root.addEventListener('pointerup', stop);
    root.addEventListener('pointercancel', stop);
  };

  const bindAll = () => IDS.forEach(bindOne);
  const obs = new MutationObserver(bindAll);
  obs.observe(document.body, { childList: true, subtree: true });
  bindAll();

  const isTypingTarget = (el) => {
    if (!el) return false;
    const tag = (el.tagName || '').toLowerCase();
    if (tag === 'input' || tag === 'textarea' || tag === 'select') return true;
    if (el.isContentEditable) return true;
    return false;
  };
  const clickElem = (id) => {
    const root = document.getElementById(id);
    if (!root) return false;
    const btn = (root.tagName === 'BUTTON') ? root : root.querySelector('button');
    if (!btn || btn.disabled) return false;
    btn.click();
    return true;
  };
  const videoTabOn = () => {
    const tabs = document.querySelectorAll('button[role="tab"], .tab-nav button');
    for (const t of tabs) {
      const on = t.getAttribute('aria-selected') === 'true' || t.classList.contains('selected');
      if (on && /video/i.test(t.textContent || '')) return true;
      if (on && /stills/i.test(t.textContent || '')) return false;
    }
    const v = document.getElementById('sam-video-viewer');
    return !!(v && v.offsetParent !== null);
  };
  document.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (isTypingTarget(e.target)) return;
    const vid = videoTabOn();
    const k = e.key;
    let handled = false;
    if (k === 'ArrowLeft' || k === 'a' || k === 'A' || k === '[') {
      handled = clickElem(vid ? 'video-prev' : 'stills-prev');
    } else if (k === 'ArrowRight' || k === 'd' || k === 'D' || k === ']') {
      handled = clickElem(vid ? 'video-next' : 'stills-next');
    } else if ((k === 'r' || k === 'R') && !e.repeat) {
      handled = clickElem(vid ? 'video-reset' : 'stills-reset');
    } else if ((k === 'x' || k === 'X') && !e.repeat && vid) {
      handled = clickElem('video-reset-fwd');
    }
    if (handled) e.preventDefault();
  }, true);
}
"""


def build_ui(default_stem: str) -> gr.Blocks:
    stems = frame_stems()
    if default_stem not in stems:
        default_stem = stems[0] if stems else ""
    videos = [p.name for p in sv.list_videos()]
    default_video = videos[0] if videos else None

    with gr.Blocks(
        title="SAM click-to-paint",
        theme=gr.themes.Soft(),
        css=VIEW_CSS,
        js=VIEW_JS,
    ) as demo:
        gr.Markdown(
            "## SAM 2.1 click-to-paint\n"
            "**Stills** = sampled walkthrough JPEGs. **Video** = load a clip, SAM2-track consecutive frames, "
            "edit every Nth, export frames+masks to a new folder.\n"
            "Green/red markers = SAM paint/erase. Brush = click circle. "
            "**View** switches pink overlay vs the black/white mask. "
            "Scroll pans. **Ctrl+wheel** zooms (scrollbars grow). **Alt-drag** or middle-mouse pans.\n"
            "**Keys:** ←/A prev · →/D next · **R** reset frame · **X** (video) reset from here for N frames."
        )
        with gr.Tabs():
            with gr.Tab("Stills"):
                state = gr.State()
                with gr.Row():
                    viewer = gr.Image(
                        label="Click to paint or erase",
                        type="numpy",
                        height=900,
                        interactive=False,
                        elem_id="sam-viewer",
                    )
                    with gr.Column(scale=0, min_width=320):
                        stem = gr.Dropdown(stems, value=default_stem, label="Frame")
                        with gr.Row():
                            prev_btn = gr.Button("Prev (A / ←)", elem_id="stills-prev")
                            next_btn = gr.Button("Next (D / →)", elem_id="stills-next")
                        load_mask_btn = gr.Button("Load existing mask")
                        with gr.Row():
                            zoom_out_btn = gr.Button("Zoom −")
                            zoom_in_btn = gr.Button("Zoom +")
                            zoom_reset_btn = gr.Button("Reset view")
                        marker_btn = gr.Button("Hide markers")
                        view = gr.Radio(
                            [VIEW_OVERLAY, VIEW_MASK],
                            value=VIEW_OVERLAY,
                            label="View",
                        )
                        mode = gr.Radio(
                            [SAM_PAINT, SAM_ERASE, BRUSH_PAINT, BRUSH_ERASE],
                            value=BRUSH_ERASE,
                            label="Click mode",
                        )
                        radius = gr.Slider(4, 80, value=22, step=1, label="Brush radius (px)")
                        n_fwd = gr.Slider(1, 15, value=8, step=1, label="Stills: image-SAM frames forward")
                        prop_btn = gr.Button("Propagate stills (image SAM)")
                        add_btn = gr.Button("Add region (keep SAM piece, start next)")
                        undo_btn = gr.Button("Undo")
                        reset_btn = gr.Button("Reset frame (R)", elem_id="stills-reset")
                        save_btn = gr.Button("Save sam2 mask", variant="primary")
                        status = gr.Textbox(label="Status", lines=3)
                still_outs = [state, viewer, status]
                demo.load(on_load, inputs=[stem, view], outputs=still_outs)
                stem.change(on_load, inputs=[stem, view], outputs=still_outs)
                viewer.select(on_click, inputs=[state, mode, radius], outputs=still_outs)
                marker_btn.click(on_toggle_markers, inputs=state, outputs=still_outs + [marker_btn])
                view.change(on_view_mode, inputs=[state, view], outputs=still_outs)
                zoom_in_btn.click(fn=None, js="() => { window.__samZoom && window.__samZoom(1, 'sam-viewer'); }")
                zoom_out_btn.click(fn=None, js="() => { window.__samZoom && window.__samZoom(-1, 'sam-viewer'); }")
                zoom_reset_btn.click(fn=None, js="() => { window.__samZoom && window.__samZoom(0, 'sam-viewer'); }")
                load_mask_btn.click(on_load_mask, inputs=state, outputs=still_outs)
                prop_btn.click(on_propagate, inputs=[state, n_fwd], outputs=still_outs)
                add_btn.click(on_add_region, inputs=state, outputs=still_outs)
                undo_btn.click(on_undo, inputs=state, outputs=still_outs)
                reset_btn.click(on_reset, inputs=state, outputs=still_outs)
                save_btn.click(on_save, inputs=state, outputs=still_outs)
                prev_btn.click(lambda s: shift_stem(s, -1), inputs=state, outputs=still_outs + [stem])
                next_btn.click(lambda s: shift_stem(s, 1), inputs=state, outputs=still_outs + [stem])

            with gr.Tab("Video"):
                vid_state = gr.State()
                with gr.Row():
                    vid_viewer = gr.Image(
                        label="Video frame — click to paint or erase",
                        type="numpy",
                        height=900,
                        interactive=False,
                        elem_id="sam-video-viewer",
                    )
                    with gr.Column(scale=0, min_width=320):
                        vid_pick = gr.Dropdown(videos, value=default_video, label="Video")
                        extract_stride = gr.Slider(1, 10, value=1, step=1, label="Extract every Nth source frame")
                        load_vid_btn = gr.Button("Load video", variant="primary")
                        vid_slider = gr.Slider(0, 0, value=0, step=1, label="Extracted frame")
                        edit_stride = gr.Slider(1, 30, value=1, step=1, label="Prev/Next skip (edit every Nth)")
                        with gr.Row():
                            vid_prev = gr.Button("Prev (A / ←)", elem_id="video-prev")
                            vid_next = gr.Button("Next (D / →)", elem_id="video-next")
                        with gr.Row():
                            vid_zoom_out = gr.Button("Zoom −")
                            vid_zoom_in = gr.Button("Zoom +")
                            vid_zoom_reset = gr.Button("Reset view")
                        vid_marker_btn = gr.Button("Hide markers")
                        vid_view = gr.Radio(
                            [VIEW_OVERLAY, VIEW_MASK],
                            value=VIEW_OVERLAY,
                            label="View",
                        )
                        vid_mode = gr.Radio(
                            [SAM_PAINT, SAM_ERASE, BRUSH_PAINT, BRUSH_ERASE],
                            value=SAM_PAINT,
                            label="Click mode",
                        )
                        vid_radius = gr.Slider(4, 80, value=22, step=1, label="Brush radius (px)")
                        n_track = gr.Slider(2, 240, value=60, step=1, label="Track this many frames forward")
                        track_btn = gr.Button("Track forward (SAM2 video)")
                        vid_add = gr.Button("Add region (keep SAM piece, start next)")
                        vid_undo = gr.Button("Undo")
                        vid_reset = gr.Button("Reset frame (R)", elem_id="video-reset")
                        n_clear = gr.Slider(
                            1, 240, value=30, step=1, label="Reset from here: frames to clear"
                        )
                        vid_reset_fwd = gr.Button(
                            "Reset from here (X)", elem_id="video-reset-fwd"
                        )
                        vid_save = gr.Button("Save this frame mask")
                        export_btn = gr.Button("Export frames + masks")
                        vid_status = gr.Textbox(label="Status", lines=4)
                vid_outs = [vid_state, vid_viewer, vid_status]
                vid_all = vid_outs + [vid_slider]
                load_vid_btn.click(
                    on_video_load, inputs=[vid_pick, extract_stride, vid_view], outputs=vid_all
                )
                vid_slider.release(on_video_goto, inputs=[vid_state, vid_slider], outputs=vid_all)
                vid_prev.click(
                    lambda s, st: on_video_step(s, st, -1),
                    inputs=[vid_state, edit_stride],
                    outputs=vid_all,
                )
                vid_next.click(
                    lambda s, st: on_video_step(s, st, 1),
                    inputs=[vid_state, edit_stride],
                    outputs=vid_all,
                )
                vid_viewer.select(on_click, inputs=[vid_state, vid_mode, vid_radius], outputs=vid_outs)
                vid_marker_btn.click(
                    on_toggle_markers, inputs=vid_state, outputs=vid_outs + [vid_marker_btn]
                )
                vid_view.change(on_view_mode, inputs=[vid_state, vid_view], outputs=vid_outs)
                vid_zoom_in.click(
                    fn=None, js="() => { window.__samZoom && window.__samZoom(1, 'sam-video-viewer'); }"
                )
                vid_zoom_out.click(
                    fn=None, js="() => { window.__samZoom && window.__samZoom(-1, 'sam-video-viewer'); }"
                )
                vid_zoom_reset.click(
                    fn=None, js="() => { window.__samZoom && window.__samZoom(0, 'sam-video-viewer'); }"
                )
                track_btn.click(on_video_track, inputs=[vid_state, n_track], outputs=vid_all)
                vid_add.click(on_add_region, inputs=vid_state, outputs=vid_outs)
                vid_undo.click(on_undo, inputs=vid_state, outputs=vid_outs)
                vid_reset.click(on_reset, inputs=vid_state, outputs=vid_outs)
                vid_reset_fwd.click(
                    on_video_reset_forward, inputs=[vid_state, n_clear], outputs=vid_all
                )
                vid_save.click(on_save, inputs=vid_state, outputs=vid_outs)
                export_btn.click(on_video_export, inputs=vid_state, outputs=vid_all)
    return demo


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--stem", default="56_20260830_183455_f000064")
    parser.add_argument("--port", type=int, default=7860)
    parser.add_argument("--weights", default=str(WEIGHTS))
    args = parser.parse_args()

    if not Path(args.weights).exists():
        raise SystemExit(f"missing weights {args.weights}")
    if not torch.cuda.is_available():
        raise SystemExit("CUDA is not available")

    global MODEL
    print("device", torch.cuda.get_device_name(0), flush=True)
    print("loading", args.weights, flush=True)
    MODEL = SAM(args.weights)
    print("ready  http://127.0.0.1:" + str(args.port), flush=True)

    demo = build_ui(args.stem)
    demo.queue().launch(
        server_name="127.0.0.1",
        server_port=args.port,
        inbrowser=True,
        show_error=True,
    )


if __name__ == "__main__":
    main()
