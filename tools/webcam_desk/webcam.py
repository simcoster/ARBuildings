"""Desk webcam overlay: fine-tuned toy segmenters (PyTorch) plus the old
LiteRT models.

Keys 1–5 load `FineTuneSingleObject/runs/<name>/best.pt` on CUDA. The original
IS-Net / DA3 / U2-Net / MODNet TFLite files stay on i/d/u/m.

    python webcam.py
    python webcam.py --cpu          # PyTorch CPU / XNNPACK, for an A/B
    python webcam.py --camera 1
"""

from __future__ import annotations

import argparse
import os
import shutil
import threading
import time
from collections import deque
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
MODELS = HERE.parent / "encoder_bench" / "occ_models"
TOY_ROOT = HERE.parents[1] / "FineTuneSingleObject"
TOY_RUNS = TOY_ROOT / "runs"
WEBVIEW_DXC = Path(r"C:\Windows\System32\Microsoft-Edge-WebView")
TOY_SIZE = 512

# ImageNet, per-channel, on x/255. The phone used a scalar stand-in; this is
# what the LiteRT conversion card actually specifies.
DA3_MEAN = np.array([0.485, 0.456, 0.406], np.float32).reshape(1, 1, 3)
DA3_STD = np.array([0.229, 0.224, 0.225], np.float32).reshape(1, 1, 3)


def interpreter_class():
    try:
        from ai_edge_litert.interpreter import Interpreter
        return Interpreter
    except ImportError:
        from tensorflow.lite.python.interpreter import Interpreter
        return Interpreter


def ensure_dxc() -> None:
    """Dawn/WebGPU on Windows needs dxil.dll + dxcompiler.dll next to the GPU
    accelerator. They ship with Edge WebView; LiteRT does not bundle them, and
    without them it reports 'Selected adapter: RTX …' then fails with error 87."""
    try:
        import ai_edge_litert
    except ImportError:
        return
    pkg = Path(ai_edge_litert.__file__).parent
    if WEBVIEW_DXC.is_dir():
        os.add_dll_directory(str(WEBVIEW_DXC))
        os.environ["PATH"] = str(WEBVIEW_DXC) + os.pathsep + os.environ.get("PATH", "")
        for n in ("dxil.dll", "dxcompiler.dll"):
            src, dst = WEBVIEW_DXC / n, pkg / n
            if src.is_file() and not dst.is_file():
                shutil.copy2(src, dst)


def load_gpu(path: str):
    from ai_edge_litert.compiled_model import CompiledModel, HardwareAccelerator

    ensure_dxc()
    return CompiledModel.from_file(path, hardware_accel=HardwareAccelerator.GPU)


class Spec:
    def __init__(
        self, key, title, *, kind="matte", norm="imagenet", crop="frame",
        backend="tflite", file=None, net=None, size=None, stock=False,
    ):
        self.key = key
        self.title = title
        self.kind = kind  # "matte" | "depth"
        self.norm = norm  # "imagenet" | "dis" | "da3" | "u2net" | "modnet"
        self.crop = crop  # "frame" | "patch" | "letterbox"
        self.backend = backend  # "torch" | "tflite"
        self.net = net
        self.size = size
        self.stock = stock
        if backend == "torch":
            if stock and net == "birefnet":
                self.file = TOY_ROOT / "pretrained" / "birefnet" / "model.safetensors"
            else:
                self.file = TOY_RUNS / net / "best.pt"
        else:
            self.file = MODELS / file


