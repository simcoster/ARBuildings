"""Segment frames with Nano Banana image models.

Full-frame neon-green overlay, then a second pass on an annotated copy
that marks the mural and the wall to the right of the tree. Masks are
unioned. First model that returns a plausible full-frame mask wins.
"""
from __future__ import annotations

import argparse
import base64
import json
import ssl
import time
from io import BytesIO
from pathlib import Path

import cv2
import numpy as np
from google import genai
from google.genai import types
from google.genai.errors import ClientError, ServerError
from PIL import Image

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent
FRAMES_DIR = ROOT / "frames"
MASKS_DIR = ROOT / "masks"
BATCH_DIR = ROOT / "batch_work"
JOB_FILE = BATCH_DIR / "job.json"
MAX_SIDE = 1280
JPEG_QUALITY = 90
OVERLAY_PINK = np.array([255.0, 0.0, 80.0])
OVERLAY_ALPHA = 0.65

# Cheapest first. Pro is last because it is expensive; we only used it
# before because it succeeded on the first try and we never fell through.
MODELS = (
    "gemini-3.1-flash-lite-image",
    "gemini-3.1-flash-image",
    "gemini-2.5-flash-image",
    "gemini-3-pro-image",
)

PROMPT = (
    "Edit this photo in place. Keep the exact camera framing, composition, "
    "and all unpainted pixels unchanged. This is an in-place recolor, not a "
    "new picture and not a black-and-white conversion.\n\n"
    "Segment the complete building structure. Paint a solid neon green "
    "(#00FF00) on EVERY visible pixel of the synagogue — walls, windows, "
    "doors, entrance alcoves, and foundational stairs. Do NOT exclude "
    "windows or doors. Paint the whole building, every fragment, not only "
    "the largest wall. Include all of these even if they are small or "
    "disconnected:\n"
    "- the blue-and-teal mosaic mural on the left (it is part of the building; "
    "paint it green, do not leave it as artwork)\n"
    "- the stone stairs, entrance alcoves, and black iron railings\n"
    "- windows and doors (paint them; they are the building)\n"
    "- the tan-beige facade left of the tree\n"
    "- the facade to the RIGHT of the tree trunk\n"
    "- every scrap of wall, window, roofline, or balcony visible BETWEEN the "
    "tree's branches and through gaps in the leaves\n"
    "- the white colonnade / wing on the far right if it is the same building\n\n"
    "Exclude trees, sky, people, poles, flags, and vehicles. Do not paint "
    "the tree trunk, leaves, branches, plaza, street, or sidewalk. Where a "
    "leaf covers the facade, leave that leaf unpainted. Do not fill through "
    "occlusions. Do not skip small fragments on the right side of the tree."
)

ANNOTATED_PROMPT = (
    "This is the same photo with yellow boxes drawn on missed parts of the "
    "synagogue. Edit in place. Keep the exact camera framing.\n\n"
    "Paint solid neon green (#00FF00) on the synagogue. You MUST paint:\n"
    "- everything inside the yellow box on the LEFT — that is the blue mosaic "
    "mural, which is the building wall itself\n"
    "- everything inside the yellow box on the RIGHT that is building wall, "
    "including pieces visible through the tree\n"
    "- every beige wall or window showing BETWEEN the branches\n\n"
    "Do not paint the yellow box lines as objects. Do not paint sky, tree, "
    "leaves, people, cars, plaza, street, or sidewalk."
)

RATIOS = {
    "1:1": 1.0,
    "3:2": 1.5,
    "2:3": 2 / 3,
    "3:4": 0.75,
    "4:3": 4 / 3,
    "4:5": 0.8,
    "5:4": 1.25,
    "9:16": 9 / 16,
    "16:9": 16 / 9,
    "21:9": 21 / 9,
}


def load_api_key() -> str:
    for line in (REPO / ".env").read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        if k.strip() == "GOOGLE_AI_STUDIO_API_KEY":
            return v.strip()
    raise SystemExit("GOOGLE_AI_STUDIO_API_KEY missing from .env")


