# Webcam desk — toy segmenters + stock BiRefNet + LiteRT

Keys **1 / 4 / 5 / n** run the fine-tuned PyTorch checkpoints in `FineTuneSingleObject/runs/`
on CUDA. **`b`** is stock ZhengPeng7 BiRefNet (local `pretrained/birefnet/`, 1024² letterbox).
`--cpu` forces PyTorch CPU.

The original LiteRT GPU models stay on letter keys. `--cpu` is XNNPACK for those.

```
cd tools/webcam_desk
python webcam.py
python webcam.py --cpu
```

Starts on stock DIS-ISNet (key `i`, LiteRT GPU). Load takes a few seconds.

| key | |
|---|---|
| `1` | MobileNetV4 (toy `.pt`) |
| `4` | PIDNet-S (toy `.pt`) |
| `5` | cnn_s (toy `.pt`) |
| `n` | IS-Net (toy `.pt`) |
| `b` | BiRefNet (stock DIS weights) |
| `g` | toggle guided filter on toy mattes (default on) |
| `o` | toggle mask-only (no camera, white = foreground) |
| `6` | Canny edges |
| `7` | 30 / 15 / 10 / 5 fps motion demo |
| `i` | DIS / IS-Net 1024 (LiteRT) |
| `d` | Depth Anything 3 Small (LiteRT) |
| `u` | U2-Net 320 centred patch (LiteRT) |
| `m` | MODNet 512 centred patch (LiteRT) |
| `q` / Esc | quit |

The HUD shows last inference, a rolling p50, and the raw output range.
Inference runs on a worker thread so the camera keeps moving while a net thinks.

## Phone overlay (`seg_server.py`)

IS-Net toy checkpoint over HTTP for the app's **seg-wifi** button. JPEG in, PNG mask out.

```
python seg_server.py
python seg_server.py --port 8787 --cpu
```

Then on the phone (same Wi-Fi, not USB):

```
printf 'wifi 192.168.x.x:8787\nwifi on\n' > cmd.txt
adb push cmd.txt /sdcard/Android/data/com.pavel.arbuildings/files/command.txt
```

Allow inbound TCP 8787 on the PC firewall. `GET /health` checks the process is up.