SPECS = [
    Spec("1", "MobileNetV4 (toy)", backend="torch", net="mobilenetv4"),
    Spec("4", "PIDNet-S (toy)", backend="torch", net="pidnet_s"),
    Spec("5", "cnn_s (toy)", backend="torch", net="cnn_s"),
    Spec("n", "IS-Net (toy)", backend="torch", net="isnet", size=1024, crop="letterbox"),
    Spec("b", "BiRefNet", backend="torch", net="birefnet", size=1024, crop="letterbox", stock=True),
    Spec("i", "DIS-ISNet 1024", file="dis_isnet_1024.tflite", kind="matte", norm="dis"),
    Spec("d", "Depth Anything 3 Small", file="depth_anything_3_small_fp16.tflite",
         kind="depth", norm="da3"),
    Spec("u", "U2-Net 320 (salient object)", file="u2net_320_fp16.tflite",
         kind="matte", norm="u2net", crop="patch"),
    Spec("m", "MODNet 512 (portrait matte)", file="modnet_512.tflite",
         kind="matte", norm="modnet", crop="patch"),
]

KEYS_LINE = (
    "1 MNv4  4 PIDNet  5 cnn_s  n IS-Net  b BiRefNet  "
    "6 Canny  7 rates  g guided  o mask  i stock-ISNet  d DA3  u U2-Net  m MODNet  q quit"
)

# Same physical speed; position is held until the next tick. 5 fps therefore
# jumps 6x as far as 30 fps — that's the stale-mask look.
RATE_LANES = (
    (30, (70, 210, 70)),
    (15, (50, 210, 220)),
    (10, (40, 150, 255)),
    (5, (50, 50, 255)),
)
CROSS_SECONDS = 4.0


# Fraction of the live frame sent to the model: left 60% of width, bottom 60%
# of height. The rest of the picture is never resized into the tensor.
CROP_FRAC = 1


def letterbox_bgr(bgr: np.ndarray, size: int):
    """Square canvas, same letterbox as training. Overlay maps back to the full frame."""
    h, w = bgr.shape[:2]
    scale = size / max(h, w)
    nh, nw = max(1, int(round(h * scale))), max(1, int(round(w * scale)))
    resized = cv2.resize(bgr, (nw, nh), interpolation=cv2.INTER_LINEAR)
    canvas = np.zeros((size, size, 3), np.uint8)
    ox, oy = (size - nw) // 2, (size - nh) // 2
    canvas[oy:oy + nh, ox:ox + nw] = resized
    return canvas, (0, 0, w, h), (ox, oy, nw, nh, w, h)


def crop_bottom_left(bgr: np.ndarray, frac: float = CROP_FRAC):
    """Keep the bottom-left `frac`×`frac` of the frame. Overlay uses the same box."""
    h, w = bgr.shape[:2]
    nw = max(1, int(round(w * frac)))
    nh = max(1, int(round(h * frac)))
    x0, y0 = 0, h - nh
    return bgr[y0:y0 + nh, x0:x0 + nw], (x0, y0, nw, nh)


def crop_native_patch(bgr: np.ndarray, iw: int, ih: int):
    """Centre crop of `iw`×`ih` camera pixels — not a squashed full frame.

    If the frame is smaller than the tensor, take the largest centred square
    (resize happens later on that square only).
    """
    h, w = bgr.shape[:2]
    if w >= iw and h >= ih:
        x0 = (w - iw) // 2
        y0 = (h - ih) // 2
        return bgr[y0:y0 + ih, x0:x0 + iw], (x0, y0, iw, ih)
    side = min(w, h)
    x0 = (w - side) // 2
    y0 = (h - side) // 2
    return bgr[y0:y0 + side, x0:x0 + side], (x0, y0, side, side)


def nchw_hw(shape):
    s = [int(x) for x in shape]
    if len(s) == 4 and s[1] <= 4 < s[2]:
        return s[2], s[3], True
    return s[1], s[2], False


