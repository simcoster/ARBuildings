"""Weight-only fp16 of the LiteRT-community DIS-ISNet 1024 graph.

HuggingFace ships only dis.tflite (176 MB float32). U2-Net's 88 MB file was the
same weights run through ai-edge-quantizer FLOAT_CASTING. I/O stays NCHW
float32 so the GL SSBO path does not change. Weights of CONV_2D /
DEPTHWISE_CONV_2D / CONV_2D_TRANSPOSE / FULLY_CONNECTED become float16.

    python export_isnet_fp16.py
"""

from __future__ import annotations

import os
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "occ_models", "dis_isnet_1024.tflite")
DST = os.path.join(ROOT, "occ_models", "dis_isnet_1024_fp16.tflite")

OPS = (
    "CONV_2D",
    "DEPTHWISE_CONV_2D",
    "CONV_2D_TRANSPOSE",
    "FULLY_CONNECTED",
)


def inspect(path: str) -> None:
    import numpy as np
    import tensorflow as tf

    it = tf.lite.Interpreter(model_path=path)
    it.allocate_tensors()
    ins, outs = it.get_input_details(), it.get_output_details()
    i, o = ins[0], outs[0]
    print(f"  in  {list(i['shape'])} {np.dtype(i['dtype']).name}")
    print(f"  out {list(o['shape'])} {np.dtype(o['dtype']).name}")
    n_fp16 = 0
    n_fp32 = 0
    for d in it.get_tensor_details():
        dt = np.dtype(d["dtype"]).name
        if dt == "float16":
            n_fp16 += 1
        elif dt == "float32":
            n_fp32 += 1
    print(f"  tensors float16={n_fp16} float32={n_fp32}")


def main() -> int:
    if not os.path.isfile(SRC):
        print(f"missing {SRC}", file=sys.stderr)
        return 1

    from ai_edge_quantizer import quantizer
    from ai_edge_quantizer.qtyping import TFLOperationName

    src_mb = os.path.getsize(SRC) / 1e6
    print(f"src {SRC} {src_mb:.1f} MB")
    inspect(SRC)

    qt = quantizer.Quantizer(SRC)
    for name in OPS:
        qt.add_weight_only_config(
            regex=".*",
            operation_name=TFLOperationName[name],
            num_bits=16,
            algorithm_key=quantizer.AlgorithmName.FLOAT_CASTING,
        )
    print("quantizing (weight-only fp16)…")
    result = qt.quantize()
    result.export_model(DST, overwrite=True)
    dst_mb = os.path.getsize(DST) / 1e6
    print(f"dst {DST} {dst_mb:.1f} MB  ({src_mb / dst_mb:.2f}x)")
    inspect(DST)
    return 0


if __name__ == "__main__":
    sys.exit(main())
