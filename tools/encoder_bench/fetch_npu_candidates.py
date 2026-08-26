#!/usr/bin/env python
"""Download INT8 classifiers and segmenters for the A35 NPU gate.

Nothing here is imported by Unity. Artifacts land in npu_models/ (gitignored).
Re-run is safe: existing files are skipped unless --force.

    python fetch_npu_candidates.py
"""

from __future__ import annotations

import argparse
import os
import tarfile
import zipfile
import urllib.request
import ssl
import sys

CERT = os.path.expandvars(r"%USERPROFILE%\.certs\ca-bundle-with-norton.pem")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "npu_models")

# (filename, url, notes) — archives are unpacked for a .tflite afterwards.
FILES = [
    (
        "mobilenet_v1_1.0_224_quant.tgz",
        "https://storage.googleapis.com/download.tensorflow.org/models/mobilenet_v1_2018_08_02/mobilenet_v1_1.0_224_quant.tgz",
        "oldest INT8 classifier; if ENN rejects this, it rejects almost everything",
    ),
    (
        "mobilenet_v2_1.0_224_quant.tgz",
        "https://storage.googleapis.com/download.tensorflow.org/models/tflite_11_05_08/mobilenet_v2_1.0_224_quant.tgz",
        "sanity classifier from the plan",
    ),
    (
        "coral_mobilenet_v2_1.0_224_quant.tflite",
        "https://github.com/google-coral/test_data/raw/master/mobilenet_v2_1.0_224_quant.tflite",
        "Edge-TPU INT8 MobileNetV2 — restricted op set",
    ),
    (
        "deeplabv3_mnv2_pascal_8bit.tar.gz",
        "http://download.tensorflow.org/models/deeplabv3_mnv2_pascal_train_aug_8bit_2019_04_26.tar.gz",
        "DeepLabV3-MNV2 PASCAL VOC INT8 — primary segmenter candidate",
    ),
    (
        "coral_deeplabv3_mnv2_pascal_quant.tflite",
        "https://github.com/google-coral/test_data/raw/master/deeplabv3_mnv2_pascal_quant.tflite",
        "Edge-TPU DeepLabV3 INT8",
    ),
    (
        "deeplabv3_257_mv_gpu.tflite",
        "https://storage.googleapis.com/download.tensorflow.org/models/tflite/gpu/deeplabv3_257_mv_gpu.tflite",
        "DeepLab 257 float GPU model — likely NPU-reject, useful as a control",
    ),
    (
        "mediapipe_selfie_segmenter_int8.tflite",
        "https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_segmenter/int8/latest/selfie_segmenter.tflite",
        "person vs background only; wrong taxonomy, tests NPU on a small graph",
    ),
    (
        "mediapipe_selfie_multiclass_256.tflite",
        "https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_multiclass_256x256/float32/latest/selfie_multiclass_256x256.tflite",
        "float32 multiclass selfie; likely NPU-reject",
    ),
    (
        "mediapipe_deeplab_v3_f32.tflite",
        "https://storage.googleapis.com/mediapipe-models/image_segmenter/deeplab_v3/float32/1/deeplab_v3.tflite",
        "MediaPipe DeepLab float32",
    ),
    (
        "android_aarch64_benchmark_model",
        "https://storage.googleapis.com/tensorflow-nightly-public/prod/tensorflow/release/lite/tools/nightly/latest/android_aarch64_benchmark_model",
        "official TFLite benchmark_model for aarch64",
    ),
]


def ssl_ctx() -> ssl.SSLContext:
    ctx = ssl.create_default_context()
    if os.path.isfile(CERT):
        ctx.load_verify_locations(CERT)
    return ctx


def fetch(url: str, dest: str) -> None:
    print(f"  GET {url}")
    req = urllib.request.Request(url, headers={"User-Agent": "AR_Buildings-npu-gate"})
    with urllib.request.urlopen(req, context=ssl_ctx(), timeout=120) as r:
        data = r.read()
    with open(dest, "wb") as f:
        f.write(data)
    print(f"      {os.path.getsize(dest)/1e6:.2f} MB -> {os.path.basename(dest)}")


def extract_tflite(archive: str, dest_dir: str) -> None:
    names: list[str] = []
    if archive.endswith(".tgz") or archive.endswith(".tar.gz"):
        with tarfile.open(archive, "r:*") as tar:
            for m in tar.getmembers():
                if m.name.lower().endswith(".tflite"):
                    m.name = os.path.basename(m.name)
                    tar.extract(m, dest_dir)
                    names.append(m.name)
    elif archive.endswith(".zip"):
        with zipfile.ZipFile(archive) as z:
            for n in z.namelist():
                if n.lower().endswith(".tflite"):
                    out = os.path.join(dest_dir, os.path.basename(n))
                    with z.open(n) as src, open(out, "wb") as dst:
                        dst.write(src.read())
                    names.append(os.path.basename(n))
    for n in names:
        print(f"      unpacked {n}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--force", action="store_true")
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)

    failures = 0
    for name, url, note in FILES:
        dest = os.path.join(OUT, name)
        print(f"\n{name}\n  {note}")
        if os.path.isfile(dest) and not args.force:
            print("  skip (exists)")
        else:
            try:
                fetch(url, dest)
            except Exception as e:  # noqa: BLE001
                print(f"  FAIL {type(e).__name__}: {e}", file=sys.stderr)
                failures += 1
                continue
        if name.endswith((".tgz", ".tar.gz", ".zip")) and os.path.isfile(dest):
            try:
                extract_tflite(dest, OUT)
            except Exception as e:  # noqa: BLE001
                print(f"  unpack FAIL {e}", file=sys.stderr)
                failures += 1

    print(f"\nwrote {OUT}")
    tflites = sorted(p for p in os.listdir(OUT) if p.endswith(".tflite"))
    for p in tflites:
        print(f"  {p:55s} {os.path.getsize(os.path.join(OUT, p))/1e6:6.2f} MB")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
