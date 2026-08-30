# NPU / occlusion model matrix

Started 2026-08-26 on the Galaxy **A35** (SM-A356E, Exynos 1380, ENN v1.7.1-2).
Later rows add the **S24 Ultra** (SM-S928B, Snapdragon 8 Gen 3) and the
matting / Canny work of 2026-08-26–30.

The first half is still the ENN gate: runtime is **TFLite `benchmark_model`**
unless a row says ORT.
NNAPI devices: `[enn]` (type 4, the NPU) and `[nnapi-reference]` (type 2, CPU).

**Never trust a run that allowed CPU fallback without checking node placement.**
`nnapi-hybrid` leaves `nnapi-reference` available. A graph that "succeeds" there
can still be 100% CPU. The column that answers "did ENN take it?" is
`nnapi-nocpu` (`--disable_nnapi_cpu=true --nnapi_accelerator_name=enn`) plus the
delegate line (`Replacing N out of M node(s) with TfLiteNnapiDelegate`).

Times are inference average, milliseconds, idle device, no AR session.

Reproduce:

```
cd tools/encoder_bench
python fetch_npu_candidates.py
python export_npu_classifiers.py
python run_npu_gate.py
```

Raw log: `tools/encoder_bench/npu_gate_raw.txt` (gitignored).

---

## What actually ran on the NPU

TFLite + NNAPI/`enn` with CPU disabled **does** take some graphs. ORT's NNAPI
EP still rejects the same architectures (see ORT section). The earlier
encoder_bench result — "ENN cannot run any of these models" — was true of
**ORT QDQ ONNX**, not of official UINT8 TFLite.

Shipped in the app: `Assets/StreamingAssets/deeplabv3_257_mv_gpu.tflite`
(DeepLab v3, 257x257, PASCAL VOC, float32). Full graph on ENN, 70/70 nodes,
56.6 ms. That is slower than its own CPU/XNNPACK (17.7 ms) and that is
recorded, not hidden. NPU was the requirement; this is the first **segmenter**
that loaded on `[enn]` with CPU disabled.

---

## TFLite

### Classifiers (sanity)

| model | format | cpu | nnapi-hybrid | nnapi-nocpu | ENN nodes | result |
|---|---|---|---|---|---|---|
| `mobilenet_v1_1.0_224_quant.tflite` | UINT8 tflite | 16.36 | 11.54 | **11.56** | 31/31, 1 partition | **ENN full** |
| `mobilenet_v2_1.0_224_quant.tflite` | UINT8 tflite | 9.68 | 13.62 | **13.71** | 65/65, 1 partition | **ENN full** (XNNPACK CPU is faster) |
| `coral_mobilenet_v2_1.0_224_quant.tflite` | UINT8 tflite, Edge-TPU | 9.60 | 13.69 | **13.69** | 66/66, 1 partition | **ENN full** |

MobileNetV1 INT8 is the floor: if this had failed, ENN would be refusing almost
every third-party graph. It did not fail.

### Segmenters

| model | format | cpu | nnapi-hybrid | nnapi-nocpu | ENN nodes | result |
|---|---|---|---|---|---|---|
| `deeplabv3_257_mv_gpu.tflite` | float32, 257x257, PASCAL | 17.67 | 56.79 | **56.62** | 70/70, 1 partition | **ENN full — shipped** |
| `mediapipe_deeplab_v3_f32.tflite` | float32, same family | 17.08 | 56.61 | **56.80** | 70/70, 1 partition | **ENN full** (same graph, not shipped) |
| `deeplabv3_mnv2_pascal_8bit.tflite` | UINT8, 513-class DeepLab | CPU fail (`RESIZE_BILINEAR` vs XNNPACK) | 86.93 | **87.09** | 70/71, 2 partitions | **ENN 70/71**; leftover node on XNNPACK even with `disable_nnapi_cpu` (that flag blocks `nnapi-reference`, not TFLite CPU kernels) |
| `coral_deeplabv3_mnv2_pascal_quant.tflite` | UINT8 Edge-TPU | 83.62 | 83.86 | 83.89 | 0 — "graph will not be executed by the delegate" | **REJECT** (XNNPACK 70/72) |
| `mediapipe_selfie_multiclass_256.tflite` | float32, 16 MB | 101.9 | 100.9 | 101.6 | 0 — not executed by the delegate | **REJECT** (wrong taxonomy anyway) |
| `mediapipe_selfie_segmenter_f16.tflite` | float16 | 3.23 | fail | fail | custom op `Convolution2DTransposeBias` | **REJECT** |

`mediapipe_selfie_segmenter` INT8 404'd (`int8/latest` and `int8/1`). Not re-tried.

