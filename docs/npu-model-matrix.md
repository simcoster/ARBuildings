# NPU model matrix — Galaxy A35 (SM-A356E, Exynos 1380, ENN v1.7.1-2)

Measured 2026-08-26. Runtime is **TFLite `benchmark_model`** unless a row says ORT.
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

- Model: `StreamingAssets/deeplabv3_257_mv_gpu.tflite` (device copy in
  `persistentDataPath` wins, same as `buildings.json`).
- Runtime: `com.pavel.arbuildings.NpuSegmenter` (TFLite 2.16.1, `NnApiDelegate`,
  accelerator `enn`, `setUseNnapiCpu(false)`). Fail closed on REJECT.
- Fusion: `SemanticOcclusion` — connected components of PASCAL THING classes;
  a component occludes iff ≥ `segmin` pixels have valid ARCore depth under
  `segmax` metres. Floor/stuff never enter the mask.
- Tripod: `seg on|off`, `segmin N`, `segdebug on|off`, `segmax N`.
