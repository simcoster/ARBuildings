# encoder_bench

Latency harness for off-the-shelf image encoders — one forward pass, no fine-tuning.
Exists to answer one question: **what runs on the A35, and what has to go to a server.**

Nothing here touches the Unity project. It lives outside `Assets/` so Unity never
imports it.

---

## Setup

```bash
cd tools/encoder_bench
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu126
pip install transformers timm onnx onnxruntime-gpu open_clip_torch
```

On this machine Norton's Web/Mail Shield re-signs every TLS connection, so pip and
`huggingface_hub` reject the chain. Fixed by trusting Norton's root rather than
disabling verification — `C:\Users\simco\.certs\ca-bundle-with-norton.pem` is
certifi's bundle plus that root, and `PIP_CERT` / `REQUESTS_CA_BUNDLE` /
`SSL_CERT_FILE` / `CURL_CA_BUNDLE` point at it at user scope. Terminals opened
before that was set need `--cert <bundle>` passed explicitly.

## Use

```bash
python bench.py --forward-only --cuda-graph        # steady-state model latency
python bench.py                                    # full per-frame pipeline
python bench.py --models clip-vit-b32 dinov2-small
python bench.py --all-backends --json results.json
python bench.py --backend torch-cpu --threads 4   # crude A35 big-cluster proxy
python bench.py --backend onnx-cuda --batch 8     # server-side throughput
python export_onnx.py --models all                # artifacts for the phone
```

Weights download from HuggingFace on first use (a few hundred MB total) and cache
in `~/.cache/huggingface`.

## Models

| name | tier | why it's here |
|---|---|---|
| `mobilenetv4-s` | mobile | pure conv, floor of the range; mobile runtimes optimise it best |
| `fastvit-t8` | mobile | Apple's hybrid, explicitly designed for phone latency |
| `efficientvit-b1` | mobile | linear attention, good accuracy per ms on mobile GPUs |
| `vit-tiny` | mobile | plain ViT lower bound — architecture control against ViT-B |
| `mobileclip2-s0` | mobile | CLIP-aligned embeddings at mobile cost (needs `open_clip_torch`) |
| `dinov2-small` | server | 22M params but /14 patches = 257 tokens, so not as cheap as it looks |
| `clip-vit-b32` | server | the number everyone quotes; 50 tokens |
| `clip-vit-b16` | server | same params as b32, 4× tokens — isolates token count from width |
| `siglip2-base16` | server | stronger representations at ViT-B/16 cost |
| `dinov2-base` | server | best structural features here; the expensive end |

Only the **vision tower** is benchmarked for the dual-tower models. The text tower
never runs for an image-embedding workload, and the projection head after the tower
is one matmul, far below measurement noise.

`clip-vit-b32` vs `clip-vit-b16` is the useful controlled pair: identical parameter
count, 4× the token count. The gap between them is pure sequence-length cost, which
is what actually decides whether attention or memory bandwidth is your problem.

## Reading the output

Times are ms. Stages are separated because on a 4070 SUPER the forward pass is
routinely *not* the expensive part:

- **decode** — JPEG → PIL. Reported once, excluded from totals. From an AR camera
  frame you already have raw pixels, so this is an upper bound, not your cost.
- **preprocess** — resize/crop/normalize, single-threaded CPU. Frequently exceeds
  the forward pass. If it dominates, move it to the GPU or to DALI before you
  reach for a smaller model.
- **h2d** — host→device copy. Pageable (not pinned), so this is the realistic
  number, not the best case.
- **forward** — timed with `torch.cuda.Event`. Wall-clock around a CUDA call
  measures kernel *launch*, not execution — that mistake reports ~0.05 ms for
  everything and is the usual reason a benchmark looks too good.
- **e2e** — prep + h2d + forward + d2h.

First 20 iterations are discarded: cudnn autotune, allocator warmup, lazy init.
Watch **p99 vs p50** — a wide gap on an otherwise-fast model usually means memory
allocator churn or clock throttling, and it's what you'd actually feel per frame.

## Getting to the phone

`export_onnx.py` writes fixed-shape ONNX, which is what Unity's Inference Engine
(the package formerly called Sentis) consumes directly. That is the low-friction
path for this project — it's already a Unity 6 app, so it needs no native plugin,
and it works on Android and iOS from the same asset.

The alternative is the ONNX Runtime Android AAR behind a native plugin, with
XNNPACK or a GPU delegate. Faster in principle, meaningfully more build work, and
NNAPI specifically is deprecated on recent Android in favour of vendor delegates —
worth it only if Inference Engine measures too slow.

Before shipping: the exports are **fp32**. Quantise to fp16 or int8 first — roughly
2–4× smaller and materially faster on a Mali-G68, usually at negligible cost to
embedding quality for a frozen encoder.

### The honest caveat

`--backend torch-cpu --threads 4` approximates the A35's four Cortex-A78 cores, and
that is all it does. It shares neither the ISA, the cache hierarchy, the memory
bandwidth, nor the thermal envelope — and the A35 will throttle under sustained AR
load in a way a desktop never does. Treat desktop numbers as **ranking** the models,
not as predicting phone latency. The only number that settles the phone/server split
is one measured on the device, sustained, with the AR session already running and
competing for the same GPU.

