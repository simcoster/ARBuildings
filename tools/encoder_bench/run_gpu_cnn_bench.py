"""Push the dummy CNNs to the A35 and time CPU (XNNPACK) vs GPU (OpenCL).

    python export_cnn_sizes.py
    python run_gpu_cnn_bench.py
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
from datetime import datetime

ADB = r"C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, "cnn_models")
REMOTE = "/data/local/tmp/cnnbench"
LOG = os.path.join(HERE, "cnn_gpu_bench_raw.txt")
BENCH_ON_PHONE = "/data/local/tmp/npugate/benchmark_model"

# Full-frame sweep only. Original 128/224/256 numbers are already measured.
MODELS_TO_RUN = [
    "cnn_s_512.tflite",
    "cnn_m_512.tflite",
    "cnn_l_512.tflite",
    "cnn_s_1024.tflite",
    "cnn_m_1024.tflite",
    "cnn_l_1024.tflite",
]

MODES = [
    (
        "gpu-cl",
        "--use_gpu=true --gpu_backend=cl --gpu_precision_loss_allowed=true "
        "--require_full_delegation=true --num_threads=1",
    ),
    ("cpu", "--use_xnnpack=true --use_gpu=false --num_threads=4"),
]

AVG_RE = re.compile(r"Inference \(avg\):\s*([\d.eE+-]+)")
DELEGATE_RE = re.compile(r"Replacing (\d+) out of (\d+) node\(s\) with delegate \(([^)]+)\)")
INIT_RE = re.compile(r"Init:\s*([\d.]+)")


def adb(args: list[str], timeout: int = 120) -> subprocess.CompletedProcess:
    return subprocess.run(
        [ADB, *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )


def sh(cmd: str, timeout: int = 180) -> str:
    p = adb(["shell", cmd], timeout=timeout)
    return (p.stdout or "") + (p.stderr or "")


def push(local: str, remote: str) -> None:
    p = adb(["push", local, remote], timeout=60)
    sys.stdout.write(p.stdout or p.stderr or "")
    sys.stdout.flush()


def parse(text: str) -> dict:
    avg_m = AVG_RE.search(text)
    init_m = INIT_RE.search(text)
    del_m = list(DELEGATE_RE.finditer(text))
    delegates = ", ".join(f"{m.group(3)} {m.group(1)}/{m.group(2)}" for m in del_m) or "none"
    failed = "Failed to apply" in text or "FAILED" in text or "ERROR" in text
    return {
        "avg_us": float(avg_m.group(1)) if avg_m else None,
        "init_us": float(init_m.group(1)) if init_m else None,
        "delegates": delegates,
        "failed": failed and avg_m is None,
        "raw": text,
    }


def main() -> int:
    missing = [n for n in MODELS_TO_RUN if not os.path.isfile(os.path.join(MODELS, n))]
    if missing:
        print("missing models: " + ", ".join(missing), file=sys.stderr)
        print("run: python export_cnn_sizes.py", file=sys.stderr)
        return 1

    lines: list[str] = [f"# cnn gpu bench {datetime.now():%Y-%m-%d %H:%M:%S}", ""]
    gpu_info = sh("getprop ro.hardware.egl; getprop ro.board.platform; getprop ro.hardware").strip()
    print(f"device GPU: {gpu_info.replace(chr(10), ' / ')}")
    lines.append(f"device: {gpu_info}")

    sh(f"mkdir -p {REMOTE}")
    exists = sh(f"if [ -x {BENCH_ON_PHONE} ]; then echo yes; else echo no; fi").strip()
    if exists != "yes":
        local_bench = os.path.join(HERE, "npu_models", "android_aarch64_benchmark_model")
        if not os.path.isfile(local_bench):
            print("benchmark_model missing on phone and disk", file=sys.stderr)
            return 1
        push(local_bench, f"{REMOTE}/benchmark_model")
        sh(f"chmod 755 {REMOTE}/benchmark_model")
        bench = f"{REMOTE}/benchmark_model"
    else:
        bench = BENCH_ON_PHONE

    for name in MODELS_TO_RUN:
        print(f"push {name}")
        push(os.path.join(MODELS, name), f"{REMOTE}/{name}")

    rows: list[tuple] = []
    for name in MODELS_TO_RUN:
        for mode, extra in MODES:
            header = f"===== {name}  {mode} ====="
            print(f"\n{header}", flush=True)
            adb(["logcat", "-c"])
            # 1024 dense 3x3s can sit in Init (OpenCL) and then be seconds per run.
            big = "1024" in name
            runs = 10 if big else 20
            warm = 4 if big else 8
            result = sh(
                f"{bench} --graph={REMOTE}/{name} {extra} "
                f"--num_runs={runs} --warmup_runs={warm} "
                f"--max_secs=400 --warmup_max_secs=90",
                timeout=500,
            )
            logcat = adb(["logcat", "-d", "-t", "200"]).stdout or ""
            gpu_log = "\n".join(
                ln for ln in logcat.splitlines()
                if any(k in ln for k in ("GpuDelegate", "OpenCL", "OpenGL", "Mali", "TfLiteGpu"))
            )
            parsed = parse(result)
            avg_ms = parsed["avg_us"] / 1000.0 if parsed["avg_us"] is not None else None
            print(result)
            if gpu_log:
                print("-- logcat --")
                print(gpu_log[-2000:])
            lines += [header, result.rstrip(), "-- logcat --", gpu_log, ""]
            rows.append((name, mode, avg_ms, parsed["delegates"], parsed["failed"]))

            if mode == "gpu-cl" and (parsed["failed"] or avg_ms is None):
                header2 = f"===== {name}  gpu-auto (cl failed) ====="
                print(f"\n{header2}", flush=True)
                extra2 = (
                    "--use_gpu=true --gpu_precision_loss_allowed=true "
                    "--require_full_delegation=true --num_threads=1"
                )
                result2 = sh(
                    f"{bench} --graph={REMOTE}/{name} {extra2} "
                    f"--num_runs={runs} --warmup_runs={warm} "
                    f"--max_secs=400 --warmup_max_secs=90",
                    timeout=500,
                )
                parsed2 = parse(result2)
                avg2 = parsed2["avg_us"] / 1000.0 if parsed2["avg_us"] is not None else None
                print(result2)
                lines += [header2, result2.rstrip(), ""]
                rows.append((name, "gpu-auto", avg2, parsed2["delegates"], parsed2["failed"]))

    print("\n" + "=" * 72)
    print(f"{'model':18s} {'mode':10s} {'avg ms':>10s}  delegate")
    for name, mode, avg, delegates, failed in rows:
        avg_s = "FAIL" if failed or avg is None else f"{avg:10.2f}"
        print(f"{name:18s} {mode:10s} {avg_s:>10s}  {delegates}")

    with open(LOG, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
        f.write("\n# summary\n")
        for name, mode, avg, delegates, failed in rows:
            avg_s = "FAIL" if failed or avg is None else f"{avg:.2f}"
            f.write(f"{name}\t{mode}\t{avg_s}\t{delegates}\n")
    print(f"\nwrote {LOG}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
