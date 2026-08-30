# Webcam desk — DIS-ISNet vs Depth Anything 3

LiteRT **GPU** (WebGPU / D3D12 on the discrete NVIDIA). `--cpu` is XNNPACK for an A/B.

The first GPU start copies `dxil.dll` / `dxcompiler.dll` from Edge WebView next to LiteRT if they are missing — without them Windows fails to create the device even after it has selected the RTX.

```
cd tools/webcam_desk
python webcam.py
python webcam.py --cpu
```

| key | |
|---|---|
| `1` | DIS / IS-Net 1024 (salient-object matte, full frame) |
| `2` | Depth Anything 3 Small (relative depth, full frame) |
| `3` | U2-Net 320 (salient object, centred 320×320 patch) |
| `4` | MODNet 512 (portrait matte, centred 512×512 patch) |
| `5` | 30 / 15 / 10 / 5 fps motion demo |
| `6` | Canny edges (OpenCV, full-frame CPU, no network) |
| `q` / Esc | quit |

The HUD shows last inference, a rolling p50, and the raw output range.
Inference runs on a worker thread so the camera keeps moving while a 1024² net thinks.
