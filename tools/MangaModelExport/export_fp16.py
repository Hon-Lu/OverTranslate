"""fp16 exports straight from PyTorch (weights fp16, graph I/O fp32 via casts inside the graph).
  <out>/encoder.onnx   pixel_values[B,3,224,224] f32 -> last_hidden_state[B,197,768] f32 (fp16 inside)
The fp16 decoder graphs come from `HALF=1 python export_kv.py`.
"""
import os, sys
import torch
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join('out', 'mo-fp16')
os.makedirs(OUT, exist_ok=True)
from transformers import VisionEncoderDecoderModel
m = VisionEncoderDecoderModel.from_pretrained('kha-white/manga-ocr-base').eval()
for p in m.parameters(): p.requires_grad_(False)


class Enc(torch.nn.Module):
    def __init__(self, e): super().__init__(); self.e = e.half()
    def forward(self, x): return self.e(pixel_values=x.half()).last_hidden_state.float()


with torch.no_grad():
    ref = m.encoder(pixel_values=torch.randn(2, 3, 224, 224)).last_hidden_state
x = torch.randn(2, 3, 224, 224)
with torch.no_grad():
    ref = m.encoder(pixel_values=x).last_hidden_state
    enc16 = Enc(VisionEncoderDecoderModel.from_pretrained('kha-white/manga-ocr-base').encoder.eval())
    for p in enc16.parameters(): p.requires_grad_(False)
    print('encoder fp16 vs fp32 max diff', (enc16(x) - ref).abs().max().item())
    torch.onnx.export(enc16, (x,), f'{OUT}/encoder.onnx', dynamo=False, opset_version=17, input_names=['pixel_values'],
                      output_names=['last_hidden_state'], dynamic_axes={'pixel_values': {0: 'B'}, 'last_hidden_state': {0: 'B'}})
print('encoder done')