class Model:
    def __init__(self, spec: Spec, threads: int, use_gpu: bool):
        if not spec.file.is_file():
            raise FileNotFoundError(spec.file)
        self.spec = spec
        self.device = "CPU XNNPACK"
        self._gpu = None
        self._torch = None
        self._torch_dev = None
        self._ins = self._outs = None
        self._out_n = 0
        self._out_shape = None
        self.it = None

        if spec.backend == "torch":
            self._load_torch(use_gpu)
            return

        if use_gpu:
            cm = load_gpu(str(spec.file))
            sig = next(iter(cm.get_signature_list()))
            inp = next(iter(cm.get_input_tensor_details(sig).values()))
            out = next(iter(cm.get_output_tensor_details(sig).values()))
            self._gpu = cm
            self._ins = cm.create_input_buffers(0)
            self._outs = cm.create_output_buffers(0)
            self._out_shape = [int(x) for x in out["shape"]]
            self._out_n = int(np.prod(self._out_shape))
            shape = [int(x) for x in inp["shape"]]
            self.ih, self.iw, self.nchw = nchw_hw(shape)
            self.dtype = np.float32
            full = "full" if cm.is_fully_accelerated() else "partial"
            self.device = f"GPU WebGPU ({full})"
        else:
            self.it = interpreter_class()(model_path=str(spec.file), num_threads=threads)
            self.it.allocate_tensors()
            self.inp = self.it.get_input_details()[0]
            self.out = self.it.get_output_details()[0]
            self.ih, self.iw, self.nchw = nchw_hw(self.inp["shape"])
            self.dtype = self.inp["dtype"]

    def _load_torch(self, use_gpu: bool) -> None:
        import sys
        import torch

        root = str(TOY_ROOT)
        if root not in sys.path:
            sys.path.insert(0, root)
        from models import build_model

        want = "cuda" if use_gpu and torch.cuda.is_available() else "cpu"
        dev = torch.device(want)
        if self.spec.stock:
            net = build_model(self.spec.net, pretrained=True)
        else:
            ckpt = torch.load(self.spec.file, map_location=dev, weights_only=False)
            net = build_model(self.spec.net, pretrained=False)
            net.load_state_dict(ckpt["state_dict"])
        net.eval().to(dev)
        self._torch = net
        self._torch_dev = dev
        self._pad = None
        self.ih = self.iw = int(self.spec.size or TOY_SIZE)
        self.nchw = True
        self.dtype = np.float32
        self.device = f"PyTorch {dev}"

    def preprocess(self, bgr: np.ndarray) -> tuple[np.ndarray, tuple[int, int, int, int]]:
        if self.spec.crop == "patch":
            crop, box = crop_native_patch(bgr, self.iw, self.ih)
        elif self.spec.crop == "letterbox":
            crop, box, self._pad = letterbox_bgr(bgr, self.iw)
        else:
            crop, box = crop_bottom_left(bgr)
            self._pad = None
        rgb = cv2.cvtColor(crop, cv2.COLOR_BGR2RGB)
        if rgb.shape[0] != self.ih or rgb.shape[1] != self.iw:
            rgb = cv2.resize(rgb, (self.iw, self.ih), interpolation=cv2.INTER_LINEAR)
        x = rgb.astype(np.float32)
        if self.spec.norm == "dis":
            x = x / 255.0 - 0.5
        elif self.spec.norm == "modnet":
            x = (x / 255.0 - 0.5) / 0.5
        elif self.spec.norm == "u2net":
            peak = float(x.max()) or 1.0
            x = (x / peak - DA3_MEAN) / DA3_STD
        else:
            # ImageNet (toy checkpoints and DA3)
            x = (x / 255.0 - DA3_MEAN) / DA3_STD
        if self.nchw:
            x = x.transpose(2, 0, 1)[None]
        else:
            x = x[None]
        return x.astype(self.dtype), box

    def run(self, x: np.ndarray) -> tuple[np.ndarray, float, str]:
        t0 = time.perf_counter()
        if self._torch is not None:
            import torch
            t = torch.from_numpy(np.ascontiguousarray(x)).to(self._torch_dev)
            with torch.inference_mode():
                y = torch.sigmoid(self._torch(t)).detach().cpu().numpy()
        elif self._gpu is not None:
            self._ins[0].write(np.ascontiguousarray(x))
            self._gpu.run_by_index(0, self._ins, self._outs)
            y = self._outs[0].read(self._out_n, np.float32).reshape(self._out_shape)
        else:
            self.it.set_tensor(self.inp["index"], x)
            self.it.invoke()
            y = self.it.get_tensor(self.out["index"])
        ms = (time.perf_counter() - t0) * 1e3
        if y.ndim == 4:
            m = y[0, 0] if y.shape[1] == 1 else y[0, :, :, 0]
        else:
            m = np.squeeze(y)
        pad = getattr(self, "_pad", None)
        if pad is not None:
            ox, oy, nw, nh, ow, oh = pad
            m = m[oy:oy + nh, ox:ox + nw]
            if m.shape[0] != oh or m.shape[1] != ow:
                m = cv2.resize(m, (ow, oh), interpolation=cv2.INTER_LINEAR)
        lo, hi = float(m.min()), float(m.max())
        return m.astype(np.float32), ms, f"{lo:.3f} .. {hi:.3f}"


