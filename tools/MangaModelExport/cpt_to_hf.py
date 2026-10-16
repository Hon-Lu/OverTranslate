"""CPT-224 (muscgab/manga-ocr-base-CPT) or CPT-flex (muscgab/manga-ocr-base-CPT-flex) as a Hugging Face
VisionEncoderDecoderModel folder: CPT-224 for export_fp16.py and export_kv.py to export like manga-ocr,
CPT-flex for export_flex.py and MASK=1 export_kv.py.

  python cpt_to_hf.py <out> [weights] [--check <manga-ocr-CPT checkout>]

weights: the folder of either model (model.safetensors, nar_vocabulary_6817.json), or "224" / "flex"
to download it from Hugging Face (nothing = "224"). The two have the same weights' names and the same
vocabulary; only what they were trained to read differs (see export_flex.py). The architecture is
manga-ocr's (kha-white/manga-ocr-base's config) with a vocabulary of 6822: the five BERT special tokens,
then the author's 6817 characters. The output layer shares the word embeddings, as in manga-ocr.

--check runs the author's own PyTorch code (github.com/muscgab/manga-ocr-CPT) on its sample images and
compares encoder output and greedy readings with the converted model. CPT-224 only: CPT-flex's input
is not the HF model's (see export_flex.py and README).
"""
import glob, hashlib, json, os, sys
import torch
from safetensors.torch import load_file
from transformers import VisionEncoderDecoderConfig, VisionEncoderDecoderModel

REPOS = {'224': 'muscgab/manga-ocr-base-CPT', 'flex': 'muscgab/manga-ocr-base-CPT-flex'}
# The weights the files of each manifest version were exported from.
WEIGHTS_SHA256 = {'1032650b1e312ddcc02f8daad06a02c37a048691c345b816996d4be357b243d2': 'CPT-224, manifest version 2',
                  'd2f507aead9c9dd9fd02660d2825363edd36aba6ba1bbf6b387519f153f69ef0': 'CPT-flex, manifest version 3'}

args = sys.argv[1:]
check = None
if '--check' in args:
    i = args.index('--check'); check = args[i + 1]; del args[i:i + 2]
OUT = args[0] if args else os.path.join('out', 'cpt224-hf')
SRC = args[1] if len(args) > 1 else '224'
if SRC in REPOS:
    from huggingface_hub import snapshot_download
    SRC = snapshot_download(REPOS[SRC])

with open(os.path.join(SRC, 'model.safetensors'), 'rb') as f:
    sha = hashlib.sha256(f.read()).hexdigest()
print('model.safetensors', sha, f'({WEIGHTS_SHA256[sha]}, as exported)' if sha in WEIGHTS_SHA256
      else '(NOT the weights any manifest version was made from)')
w = load_file(os.path.join(SRC, 'model.safetensors'))
chars = json.load(open(os.path.join(SRC, 'nar_vocabulary_6817.json'), encoding='utf-8'))['characters']

cfg = VisionEncoderDecoderConfig.from_pretrained('kha-white/manga-ocr-base')
cfg.decoder.vocab_size = cfg.vocab_size = 5 + len(chars)
# Seeded so the pooler's unused random weights, the only ones not loaded below, come out the same.
torch.manual_seed(0)
m = VisionEncoderDecoderModel(cfg).eval()

# The author's key names -> transformers 4.x ViT / BERT names (5.x renamed ViT's; see README).
sd = {}
E, D = 'encoder.', 'decoder.bert.'
sd[E + 'embeddings.patch_embeddings.projection.weight'] = w['enc.patch.weight'].reshape(768, 3, 16, 16)
sd[E + 'embeddings.patch_embeddings.projection.bias'] = w['enc.patch.bias']
sd[E + 'embeddings.cls_token'] = w['enc.cls'].reshape(1, 1, 768)
sd[E + 'embeddings.position_embeddings'] = w['enc.pos'][None]
for i in range(12):
    p, q = f'enc.{i}.', f'{E}encoder.layer.{i}.'
    for a, b in [('ln1', 'layernorm_before'), ('ln2', 'layernorm_after'), ('q', 'attention.attention.query'),
                 ('k', 'attention.attention.key'), ('v', 'attention.attention.value'), ('o', 'attention.output.dense'),
                 ('f1', 'intermediate.dense'), ('f2', 'output.dense')]:
        sd[q + b + '.weight'] = w[p + a + '.weight']; sd[q + b + '.bias'] = w[p + a + '.bias']
