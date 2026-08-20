#!/usr/bin/env python
"""Merge bench.py --json outputs into one cross-backend comparison.

    python summary.py results_*.json
"""
from __future__ import annotations

import json
import sys

LABELS = {
    "torch-cuda-fp16": "4070S fp16",
    "torch-cuda-fp32": "4070S fp32",
    "torch-cpu": "CPU x4 torch",
    "onnx-cpu": "CPU x4 ORT",
    "onnx-cuda": "4070S ORT",
}


def main(paths: list[str]) -> int:
    rows: dict[str, dict[str, float]] = {}
    meta: dict[str, tuple] = {}
    backends: list[str] = []

    for path in paths:
        with open(path) as fh:
            data = json.load(fh)
        for r in data["rows"]:
            backend = r["backend"]
            if backend not in backends:
                backends.append(backend)
            rows.setdefault(r["model"], {})[backend] = r["fwd_p50"]
            meta[r["model"]] = (r["tier"], r["params_m"], r["input"], r["dim"])

    head = f"{'model':<16}{'tier':>7}{'params':>8}{'in':>5}{'dim':>6}"
    for b in backends:
        head += f"{LABELS.get(b, b):>14}"
    print(head)
    print("-" * len(head))

    for model, (tier, params, size, dim) in meta.items():
        line = f"{model:<16}{tier:>7}{params:>7.1f}M{size:>5}{dim:>6}"
        for b in backends:
            val = rows[model].get(b)
            line += f"{val:>14.2f}" if val is not None else f"{'-':>14}"
        print(line)

    print("\nforward-pass p50 in ms, batch 1, steady state")
    return 0


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(1)
    sys.exit(main(args))