### Not obtained

| model | why |
|---|---|
| BiSeNetV2 INT8 tflite | Qualcomm Hub and PINTO URLs 404'd; no public file used |
| Fast-SCNN / DDRNet23-slim INT8 | same — no fetchable artifact. Samsung ENN SDK samples use a converted `.nnc` we cannot produce without their conversion service |
| Qualcomm `BiseNet_w8a8.tflite` / `DeepLabV3_Plus_MobileNet_w8a8.tflite` | HuggingFace resolve URLs 404 |

Stopped at the first fully-ENN segmenter (`deeplabv3_257_mv_gpu`). PASCAL VOC
classes are the right taxonomy for this site: person / car / bus / bicycle /
motorbike are THING; class 0 (background) is plaza, road, sky, building and
never occludes.

---

## CPU sweep — measured 2026-08-26, in the app, on camera frames

The NPU requirement is what picked `deeplabv3_257_mv_gpu`, and it is the wrong
constraint: ENN ran that graph at 56.6 ms against 17.7 ms on its own CPU. Once
inference moved **off the render thread** (see below) the accelerator stopped
mattering for frame rate, so these numbers are the ones that decide the model.

Timings are `seg inference` from the state dump — real camera frames, AR session
live, warm device. Swap models with `segmodel <file>`; no rebuild.

| model | in | out tensor | infer | verdict |
|---|---|---|---|---|
| `deeplabv3_257_mv_gpu.tflite` | 257² f32 | FLOAT32 257²×21 | **89 ms** | shipped; coarse, and MISSED a chair the 513 found |
| `deeplabv3_mnv2_pascal_8bit.tflite` | 513² u8 | INT64 513²×1 | **83 ms** | **best of these** — 4× the mask pixels, and faster |
| `coral_deeplabv3_mnv2_pascal_quant.tflite` | 513² u8 | INT64 513²×1 | 154 ms | same output, ~2× the cost. Reject |
| `mediapipe_selfie_multiclass_256.tflite` | 256² f32 | 256²×6 | **515 ms** | reject on speed. 6 channels are person PARTS, not PASCAL |
| `mediapipe_selfie_segmenter_f16.tflite` | — | — | — | will not load: custom op `Convolution2DTransposeBias` |
| `frozen_inference_graph.tflite` | — | — | — | byte-identical to `deeplabv3_mnv2_pascal_8bit`. Not a separate model |

`XNNPACK on/off` (`segxnn`) changed nothing measurable for any graph that loaded.
The earlier "CPU fail (`RESIZE_BILINEAR` vs XNNPACK)" note against DeepLab 513 did
not reproduce in-app.

### Three faults this sweep found, each of which forges a plausible result

**1. Synchronous inference makes frame time equal inference time.** `InferLabels`
was a blocking JNI call from `Update`. Frame time tracked it exactly: 33 ms with
segmentation off, 60–106 ms on, always ≈ the inference. That reads as "the
accelerator is slow" and it is really "the caller is blocked", so it penalised
every backend and every model identically and could never be optimised away by
choosing between them. `NpuSegmenter` now owns a worker thread; C# calls
`submit()` / `pollLabels()`. **Measured after: 90 ms inference, 33.4 ms frame time
— the same as segmentation off.**

**2. `XRCpuImage.Convert` cannot UPSCALE.** The camera CPU image is 640×480, so
every 513×513 model threw `ArgumentOutOfRangeException: Converted image height
must be less than or equal to native image height. 513 > 480` once per frame, and
the state dump kept showing the *previous* model's inference time and class
histogram. Three different models reported byte-identical pixel counts, which is
the only reason it was caught. Convert now targets the largest size that fits and
`ResizeRgb` scales up to the tensor; `seg convert` says which happened.

**3. A one-channel output is a label map, and the integer width must match.**
DeepLab 513's exported `ArgMax` is **INT64** — 8 bytes per pixel. Reading it a
byte at a time yields a label followed by seven zeros, decoding as
`background=263169`, i.e. *every pixel is background*: indistinguishable from a
model that detects nothing, and it would have been written down as "513 is no
better". `seg output tensor` now prints dtype, shape and bytes-per-pixel, which
identifies this instantly.

### What is now the frame-time bottleneck

`quality:` reads 63–77 ms with a 513 model. Inference is off the main thread but
`VoteAndExpand` (connected components over 263k label pixels) and the mask upload
are not. That is the next thing to move or subsample, not the model.

---

## ONNX Runtime 1.29.0 (same device, `NNAPI_FLAG_CPU_DISABLED`)

Confirms the 2026-08-20 encoder_bench finding. TFLite and ORT do **not** lower
to ENN the same way.