def guided_filter(guide_bgr: np.ndarray, src: np.ndarray, radius: int, eps: float) -> np.ndarray:
    """Colour guided filter (He et al.). No ximgproc in this OpenCV build."""
    I = guide_bgr.astype(np.float32) * (1.0 / 255.0)
    p = src.astype(np.float32)
    k = 2 * int(radius) + 1
    ksize = (k, k)

    def bf(x: np.ndarray) -> np.ndarray:
        return cv2.boxFilter(x, cv2.CV_32F, ksize, normalize=True)

    mean_I = bf(I)
    mean_p = bf(p)
    mean_Ip = bf(I * p[:, :, None])
    cov = mean_Ip - mean_I * mean_p[:, :, None]
    Ir, Ig, Ib = I[:, :, 0], I[:, :, 1], I[:, :, 2]
    mr, mg, mb = mean_I[:, :, 0], mean_I[:, :, 1], mean_I[:, :, 2]
    var_rr = bf(Ir * Ir) - mr * mr + eps
    var_rg = bf(Ir * Ig) - mr * mg
    var_rb = bf(Ir * Ib) - mr * mb
    var_gg = bf(Ig * Ig) - mg * mg + eps
    var_gb = bf(Ig * Ib) - mg * mb
    var_bb = bf(Ib * Ib) - mb * mb + eps
    inv_gg_bb = var_gg * var_bb - var_gb * var_gb
    inv_rb_gb = var_rb * var_gb - var_rg * var_bb
    inv_rg_gb = var_rg * var_gb - var_rb * var_gg
    det = var_rr * inv_gg_bb + var_rg * inv_rb_gb + var_rb * inv_rg_gb
    det = np.where(np.abs(det) < 1e-12, 1e-12, det)
    inv_rr = inv_gg_bb / det
    inv_rg = inv_rb_gb / det
    inv_rb = inv_rg_gb / det
    inv_gg = (var_rr * var_bb - var_rb * var_rb) / det
    inv_gb = (var_rb * var_rg - var_rr * var_gb) / det
    inv_bb = (var_rr * var_gg - var_rg * var_rg) / det
    cr, cg, cb = cov[:, :, 0], cov[:, :, 1], cov[:, :, 2]
    ar = inv_rr * cr + inv_rg * cg + inv_rb * cb
    ag = inv_rg * cr + inv_gg * cg + inv_gb * cb
    ab = inv_rb * cr + inv_gb * cg + inv_bb * cb
    b = mean_p - (ar * mr + ag * mg + ab * mb)
    q = (bf(ar) * Ir + bf(ag) * Ig + bf(ab) * Ib) + bf(b)
    return np.clip(q, 0.0, 1.0)


