"""Export manga-ocr's 2-layer BERT decoder as two ONNX graphs with an explicit KV cache.
  decoder_init.onnx : input_ids[B,1], encoder_hidden_states[B,S,768]
                      -> logits[B,V], self_k[L,B,H,1,D], self_v, cross_k[L,B,H,S,D], cross_v
  decoder_step.onnx : input_ids[B,1], position_ids[B,1], past_k[L,B,H,T,D], past_v, cross_k, cross_v
                      -> logits[B,V], present_k[L,B,H,T+1,D], present_v
Batch is lock-step (all rows have the same length), so no self-attention mask is needed.
MASK=1 (CPT-flex, whose encoder output has padding; see export_flex.py): both graphs also take
memory_mask[B,S] f32, 1 on the encoder positions to attend to and 0 on padding, for the cross-attention."""
import math, os, sys
import numpy as np
import torch
from transformers import VisionEncoderDecoderModel

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join('out', 'mo-kv-fp16' if os.environ.get('HALF') == '1' else 'mo-kv')
# A Hugging Face id or a local folder, e.g. what cpt_to_hf.py writes.
MODEL = sys.argv[2] if len(sys.argv) > 2 else 'kha-white/manga-ocr-base'
m = VisionEncoderDecoderModel.from_pretrained(MODEL).eval()
dec = m.decoder
for p in m.parameters(): p.requires_grad_(False)
HALF = os.environ.get('HALF') == '1'
if HALF: m.half()
bert, head = dec.bert, dec.cls
L = len(bert.encoder.layer); H = dec.config.num_attention_heads; D = dec.config.hidden_size // H
gelu = torch.nn.functional.gelu


def heads(x):  # [B,T,768] -> [B,H,T,D]
    B, T, _ = x.shape
    return x.view(B, T, H, D).transpose(1, 2)


MASK = os.environ.get('MASK') == '1'


def attend(q, k, v, bias=None):
    s = q @ k.transpose(-1, -2) / math.sqrt(D)
    w = torch.softmax(s if bias is None else s + bias, -1)
    o = w @ v  # [B,H,T,D]
    B, _, T, _ = o.shape
    return o.transpose(1, 2).reshape(B, T, H * D)


def embed(ids, pos):
    e = bert.embeddings
    x = e.word_embeddings(ids) + e.position_embeddings(pos) + e.token_type_embeddings(torch.zeros_like(ids))
    return e.LayerNorm(x)


def cross_bias(memory_mask, dtype):  # [B,S] 1/0 -> [B,1,1,S] 0/-10000
    return ((memory_mask - 1) * 10000).to(dtype)[:, None, None, :]


def layer_step(lay, x, pk, pv, ck, cv, xb=None):
    sa = lay.attention.self
    q = heads(sa.query(x)); k = torch.cat([pk, heads(sa.key(x))], 2); v = torch.cat([pv, heads(sa.value(x))], 2)
    x = lay.attention.output.LayerNorm(lay.attention.output.dense(attend(q, k, v)) + x)
    ca = lay.crossattention.self
    x = lay.crossattention.output.LayerNorm(lay.crossattention.output.dense(attend(heads(ca.query(x)), ck, cv, xb)) + x)
    h = lay.output.LayerNorm(lay.output.dense(gelu(lay.intermediate.dense(x))) + x)
    return h, k, v


def lm(x):
    t = head.predictions.transform
    return head.predictions.decoder(t.LayerNorm(gelu(t.dense(x))))[:, -1]


class Init(torch.nn.Module):
    def forward(self, input_ids, enc, memory_mask=None):
        if HALF: enc = enc.half()
        xb = None if memory_mask is None else cross_bias(memory_mask, enc.dtype)
        B = input_ids.shape[0]
        x = embed(input_ids, torch.zeros_like(input_ids))
        ks, vs, cks, cvs = [], [], [], []
        empty = torch.zeros(B, H, 0, D, dtype=enc.dtype)
        for lay in bert.encoder.layer:
            ca = lay.crossattention.self
            ck, cv = heads(ca.key(enc)), heads(ca.value(enc))
            x, k, v = layer_step(lay, x, empty, empty, ck, cv, xb)
            ks.append(k); vs.append(v); cks.append(ck); cvs.append(cv)
        r = lm(x), torch.stack(ks), torch.stack(vs), torch.stack(cks), torch.stack(cvs)
        return tuple(t.float() for t in r) if HALF else r


