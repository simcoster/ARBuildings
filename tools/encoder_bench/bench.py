#!/usr/bin/env python
"""Latency benchmark for off-the-shelf image encoders.

Times the pipeline in stages, because on a modern GPU the encoder forward pass is
routinely *not* the expensive part:

    decode      JPEG bytes -> PIL image        (only if your source is a file;
                                                a camera frame arrives as raw
                                                pixels, so treat this as an
                                                upper bound, not your cost)
    preprocess  resize / crop / normalize      CPU, single-threaded, often > forward
    h2d         pinned-less host -> device copy
    forward     the encoder itself             CUDA events, not wall clock
    d2h         embedding back to host

Wall-clock timing around a CUDA call measures kernel *launch*, not execution, so
GPU stages use torch.cuda.Event throughout.

Examples
--------
    python bench.py                                  # default set, cuda fp16
    python bench.py --models clip-vit-b32 dinov2-small
    python bench.py --backend torch-cpu --threads 4  # crude A35 big-cluster proxy
    python bench.py --backend onnx-cuda --batch 8    # server-side throughput
    python bench.py --all-backends --json out.json
"""

from __future__ import annotations

import argparse
import io
import json
import logging
import platform
import sys
import time
import warnings
from dataclasses import dataclass, field
from typing import Callable

import numpy as np
import torch
from PIL import Image

import models as model_registry

# Windows consoles default to cp1252; the ONNX exporter prints emoji to stderr.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass

logging.getLogger("onnxscript").setLevel(logging.ERROR)
warnings.filterwarnings("ignore", category=FutureWarning)

BACKENDS = ("torch-cuda-fp16", "torch-cuda-fp32", "torch-cpu", "onnx-cuda", "onnx-cpu")


# --------------------------------------------------------------------------- #
# stats
# --------------------------------------------------------------------------- #

def pct(xs: list[float], p: float) -> float:
    if not xs:
        return float("nan")
    xs = sorted(xs)
    k = (len(xs) - 1) * (p / 100.0)
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)


@dataclass
class Timings:
    preprocess: list[float] = field(default_factory=list)
    h2d: list[float] = field(default_factory=list)
    forward: list[float] = field(default_factory=list)
    d2h: list[float] = field(default_factory=list)

    def e2e(self) -> list[float]:
        return [sum(t) for t in zip(self.preprocess, self.h2d, self.forward, self.d2h)]


# --------------------------------------------------------------------------- #
# input
# --------------------------------------------------------------------------- #

