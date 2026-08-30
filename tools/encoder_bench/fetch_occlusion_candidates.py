#!/usr/bin/env python
"""Download the matting / semantic / depth candidates for the occlusion comparison.

Everything here already ships as a .tflite, so nothing is converted: push a file to the
device and SemanticOcclusion.Catalogue() picks it up, no rebuild. Run with --inspect to
print each model's input and output signature, which is what decides whether the Java
wrapper can drive it at all (it handles ONE input and ONE output) and what normalisation
it needs.

    python fetch_occlusion_candidates.py --inspect
"""

from __future__ import annotations

import argparse
import os
import ssl
import sys
import urllib.request

CERT = os.path.expandvars(r"%USERPROFILE%\.certs\ca-bundle-with-norton.pem")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "occ_models")

HF = "https://huggingface.co/{repo}/resolve/main/{path}"


def hf(repo: str, path: str) -> str:
    return HF.format(repo=repo, path=path)


# (local name, url, notes). Local names are what `segmodel` will be given.
FILES = [
    # --- matting / background removal: a soft alpha, not a class ---
    (
        "modnet_512.tflite",
        hf("litert-community/MODNet-LiteRT", "modnet.tflite"),
        "MODNet portrait matting. Exactly the model asked for, already converted.",
    ),
    (
        "u2net_320_fp16.tflite",
        hf("litert-community/U-2-Net", "u2net_fp16.tflite"),
        "U^2-Net salient-object matting - rembg's default, class-agnostic foreground.",
    ),
    (
        "dis_isnet_1024.tflite",
        hf("litert-community/DIS-ISNet-LiteRT", "dis.tflite"),
        "IS-Net / DIS. The dichotomous-segmentation line RMBG-2.0 descends from, and the "
        "closest thing to it that is neither gated nor 0.2B parameters.",
    ),
    # --- semantic: a class per pixel, road-scene taxonomy ---
    (
        "pidnet_s_cityscapes.tflite",
        hf("litert-community/PIDNet-S-Cityscapes-LiteRT", "pidnet_s.tflite"),
        "PIDNet-S, Cityscapes 19 classes. Road-scene taxonomy, so cars and poles exist "
        "as classes - which PASCAL's 21 could not say for this scene.",
    ),
    # --- monocular relative depth: ordering, not metres ---
    (
        "midas_v21_small_256.tflite",
        "https://github.com/isl-org/MiDaS/releases/download/v2_1/model_opt.tflite",
        "MiDaS v2.1 small, the Android sample's own float32 artifact.",
    ),
    (
        "midas_small_256_fp16.tflite",
        hf("litert-community/MiDaS-small", "midas_small_256_fp16.tflite"),
        "Same net, fp16 weights - half the file for the same graph.",
    ),
    (
        "depth_anything_3_small_fp16.tflite",
        hf("litert-community/Depth-Anything-3-Small", "da3_small_gpu_fp16.tflite"),
        "Depth Anything 3 Small. Newer than the V2 asked for and the only one of the "
        "family with a ready TFLite build.",
    ),
]

# Asked for, deliberately not here. Kept in the file so the next person does not go
# looking: the reason is the point, not the absence.
UNAVAILABLE = [
    (
        "RobustVideoMatting",
        "Recurrent: 5 inputs (frame + 4 hidden states) and 6 outputs. No TFLite build "
        "exists, and the Java wrapper drives exactly one input and one output. Feeding "
        "zero states every frame would fit, at the cost of the temporal stability that is "
        "the entire reason to prefer RVM over MODNet.",
    ),
    (
        "RMBG-2.0",
        "Gated behind a licence form, CC BY-NC, BiRefNet at 0.2B parameters and 1024x1024 "
        "- roughly 900 MB of float weights. Not a mobile model. dis_isnet_1024 above is "
        "the same task and the architecture it grew out of.",
    ),
    (
        "FastDepth",
        "PyTorch-0.4-era pickle on datasets.lids.mit.edu, no TFLite anywhere, and trained "
        "on NYU Depth v2 - indoor rooms at 224x224. A facade 28 m away is outside "
        "everything it has ever seen.",
    ),
]


def context() -> ssl.SSLContext:
    """This machine sits behind a TLS-inspecting proxy; the bundle is how the sibling
    fetch script gets out, so reuse it and fall back to system trust."""
    if os.path.exists(CERT):
        return ssl.create_default_context(cafile=CERT)
    return ssl.create_default_context()


def fetch(name: str, url: str, notes: str, ctx: ssl.SSLContext, force: bool) -> bool:
    path = os.path.join(OUT, name)
    if os.path.exists(path) and not force:
        print(f"[skip] {name} ({os.path.getsize(path) / 1e6:.1f} MB)")
        return True

    print(f"[get ] {name}\n       {notes}")
    req = urllib.request.Request(url, headers={"User-Agent": "curl/8"})
    try:
        with urllib.request.urlopen(req, context=ctx, timeout=600) as r:
            data = r.read()
    except Exception as e:  # noqa: BLE001 - the reason matters more than the type
        print(f"       FAILED {type(e).__name__}: {e}")
        return False

    # A proxy error page is still a 200 with bytes in it. TFLite flatbuffers carry "TFL3"
    # at offset 4, so this is the cheap way to notice HTML.
    if data[4:8] != b"TFL3":
        print(f"       FAILED not a tflite flatbuffer (first bytes {data[:16]!r})")
        return False

    with open(path, "wb") as f:
        f.write(data)
    print(f"       ok {len(data) / 1e6:.1f} MB")
    return True


def inspect() -> None:
    """Input and output signatures, read on the desktop rather than discovered on the
    phone. Three separate faults on 2026-08-26 were all signature mismatches that each
    produced a believable wrong answer, so this is the cheap way to pre-empt them."""
    import numpy as np  # noqa: PLC0415 - only needed for --inspect
    import tensorflow as tf  # noqa: PLC0415

    print(f"\n{'model':<36} {'in':>20} {'in dtype':>10} {'out':>20} {'out dtype':>10}")
    print("-" * 102)
    for name, _, _ in FILES:
        path = os.path.join(OUT, name)
        if not os.path.exists(path):
            continue
        try:
            it = tf.lite.Interpreter(model_path=path)
            it.allocate_tensors()
            ins, outs = it.get_input_details(), it.get_output_details()
            i, o = ins[0], outs[0]
            extra = ""
            if len(ins) > 1 or len(outs) > 1:
                extra = f"   <-- {len(ins)} in / {len(outs)} out, wrapper drives 1/1"
            print(f"{name:<36} {str(list(i['shape'])):>20} {np.dtype(i['dtype']).name:>10} "
                  f"{str(list(o['shape'])):>20} {np.dtype(o['dtype']).name:>10}{extra}")
        except Exception as e:  # noqa: BLE001
            print(f"{name:<36} FAILED {type(e).__name__}: {e}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--inspect", action="store_true")
    args = ap.parse_args()

    os.makedirs(OUT, exist_ok=True)
    ctx = context()
    ok = sum(fetch(n, u, d, ctx, args.force) for n, u, d in FILES)
    print(f"\n{ok}/{len(FILES)} available in {OUT}")

    print("\nasked for, not obtainable:")
    for name, why in UNAVAILABLE:
        print(f"  {name}: {why}")

    if args.inspect:
        inspect()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