## Layout

```
models.py       registry + loaders, wrapped to forward(pixel_values) -> [B, D]
bench.py        the harness
export_onnx.py  ONNX export, also used by bench.py's onnx-* backends
onnx/           exported artifacts (gitignored)
```

---

## Measured, 2026-08-20

Forward-pass p50, ms, batch 1, steady state. GPU uses `--cuda-graph`; CPU columns
are `--threads 4`, **one model per process** (see the warning below).

```
model              tier  params   in   dim    4070S fp16  CPU x4 torch    CPU x4 ORT
mobilenetv4-s    mobile    2.5M  224  1280          0.38          4.72          1.66
fastvit-t8       mobile    3.3M  256   768          0.79         15.85          6.28
efficientvit-b1  mobile    7.5M  224  1600          0.71         10.90          7.27
vit-tiny         mobile    5.5M  224   192          0.38          8.01          7.69
mobileclip2-s0   mobile   11.4M  256   512          1.51         30.94         16.15
dinov2-small     server   22.1M  224   384          0.88         28.91         27.33
clip-vit-b32     server   87.5M  224   768          0.87         26.82         21.30
clip-vit-b16     server   85.8M  224   768          1.32         71.60         67.65
siglip2-base16   server   92.9M  224   768          1.47         79.67         68.71
dinov2-base      server   86.6M  224   768          1.86         91.40         89.15
```

Reproduce:

```bash
python bench.py --models all --forward-only --cuda-graph --iters 200 --warmup 50 \
  --json results_graph_fp16.json

# CPU: one model per process, then merge
for b in torch-cpu onnx-cpu; do for m in $(python -c "import models;print(' '.join(models.REGISTRY))"); do
  python bench.py --models $m --forward-only --backend $b --threads 4 \
    --iters 30 --warmup 15 --json "iso_${b}_${m}.json"
done; done
python summary.py results_graph_fp16.json iso_torch-cpu_*.json iso_onnx-cpu_*.json
```

### Three traps, all of which produced wrong numbers here

**1. Batch-1 GPU timing without CUDA graphs measures Python.** Eager PyTorch
reported 8.89 ms for `clip-vit-b32` and 8.80 ms for `clip-vit-b16` — identical,
despite b16 having 4× the tokens. These models are ~150 kernels of a few
microseconds each; per-op dispatch cannot feed the GPU and CUDA events honestly
record the starvation gaps. Graph capture gives 0.87 / 1.32 ms, **10–24× lower**,
and restores the expected b32 < b16 ordering.

**2. Benchmarking many models in one process contaminates the CPU results.**
Sweeping all ten sequentially reported `siglip2-base16` at 260 ms and
`dinov2-base` at 268 ms. Run alone they are 80 ms and 91 ms — a **3× error**, and
it moved with sweep position, not with the model. It also fabricated an entirely
false conclusion ("ORT is 3× slower than PyTorch on small ViTs") that vanished
under isolation. Always one model per process for CPU numbers.

**3. `size` is not the input shape.** DINOv2's processor resizes the shortest edge
to 256 and *then* centre-crops to 224. Reading `size` instead of `crop_size` fed it
325 tokens where the real config is 257, overstating `dinov2-small` on ORT by 4×
(110 ms vs 27 ms).

### What the corrected numbers say

**Token count dominates on CPU; parameter count barely matters.** `clip-vit-b32`
has **4× the parameters** of `dinov2-small` and is still faster (21.3 vs 27.3 ms),
because 224/32 gives 50 tokens against 257. Within one family it is nearly linear:
b16 at 197 tokens costs 3.2× b32 at 50. **Choose patch size before parameter
count.**

**ORT beats PyTorch on CPU everywhere, by 1.3–2.5×.** The margin is largest on
convolutional models (`fastvit-t8` 15.9 → 6.3 ms) and smallest on plain ViTs
(`vit-tiny` 8.0 → 7.7 ms). The export is worth doing for the phone regardless, and
it is free speed on a CPU server.

**Preprocessing dominates the GPU path.** The full-pipeline run showed ~6 ms of
single-threaded resize/normalize against a 0.4–1.9 ms forward. On a 4070 the model
is not the bottleneck — the CPU in front of it is. Batch, or move preprocessing to
the GPU, before reaching for a smaller model.

### Reading it for the phone/server split

Desktop ORT CPU is the closest available proxy, and an A78 core will be roughly
3–5× slower than these Raptor Lake cores per thread:

- `mobilenetv4-s` (1.7 ms) has enormous margin — comfortably per-frame on device.
- `fastvit-t8` / `efficientvit-b1` / `vit-tiny` (6–8 ms) extrapolate to ~20–40 ms:
  viable per-frame only if you can accept a lower cadence than the AR frame rate.
- `mobileclip2-s0` (16 ms) → ~50–80 ms. On-demand only, not per frame — but it is
  the only mobile-tier model with a CLIP-aligned embedding space, which matters if
  you want text queries against building embeddings.
