"""Export TEED (Tiny and Efficient Edge Detector) to ONNX.

TEED: Xavier Soria et al., "Tiny and Efficient Model for the Edge Detection Generalization", ICCV-W 2023.
Code and BIPED checkpoint: https://github.com/xavysp/TEED (MIT License, Copyright (c) 2022 Xavier Soria Poma).
The network below restates TEED's ted.py so the checkpoint loads into it; only the raw weights are read
from the downloaded checkpoint (torch.load with weights_only=True, so no pickled code runs).

Usage: python -I export_teed.py <5_model.pth> <out.onnx>

The exported graph takes the image as BGR float 0..255 (N,3,H,W; H and W multiples of 8), subtracts the
BIPED mean itself, and returns two edge probability maps in 0..1:
  fused   - TEED's fused output (crisp; the usual TEED result)
  average - mean of the three side outputs and the fused map (softer; keeps faint detail)
"""
import os
import sys

import torch
import torch.nn as nn


def smish(x):
    return x * torch.tanh(torch.log(1 + torch.sigmoid(x)))


class Smish(nn.Module):
    def forward(self, x):
        return smish(x)


class DoubleFusion(nn.Module):
    def __init__(self, in_ch, out_ch):
        super().__init__()
        self.DWconv1 = nn.Conv2d(in_ch, in_ch * 8, kernel_size=3, stride=1, padding=1, groups=in_ch)
        self.PSconv1 = nn.PixelShuffle(1)
        self.DWconv2 = nn.Conv2d(24, 24, kernel_size=3, stride=1, padding=1, groups=24)
        self.AF = Smish()

    def forward(self, x):
        attn = self.PSconv1(self.DWconv1(self.AF(x)))
        attn2 = self.PSconv1(self.DWconv2(self.AF(attn)))
        return smish(((attn2 + attn).sum(1)).unsqueeze(1))


class _DenseLayer(nn.Sequential):
    def __init__(self, input_features, out_features):
        super().__init__()
        self.add_module("conv1", nn.Conv2d(input_features, out_features, kernel_size=3, stride=1, padding=2, bias=True))
        self.add_module("smish1", Smish())
        self.add_module("conv2", nn.Conv2d(out_features, out_features, kernel_size=3, stride=1, bias=True))

    def forward(self, x):
        x1, x2 = x
        new_features = super().forward(smish(x1))
        return 0.5 * (new_features + x2), x2


class _DenseBlock(nn.Sequential):
    def __init__(self, num_layers, input_features, out_features):
        super().__init__()
        for i in range(num_layers):
            self.add_module("denselayer%d" % (i + 1), _DenseLayer(input_features, out_features))
            input_features = out_features


class UpConvBlock(nn.Module):
    def __init__(self, in_features, up_scale):
        super().__init__()
        layers = []
        all_pads = [0, 0, 1, 3, 7]
        for i in range(up_scale):
            kernel_size = 2 ** up_scale
            pad = all_pads[up_scale]
            out_features = 1 if i == up_scale - 1 else 16
            layers.append(nn.Conv2d(in_features, out_features, 1))
            layers.append(Smish())
            layers.append(nn.ConvTranspose2d(out_features, out_features, kernel_size, stride=2, padding=pad))
            in_features = out_features
        self.features = nn.Sequential(*layers)

    def forward(self, x):
        return self.features(x)


class SingleConvBlock(nn.Module):
    def __init__(self, in_features, out_features, stride):
        super().__init__()
        self.conv = nn.Conv2d(in_features, out_features, 1, stride=stride, bias=True)

    def forward(self, x):
        return self.conv(x)


class DoubleConvBlock(nn.Module):
    def __init__(self, in_features, mid_features, out_features=None, stride=1, use_act=True):
        super().__init__()
        self.use_act = use_act
        out_features = out_features or mid_features
        self.conv1 = nn.Conv2d(in_features, mid_features, 3, padding=1, stride=stride)
        self.conv2 = nn.Conv2d(mid_features, out_features, 3, padding=1)
        self.smish = Smish()

    def forward(self, x):
        x = self.smish(self.conv1(x))
        x = self.conv2(x)
        return self.smish(x) if self.use_act else x


class TED(nn.Module):
    def __init__(self):
        super().__init__()
        self.block_1 = DoubleConvBlock(3, 16, 16, stride=2)
        self.block_2 = DoubleConvBlock(16, 32, use_act=False)
        self.dblock_3 = _DenseBlock(1, 32, 48)
        self.maxpool = nn.MaxPool2d(kernel_size=3, stride=2, padding=1)
        self.side_1 = SingleConvBlock(16, 32, 2)
        self.pre_dense_3 = SingleConvBlock(32, 48, 1)
        self.up_block_1 = UpConvBlock(16, 1)
        self.up_block_2 = UpConvBlock(32, 1)
        self.up_block_3 = UpConvBlock(48, 2)
        self.block_cat = DoubleFusion(3, 3)

    def forward(self, x):
        block_1 = self.block_1(x)
        block_1_side = self.side_1(block_1)
        block_2 = self.block_2(block_1)
        block_2_down = self.maxpool(block_2)
        block_2_add = block_2_down + block_1_side
        block_3_pre_dense = self.pre_dense_3(block_2_down)
        block_3, _ = self.dblock_3([block_2_add, block_3_pre_dense])
        results = [self.up_block_1(block_1), self.up_block_2(block_2), self.up_block_3(block_3)]
        results.append(self.block_cat(torch.cat(results, dim=1)))
        return results


class Exported(nn.Module):
    def __init__(self, net):
        super().__init__()
        self.net = net
        self.register_buffer("mean", torch.tensor([103.939, 116.779, 123.68]).view(1, 3, 1, 1))

    def forward(self, bgr):
        probs = [torch.sigmoid(o) for o in self.net(bgr - self.mean)]
        return probs[-1], torch.stack(probs, 0).mean(0)


def main():
    checkpoint, out = sys.argv[1], sys.argv[2]
    net = TED()
    net.load_state_dict(torch.load(checkpoint, map_location="cpu", weights_only=True))
    model = Exported(net).eval()
    dummy = torch.rand(1, 3, 256, 320) * 255
    axes = {2: "h", 3: "w"}
    with torch.no_grad():
        torch.onnx.export(model, (dummy,), out, input_names=["bgr"], output_names=["fused", "average"],
                          dynamic_axes={"bgr": axes, "fused": axes, "average": axes}, opset_version=17, dynamo=False)
    print("wrote", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
