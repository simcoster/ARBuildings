#!/usr/bin/env python
"""Export registry encoders to ONNX.

Two consumers:
  * bench.py --backend onnx-* imports ensure_exported() to get a cached file
  * the phone — Unity's Inference Engine (ex-Sentis) loads ONNX directly, so the
    artifact this writes is the same one that ships in the APK

Fixed batch and fixed spatial dims by default: mobile runtimes specialise far more
aggressively on static shapes, and a dynamic batch axis costs real latency there.
Use --dynamic-batch only for the server side.

    python export_onnx.py --models all
    python export_onnx.py --models mobilenetv4-s fastvit-t8 --out ../../Assets/StreamingAssets/models
"""

from __future__ import annotations

import argparse
import os
import sys

import torch

import models as model_registry

# None = let the exporter pick. Forcing 17 fails: the version converter has no
# downgrade adapter for Resize/Pad and leaves the model unmodified anyway.
DEFAULT_OPSET = None


def export(loaded, out_dir: str, batch: int = 1, opset: int = DEFAULT_OPSET,
           dynamic_batch: bool = False) -> str:
    os.makedirs(out_dir, exist_ok=True)
    suffix = "dyn" if dynamic_batch else f"b{batch}"
    path = os.path.join(out_dir, f"{loaded.spec.name}_{suffix}.onnx")

    dummy = torch.zeros(batch, 3, loaded.size, loaded.size)
    dynamic_axes = {"pixel_values": {0: "batch"}, "image_embedding": {0: "batch"}} if dynamic_batch else None

    module = loaded.module.eval().to("cpu", dtype=torch.float32)
    with torch.inference_mode():
        torch.onnx.export(
            module,
            dummy,
            path,
            input_names=["pixel_values"],
            output_names=["image_embedding"],
            **({"opset_version": opset} if opset else {}),
            dynamic_axes=dynamic_axes,
            do_constant_folding=True,
            verbose=False,
        )
    return path


def ensure_exported(loaded, out_dir: str, batch: int = 1, force: bool = False) -> str:
    """Cached export — bench.py calls this rather than re-exporting per run."""
    path = os.path.join(out_dir, f"{loaded.spec.name}_b{batch}.onnx")
    if os.path.exists(path) and not force:
        return path
    return export(loaded, out_dir, batch=batch)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models", nargs="+", default=model_registry.DEFAULT_SET)
    ap.add_argument("--out", default="onnx")
    ap.add_argument("--batch", type=int, default=1)
    ap.add_argument("--opset", type=int, default=DEFAULT_OPSET)
    ap.add_argument("--dynamic-batch", action="store_true")
    ap.add_argument("--force", action="store_true")
    args = ap.parse_args()

    names = list(model_registry.REGISTRY) if args.models == ["all"] else args.models

    print(f"{'model':<16}{'input':>7}{'dim':>6}{'params':>9}{'size':>10}")
    print("-" * 48)
    failures = 0
    for name in names:
        try:
            loaded = model_registry.load(name)
            path = export(loaded, args.out, args.batch, args.opset, args.dynamic_batch)
        except Exception as exc:  # noqa: BLE001
            print(f"    ! {name}: {exc}", file=sys.stderr)
            failures += 1
            continue

        mb = os.path.getsize(path) / 1e6
        print(f"{name:<16}{loaded.size:>7}{loaded.dim:>6}"
              f"{loaded.param_millions:>8.1f}M{mb:>9.1f}MB")

    print(f"\nwrote to {os.path.abspath(args.out)}")
    print("fp32 weights — quantise to int8/fp16 before shipping to the phone")
    return 1 if failures and failures == len(names) else 0


if __name__ == "__main__":
    sys.exit(main())
