"""Re-export MODNet at 256x256 TFLite — same architecture as occ_models/modnet_512.tflite.

The 512 file is frozen at [1,3,512,512]. MODNet is fully convolutional, so a 256
dummy is the same weights and 4x fewer MACs. File size stays ~26 MB.

Uses the same two GPU patches as litert-community/MODNet-LiteRT (SE Linear→1x1,
hierarchical-mean InstanceNorm). Conversion prefers litert_torch / ai_edge_torch
(Linux; the Windows wheel for litert-converter does not exist). onnx-tf is the
fallback on this machine.

    python export_modnet_256.py
"""

from __future__ import annotations

import os
import sys

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

ROOT = os.path.dirname(os.path.abspath(__file__))
MODNET = os.path.join(ROOT, "_modnet")
CKPT = os.path.join(MODNET, "pretrained", "modnet_photographic_portrait_matting.ckpt")
ONNX_PATH = os.path.join(ROOT, "occ_models", "modnet_256.onnx")
TFLITE_PATH = os.path.join(ROOT, "occ_models", "modnet_256.tflite")
SIZE = 256


def _hier_mean(t):
    """Exact global spatial mean via /2 avg-pools — fp16-safe on Mali."""
    while t.shape[-1] > 1 or t.shape[-2] > 1:
        kh = 2 if t.shape[-2] > 1 else 1
        kw = 2 if t.shape[-1] > 1 else 1
        t = F.avg_pool2d(t, (kh, kw), ceil_mode=True)
    return t


def patch_se(se):
    lin1, lin2 = se.fc[0], se.fc[2]
    ci, cm, co = lin1.in_features, lin1.out_features, lin2.out_features
    c1 = nn.Conv2d(ci, cm, 1, bias=False)
    c1.weight.data = lin1.weight.data.view(cm, ci, 1, 1)
    c2 = nn.Conv2d(cm, co, 1, bias=False)
    c2.weight.data = lin2.weight.data.view(co, cm, 1, 1)
    se._c1, se._c2 = c1, c2
    se.forward = lambda x: x * torch.sigmoid(se._c2(F.relu(se._c1(se.pool(x)))))


def patch_ibnorm(ib, eps=1e-5):
    bc = ib.bnorm_channels

    def fwd(x):
        bn_x = ib.bnorm(x[:, :bc].contiguous())
        ix = x[:, bc:].contiguous()
        mean = _hier_mean(ix)
        dd = ix - mean
        in_x = dd * torch.rsqrt(_hier_mean(dd * dd) + eps)
        return torch.cat((bn_x, in_x), 1)

    ib.forward = fwd


class Wrap(nn.Module):
    def __init__(self, n):
        super().__init__()
        self.n = n

    def forward(self, x):
        return self.n(x, True)[2]


def load_modnet():
    sys.path.insert(0, MODNET)
    try:
        from src.models.modnet import IBNorm, MODNet, SEBlock

        net = MODNet(backbone_pretrained=False).eval()
        state = torch.load(CKPT, map_location="cpu")
        if isinstance(state, dict) and "state_dict" in state:
            state = state["state_dict"]
        state = {k.replace("module.", ""): v for k, v in state.items()}
        net.load_state_dict(state)
        n_se = n_ib = 0
        for mod in net.modules():
            if isinstance(mod, SEBlock):
                patch_se(mod)
                n_se += 1
            if isinstance(mod, IBNorm):
                patch_ibnorm(mod)
                n_ib += 1
        print(f"patched {n_se} SE blocks, {n_ib} IBNorms", flush=True)
        return Wrap(net).eval()
    finally:
        if sys.path and sys.path[0] == MODNET:
            sys.path.pop(0)


def convert_litert(net) -> bool:
    dummy = (torch.randn(1, 3, SIZE, SIZE),)
    try:
        import litert_torch

        print("converting with litert_torch...", flush=True)
        litert_torch.convert(net, dummy).export(TFLITE_PATH)
        return True
    except Exception as e:
        print(f"litert_torch unavailable: {type(e).__name__}: {e}", flush=True)
    try:
        import ai_edge_torch

        print("converting with ai_edge_torch...", flush=True)
        edge = ai_edge_torch.convert(net, dummy)
        edge.export(TFLITE_PATH)
        return True
    except Exception as e:
        print(f"ai_edge_torch unavailable: {type(e).__name__}: {e}", flush=True)
    return False


def export_onnx(net) -> None:
    os.makedirs(os.path.dirname(ONNX_PATH), exist_ok=True)
    dummy = torch.randn(1, 3, SIZE, SIZE)
    with torch.inference_mode():
        torch.onnx.export(
            net,
            dummy,
            ONNX_PATH,
            export_params=True,
            opset_version=13,
            do_constant_folding=True,
            input_names=["input"],
            output_names=["output"],
            dynamo=False,
        )
    print(f"onnx {os.path.getsize(ONNX_PATH) / 1e6:.1f} MB {ONNX_PATH}", flush=True)


def convert_onnx_tf() -> None:
    import onnx
    from onnx_tf.backend import prepare
    import tensorflow as tf

    saved = os.path.join(ROOT, "occ_models", "_modnet_256_saved")
    print("onnx-tf prepare...", flush=True)
    model = onnx.load(ONNX_PATH)
    tf_rep = prepare(model)
    tf_rep.export_graph(saved)
    print("TFLiteConverter...", flush=True)
    converter = tf.lite.TFLiteConverter.from_saved_model(saved)
    converter.target_spec.supported_ops = [
        tf.lite.OpsSet.TFLITE_BUILTINS,
        tf.lite.OpsSet.SELECT_TF_OPS,
    ]
    converter.optimizations = []
    buf = converter.convert()
    with open(TFLITE_PATH, "wb") as f:
        f.write(buf)
    print(f"tflite {os.path.getsize(TFLITE_PATH) / 1e6:.1f} MB {TFLITE_PATH}", flush=True)


def inspect() -> None:
    try:
        from ai_edge_litert.interpreter import Interpreter
    except ImportError:
        from tensorflow.lite.python.interpreter import Interpreter

    it = Interpreter(model_path=TFLITE_PATH)
    it.allocate_tensors()
    i, o = it.get_input_details()[0], it.get_output_details()[0]
    print(f"in  {list(i['shape'])} {np.dtype(i['dtype']).name}")
    print(f"out {list(o['shape'])} {np.dtype(o['dtype']).name}")
    x = np.zeros(i["shape"], dtype=i["dtype"])
    it.set_tensor(i["index"], x)
    it.invoke()
    y = it.get_tensor(o["index"])
    print(f"dry-run out range {float(y.min()):.4f} .. {float(y.max()):.4f}")


def main() -> int:
    if not os.path.exists(CKPT):
        raise SystemExit(f"missing checkpoint {CKPT}")
    print("loading MODNet...", flush=True)
    net = load_modnet()
    if convert_litert(net):
        inspect()
        return 0
    if not os.path.exists(ONNX_PATH):
        print("exporting ONNX 256x256...", flush=True)
        export_onnx(net)
    else:
        print(f"reusing {ONNX_PATH}", flush=True)
    print("converting via onnx-tf...", flush=True)
    convert_onnx_tf()
    inspect()
    return 0


if __name__ == "__main__":
    sys.exit(main())