| model | cpu p50 | nnapi-nocpu |
|---|---|---|
| `mobilenetv4-s_int8.onnx` (QDQ uint8, from encoder_bench) | 2.36 ms | **REJECT** — `The model cannot run using the current set of target devices, [Name: [enn], Type [4]]` |
| `mobilenet_v2_224_int8.onnx` (QDQ uint8, torchvision export) | 5.11 ms | **REJECT** — same ENN error |

The official **TFLite** MobileNetV2 UINT8 on the row above is 13.7 ms fully on
ENN. The ORT QDQ ONNX of MobileNetV2 is refused. Do not ship ORT NNAPI on this
phone; use TFLite.

MobileNetV4 remains ENN-incompatible in the only form we have (ORT QDQ ONNX).
There was no official MobileNetV4 UINT8 TFLite in this sweep.

---

## How to read a future run

A pass requires **all** of:

1. `--nnapi_accelerator_name=enn`
2. `--disable_nnapi_cpu=true`
3. log line `Replacing N out of N node(s) with delegate (TfLiteNnapiDelegate)` (or `N out of M` with M-N named, and those leftovers **not** `nnapi-reference`)
4. `NNAPI accelerators available` still lists `enn`

`Though NNAPI delegate is explicitly applied, the model graph will not be executed by the delegate` means REJECT, even if inference "succeeds" at CPU speed.

`disable_nnapi_cpu` is not the same as ORT's `NNAPI_FLAG_CPU_DISABLED`. TFLite
can still run leftover partitions on XNNPACK. That is why DeepLab 8bit shows
70/71 on ENN *and* an XNNPACK line in the same `nnapi-nocpu` run.

---

## App wiring

- Shipped in the APK: `StreamingAssets/coral_deeplabv3_mnv2_pascal_quant.tflite`.
  The 257 DeepLab that first hit ENN was retired. Everything else in the HUD
  cycle is either **pushed** to `persistentDataPath` or **built in** (`canny`).
- Runtime: `com.pavel.arbuildings.NpuSegmenter` (TFLite 2.16.1). Worker thread
  (`submit` / `pollLabels`); never `interpreter.run` from `Update`.
- Fusion: `SemanticOcclusion` — PASCAL THING connected components, or a scalar
  matte / Canny edge map painted as alpha. Device `*.tflite` under 100 MB join
  the cycle; IS-Net (176 MB) stays off it even if the file is sitting there.
- Tripod: `seg on|off`, `segmin N`, `segdebug on|off`, `segmax N`,
  `seg cpu|gpu|gpudec|npu`, `segrot N`, `segcrop on|off`, `segnorm <mean> <scale>`,
  `segkind auto|labels|alpha|depth`, `segbox on|off`, `segdump`,
  `segmodel FILE|canny`, `segnext`, `seglist`.
- **Model swapping is a file push** except Canny, which has no file.
  `adb push model.tflite $DEV/` then `segmodel model.tflite`.

---

## Occlusion candidates — 2026-08-26 to 2026-08-30

DeepLab answers "which PASCAL class is this pixel". That is the wrong question
once the thing in front of the building is a person, a bottle, or a chair the
257-model missed. The later work compared **mattes**, **monocular depth**, a
**dummy CNN size sweep**, and **Canny**, first on the desk webcam and then on
both phones.

### Dummy CNN — how big a conv stack is 30 fps?

Four increasing Conv+ReLU stacks (`tools/encoder_bench/export_cnn_sizes.py`),
TFLite `benchmark_model`, GPU = `--use_gpu --gpu_backend=cl`, CPU = XNNPACK ×4.
A35 is Mali OpenCL; S24 is Adreno. Full delegation on both.

| size | in | A35 GPU | A35 CPU | S24 GPU | S24 CPU |
|---|---|---|---|---|---|
| xs | 128² | 6.5 | 1.0 | — | — |
| s | 224² | 22 | 15 | — | — |
| m | 224² | 30 | 47 | — | — |
| l | 256² | 48 | 301 | — | — |
| s | 512² | 34 | 66 | **7.1** | 40 |
| m | 512² | 58 | 263 | — | — |
| l | 512² | 161 | 1272 | — | — |
| s | 1024² | 77 | 344 | — | — |
| m | 1024² | 211 | 1303 | — | — |
| l | 1024² | 627 | 5144 | — | — |

Tiny graphs are **slower on Mali than on CPU**. The 512 / 1024 rows are the
ones that matter: a 512² "small" stack is 34 ms on the A35 GPU (borderline
one-mask-per-frame) and 7 ms on the S24. Unity's **frame** budget is ~33 ms
and is already locked; inference can be slower if it stays on the worker
(measured: 90 ms infer, 33.4 ms frame). A fresh mask every displayed frame
needs infer ≲33 ms.

