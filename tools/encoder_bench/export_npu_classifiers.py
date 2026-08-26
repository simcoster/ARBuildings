#!/usr/bin/env python
"""Export MobileNetV2 INT8 ONNX — the sanity classifier for the ORT NNAPI path.

Reuses quantize.py's QDQ / per-tensor / uint8 recipe so the graph is the same
shape ENN was asked to run for MobileNetV4.

    python export_npu_classifiers.py
"""

from __future__ import annotations

import os
import sys

import numpy as np
import torch
import torchvision
from onnxruntime.quantization import (
    CalibrationMethod,
    QuantFormat,
    QuantType,
    quantize_static,
)
from onnxruntime.quantization.shape_inference import quant_pre_process

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "npu_models")


class NoiseReader:
    def __init__(self, input_name: str, size: int = 224, n: int = 16):
        self.input_name = input_name
        rng = np.random.default_rng(0)
        self.samples = [rng.random((1, 3, size, size), dtype=np.float32) for _ in range(n)]
        self.it = iter(self.samples)

    def get_next(self):
        nxt = next(self.it, None)
        return None if nxt is None else {self.input_name: nxt}

    def rewind(self):
        self.it = iter(self.samples)


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    print("exporting torchvision mobilenet_v2 fp32...", flush=True)
    model = torchvision.models.mobilenet_v2(weights="DEFAULT").eval()
    dummy = torch.zeros(1, 3, 224, 224)
    fp32 = os.path.join(OUT, "mobilenet_v2_224_fp32.onnx")
    with torch.inference_mode():
        torch.onnx.export(
            model, dummy, fp32,
            input_names=["pixel_values"],
            output_names=["logits"],
            do_constant_folding=True,
            dynamo=False,
        )
    print(f"  {os.path.getsize(fp32)/1e6:.2f} MB {fp32}")

    prepped = os.path.join(OUT, "mobilenet_v2_224_prep.onnx")
    int8 = os.path.join(OUT, "mobilenet_v2_224_int8.onnx")
    quant_pre_process(fp32, prepped, skip_symbolic_shape=False)

    import onnxruntime as ort
    in_name = ort.InferenceSession(prepped, providers=["CPUExecutionProvider"]).get_inputs()[0].name
    print("quantizing QDQ uint8 per-tensor (synthetic calib — latency only)...", flush=True)
    quantize_static(
        prepped, int8, NoiseReader(in_name),
        quant_format=QuantFormat.QDQ,
        activation_type=QuantType.QUInt8,
        weight_type=QuantType.QUInt8,
        per_channel=False,
        calibrate_method=CalibrationMethod.MinMax,
    )
    os.remove(prepped)
    print(f"  {os.path.getsize(int8)/1e6:.2f} MB {int8}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