sd[E + 'layernorm.weight'] = w['enc.ln.weight']; sd[E + 'layernorm.bias'] = w['enc.ln.bias']
sd[D + 'embeddings.word_embeddings.weight'] = w['dec.word']
sd[D + 'embeddings.position_embeddings.weight'] = w['dec.pos']
# Only token type 0 is ever used; the author keeps just that row.
sd[D + 'embeddings.token_type_embeddings.weight'] = torch.stack([w['dec.type0'], torch.zeros(768)])
sd[D + 'embeddings.LayerNorm.weight'] = w['dec.ln.weight']; sd[D + 'embeddings.LayerNorm.bias'] = w['dec.ln.bias']
for i in range(2):
    p, q = f'dec.{i}.', f'{D}encoder.layer.{i}.'
    for a, b in [('q', 'attention.self.query'), ('k', 'attention.self.key'), ('v', 'attention.self.value'),
                 ('o', 'attention.output.dense'), ('ln1', 'attention.output.LayerNorm'),
                 ('cq', 'crossattention.self.query'), ('ck', 'crossattention.self.key'), ('cv', 'crossattention.self.value'),
                 ('co', 'crossattention.output.dense'), ('ln2', 'crossattention.output.LayerNorm'),
                 ('f1', 'intermediate.dense'), ('f2', 'output.dense'), ('ln3', 'output.LayerNorm')]:
        sd[q + b + '.weight'] = w[p + a + '.weight']; sd[q + b + '.bias'] = w[p + a + '.bias']
C = 'decoder.cls.predictions.'
sd[C + 'transform.dense.weight'] = w['dec.head.weight']; sd[C + 'transform.dense.bias'] = w['dec.head.bias']
sd[C + 'transform.LayerNorm.weight'] = w['dec.head_ln.weight']; sd[C + 'transform.LayerNorm.bias'] = w['dec.head_ln.bias']
sd[C + 'decoder.weight'] = w['dec.word']
sd[C + 'bias'] = w['dec.out_bias']; sd[C + 'decoder.bias'] = w['dec.out_bias']

own = m.state_dict()
missing = [k for k in own if k not in sd]
extra = [k for k in sd if k not in own]
# Only ViT's pooler has no weights here; the export reads last_hidden_state and never runs it. Under
# transformers 5.x every ViT key goes unmatched instead, and the model would read garbage.
assert all(k.startswith('encoder.pooler.') for k in missing) and not extra, \
    f'unmatched keys {missing[:4]} {extra[:4]}: use transformers 4.57.6 (see README)'
for k, v in sd.items():
    assert own[k].shape == v.shape, (k, own[k].shape, v.shape)
m.load_state_dict(sd, strict=False)

if check:
    sys.path.insert(0, check)
    from manga_ocr_cpt.model import MangaOcrNet
    from manga_ocr_cpt.preprocess import preprocess
    from PIL import Image
    import numpy as np
    net = MangaOcrNet.load(SRC, 'cpu')
    worst = 0; same = 0
    files = sorted(glob.glob(os.path.join(check, 'samples', '*.png')))[:12]
    def text(ids): return ''.join(chars[t - 5] for t in (ids[:ids.index(3)] if 3 in ids else ids) if t >= 5)
    for f in files:
        im = Image.open(f)
        P, V, gh, gw = preprocess(im, 'square')
        Pt = torch.from_numpy(P)[None]; Vt = torch.from_numpy(V)[None]; pe = net.pe(gh, gw)[None]
        with torch.no_grad():
            mem, _ = net.encode(Pt, Vt, pe)
            a = np.asarray(im.convert('L').convert('RGB').resize((224, 224), Image.BILINEAR), np.float32)
            px = torch.from_numpy(((a / 255 - 0.5) / 0.5).transpose(2, 0, 1)[None].copy())
            worst = max(worst, (m.encoder(pixel_values=px).last_hidden_state - mem).abs().max().item())
            ta = text(net.greedy(Pt, Vt, pe)[0].tolist())
            tb = text(m.generate(px, max_new_tokens=65, num_beams=1, do_sample=False, decoder_start_token_id=2,
                                 eos_token_id=3, pad_token_id=0)[0].tolist()[1:])
        same += ta == tb
        print(os.path.basename(f), ta == tb, ta, '|', tb)
    print(f'encoder max diff {worst:.2e}; greedy readings equal {same}/{len(files)}')

os.makedirs(OUT, exist_ok=True)
m.config.decoder_start_token_id = 2; m.config.pad_token_id = 0; m.config.eos_token_id = 3
m.save_pretrained(OUT)
# The app's vocab.txt: one token per line, ids in order, \n endings.
with open(os.path.join(OUT, 'vocab.txt'), 'w', encoding='utf-8', newline='\n') as fh:
    fh.write('\n'.join(['[PAD]', '[UNK]', '[CLS]', '[SEP]', '[MASK]'] + chars) + '\n')
print('written to', OUT)