def source_image(path: str | None) -> bytes:
    """JPEG bytes to benchmark against; synthetic if no path given."""
    if path:
        with open(path, "rb") as fh:
            return fh.read()

    # Structured content, not flat noise — JPEG cost depends on entropy.
    h, w = 720, 1280
    yy, xx = np.mgrid[0:h, 0:w]
    base = ((xx * 255 // w) ^ (yy * 255 // h)).astype(np.uint8)
    rgb = np.stack([base, np.roll(base, 64, 1), np.roll(base, 128, 0)], -1)
    rgb = (rgb * 0.75 + np.random.default_rng(0).integers(0, 64, rgb.shape)).astype(np.uint8)

    buf = io.BytesIO()
    Image.fromarray(rgb).save(buf, format="JPEG", quality=85)
    return buf.getvalue()


def build_preprocess(size: int, mean, std) -> Callable[[Image.Image], torch.Tensor]:
    from torchvision import transforms

    return transforms.Compose([
        transforms.Resize(size, interpolation=transforms.InterpolationMode.BICUBIC),
        transforms.CenterCrop(size),
        transforms.ToTensor(),
        transforms.Normalize(mean=mean, std=std),
    ])


# --------------------------------------------------------------------------- #
# backends
# --------------------------------------------------------------------------- #

class TorchBackend:
    def __init__(self, loaded, backend: str, batch: int, threads: int | None,
                 cuda_graph: bool = False):
        self.batch = batch
        self.cuda = backend.startswith("torch-cuda")
        self.dtype = torch.float16 if backend.endswith("fp16") else torch.float32
        self.device = "cuda" if self.cuda else "cpu"
        self.graph = None

        if not self.cuda:
            self.dtype = torch.float32
            if threads:
                torch.set_num_threads(threads)

        self.module = loaded.module.to(self.device, dtype=self.dtype)
        if self.cuda:
            torch.backends.cudnn.benchmark = True
            self.events = [torch.cuda.Event(enable_timing=True) for _ in range(4)]
            if cuda_graph:
                self._capture(loaded.size)

    def _capture(self, size: int) -> None:
        """Capture the forward pass into a CUDA graph.

        At batch 1 these encoders are launch-bound: ~150 kernels of a few us each,
        behind PyTorch's per-op CPU dispatch. The GPU sits starved between kernels
        and CUDA events dutifully record the gaps, so eager timings measure Python,
        not the GPU. One graph replay removes the dispatch entirely.
        """
        self.static_in = torch.zeros(self.batch, 3, size, size,
                                     device="cuda", dtype=self.dtype)
        try:
            # Warm up on a side stream first — capture of an un-warmed module
            # records cuBLAS/cudnn init work into the graph.
            side = torch.cuda.Stream()
            side.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(side):
                for _ in range(5):
                    self.module(self.static_in)
            torch.cuda.current_stream().wait_stream(side)
            torch.cuda.synchronize()

            self.graph = torch.cuda.CUDAGraph()
            with torch.cuda.graph(self.graph):
                self.static_out = self.module(self.static_in)
        except Exception as exc:  # noqa: BLE001 - dynamic control flow can't capture
            print(f"    ! cuda-graph capture failed, using eager: {exc}", file=sys.stderr)
            self.graph = None

    def infer(self, x: torch.Tensor) -> tuple[float, float, float]:
        if not self.cuda:
            t0 = time.perf_counter()
            self.module(x)
            return 0.0, (time.perf_counter() - t0) * 1e3, 0.0

        e = self.events
        if self.graph is not None:
            e[0].record()
            self.static_in.copy_(x, non_blocking=True)
            e[1].record()
            self.graph.replay()
            e[2].record()
            self.static_out.float().cpu()
            e[3].record()
        else:
            e[0].record()
            xg = x.to(self.device, dtype=self.dtype, non_blocking=True)
            e[1].record()
            y = self.module(xg)
            e[2].record()
            y.float().cpu()
            e[3].record()
        torch.cuda.synchronize()
        return e[0].elapsed_time(e[1]), e[1].elapsed_time(e[2]), e[2].elapsed_time(e[3])


class OnnxBackend:
    """ORT hides the transfer inside run(), so h2d/d2h fold into forward."""

    def __init__(self, loaded, backend: str, batch: int, threads: int | None, onnx_dir: str):
        import onnxruntime as ort
        from export_onnx import ensure_exported

        self.batch = batch
        path = ensure_exported(loaded, onnx_dir, batch=batch)

        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        if threads:
            opts.intra_op_num_threads = threads

        providers = (["CUDAExecutionProvider", "CPUExecutionProvider"]
                     if backend == "onnx-cuda" else ["CPUExecutionProvider"])
        self.sess = ort.InferenceSession(path, opts, providers=providers)
        self.input_name = self.sess.get_inputs()[0].name

        actual = self.sess.get_providers()[0]
        if backend == "onnx-cuda" and actual != "CUDAExecutionProvider":
            print(f"    ! CUDA provider unavailable, fell back to {actual}", file=sys.stderr)

    def infer(self, x: torch.Tensor) -> tuple[float, float, float]:
        arr = x.numpy()
        t0 = time.perf_counter()
        self.sess.run(None, {self.input_name: arr})
        return 0.0, (time.perf_counter() - t0) * 1e3, 0.0


def make_backend(loaded, backend, batch, threads, onnx_dir, cuda_graph=False):
    if backend.startswith("onnx"):
        return OnnxBackend(loaded, backend, batch, threads, onnx_dir)
    return TorchBackend(loaded, backend, batch, threads, cuda_graph)


# --------------------------------------------------------------------------- #
# run
# --------------------------------------------------------------------------- #

def bench_one(loaded, backend_name, jpeg, args) -> Timings | None:
    try:
        backend = make_backend(loaded, backend_name, args.batch, args.threads,
                               args.onnx_dir, args.cuda_graph)
    except Exception as exc:  # noqa: BLE001 - one bad backend shouldn't kill the sweep
        print(f"    ! {backend_name} unavailable: {exc}", file=sys.stderr)
        return None

    preprocess = build_preprocess(loaded.size, loaded.mean, loaded.std)
    img = Image.open(io.BytesIO(jpeg)).convert("RGB")
    t = Timings()

    def stage() -> torch.Tensor:
        x = preprocess(img).unsqueeze(0)
        return x.expand(args.batch, -1, -1, -1).contiguous() if args.batch > 1 else x

    # Steady-state mode: preprocess once and hammer the forward pass back to back.
    # Otherwise several ms of single-threaded CPU work sits between GPU bursts, the
    # GPU drops to idle clocks, and every forward number comes out inflated.
    staged = stage() if args.forward_only else None

    with torch.inference_mode():
        for i in range(args.warmup + args.iters):
            if staged is not None:
                x, prep_ms = staged, 0.0
            else:
                t0 = time.perf_counter()
                x = stage()
                prep_ms = (time.perf_counter() - t0) * 1e3

            h2d, fwd, d2h = backend.infer(x)

            if i >= args.warmup:  # discard warmup: cudnn autotune, allocator, JIT
                t.preprocess.append(prep_ms)
                t.h2d.append(h2d)
                t.forward.append(fwd)
                t.d2h.append(d2h)

    return t


def measure_decode(jpeg: bytes, iters: int) -> list[float]:
    out = []
    for _ in range(iters):
        t0 = time.perf_counter()
        Image.open(io.BytesIO(jpeg)).convert("RGB")
        out.append((time.perf_counter() - t0) * 1e3)
    return out


ROW = "{:<16}{:>7}{:>8}{:>6}{:>6}{:>9}{:>9}{:>9}{:>9}{:>9}{:>8}"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models", nargs="+", default=model_registry.DEFAULT_SET,
                    help="registry names, or 'all'")
    ap.add_argument("--backend", default="torch-cuda-fp16", choices=BACKENDS)
    ap.add_argument("--all-backends", action="store_true")
    ap.add_argument("--batch", type=int, default=1)
    ap.add_argument("--iters", type=int, default=100)
    ap.add_argument("--warmup", type=int, default=20)
    ap.add_argument("--cuda-graph", action="store_true",
                    help="capture the forward into a CUDA graph — removes PyTorch's "
                         "per-op dispatch, which otherwise dominates at batch 1")
    ap.add_argument("--forward-only", action="store_true",
                    help="preprocess once, loop the forward pass back to back — "
                         "steady-state model latency, GPU never drops to idle clocks")
    ap.add_argument("--threads", type=int, default=None,
                    help="CPU threads (torch-cpu / onnx-cpu). 4 ~ A35 big cluster")
    ap.add_argument("--image", default=None, help="JPEG to use; synthetic 1280x720 if omitted")
    ap.add_argument("--onnx-dir", default="onnx", help="where exported .onnx files live")
    ap.add_argument("--json", default=None)
    args = ap.parse_args()

    names = list(model_registry.REGISTRY) if args.models == ["all"] else args.models
    backends = list(BACKENDS) if args.all_backends else [args.backend]

    print(f"host    {platform.processor() or platform.machine()}")
    if torch.cuda.is_available():
        print(f"gpu     {torch.cuda.get_device_name(0)}  torch {torch.__version__}")
    else:
        print(f"gpu     none available  torch {torch.__version__}")
    mode = "forward-only (steady state)" if args.forward_only else "full pipeline"
    print(f"batch   {args.batch}   iters {args.iters} (+{args.warmup} warmup)   {mode}")

    jpeg = source_image(args.image)
    dec = measure_decode(jpeg, 50)
    src = args.image or "synthetic 1280x720 q85"
    print(f"decode  {pct(dec, 50):.2f} ms p50 for {src} "
          f"({len(jpeg)/1024:.0f} kB) — excluded from totals below\n")

    results: list[dict] = []
    for backend_name in backends:
        print(f"[{backend_name}]")
        print(ROW.format("model", "tier", "params", "in", "dim",
                         "prep", "h2d", "fwd p50", "fwd p90", "fwd p99", "e2e"))
        print("-" * 88)

        for name in names:
            try:
                loaded = model_registry.load(name)
            except Exception as exc:  # noqa: BLE001
                print(f"    ! {name} failed to load: {exc}", file=sys.stderr)
                continue

            t = bench_one(loaded, backend_name, jpeg, args)
            if t is None:
                continue

            row = dict(
                model=name, backend=backend_name, tier=loaded.spec.tier,
                params_m=round(loaded.param_millions, 1), input=loaded.size, dim=loaded.dim,
                batch=args.batch,
                prep_p50=pct(t.preprocess, 50), h2d_p50=pct(t.h2d, 50),
                fwd_p50=pct(t.forward, 50), fwd_p90=pct(t.forward, 90),
                fwd_p99=pct(t.forward, 99), e2e_p50=pct(t.e2e(), 50),
            )
            results.append(row)
            print(ROW.format(
                name, loaded.spec.tier, f"{loaded.param_millions:.1f}M", loaded.size, loaded.dim,
                f"{row['prep_p50']:.2f}", f"{row['h2d_p50']:.2f}", f"{row['fwd_p50']:.2f}",
                f"{row['fwd_p90']:.2f}", f"{row['fwd_p99']:.2f}", f"{row['e2e_p50']:.2f}"))

            del loaded
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        print()

    print("all times in ms; e2e = prep + h2d + fwd + d2h (no decode)")
    if args.batch > 1:
        print(f"note: batch={args.batch}, so per-image cost is these divided by {args.batch}")

    if args.json:
        with open(args.json, "w") as fh:
            json.dump({"decode_p50_ms": pct(dec, 50), "rows": results}, fh, indent=2)
        print(f"\nwrote {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
