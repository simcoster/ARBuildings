"""LAN inference for the phone's seg-wifi button.

JPEG in, 8-bit PNG mask out (255 = toy). IS-Net toy checkpoint, same 1024
letterbox + ImageNet prep as webcam.py key `n`.

    python seg_server.py
    python seg_server.py --port 8787 --cpu

Phone and PC must share Wi-Fi. USB adb does not carry these frames.
Allow inbound TCP on the listen port (Windows Firewall).
"""
from __future__ import annotations

import argparse
import io
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
TOY_ROOT = HERE.parents[1] / "FineTuneSingleObject"
CKPT = TOY_ROOT / "runs" / "isnet" / "best.pt"
SIZE = 1024
IMAGENET_MEAN = np.array([0.485, 0.456, 0.406], np.float32).reshape(1, 1, 3)
IMAGENET_STD = np.array([0.229, 0.224, 0.225], np.float32).reshape(1, 1, 3)

_lock = threading.Lock()
_net = None
_dev = None
_device_name = "cpu"
_seen: set[str] = set()
_frames = 0


def letterbox_bgr(bgr: np.ndarray, size: int):
    h, w = bgr.shape[:2]
    scale = size / max(h, w)
    nh, nw = max(1, int(round(h * scale))), max(1, int(round(w * scale)))
    resized = cv2.resize(bgr, (nw, nh), interpolation=cv2.INTER_LINEAR)
    canvas = np.zeros((size, size, 3), np.uint8)
    ox, oy = (size - nw) // 2, (size - nh) // 2
    canvas[oy:oy + nh, ox:ox + nw] = resized
    return canvas, (ox, oy, nw, nh, w, h)


def load_isnet(use_cuda: bool):
    global _net, _dev, _device_name
    import torch

    root = str(TOY_ROOT)
    if root not in sys.path:
        sys.path.insert(0, root)
    from models import build_model

    if not CKPT.is_file():
        raise FileNotFoundError(f"IS-Net toy checkpoint missing: {CKPT}")
    want = "cuda" if use_cuda and torch.cuda.is_available() else "cpu"
    _dev = torch.device(want)
    ckpt = torch.load(CKPT, map_location=_dev, weights_only=False)
    net = build_model("isnet", pretrained=False)
    net.load_state_dict(ckpt["state_dict"])
    net.eval().to(_dev)
    _net = net
    _device_name = f"PyTorch {_dev}"
    print(f"loaded {CKPT} on {_device_name} epoch={ckpt.get('epoch')}", flush=True)


def infer_jpeg(data: bytes) -> tuple[bytes, float]:
    import torch
    from PIL import Image

    arr = np.frombuffer(data, dtype=np.uint8)
    bgr = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    if bgr is None:
        raise ValueError("not a JPEG")
    canvas, pad = letterbox_bgr(bgr, SIZE)
    ox, oy, nw, nh, ow, oh = pad
    rgb = cv2.cvtColor(canvas, cv2.COLOR_BGR2RGB).astype(np.float32)
    x = (rgb / 255.0 - IMAGENET_MEAN) / IMAGENET_STD
    t = torch.from_numpy(np.ascontiguousarray(x.transpose(2, 0, 1)[None])).to(_dev)
    t0 = time.perf_counter()
    with torch.inference_mode():
        y = torch.sigmoid(_net(t)).detach().cpu().numpy()
    ms = (time.perf_counter() - t0) * 1e3
    m = y[0, 0] if y.ndim == 4 else np.squeeze(y)
    m = m[oy:oy + nh, ox:ox + nw]
    if m.shape[0] != oh or m.shape[1] != ow:
        m = cv2.resize(m, (ow, oh), interpolation=cv2.INTER_LINEAR)
    mask = (m >= 0.5).astype(np.uint8) * 255
    toy = int(mask.sum() // 255)
    buf = io.BytesIO()
    Image.fromarray(mask, mode="L").save(buf, format="PNG")
    return buf.getvalue(), ms, bgr.shape, toy


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))

    def do_GET(self):
        if self.path.split("?")[0] != "/health":
            self.send_error(404)
            return
        peer = self.client_address[0]
        print(f"health  {peer}  {_device_name}", flush=True)
        body = json.dumps(
            {
                "ok": True,
                "model": "isnet",
                "device": _device_name,
                "ckpt": str(CKPT),
                "size": SIZE,
            }
        ).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        if self.path.split("?")[0] != "/infer":
            self.send_error(404)
            return
        n = int(self.headers.get("Content-Length", "0"))
        data = self.rfile.read(n)
        peer = self.client_address[0]
        try:
            with _lock:
                first = peer not in _seen
                if first:
                    _seen.add(peer)
                png, ms, hw, toy = infer_jpeg(data)
                global _frames
                _frames += 1
                nframe = _frames
        except Exception as e:
            print(f"infer failed from {peer}: {e}", flush=True)
            msg = str(e).encode("utf-8")
            self.send_response(400)
            self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(msg)))
            self.end_headers()
            self.wfile.write(msg)
            return
        if first:
            print(f"phone connected  {peer}", flush=True)
        if nframe == 1 or nframe % 5 == 0:
            h, w = hw[0], hw[1]
            print(
                f"frame {nframe}  {peer}  jpeg {n}B  {w}x{h}  "
                f"infer {ms:.0f}ms  toy {toy}px  {_device_name}",
                flush=True,
            )
        self.send_response(200)
        self.send_header("Content-Type", "image/png")
        self.send_header("Content-Length", str(len(png)))
        self.send_header("X-Infer-Ms", f"{ms:.1f}")
        self.end_headers()
        self.wfile.write(png)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8787)
    ap.add_argument("--cpu", action="store_true")
    args = ap.parse_args()
    load_isnet(use_cuda=not args.cpu)
    httpd = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"seg-wifi IS-Net on http://{args.host}:{args.port}/infer", flush=True)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("stop", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
