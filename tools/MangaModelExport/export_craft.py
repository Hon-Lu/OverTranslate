"""CRAFT (clovaai/CRAFT-pytorch, MIT) to ONNX, then fp16 with float32 inputs and outputs.
  <out>/craft.onnx       image[1,3,H,W] f32 -> score[1,H/2,W/2,2] f32 (region, affinity)
  <out>/craft.fp16.onnx  the same, fp16 inside: the file the manifest calls craft.fp16.onnx

usage: python export_craft.py <craft_mlt_25k.pth> <out>

The weights are craft_mlt_25k.pth as EasyOCR's release carries it (the same file CRAFT-pytorch links
to on Google Drive). The network is re-declared here from CRAFT-pytorch's craft.py and
basenet/vgg16_bn.py so that nothing of that repository is needed; the state dict loads into it
with no key missing or left over.

Re-run with torch 2.14.1+cpu, onnx 1.23.2 and onnxconverter-common 1.16.0 the output is the same file
every time, the craft.fp16.onnx the manifest names to the bit.
"""
import os, sys
from collections import OrderedDict
import torch, torch.nn as nn, torch.nn.functional as F
from torchvision import models


class vgg16_bn(nn.Module):
    def __init__(self):
        super().__init__()
        f = models.vgg16_bn(weights=None).features
        self.slice1, self.slice2, self.slice3, self.slice4 = (nn.Sequential() for _ in range(4))
        for x in range(12): self.slice1.add_module(str(x), f[x])
        for x in range(12, 19): self.slice2.add_module(str(x), f[x])
        for x in range(19, 29): self.slice3.add_module(str(x), f[x])
        for x in range(29, 39): self.slice4.add_module(str(x), f[x])
        self.slice5 = nn.Sequential(nn.MaxPool2d(3, 1, 1), nn.Conv2d(512, 1024, 3, padding=6, dilation=6), nn.Conv2d(1024, 1024, 1))

    def forward(self, x):
        h = self.slice1(x); r2 = h
        h = self.slice2(h); r3 = h
        h = self.slice3(h); r4 = h
        h = self.slice4(h); r5 = h
        h = self.slice5(h)
        return h, r5, r4, r3, r2


class double_conv(nn.Module):
    def __init__(self, i, m, o):
        super().__init__()
        self.conv = nn.Sequential(nn.Conv2d(i + m, m, 1), nn.BatchNorm2d(m), nn.ReLU(True),
                                  nn.Conv2d(m, o, 3, padding=1), nn.BatchNorm2d(o), nn.ReLU(True))

    def forward(self, x): return self.conv(x)


class CRAFT(nn.Module):
    def __init__(self):
        super().__init__()
        self.basenet = vgg16_bn()
        self.upconv1 = double_conv(1024, 512, 256)
        self.upconv2 = double_conv(512, 256, 128)
        self.upconv3 = double_conv(256, 128, 64)
        self.upconv4 = double_conv(128, 64, 32)
        self.conv_cls = nn.Sequential(nn.Conv2d(32, 32, 3, padding=1), nn.ReLU(True), nn.Conv2d(32, 32, 3, padding=1), nn.ReLU(True),
                                      nn.Conv2d(32, 16, 3, padding=1), nn.ReLU(True), nn.Conv2d(16, 16, 1), nn.ReLU(True), nn.Conv2d(16, 2, 1))

    def forward(self, x):
        s = self.basenet(x)
        y = torch.cat([s[0], s[1]], 1); y = self.upconv1(y)
        y = F.interpolate(y, size=s[2].shape[2:], mode='bilinear', align_corners=False)
        y = torch.cat([y, s[2]], 1); y = self.upconv2(y)
        y = F.interpolate(y, size=s[3].shape[2:], mode='bilinear', align_corners=False)
        y = torch.cat([y, s[3]], 1); y = self.upconv3(y)
        y = F.interpolate(y, size=s[4].shape[2:], mode='bilinear', align_corners=False)
        y = torch.cat([y, s[4]], 1); feature = self.upconv4(y)
        return self.conv_cls(feature).permute(0, 2, 3, 1)


if __name__ == '__main__':
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    sd = torch.load(src, map_location='cpu')
    sd = OrderedDict((k[7:] if k.startswith('module.') else k, v) for k, v in sd.items())
    m = CRAFT()
    print(m.load_state_dict(sd))
    m.eval()
    x = torch.randn(1, 3, 640, 640)
    fp32 = os.path.join(out, 'craft.onnx')
    torch.onnx.export(m, x, fp32, input_names=['image'], output_names=['score'], opset_version=17, dynamo=False,
                      dynamic_axes={'image': {2: 'h', 3: 'w'}, 'score': {1: 'h2', 2: 'w2'}})

    import onnx
    from onnxconverter_common import float16
    onnx.save(float16.convert_float_to_float16(onnx.load(fp32), keep_io_types=True), os.path.join(out, 'craft.fp16.onnx'))
    print('ok')