class Step(torch.nn.Module):
    def forward(self, input_ids, position_ids, past_k, past_v, cross_k, cross_v, memory_mask=None):
        if HALF: past_k, past_v, cross_k, cross_v = past_k.half(), past_v.half(), cross_k.half(), cross_v.half()
        xb = None if memory_mask is None else cross_bias(memory_mask, cross_k.dtype)
        x = embed(input_ids, position_ids)
        ks, vs = [], []
        for i, lay in enumerate(bert.encoder.layer):
            x, k, v = layer_step(lay, x, past_k[i], past_v[i], cross_k[i], cross_v[i], xb)
            ks.append(k); vs.append(v)
        if HALF: return lm(x).float(), torch.stack(ks).float(), torch.stack(vs).float()
        return lm(x), torch.stack(ks), torch.stack(vs)


import os
os.makedirs(OUT, exist_ok=True)
B, S = 2, 197
ids = torch.full((B, 1), 2, dtype=torch.long); enc = torch.randn(B, S, 768)
# MASK=1: the second row's memory ends in padding, as a CPT-flex grid of 7 x 26 = 182 patches does.
mm = torch.ones(B, S); mm[1, 183:] = 0
mi, mo = (['memory_mask'], (mm,)) if MASK else ([], ())
mx = {'memory_mask': {0: 'B', 1: 'S'}} if MASK else {}
with torch.no_grad():
    torch.onnx.export(Init(), (ids, enc) + mo, f'{OUT}/decoder_init.onnx', dynamo=False, opset_version=17,
                      input_names=['input_ids', 'encoder_hidden_states'] + mi, output_names=['logits', 'self_k', 'self_v', 'cross_k', 'cross_v'],
                      dynamic_axes={'input_ids': {0: 'B'}, 'encoder_hidden_states': {0: 'B', 1: 'S'}, 'logits': {0: 'B'},
                                    'self_k': {1: 'B'}, 'self_v': {1: 'B'}, 'cross_k': {1: 'B', 3: 'S'}, 'cross_v': {1: 'B', 3: 'S'}} | mx)
    lg, k, v, ck, cv = Init()(ids, enc, *mo)
    torch.onnx.export(Step(), (ids, torch.ones_like(ids), k, v, ck, cv) + mo, f'{OUT}/decoder_step.onnx', dynamo=False, opset_version=17,
                      input_names=['input_ids', 'position_ids', 'past_k', 'past_v', 'cross_k', 'cross_v'] + mi,
                      output_names=['logits', 'present_k', 'present_v'],
                      dynamic_axes={'input_ids': {0: 'B'}, 'position_ids': {0: 'B'}, 'past_k': {1: 'B', 3: 'T'}, 'past_v': {1: 'B', 3: 'T'},
                                    'cross_k': {1: 'B', 3: 'S'}, 'cross_v': {1: 'B', 3: 'S'}, 'logits': {0: 'B'},
                                    'present_k': {1: 'B', 3: 'T1'}, 'present_v': {1: 'B', 3: 'T1'}} | mx)

    # parity vs the HF decoder (no cache): greedy 8 steps on random encoder states
    seq = ids.clone(); lg, k, v, ck, cv = Init()(ids, enc, *mo); worst = 0
    for t in range(0 if HALF else 8):
        ref = dec(input_ids=seq, encoder_hidden_states=enc, encoder_attention_mask=mm if MASK else None, use_cache=False).logits[:, -1]
        worst = max(worst, (ref - lg).abs().max().item())
        nxt = lg.argmax(-1, keepdim=True); seq = torch.cat([seq, nxt], 1)
        lg, k, v = Step()(nxt, torch.full_like(nxt, seq.shape[1] - 1), k, v, ck, cv, *mo)
    print('max |logit diff| vs HF over 8 steps:', worst)
print('exported to', OUT)


class Cross(torch.nn.Module):
    def forward(self, enc):
        if HALF: enc = enc.half()
        cks, cvs = [], []
        for lay in bert.encoder.layer:
            ca = lay.crossattention.self
            cks.append(heads(ca.key(enc))); cvs.append(heads(ca.value(enc)))
        if HALF: return torch.stack(cks).float(), torch.stack(cvs).float()
        return torch.stack(cks), torch.stack(cvs)


OUT2 = OUT + '-small'
os.makedirs(OUT2, exist_ok=True)
import shutil
shutil.copy(f'{OUT}/decoder_step.onnx', f'{OUT2}/decoder_step.onnx')
with torch.no_grad():
    torch.onnx.export(Cross(), (enc,), f'{OUT2}/decoder_cross.onnx', dynamo=False, opset_version=17,
                      input_names=['encoder_hidden_states'], output_names=['cross_k', 'cross_v'],
                      dynamic_axes={'encoder_hidden_states': {0: 'B', 1: 'S'}, 'cross_k': {1: 'B', 3: 'S'}, 'cross_v': {1: 'B', 3: 'S'}})
print('exported', OUT2)