def remaining_frames(include_done: bool = False) -> list[Path]:
    frames = sorted(
        FRAMES_DIR.glob("*.jpg"),
        key=lambda p: int(p.name.split("_")[0]),
    )
    if include_done:
        return frames
    out = []
    for path in frames:
        dest = MASKS_DIR / (path.stem + ".png")
        if dest.exists():
            arr = np.array(Image.open(dest).convert("L"))
            if int(np.count_nonzero(arr)) > 10_000:
                continue
        out.append(path)
    return out


def downscale(image: Image.Image) -> Image.Image:
    w, h = image.size
    scale = MAX_SIDE / max(w, h)
    if scale >= 1:
        return image.convert("RGB")
    return image.convert("RGB").resize(
        (max(1, int(round(w * scale))), max(1, int(round(h * scale)))),
        Image.Resampling.LANCZOS,
    )


def jpeg_bytes(image: Image.Image) -> bytes:
    buf = BytesIO()
    image.save(buf, format="JPEG", quality=JPEG_QUALITY)
    return buf.getvalue()


def aspect_ratio(image: Image.Image) -> str:
    r = image.size[0] / max(1, image.size[1])
    return min(RATIOS, key=lambda k: abs(RATIOS[k] - r))


def extract_image(response) -> Image.Image:
    candidates = response.candidates or []
    if not candidates:
        raise ValueError("no candidates")
    cand = candidates[0]
    reason = getattr(cand, "finish_reason", None)
    parts = (cand.content.parts if cand.content else None) or []
    texts = []
    for part in parts:
        if part.text:
            texts.append(part.text.strip())
        data = getattr(part.inline_data, "data", None) if part.inline_data else None
        if data:
            return Image.open(BytesIO(data)).convert("RGB")
    extra = " | ".join(texts)[:300]
    raise ValueError(f"no image part (finish={reason}): {extra}")


def green_mask(img: Image.Image) -> Image.Image:
    arr = np.array(img.convert("RGB"), dtype=np.int16)
    r, g, b = arr[:, :, 0], arr[:, :, 1], arr[:, :, 2]
    hit = (g - r > 40) & (g - b > 40) & (g > 80)
    return Image.fromarray(np.where(hit, 255, 0).astype(np.uint8), mode="L")


def binary_mask(img: Image.Image) -> Image.Image:
    arr = np.array(img.convert("L"))
    return Image.fromarray(np.where(arr >= 127, 255, 0).astype(np.uint8), mode="L")


