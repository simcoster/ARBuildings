"""Export dummy Conv+ReLU CNNs as float32 TFLite.

Random weights — latency only, not accuracy. Plain 3x3 convs, not depthwise,
so the numbers are an honest upper bound on a from-scratch train.

    python export_cnn_sizes.py
"""

from __future__ import annotations

import os
import shutil
import tempfile

os.environ["TF_CPP_MIN_LOG_LEVEL"] = "2"

import tensorflow as tf

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cnn_models")

# (name, input, channel_per_stage, convs_per_stage)
# Original four, then S/M/L again at 512 and 1024 (full-frame, not 256).
ARCH_S = (32, 64, 128, 256)
ARCH_M = (32, 64, 128, 256, 256)
ARCH_L = (64, 128, 256, 512, 512)
SPECS = [
    ("cnn_xs", 128, (16, 32, 64, 128), 1),
    ("cnn_s", 224, ARCH_S, 1),
    ("cnn_m", 224, ARCH_M, 2),
    ("cnn_l", 256, ARCH_L, 2),
    ("cnn_s_512", 512, ARCH_S, 1),
    ("cnn_m_512", 512, ARCH_M, 2),
    ("cnn_l_512", 512, ARCH_L, 2),
    ("cnn_s_1024", 1024, ARCH_S, 1),
    ("cnn_m_1024", 1024, ARCH_M, 2),
    ("cnn_l_1024", 1024, ARCH_L, 2),
]


def estimate_macs(input_size: int, widths: tuple[int, ...], convs_per: int) -> int:
    h = input_size
    cin = 3
    macs = 0
    for c in widths:
        for _ in range(convs_per):
            macs += h * h * 9 * cin * c
            cin = c
        h //= 2
    macs += cin * 10  # GAP -> Dense(10)
    return macs


def build_cnn(input_size: int, widths: tuple[int, ...], convs_per: int) -> tf.keras.Model:
    inp = tf.keras.Input(shape=(input_size, input_size, 3), batch_size=1, name="input")
    x = inp
    for i, c in enumerate(widths):
        for j in range(convs_per):
            x = tf.keras.layers.Conv2D(c, 3, padding="same", activation="relu", name=f"c{i}_{j}")(x)
        x = tf.keras.layers.MaxPool2D(2, name=f"p{i}")(x)
    x = tf.keras.layers.GlobalAveragePooling2D(name="gap")(x)
    x = tf.keras.layers.Dense(10, name="logits")(x)
    return tf.keras.Model(inp, x, name="cnn")


def to_tflite(model: tf.keras.Model) -> bytes:
    try:
        converter = tf.lite.TFLiteConverter.from_keras_model(model)
        return converter.convert()
    except Exception as e:
        print(f"  from_keras_model failed ({type(e).__name__}: {e}); using SavedModel", flush=True)
        tmp = tempfile.mkdtemp(prefix="cnn_sm_")
        try:
            model.export(tmp)
            converter = tf.lite.TFLiteConverter.from_saved_model(tmp)
            return converter.convert()
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    print(f"{'name':12s} {'in':>5s} {'params':>10s} {'GMACs':>8s} {'file':>8s}  stages", flush=True)
    for name, size, widths, convs in SPECS:
        path = os.path.join(OUT, f"{name}.tflite")
        if os.path.isfile(path):
            params = "skip"
            print(f"{name:12s} {size:5d}  (exists, skip)", flush=True)
            continue
        model = build_cnn(size, widths, convs)
        params = int(model.count_params())
        macs = estimate_macs(size, widths, convs)
        buf = to_tflite(model)
        with open(path, "wb") as f:
            f.write(buf)
        stages = " ".join(f"{c}x{convs}" for c in widths)
        print(
            f"{name:12s} {size:5d} {params/1e6:9.3f}M {macs/1e9:7.3f} "
            f"{len(buf)/1e6:7.2f}MB  {stages}",
            flush=True,
        )
    print(f"\nwrote {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