### Real nets — params, files, batch

| model | params | file | input | batch |
|---|---|---|---|---|
| DIS-ISNet `dis_isnet_1024.tflite` | 44.0M f32 | 176 MB | `[1,3,1024,1024]` | **fixed 1** |
| U2-Net `u2net_320_fp16.tflite` | ~44M fp16 | 88 MB | `[1,3,320,320]` | fixed 1 |
| MODNet `modnet_512.tflite` | 6.46M f32 | 26 MB | `[1,3,512,512]` | fixed 1 |

HuggingFace [DIS-ISNet-LiteRT](https://huggingface.co/litert-community/DIS-ISNet-LiteRT)
ships only the 1024 file. Adding batch means a re-export, not a runtime resize.
Dynamic batch is a bad idea on mobile GPU. `export_modnet_256.py` already
re-exports MODNet (Windows: onnx-tf; no litert-converter wheel here).

IS-Net on the A35 CPU was ~6.8 s/frame. It is excluded from `Catalogue()` by
the 100 MB cap.

### Desk webcam — `tools/webcam_desk`

Does not touch the Unity project. LiteRT CompiledModel on the laptop GPU.
Keys: **1** IS-Net 1024 full-frame, **2** Depth Anything 3, **3** U2-Net 320
centred 320² patch, **4** MODNet 512 centred 512² patch, **5** 30/15/10/5 fps
motion dots, **6** full-frame OpenCV Canny (Gaussian 5×5, 80/160, cyan).

Norms: DIS `x/255-0.5`; MODNet `(x/255-0.5)/0.5`; U2-Net `/max` then ImageNet;
DA3 ImageNet. Phone `ApplyModelDefaults` special-cases `modnet` / `u2net` /
`isnet` the same way.

### Phone GPU vs CPU — S24 Ultra, 2026-08-29

Classic TfLiteGpuDelegateV2 OpenCL vs XNNPACK. NNAPI on this S24 listed only
`nnapi-reference` — Hexagon is **not** a generic NNAPI device. `--use_hexagon`
failed (`libhexagon_interface.so` missing). Galaxy AI's NPU is a closed path;
it is not this delegate.

| model | S24 GPU | S24 CPU | notes |
|---|---|---|---|
| dummy cnn_s 512 | **7.1 ms** | 40 ms | A35 GPU was 34 ms |
| MODNet 512 | **42 ms** | 241 ms | 551/551 GPU |
| U2-Net 320 | **99 ms** | 806 ms | 374/374 GPU |
| IS-Net 1024 | **193 ms** | 2373 ms | 247/247 GPU; A35 CPU was 6.8 s |

LiteRT `CompiledModel` numbers on the DIS card (~11 ms / Hexagon ~24 ms) are
a **different runtime** than the TFLite GPU delegate the app uses.

Pushed to both phones' `persistentDataPath`: `u2net_320_fp16.tflite`,
`modnet_512.tflite`. S24 already had IS-Net, MODNet 256/512, DA3 from earlier
pushes. None of those ship in the APK.

### Canny on the phone — built in, no `.tflite`

Same HUD cycle as the nets (`segmodel canny` / `segnext`). Java worker,
480×480 — the largest square the 640×480 camera image can feed without
`Convert` upscaling. Gaussian + Sobel + NMS + hysteresis 80/160, painted as
an alpha matte (edges 255, rest 0).

It is **not** a silhouette. Overlay yes; it will not fill a person the way
MODNet does.

Two faults before it was visible, both of the "looks like it works and shows
nothing" kind:

1. **The HUD button never printed the name.** It said `model 1/N 257`. Canny
   was slot 2 after coral the whole time. The button now shows
   `2/N canny 480`. `seglist` always listed `[2]canny`.
2. **Magnitude was divided by 8 before the 80/160 thresholds.** OpenCV's
   thresholds are on the unscaled L1 Sobel. The first APK ran Canny in 18 ms
   at 480² and reported `raw 0.0000 .. 0.0000`, 0 painted pixels — ready,
   fast, and empty. Confirmed on the S24 2026-08-29 by `segmodel canny` +
   `state.txt`. Fixed: full L1 in 16 bits, 3×3 dilate so a 1-wide ridge
   survives the 480→screen upsample, cyan overlay (Ramp(1) is dark red and
   invisible as a line), point filter on the mask.

Working on device 2026-08-30. HUD should read `alpha (canny) N px` and
`raw 0.0000 .. 1.0000`, not a zero range.