- Everything server-tier is 20–90 ms on desktop CPU and belongs behind the network,
  where a 4070 runs any of them under 2 ms.

### Open

- Exports land at the exporter's native opset; forcing 17 fails (no downgrade
  adapter for `Resize`/`Pad`). Unity Inference Engine supports a bounded opset
  range, so **verify it accepts these files** before assuming the phone path works.
- Nothing here has run on an A35. Every number above is desktop.

---

## On-device, Galaxy A35 (SM-A356E, Exynos 1380, Android 16), 2026-08-20

Run with `android/bench_ort`, a native arm64 binary linked against the prebuilt
ONNX Runtime 1.29.0 Android `.so`. No APK, no JNI — push and run over adb.

### The NPU cannot run any of these models

```
NNAPI devices found:  [enn]              Type 4 (ACCELERATOR)  = the NPU
                      [nnapi-reference]  Type 2 (CPU)
ENN driver version:   Exynos ENN v1.7.1-2
```

With `NNAPI_FLAG_CPU_DISABLED` set — NPU/GPU only, no silent CPU fallback — **all
ten models fail**:

```
The model cannot run using the current set of target devices, [Name: [enn], Type [4]]
```

This is not an ORT op-coverage problem. ORT reports `number of nodes supported by
NNAPI: 275 / 278` and builds two fused partitions. The refusal comes from
Samsung's driver, which logs:

```
[Exynos][ENN][v1.7.1-2][EnnDriver::GraphGenManager] getSupportedOperations:181:
    Operation Not Supported
```

**Never run NNAPI with CPU fallback enabled.** It "succeeds" by executing on
`nnapi-reference`, NNAPI's own unoptimised CPU kernels: `mobilenetv4-s` takes
**88.8 ms** that way against **2.4 ms** on ORT's CPU EP — 37× slower while
appearing to be hardware-accelerated. A benchmark without `CPU_DISABLED` would
have reported "NPU works" and been wrong.

### What actually runs: ORT CPU, int8, 4 threads

```
model              int8 p50    fp32 p50   int8 gain    desktop int8-equiv ratio
mobilenetv4-s          2.37        5.81       2.45x    3.5x slower than desktop
efficientvit-b1       20.79           -           -
vit-tiny              20.37       30.77       1.51x    4.0x
clip-vit-b32          37.51       92.36       2.46x    4.3x
fastvit-t8            41.81           -           -
dinov2-small          68.65      134.49       1.96x    4.9x
mobileclip2-s0        92.77           -           -
clip-vit-b16         127.66           -           -
siglip2-base16       145.29           -           -
dinov2-base          204.98           -           -
```

**The desktop proxy held up.** fp32-to-fp32, the A35 came in 3.5–4.9× slower than
Raptor Lake at 4 threads — inside the 3–5× band estimated from the desktop runs.
The `--threads 4` proxy is a usable predictor for ranking, within about ±30%.

**int8 is worth doing: 1.5–2.5×**, largest on convolutional models. It is required
for the NPU (which then rejects the graph anyway) but it earns its place on the CPU
regardless.

`mobilenetv4-s` at **2.4 ms** is comfortably per-frame on device with the AR
session running. Everything ViT-shaped is 20 ms+ and belongs in a bounded loop, not
a per-frame path.

### Reproduce

```bash
# build (uses the NDK bundled with Unity)
cd android
curl -sLO https://repo1.maven.org/maven2/com/microsoft/onnxruntime/onnxruntime-android/1.29.0/onnxruntime-android-1.29.0.aar
unzip -q onnxruntime-android-1.29.0.aar -d ort_aar
"$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android29-clang++" \
  -std=c++17 -O2 -fPIE -pie -I ort_aar/headers bench_ort.cpp \
  -L ort_aar/jni/arm64-v8a -lonnxruntime -o bench_ort

# quantize, push, run
cd .. && python quantize.py --models all
adb push android/bench_ort android/ort_aar/jni/arm64-v8a/libonnxruntime.so \
  "$NDK/.../sysroot/usr/lib/aarch64-linux-android/libc++_shared.so" /data/local/tmp/encbench/
adb push onnx_int8/. /data/local/tmp/encbench/
adb shell "cd /data/local/tmp/encbench && LD_LIBRARY_PATH=. \
  ./bench_ort -m mobilenetv4-s_int8.onnx -e nnapi-nocpu -r 30 -w 10 -t 4"
```

Note: run adb from PowerShell, or set `MSYS_NO_PATHCONV=1` — Git Bash rewrites
`/data/local/tmp/...` into a Windows path and the push silently lands elsewhere.

### Caveats

- These numbers are on an **idle** device. Nothing else was running: no AR session,
  no camera, no rendering, and no accumulated heat. Real figures under ARCore load
  will be worse, and the A35 throttles.
- Calibration was synthetic, so **latency is valid and accuracy is unmeasured**.
  Re-run `quantize.py --calib-dir` with real frames before trusting any output.