def colorize(spec: Spec, raw: np.ndarray, bgr_crop: np.ndarray,
             guided: bool = False, mask_only: bool = False) -> np.ndarray:
    vis = cv2.resize(raw, (bgr_crop.shape[1], bgr_crop.shape[0]), interpolation=cv2.INTER_LINEAR)
    if spec.kind == "matte":
        # DIS sits at ~0.5 on empty frames. U2-Net / MODNet already emit [0,1]
        # with background near 0.
        if spec.norm == "dis":
            a = np.clip((vis - 0.52) / 0.25, 0.0, 1.0)
        elif spec.norm == "imagenet":
            a = np.clip(vis, 0.0, 1.0)
            if guided:
                # Radius has to reach the overshoot or the 0.5 contour never
                # sees the real colour edge. Two passes pull a ~20px halo in.
                radius = max(16, min(bgr_crop.shape[0], bgr_crop.shape[1]) // 16)
                for _ in range(2):
                    a = guided_filter(bgr_crop, a, radius=radius, eps=1e-4)
            a = (a >= 0.5).astype(np.float32)
        else:
            a = np.clip(vis, 0.0, 1.0)
        if mask_only:
            g = (a * 255.0).astype(np.uint8)
            return cv2.cvtColor(g, cv2.COLOR_GRAY2BGR)
        a3 = a[:, :, None]
        dim = (bgr_crop.astype(np.float32) * 0.22)
        keep = bgr_crop.astype(np.float32)
        tint = keep.copy()
        tint[:, :, 1] = np.clip(tint[:, :, 1] + 70, 0, 255)  # green-ish confirm
        out = keep * a3 + dim * (1.0 - a3)
        edge = (a3 > 0.15) & (a3 < 0.85)
        out = np.where(edge, 0.45 * out + 0.55 * tint, out)
        return np.clip(out, 0, 255).astype(np.uint8)
    lo, hi = float(vis.min()), float(vis.max())
    t = np.zeros_like(vis) if hi - lo < 1e-9 else (vis - lo) / (hi - lo)
    heat = cv2.applyColorMap((t * 255).astype(np.uint8), cv2.COLORMAP_TURBO)
    if mask_only:
        return heat
    return cv2.addWeighted(bgr_crop, 0.45, heat, 0.55, 0)


def draw_canny(frame: np.ndarray) -> tuple[np.ndarray, float]:
    """Full-frame Canny on the live pixels — no network, no resize."""
    t0 = time.perf_counter()
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    gray = cv2.GaussianBlur(gray, (5, 5), 1.2)
    edges = cv2.Canny(gray, 80, 160)
    ms = (time.perf_counter() - t0) * 1e3
    vis = frame.copy()
    vis[edges != 0] = (0, 220, 255)
    return vis, ms


def draw_rate_lanes(frame: np.ndarray, t0: float) -> np.ndarray:
    """Four silhouettes, identical speed, position quantized to 30/15/10/5 fps."""
    vis = frame.copy()
    h, w = vis.shape[:2]
    n = len(RATE_LANES)
    band_h = max(56, h // (n + 2))
    y0 = (h - n * band_h) // 2
    t = time.perf_counter() - t0
    margin = 36
    span = max(1, w - 2 * margin)
    r = max(14, band_h // 3 - 4)
    for i, (fps, col) in enumerate(RATE_LANES):
        y = y0 + i * band_h
        y1 = y + band_h
        vis[y:y1] = (vis[y:y1].astype(np.float32) * 0.40).astype(np.uint8)
        cy = (y + y1) // 2
        cv2.line(vis, (margin, cy), (w - margin, cy), (70, 70, 70), 2, cv2.LINE_AA)
        tq = np.floor(t * fps) / fps
        x = margin + int((tq / CROSS_SECONDS) % 1.0 * span)
        cv2.circle(vis, (x, cy), r, col, -1, cv2.LINE_AA)
        cv2.circle(vis, (x, cy), r, (255, 255, 255), 2, cv2.LINE_AA)
        cv2.putText(vis, f"{fps} fps", (margin, y + 22), cv2.FONT_HERSHEY_SIMPLEX,
                    0.7, (0, 0, 0), 3, cv2.LINE_AA)
        cv2.putText(vis, f"{fps} fps", (margin, y + 22), cv2.FONT_HERSHEY_SIMPLEX,
                    0.7, col, 1, cv2.LINE_AA)
    return vis


def hud(frame: np.ndarray, lines: list[str]) -> None:
    y = 28
    for line in lines:
        cv2.putText(frame, line, (12, y), cv2.FONT_HERSHEY_SIMPLEX, 0.62,
                    (0, 0, 0), 4, cv2.LINE_AA)
        cv2.putText(frame, line, (12, y), cv2.FONT_HERSHEY_SIMPLEX, 0.62,
                    (240, 240, 240), 1, cv2.LINE_AA)
        y += 26


class Worker(threading.Thread):
    def __init__(self, use_gpu: bool):
        super().__init__(daemon=True)
        self.use_gpu = use_gpu
        self.lock = threading.Lock()
        self._frame = None
        self._pending_key = next((s.key for s in SPECS if s.key == "i"), SPECS[0].key)
        self._stop = False
        self.result = None  # dict
        self.status = "starting"
        self.times: deque[float] = deque(maxlen=30)
        self.guided = True
        self.mask_only = False

    def submit(self, bgr: np.ndarray) -> None:
        with self.lock:
            self._frame = bgr

    def switch(self, key: str) -> None:
        with self.lock:
            self._pending_key = key
            self.result = None
            self.times.clear()
            self.status = f"loading {key}"

    def stop(self) -> None:
        self._stop = True

    def run(self) -> None:
        model = None
        live_key = None
        threads = min(8, os.cpu_count() or 4)
        while not self._stop:
            with self.lock:
                key = self._pending_key
                frame = self._frame
                guided = self.guided
                mask_only = self.mask_only
                self._frame = None
            if key != live_key:
                spec = next(s for s in SPECS if s.key == key)
                self.status = f"loading {spec.title}"
                t0 = time.perf_counter()
                try:
                    model = Model(spec, threads, self.use_gpu)
                except Exception as e:
                    if self.use_gpu:
                        self.status = f"GPU failed ({type(e).__name__}: {e}); trying CPU"
                        model = Model(spec, threads, False)
                    else:
                        self.status = f"{type(e).__name__}: {e}"
                        time.sleep(0.5)
                        continue
                self.status = (
                    f"loaded {spec.title} on {model.device} "
                    f"in {time.perf_counter() - t0:.1f}s"
                )
                live_key = key
            if frame is None or model is None:
                time.sleep(0.01)
                continue
            try:
                x, box = model.preprocess(frame)
                raw, ms, raw_rng = model.run(x)
                x0, y0, bw, bh = box
                crop = frame[y0:y0 + bh, x0:x0 + bw]
                over = colorize(model.spec, raw, crop, guided=guided, mask_only=mask_only)
                with self.lock:
                    self.times.append(ms)
                    self.result = dict(
                        spec=model.spec, overlay=over, box=box, ms=ms,
                        raw=raw_rng, shape=f"{model.iw}x{model.ih}",
                        device=model.device, guided=guided, mask_only=mask_only,
                    )
                    self.status = "ok"
            except Exception as e:  # noqa: BLE001 — show it on the HUD
                self.status = f"{type(e).__name__}: {e}"
                time.sleep(0.25)


def p50(times: deque[float]) -> str:
    if len(times) < 3:
        return "p50 —"
    s = sorted(times)
    return f"p50 {s[len(s) // 2]:.0f} ms n={len(s)}"


def open_camera(index: int) -> cv2.VideoCapture:
    cap = cv2.VideoCapture(index, cv2.CAP_DSHOW)
    if not cap.isOpened():
        cap.release()
        cap = cv2.VideoCapture(index)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    return cap


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--camera", type=int, default=0)
    ap.add_argument("--cpu", action="store_true",
                    help="PyTorch CPU / XNNPACK instead of CUDA / LiteRT GPU")
    args = ap.parse_args()

    missing = [s for s in SPECS if s.backend == "torch" and not s.file.is_file()]
    if missing:
        print("skipping until a checkpoint exists:")
        for s in missing:
            print(f"  {s.title}  {s.file}")
        for s in missing:
            SPECS.remove(s)
    if not SPECS:
        print("no models left to run")
        return 1

    cap = open_camera(args.camera)
    if not cap.isOpened():
        print(f"could not open camera {args.camera}")
        return 1

    spec_keys = {s.key for s in SPECS}
    worker = Worker(use_gpu=not args.cpu)
    worker.start()
    print(KEYS_LINE)
    print("device:", "CUDA/LiteRT GPU" if not args.cpu else "CPU")

    mode = worker._pending_key
    rate_t0 = time.perf_counter()
    canny_times: deque[float] = deque(maxlen=30)
    keys_line = KEYS_LINE
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                print("camera read failed")
                break
            if mode == "7":
                vis = draw_rate_lanes(frame, rate_t0)
                hud(vis, [
                    "frame-rate demo   camera live, dots held between ticks",
                    "30 / 15 / 10 / 5 fps   same speed, 4 s to cross",
                    keys_line,
                ])
            elif mode == "6":
                vis, ms = draw_canny(frame)
                canny_times.append(ms)
                h, w = frame.shape[:2]
                hud(vis, [
                    f"Canny edges   CPU OpenCV   {w}x{h} native   "
                    f"last {ms:.1f} ms   {p50(canny_times)}",
                    "Gaussian 5x5 + Canny 80/160   full frame, every camera tick",
                    keys_line,
                ])
            else:
                worker.submit(frame)
                vis = frame
                with worker.lock:
                    result = worker.result
                    status = worker.status
                    times = list(worker.times)
                if result is not None:
                    x0, y0, bw, bh = result["box"]
                    if result.get("mask_only"):
                        vis = np.zeros_like(frame)
                    else:
                        vis = frame.copy()
                    vis[y0:y0 + bh, x0:x0 + bw] = result["overlay"]
                    spec = result["spec"]
                    if not result.get("mask_only"):
                        thick = 2 if spec.crop == "patch" else 1
                        cv2.rectangle(vis, (x0, y0), (x0 + bw - 1, y0 + bh - 1),
                                      (0, 200, 255), thick)
                    crop_note = (
                        f"patch {bw}x{bh} centred (native px)"
                        if spec.crop == "patch"
                        else f"full frame resized to {result['shape']}"
                    )
                    refine = "guided+0.5" if result.get("guided") else "hard 0.5"
                    view = "MASK" if result.get("mask_only") else "overlay"
                    hud(vis, [
                        f"{spec.title}   {result['device']}   {result['shape']}   "
                        f"last {result['ms']:.0f} ms   {p50(times)}   {refine}   {view}",
                        f"{crop_note}   raw {result['raw']}   {status}",
                        keys_line,
                    ])
                else:
                    hud(vis, [status, keys_line])

            cv2.imshow("webcam desk", vis)
            k = cv2.waitKey(1) & 0xFF
            if k in (ord("q"), 27):
                break
            ch = chr(k) if k < 128 else ""
            if ch in spec_keys:
                mode = ch
                worker.switch(mode)
            elif k == ord("7"):
                mode = "7"
                rate_t0 = time.perf_counter()
            elif k == ord("6"):
                mode = "6"
                canny_times.clear()
            elif k == ord("g"):
                with worker.lock:
                    worker.guided = not worker.guided
            elif k == ord("o"):
                with worker.lock:
                    worker.mask_only = not worker.mask_only
    finally:
        worker.stop()
        cap.release()
        cv2.destroyAllWindows()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
