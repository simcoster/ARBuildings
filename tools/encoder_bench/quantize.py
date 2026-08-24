#!/usr/bin/env python
"""Static int8 quantization of the exported encoders, targeting NNAPI.

NNAPI will not execute an fp32 graph — it silently hands the model back and you
end up timing the CPU. It also wants QDQ format and per-tensor scales; per-channel
weights push several ops off the NPU.

Calibration data only affects *accuracy*, never latency. A latency benchmark is
therefore valid with synthetic input, but any accuracy claim needs --calib-dir
pointing at real frames of the actual building — activation ranges from ImageNet
or noise will not match your camera.

    python quantize.py --models all
    python quantize.py --models all --calib-dir ../../captures/synagogue
"""

from __future__ import annotations

import argparse
import glob
import os
import sys

import numpy as np
from PIL import Image

import models as model_registry
from export_onnx import ensure_exported


class ImageCalibrationReader:
    """Feeds calibration batches; real frames if given, synthetic otherwise."""

    def __init__(self, input_name: str, size: int, mean, std,
                 calib_dir: str | None, limit: int = 64):
        self.input_name = input_name
        self.samples: list[np.ndarray] = []
        mean_a = np.array(mean, dtype=np.float32).reshape(3, 1, 1)
        std_a = np.array(std, dtype=np.float32).reshape(3, 1, 1)

        paths: list[str] = []
        if calib_dir:
            for ext in ("*.jpg", "*.jpeg", "*.png", "*.bmp"):
                paths += glob.glob(os.path.join(calib_dir, "**", ext), recursive=True)
            paths = sorted(paths)[:limit]
            if not paths:
                raise SystemExit(f"no images found under {calib_dir}")

        if paths:
            for p in paths:
                img = Image.open(p).convert("RGB").resize((size, size), Image.BICUBIC)
                arr = np.asarray(img, dtype=np.float32).transpose(2, 0, 1) / 255.0
                self.samples.append(((arr - mean_a) / std_a)[None])
        else:
            rng = np.random.default_rng(0)
            for _ in range(16):
                arr = rng.random((3, size, size), dtype=np.float32)
                self.samples.append(((arr - mean_a) / std_a)[None])

        self.it = iter(self.samples)

    def get_next(self):
        nxt = next(self.it, None)
        return None if nxt is None else {self.input_name: nxt}

    def rewind(self):
        self.it = iter(self.samples)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models", nargs="+", default=model_registry.DEFAULT_SET)
    ap.add_argument("--onnx-dir", default="onnx")
    ap.add_argument("--out-dir", default="onnx_int8")
    ap.add_argument("--calib-dir", default=None,
                    help="real frames for calibration; synthetic if omitted "
                         "(fine for latency, NOT for accuracy)")
    args = ap.parse_args()

    from onnxruntime.quantization import (CalibrationMethod, QuantFormat, QuantType,
                                          quantize_static)
    from onnxruntime.quantization.shape_inference import quant_pre_process

    names = list(model_registry.REGISTRY) if args.models == ["all"] else args.models
    os.makedirs(args.out_dir, exist_ok=True)

    if not args.calib_dir:
        print("! synthetic calibration — latency valid, accuracy NOT\n", file=sys.stderr)

    print(f"{'model':<18}{'fp32 MB':>9}{'int8 MB':>9}{'ratio':>8}")
    print("-" * 44)
    failures = 0

    for name in names:
        try:
            loaded = model_registry.load(name)
            fp32 = ensure_exported(loaded, args.onnx_dir, batch=1)
            prepped = os.path.join(args.out_dir, f"{name}_prep.onnx")
            out = os.path.join(args.out_dir, f"{name}_int8.onnx")

            quant_pre_process(fp32, prepped, skip_symbolic_shape=False)

            import onnxruntime as ort
            in_name = ort.InferenceSession(
                prepped, providers=["CPUExecutionProvider"]).get_inputs()[0].name

            reader = ImageCalibrationReader(in_name, loaded.size, loaded.mean,
                                            loaded.std, args.calib_dir)
            quantize_static(
                prepped, out, reader,
                quant_format=QuantFormat.QDQ,
                activation_type=QuantType.QUInt8,
                weight_type=QuantType.QUInt8,
                per_channel=False,            # per-channel pushes ops off the NPU
                calibrate_method=CalibrationMethod.MinMax,
            )
            os.remove(prepped)

            a, b = os.path.getsize(fp32) / 1e6, os.path.getsize(out) / 1e6
            print(f"{name:<18}{a:>9.1f}{b:>9.1f}{a / b:>7.1f}x")
        except Exception as exc:  # noqa: BLE001
            print(f"    ! {name}: {type(exc).__name__}: {str(exc)[:110]}", file=sys.stderr)
            failures += 1

    print(f"\nwrote {os.path.abspath(args.out_dir)}")
    return 1 if failures == len(names) else 0


if __name__ == "__main__":
    sys.exit(main())
