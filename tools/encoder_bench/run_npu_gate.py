#!/usr/bin/env python
"""Push candidate models to the A35 and run TFLite NNAPI with CPU disabled.

Never trust a run that allowed CPU fallback -- compare enn-nocpu vs CPU times.

    python run_npu_gate.py
"""

from __future__ import annotations

import os
import subprocess
import sys
from datetime import datetime

ADB = r"C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, "npu_models")
REMOTE = "/data/local/tmp/npugate"
LOG = os.path.join(HERE, "npu_gate_raw.txt")

TFLITES = [
    "mobilenet_v1_1.0_224_quant.tflite",
    "mobilenet_v2_1.0_224_quant.tflite",
    "coral_mobilenet_v2_1.0_224_quant.tflite",
    "deeplabv3_mnv2_pascal_8bit.tflite",
    "coral_deeplabv3_mnv2_pascal_quant.tflite",
    "deeplabv3_257_mv_gpu.tflite",
    "mediapipe_deeplab_v3_f32.tflite",
    "mediapipe_selfie_multiclass_256.tflite",
    "mediapipe_selfie_segmenter_f16.tflite",
]

MODES = [
    ("cpu", "--use_nnapi=false --num_threads=4"),
    ("nnapi-hybrid", "--use_nnapi=true --nnapi_accelerator_name=enn"),
    ("nnapi-nocpu", "--use_nnapi=true --nnapi_accelerator_name=enn --disable_nnapi_cpu=true"),
]


def adb(args: list[str], check: bool = False) -> subprocess.CompletedProcess:
    return subprocess.run(
        [ADB, *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=check,
    )


def sh(cmd: str) -> str:
    p = adb(["shell", cmd])
    return (p.stdout or "") + (p.stderr or "")


def push(local: str, remote: str) -> None:
    p = adb(["push", local, remote])
    sys.stdout.write(p.stdout or p.stderr or "")
    sys.stdout.flush()


def main() -> int:
    lines: list[str] = [f"# npu gate raw {datetime.now():%Y-%m-%d %H:%M:%S}", ""]
    bench = os.path.join(MODELS, "android_aarch64_benchmark_model")
    if not os.path.isfile(bench):
        print("benchmark_model missing. Run fetch_npu_candidates.py", file=sys.stderr)
        return 1

    sh(f"mkdir -p {REMOTE}")
    push(bench, f"{REMOTE}/benchmark_model")
    sh(f"chmod 755 {REMOTE}/benchmark_model")

    for name in os.listdir(MODELS):
        if name.endswith(".tflite"):
            print(f"push {name}")
            push(os.path.join(MODELS, name), f"{REMOTE}/{name}")

    onnx_v4 = os.path.join(HERE, "onnx_int8", "mobilenetv4-s_int8.onnx")
    if os.path.isfile(onnx_v4):
        push(onnx_v4, f"{REMOTE}/mobilenetv4-s_int8.onnx")
    onnx_v2 = os.path.join(MODELS, "mobilenet_v2_224_int8.onnx")
    if os.path.isfile(onnx_v2):
        push(onnx_v2, f"{REMOTE}/mobilenet_v2_224_int8.onnx")

    for model in TFLITES:
        exists = sh(f"if [ -f {REMOTE}/{model} ]; then echo yes; else echo no; fi").strip()
        if exists != "yes":
            lines.append(f"MISSING {model}")
            print(f"MISSING {model}")
            continue
        for mode, extra in MODES:
            header = f"===== {model}  {mode} ====="
            print(f"\n{header}", flush=True)
            adb(["logcat", "-c"])
            result = sh(
                f"cd {REMOTE}; ./benchmark_model --graph={model} {extra} "
                f"--num_runs=20 --warmup_runs=8 --num_threads=4"
            )
            logcat = adb(["logcat", "-d"]).stdout or ""
            enn = "\n".join(
                ln for ln in logcat.splitlines()
                if any(k in ln for k in ("ENN", "Operation Not Supported", "NNAPI", "accelerator", "nnapi"))
            )
            print(result)
            lines += [header, result.rstrip(), "-- logcat ENN/NNAPI --", enn, ""]

    ort = os.path.join(HERE, "android", "bench_ort")
    if os.path.isfile(ort):
        push(ort, f"{REMOTE}/bench_ort")
        so = os.path.join(HERE, "android", "ort_aar", "jni", "arm64-v8a", "libonnxruntime.so")
        push(so, f"{REMOTE}/libonnxruntime.so")
        sh(f"chmod 755 {REMOTE}/bench_ort")
        for onnx in ("mobilenetv4-s_int8.onnx", "mobilenet_v2_224_int8.onnx"):
            exists = sh(f"if [ -f {REMOTE}/{onnx} ]; then echo yes; else echo no; fi").strip()
            if exists != "yes":
                continue
            for ep in ("cpu", "nnapi-nocpu"):
                header = f"===== ORT {onnx}  {ep} ====="
                print(f"\n{header}", flush=True)
                result = sh(
                    f"cd {REMOTE}; LD_LIBRARY_PATH=. ./bench_ort -m {onnx} -e {ep} -r 20 -w 8 -t 4"
                )
                print(result)
                lines += [header, result.rstrip(), ""]

    with open(LOG, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"\nwrote {LOG}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