def mask_stats(mask: Image.Image) -> dict:
    arr = np.array(mask)
    h = arr.shape[0]
    frac = float(np.count_nonzero(arr) / arr.size)
    sky = float(np.count_nonzero(arr[: max(1, h // 10)]) / arr[: max(1, h // 10)].size)
    ground = float(
        np.count_nonzero(arr[int(h * 0.7) :]) / arr[int(h * 0.7) :].size
    )
    return {"frac": frac, "sky": sky, "ground": ground, "white": int(np.count_nonzero(arr))}


def accept(stats: dict) -> bool:
    return 0.02 <= stats["frac"] <= 0.45 and stats["sky"] < 0.20


def decode_mask(painted: Image.Image, size: tuple[int, int] | None = None) -> tuple[Image.Image, str, dict]:
    green = green_mask(painted)
    if size and green.size != size:
        green = green.resize(size, Image.Resampling.NEAREST)
    gstats = mask_stats(green)
    if accept(gstats):
        return green, "green", gstats
    binary = binary_mask(painted)
    if size and binary.size != size:
        binary = binary.resize(size, Image.Resampling.NEAREST)
    bstats = mask_stats(binary)
    if accept(bstats):
        return binary, "binary", bstats
    raise ValueError(f"unusable mask green={gstats} binary={bstats}")


def call_model(client: genai.Client, model: str, parts: list, ratio: str):
    return client.models.generate_content(
        model=model,
        contents=parts,
        config=types.GenerateContentConfig(
            response_modalities=["TEXT", "IMAGE"],
            image_config=types.ImageConfig(aspect_ratio=ratio),
            candidate_count=1,
            automatic_function_calling=types.AutomaticFunctionCallingConfig(
                disable=True
            ),
        ),
    )


def mural_box(small: np.ndarray, pad: int = 12) -> tuple[int, int, int, int] | None:
    h, w = small.shape[:2]
    img = small.astype(np.int16)
    r, g, b = img[:, :, 0], img[:, :, 1], img[:, :, 2]
    mural = (b > r + 20) & (b > g) & (b > 70)
    mural[:, int(0.45 * w) :] = False
    mural[: int(0.15 * h), :] = False
    mural[int(0.65 * h) :, :] = False
    n, _, stats, _ = cv2.connectedComponentsWithStats(mural.astype(np.uint8), 8)
    if n < 2:
        return None
    idx = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    area = int(stats[idx, cv2.CC_STAT_AREA])
    if area < 400:
        return None
    x, y, bw, bh = (int(stats[idx, k]) for k in range(4))
    return (
        max(0, x - pad),
        max(0, y - pad),
        min(w, x + bw + pad),
        min(h, y + bh + pad),
    )


def right_box(mask: np.ndarray) -> tuple[int, int, int, int] | None:
    ys, xs = np.where(mask)
    if xs.size == 0:
        return None
    h, w = mask.shape
    y0, y1 = int(ys.min()), int(ys.max())
    x_right = int(np.percentile(xs, 75))
    x0 = min(w - 20, max(x_right, int(xs.max() * 0.85)))
    x1 = min(w, max(x0 + 40, int(w * 0.98)))
    if x1 - x0 < 30 or y1 - y0 < 40:
        return None
    return (x0, y0, x1, y1)


def fill_mural(original: Image.Image, mask: Image.Image) -> Image.Image:
    """Force-include the blue mosaic once a mural box can be found."""
    arr = np.array(original.convert("RGB"))
    box = mural_box(arr)
    if not box:
        return mask
    x0, y0, x1, y1 = box
    img = arr.astype(np.int16)
    r, g, b = img[:, :, 0], img[:, :, 1], img[:, :, 2]
    veg = (g - r > 18) & (g - b > 18) & (g > 50)
    extra = np.zeros(arr.shape[:2], dtype=bool)
    extra[y0:y1, x0:x1] = True
    extra &= ~veg
    acc = (np.array(mask) > 0) | extra
    return Image.fromarray(np.where(acc, 255, 0).astype(np.uint8), mode="L")


def clip_above_mural(original: Image.Image, mask: Image.Image) -> Image.Image:
    """The mural top is the building's highest point — drop cloud above it."""
    arr = np.array(original.convert("RGB"))
    box = mural_box(arr, pad=0)
    if not box:
        return mask
    roof = max(0, box[1] - 8)
    acc = np.array(mask) > 0
    acc[:roof, :] = False
    return Image.fromarray(np.where(acc, 255, 0).astype(np.uint8), mode="L")


def annotate(small: Image.Image, mask: Image.Image) -> Image.Image:
    arr = np.array(small.convert("RGB"))
    boxes = []
    mb = mural_box(arr)
    if mb:
        boxes.append(mb)
    rb = right_box(np.array(mask) > 0)
    if rb:
        boxes.append(rb)
    for x0, y0, x1, y1 in boxes:
        cv2.rectangle(arr, (x0, y0), (x1, y1), (255, 220, 0), 6)
    return Image.fromarray(arr)


def overlay_to_mask(original: Image.Image, overlay: Image.Image) -> Image.Image:
    """Recover a binary mask from a pink overlay, including Paint edits.

    Pink stays in the mask. Painting over with anything non-pink
    (eyedropper, black, white, eraser) removes it.
    """
    src = np.array(
        original.convert("RGB").resize(overlay.size, Image.Resampling.BILINEAR)
    )
    over = np.array(overlay.convert("RGB"))
    src_f = src.astype(np.float32)
    over_f = over.astype(np.float32)
    expected = src_f * (1.0 - OVERLAY_ALPHA) + OVERLAY_PINK * OVERLAY_ALPHA
    blend_err = np.sqrt(((over_f - expected) ** 2).sum(axis=2))
    orig_err = np.sqrt(((over_f - src_f) ** 2).sum(axis=2))
    r, g, b = over_f[:, :, 0], over_f[:, :, 1], over_f[:, :, 2]
    magenta = (r > g + 28) & (r > b - 10)
    painted = (r > 160) & (g < 110) & (b < 180) & (r > g + 40)
    hit = (blend_err < 50) | ((orig_err > 28) & magenta & painted)
    if overlay.size != original.size:
        hit_img = Image.fromarray(hit.astype(np.uint8) * 255, mode="L")
        hit_img = hit_img.resize(original.size, Image.Resampling.NEAREST)
        hit = np.array(hit_img) >= 127
    return Image.fromarray(np.where(hit, 255, 0).astype(np.uint8), mode="L")


def frame_for_overlay(overlay: Path) -> Path:
    name = overlay.stem
    if name.endswith("_overlay"):
        stem = name[: -len("_overlay")]
        path = FRAMES_DIR / f"{stem}.jpg"
        if path.exists():
            return path
    prefix = name.split("_")[0]
    matches = sorted(FRAMES_DIR.glob(f"{prefix}_*.jpg"))
    if len(matches) == 1:
        return matches[0]
    raise SystemExit(f"no frame for overlay {overlay.name}")


def extract_from_overlays(paths: list[Path] | None = None) -> None:
    if paths:
        files = paths
    else:
        files = sorted(MASKS_DIR.glob("*_overlay.jpg")) + sorted(
            MASKS_DIR.glob("*_overlay.png")
        )
    if not files:
        raise SystemExit(f"no overlay files in {MASKS_DIR}")
    for over_path in files:
        frame_path = frame_for_overlay(over_path)
        original = Image.open(frame_path)
        overlay = Image.open(over_path)
        mask = overlay_to_mask(original, overlay)
        stats = mask_stats(mask)
        out = MASKS_DIR / (frame_path.stem + ".png")
        mask.save(out)
        print(
            f"    {over_path.name} -> {out.name}  white={stats['white']} px  "
            f"frac={stats['frac']:.3f}",
            flush=True,
        )


def save_mask(
    original: Image.Image,
    mask: Image.Image,
    path: Path,
    model: str,
    kind: str,
    stats: dict,
    raw: Path,
    suffix: str = "",
) -> None:
    w, h = original.size
    stem = path.stem + suffix
    out = MASKS_DIR / (stem + ".png")
    mask.save(out)
    overlay = np.array(original.convert("RGB"))
    hit = np.array(mask) >= 127
    overlay[hit] = (
        overlay[hit] * (1.0 - OVERLAY_ALPHA) + OVERLAY_PINK * OVERLAY_ALPHA
    ).astype(np.uint8)
    over_path = MASKS_DIR / (stem + "_overlay.jpg")
    Image.fromarray(overlay).save(over_path, quality=85)
    if not suffix and path.stem.startswith("47_"):
        Image.fromarray(overlay).save(MASKS_DIR / "47_overlay.jpg", quality=85)
    print(
        f"    wrote {out.name} via {model} ({kind})  {w}x{h}  "
        f"white={stats['white']} px  frac={stats['frac']:.3f}  "
        f"sky={stats['sky']:.3f}  overlay={over_path.name}  raw={raw.name}",
        flush=True,
    )


def segment_one(
    client: genai.Client,
    path: Path,
    models: tuple[str, ...] = MODELS,
    refine: bool = True,
    suffix: str = "",
) -> None:
    original = Image.open(path)
    w, h = original.size
    small = downscale(original)
    jpeg = jpeg_bytes(small)
    ratio = aspect_ratio(small)
    print(f"    send {small.size} jpeg={len(jpeg)}B", flush=True)

    last_err = None
    for model in models:
        print(f"    model={model}", flush=True)
        try:
            painted = extract_image(
                call_model(
                    client,
                    model,
                    [
                        types.Part.from_bytes(data=jpeg, mime_type="image/jpeg"),
                        PROMPT,
                    ],
                    ratio,
                )
            )
        except (ClientError, ServerError, ValueError, TimeoutError) as e:
            last_err = e
            print(f"    {model} failed: {e}", flush=True)
            continue

        raw = MASKS_DIR / f"{path.stem}_{model.replace('.', '-')}_raw.jpg"
        painted.save(raw, quality=90)
        try:
            mask, kind, stats = decode_mask(painted, size=small.size)
        except ValueError as e:
            last_err = e
            print(f"    {model} rejected: {e}  raw={raw.name}", flush=True)
            continue

        print(
            f"    full {kind}  frac={stats['frac']:.3f}  sky={stats['sky']:.3f}  "
            f"white={stats['white']}",
            flush=True,
        )

        if refine:
            marked = annotate(small, mask)
            marked_path = MASKS_DIR / f"{path.stem}_annotated.jpg"
            marked.save(marked_path, quality=90)
            print("    annotated pass", flush=True)
            try:
                painted2 = extract_image(
                    call_model(
                        client,
                        model,
                        [
                            types.Part.from_bytes(
                                data=jpeg_bytes(marked), mime_type="image/jpeg"
                            ),
                            ANNOTATED_PROMPT,
                        ],
                        ratio,
                    )
                )
                raw2 = MASKS_DIR / f"{path.stem}_{model.replace('.', '-')}_raw2.jpg"
                painted2.save(raw2, quality=90)
                mask2, kind2, stats2 = decode_mask(painted2, size=small.size)
                if stats2["sky"] <= 0.20:
                    acc = np.maximum(np.array(mask), np.array(mask2))
                    mask = Image.fromarray(acc, mode="L")
                    kind = f"{kind}+{kind2}"
                    stats = mask_stats(mask)
                    print(
                        f"    annotated union  frac={stats['frac']:.3f}  "
                        f"white={stats['white']}  raw={raw2.name}",
                        flush=True,
                    )
                else:
                    print(
                        f"    annotated discarded  sky={stats2['sky']:.3f}  "
                        f"frac={stats2['frac']:.3f}",
                        flush=True,
                    )
            except (ClientError, ServerError, ValueError, TimeoutError) as e:
                print(f"    annotated failed: {e}", flush=True)

        if mask.size != (w, h):
            mask = mask.resize((w, h), Image.Resampling.NEAREST)
        mask = fill_mural(original.convert("RGB"), mask)
        mask = clip_above_mural(original.convert("RGB"), mask)
        stats = mask_stats(mask)
        save_mask(original, mask, path, model, kind, stats, raw, suffix=suffix)
        return

    raise RuntimeError(f"no usable mask: {last_err}")


def make_client(timeout_ms: int = 180_000) -> genai.Client:
    return genai.Client(
        api_key=load_api_key(),
        http_options=types.HttpOptions(
            timeout=timeout_ms,
            client_args={"verify": ssl.create_default_context()},
        ),
    )


def painted_from_parts(parts) -> Image.Image:
    for part in parts:
        if isinstance(part, dict):
            blob = part.get("inlineData") or part.get("inline_data") or {}
            data = blob.get("data")
            if not data:
                continue
            raw = base64.b64decode(data) if isinstance(data, str) else data
            return Image.open(BytesIO(raw)).convert("RGB")
        data = getattr(getattr(part, "inline_data", None), "data", None)
        if data:
            raw = data if isinstance(data, (bytes, bytearray)) else base64.b64decode(data)
            return Image.open(BytesIO(raw)).convert("RGB")
    raise ValueError("no image part")


def write_batch_jsonl(frames: list[Path], jsonl_path: Path) -> list[str]:
    stems: list[str] = []
    with jsonl_path.open("w", encoding="utf-8") as f:
        for path in frames:
            original = Image.open(path)
            small = downscale(original)
            ratio = aspect_ratio(small)
            b64 = base64.b64encode(jpeg_bytes(small)).decode("ascii")
            row = {
                "key": path.stem,
                "request": {
                    "contents": [
                        {
                            "parts": [
                                {
                                    "inline_data": {
                                        "mime_type": "image/jpeg",
                                        "data": b64,
                                    }
                                },
                                {"text": PROMPT},
                            ]
                        }
                    ],
                    "generation_config": {
                        "responseModalities": ["TEXT", "IMAGE"],
                        "imageConfig": {"aspectRatio": ratio},
                    },
                },
            }
            f.write(json.dumps(row) + "\n")
            stems.append(path.stem)
            print(f"    packed {path.name} {small.size} ratio={ratio}", flush=True)
    return stems


def process_painted(
    path: Path,
    painted: Image.Image,
    model: str,
    suffix: str,
) -> None:
    original = Image.open(path)
    w, h = original.size
    small = downscale(original)
    raw = MASKS_DIR / f"{path.stem}_{model.replace('.', '-')}{suffix}_raw.jpg"
    painted.save(raw, quality=90)
    mask, kind, stats = decode_mask(painted, size=small.size)
    print(
        f"    {path.name} {kind}  frac={stats['frac']:.3f}  "
        f"sky={stats['sky']:.3f}  white={stats['white']}",
        flush=True,
    )
    if mask.size != (w, h):
        mask = mask.resize((w, h), Image.Resampling.NEAREST)
    mask = fill_mural(original.convert("RGB"), mask)
    mask = clip_above_mural(original.convert("RGB"), mask)
    stats = mask_stats(mask)
    save_mask(original, mask, path, model, kind, stats, raw, suffix=suffix)


def collect_batch_job(client: genai.Client, job_name: str, model: str, suffix: str) -> None:
    job = client.batches.get(name=job_name)
    state = job.state.name if job.state else "?"
    print(f"collect {job_name} state={state}", flush=True)
    if state != "JOB_STATE_SUCCEEDED":
        raise SystemExit(f"batch not succeeded: {state} error={getattr(job, 'error', None)}")

    MASKS_DIR.mkdir(parents=True, exist_ok=True)
    n_ok = n_fail = 0

    def handle(key: str, parsed_or_inline) -> None:
        nonlocal n_ok, n_fail
        if key.startswith("47_"):
            print(f"    skip official 47 {key}", flush=True)
            return
        path = FRAMES_DIR / f"{key}.jpg"
        if not path.exists():
            print(f"    missing frame for key {key}", flush=True)
            n_fail += 1
            return
        try:
            if isinstance(parsed_or_inline, dict):
                if parsed_or_inline.get("error"):
                    raise ValueError(str(parsed_or_inline["error"]))
                resp = parsed_or_inline.get("response") or {}
                cands = resp.get("candidates") or []
                if not cands:
                    raise ValueError("no candidates")
                parts = ((cands[0].get("content") or {}).get("parts")) or []
                painted = painted_from_parts(parts)
            else:
                if getattr(parsed_or_inline, "error", None):
                    raise ValueError(str(parsed_or_inline.error))
                painted = extract_image(parsed_or_inline.response)
            process_painted(path, painted, model, suffix)
            n_ok += 1
        except Exception as e:
            print(f"    FAIL {key}: {e}", flush=True)
            n_fail += 1

    dest = job.dest
    if dest and getattr(dest, "inlined_responses", None):
        meta = json.loads(JOB_FILE.read_text(encoding="utf-8")) if JOB_FILE.exists() else {}
        stems = meta.get("stems") or []
        for i, item in enumerate(dest.inlined_responses):
            key = stems[i] if i < len(stems) else f"unknown_{i}"
            handle(key, item)
    elif dest and getattr(dest, "file_name", None):
        raw = client.files.download(file=dest.file_name)
        text = raw.decode("utf-8") if isinstance(raw, (bytes, bytearray)) else str(raw)
        (BATCH_DIR / "results.jsonl").write_text(text, encoding="utf-8")
        for line in text.splitlines():
            if not line.strip():
                continue
            parsed = json.loads(line)
            key = parsed.get("key") or parsed.get("metadata", {}).get("key")
            if not key:
                print(f"    FAIL missing key in result line", flush=True)
                n_fail += 1
                continue
            handle(key, parsed)
    else:
        raise SystemExit(f"no batch dest on job: {dest}")
    print(f"batch done ok={n_ok} fail={n_fail}", flush=True)


def poll_batch(client: genai.Client, job_name: str, interval: int = 30):
    done = {
        "JOB_STATE_SUCCEEDED",
        "JOB_STATE_FAILED",
        "JOB_STATE_CANCELLED",
        "JOB_STATE_EXPIRED",
    }
    while True:
        job = client.batches.get(name=job_name)
        state = job.state.name if job.state else "?"
        print(f"    {job_name} {state}", flush=True)
        if state in done:
            return job
        time.sleep(interval)


def run_batch(
    frames: list[Path],
    model: str,
    suffix: str,
    collect_name: str | None = None,
) -> None:
    BATCH_DIR.mkdir(parents=True, exist_ok=True)
    client = make_client(timeout_ms=600_000)
    if collect_name:
        collect_batch_job(client, collect_name, model, suffix)
        return

    jsonl_path = BATCH_DIR / "input.jsonl"
    print(f"packing {len(frames)} frames -> {jsonl_path}", flush=True)
    stems = write_batch_jsonl(frames, jsonl_path)
    size_mb = jsonl_path.stat().st_size / 1e6
    print(f"jsonl {size_mb:.1f} MB, uploading", flush=True)
    uploaded = client.files.upload(
        file=str(jsonl_path),
        config=types.UploadFileConfig(
            display_name="synagogue-mask-batch",
            mime_type="jsonl",
        ),
    )
    print(f"uploaded {uploaded.name}", flush=True)
    job = client.batches.create(
        model=model,
        src=uploaded.name,
        config={"display_name": f"synagogue-masks-{len(frames)}"},
    )
    meta = {
        "name": job.name,
        "model": model,
        "suffix": suffix,
        "stems": stems,
        "file": uploaded.name,
    }
    JOB_FILE.write_text(json.dumps(meta, indent=2), encoding="utf-8")
    print(f"submitted {job.name}  (saved {JOB_FILE})", flush=True)
    poll_batch(client, job.name)
    collect_batch_job(client, job.name, model, suffix)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=5)
    ap.add_argument("--stem", help="only this frame stem, even if already done")
    ap.add_argument("--model", help="single model id, no fallback")
    ap.add_argument("--no-refine", action="store_true", help="skip the second annotated pass")
    ap.add_argument("--suffix", default="", help="append to output names (e.g. _lite)")
    ap.add_argument(
        "--batch",
        action="store_true",
        help="Gemini Batch API, single-pass Pro (no refine)",
    )
    ap.add_argument(
        "--collect",
        metavar="JOB",
        help="download an existing batch job (batches/...) and write masks",
    )
    ap.add_argument(
        "--from-overlay",
        nargs="*",
        metavar="FILE",
        help="rebuild masks from edited *_overlay.jpg/png (Paint). "
        "No args = all overlays in masks/",
    )
    args = ap.parse_args()

    if args.from_overlay is not None:
        paths = [Path(p) for p in args.from_overlay] if args.from_overlay else None
        extract_from_overlays(paths)
        return

    if args.stem:
        frames = [p for p in remaining_frames(include_done=True) if args.stem in p.stem]
        if not frames:
            raise SystemExit(f"no frame matching {args.stem!r}")
    else:
        frames = remaining_frames()[: args.limit]
    frames = [p for p in frames if not p.stem.startswith("47_")]
    if not frames and not args.collect:
        raise SystemExit(f"no frames in {FRAMES_DIR}")
    MASKS_DIR.mkdir(parents=True, exist_ok=True)

    if args.batch or args.collect:
        model = args.model or "gemini-3-pro-image"
        suffix = args.suffix
        print(f"batch model={model} suffix={suffix!r} frames={len(frames)}", flush=True)
        run_batch(frames, model, suffix, collect_name=args.collect)
        return

    models = (args.model,) if args.model else MODELS
    client = make_client()
    print(f"models {models}, {len(frames)} frames", flush=True)
    for i, path in enumerate(frames, 1):
        print(f"[{i}/{len(frames)}] {path.name}", flush=True)
        segment_one(
            client,
            path,
            models=models,
            refine=not args.no_refine,
            suffix=args.suffix,
        )


if __name__ == "__main__":
    main()
