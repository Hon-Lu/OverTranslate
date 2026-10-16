"""CPT-flex (muscgab/manga-ocr-base-CPT-flex) encoder, fp16 straight from PyTorch like export_fp16.py.
  <out>/encoder.onnx   patches[B,196,768] f32, patch_mask[B,196] f32, position_weights[B,196,196] f32
                       -> last_hidden_state[B,197,768] f32 (fp16 inside)

  python export_flex.py <out> <model>     model: what cpt_to_hf.py writes for the CPT-flex weights

CPT-flex keeps the crop's aspect ratio: it is resized into a grid of at most 196 patches of 16 px, and
the grid's patches come first, the rest of the 196 are padding. The graph is fixed at 196 patches (197
with the class token) whatever the grid; the caller says which patches are real (patch_mask, 1 or 0)
and where they sit (position_weights). The learned position table is 14 x 14; CPT-flex resizes it to
the grid bicubically (torch's interpolate, align_corners=False), which is a fixed linear map of the
table, so row i of position_weights holds the 196 weights that make patch i's position from the table
(zeros on padding). The table stays fp32 inside, as the author computes it.
Padding is masked out of the encoder's self-attention here and of the decoder's cross-attention by
`MASK=1 export_kv.py`.
"""
import math, os, sys
import numpy as np
import torch
import torch.nn.functional as F
from transformers import VisionEncoderDecoderModel

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join('out', 'flex-fp16')
MODEL = sys.argv[2] if len(sys.argv) > 2 else os.path.join('out', 'cptflex-hf')
os.makedirs(OUT, exist_ok=True)
N, D, H = 196, 768, 12


def cubic_weights(out_size, in_size=14, a=-0.75):
    """torch's bicubic resize along one axis (align_corners=False, edges clamped) as a matrix [out, in]."""
    w = np.zeros((out_size, in_size), np.float64)
    scale = in_size / out_size
    for i in range(out_size):
        x = scale * (i + 0.5) - 0.5
        x0 = math.floor(x); t = x - x0
        ks = [((a * (t + 1) - 5 * a) * (t + 1) + 8 * a) * (t + 1) - 4 * a,
              ((a + 2) * t - (a + 3)) * t * t + 1,
              ((a + 2) * (1 - t) - (a + 3)) * (1 - t) * (1 - t) + 1,
              ((a * (2 - t) - 5 * a) * (2 - t) + 8 * a) * (2 - t) - 4 * a]
        for k in range(4):
            w[i, min(max(x0 - 1 + k, 0), in_size - 1)] += ks[k]
    return w


def position_weights(gh, gw):
    """[196, 196]: row r = patch (r // gw, r % gw) of a gh x gw grid, over the 14 x 14 table; zero rows on padding."""
    m = np.zeros((N, N), np.float32)
    if (gh, gw) == (14, 14):
        m[np.arange(N), np.arange(N)] = 1
        return m
    m[:gh * gw] = np.einsum('ia,jb->ijab', cubic_weights(gh), cubic_weights(gw)).reshape(gh * gw, N)
    return m


class Enc(torch.nn.Module):
    def __init__(self, vit, half):
        super().__init__()
        self.vit, self.half_ = vit, half
        e = vit.embeddings
        # The patch projection is a 16 x 16 stride-16 convolution over (channel, row, column): a linear
        # map of the patch vector in that order.
        self.proj_w = e.patch_embeddings.projection.weight.reshape(D, -1)
        self.proj_b = e.patch_embeddings.projection.bias
        self.cls = e.cls_token[0, 0] + e.position_embeddings[0, 0]
        self.table = e.position_embeddings[0, 1:].float().clone()
        if half: self.vit.half(); self.proj_w = self.proj_w.half(); self.proj_b = self.proj_b.half(); self.cls = self.cls.half()

    def forward(self, patches, patch_mask, position_weights):
        dt = torch.float16 if self.half_ else torch.float32
        B = patches.shape[0]
        x = F.linear(patches.to(dt), self.proj_w, self.proj_b) + (position_weights @ self.table).to(dt)
        h = torch.cat([self.cls.expand(B, 1, D), x], 1)
        bias = torch.cat([torch.zeros_like(patch_mask[:, :1]), (patch_mask - 1) * 10000], 1).to(dt)[:, None, None, :]
        for lay in self.vit.encoder.layer:
            sa = lay.attention.attention
            y = lay.layernorm_before(h)
            q, k, v = (t(y).view(B, -1, H, D // H).transpose(1, 2) for t in (sa.query, sa.key, sa.value))
            w = torch.softmax(q @ k.transpose(-1, -2) / math.sqrt(D // H) + bias, -1)
            h = h + lay.attention.output.dense((w @ v).transpose(1, 2).reshape(B, -1, D))
            h = h + lay.output.dense(F.gelu(lay.intermediate.dense(lay.layernorm_after(h))))
        return self.vit.layernorm(h).float()


def load():
    vit = VisionEncoderDecoderModel.from_pretrained(MODEL).encoder.eval()
    for p in vit.parameters(): p.requires_grad_(False)
    return vit


def sample(B=2):
    """Random patches on two grids, one full (14 x 14) and one with padding."""
    g = torch.Generator().manual_seed(0)
    grids = [(14, 14), (7, 26)][:B]
    P = torch.zeros(B, N, D); V = torch.zeros(B, N); M = torch.zeros(B, N, N)
    for b, (gh, gw) in enumerate(grids):
        n = gh * gw
        P[b, :n] = torch.rand(n, D, generator=g) * 2 - 1; V[b, :n] = 1
        M[b] = torch.from_numpy(position_weights(gh, gw))
    return P, V, M, grids


with torch.no_grad():
    vit = load()
    P, V, M, grids = sample()
    # The position map against torch's own bicubic resize of the table.
    table = vit.embeddings.position_embeddings[0, 1:].reshape(14, 14, D).permute(2, 0, 1)[None]
    gh, gw = grids[1]
    ref = F.interpolate(table, size=(gh, gw), mode='bicubic', align_corners=False)[0].flatten(1).T
    print('position map vs F.interpolate max diff', (M[1, :gh * gw] @ vit.embeddings.position_embeddings[0, 1:] - ref).abs().max().item())
    ref = Enc(vit, False)(P, V, M)
    enc16 = Enc(load(), True)
    out = enc16(P, V, M)
    print('encoder fp16 vs fp32 max diff', (out - ref).abs().max().item())
    # A padded patch must not change the real ones.
    P2 = P.clone(); P2[1, grids[1][0] * grids[1][1]:] = 5
    print('padding leak (fp32)', (Enc(vit, False)(P2, V, M) - ref)[1, :1 + grids[1][0] * grids[1][1]].abs().max().item())
    torch.onnx.export(enc16, (P, V, M), f'{OUT}/encoder.onnx', dynamo=False, opset_version=17,
                      input_names=['patches', 'patch_mask', 'position_weights'], output_names=['last_hidden_state'],
                      dynamic_axes={'patches': {0: 'B'}, 'patch_mask': {0: 'B'}, 'position_weights': {0: 'B'},
                                    'last_hidden_state': {0: 'B'}})
print('encoder done')
