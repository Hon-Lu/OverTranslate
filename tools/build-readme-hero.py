# -*- coding: utf-8 -*-
"""產生 README 開頭那張會自己掃動的前後對照圖（APNG）。

就是首頁比較滑桿的動態版：左半原文、右半譯文，中間那條線自己左右來回掃。
README 不能跑 JavaScript，拖不動的滑桿只好做成動畫。來源與首頁滑桿是同一組：

    docs/images/截圖翻譯-前.png
    docs/images/截圖翻譯-後.png

輸出 docs/images/readme-hero.png。

檔案大小是這張圖唯一的難題，以下都是量過的（1456x1138、來回、每秒 25 格）：

- 全彩 5.1 MB。改成原文譯文共用一組 256 色調色盤後 1.8 MB，文字看不出差別，
  只有遊戲縮圖有一點色偏。共用是關鍵 —— 各自量化的話，線掃過去顏色會跳。
- 抖色（Floyd–Steinberg）顏色較準，但 2.2 MB，在 README 的寬度下不值得。
- 不要縮小解析度：縮到 1200 寬反而變大（6.4 MB）。重新取樣讓字緣多出細碎的色階，壓不下來。
- 單向掃（掃完直接跳回原文）能再省一半，但使用者否決了，來回才看得出「蓋上去」。

    python tools/build-readme-hero.py

需要 Pillow（只有產圖時要，CI 不會跑這支）。產出的 PNG 會 commit 進版控。
"""
import math
import os
import sys

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IMAGES = os.path.join(ROOT, 'docs', 'images')
BEFORE = os.path.join(IMAGES, u'截圖翻譯-前.png')
AFTER = os.path.join(IMAGES, u'截圖翻譯-後.png')
OUT = os.path.join(IMAGES, 'readme-hero.png')

# 裁掉來源圖左右的空白，工具列整條要留著。換了來源截圖要重新量。
CROP = (296, 14, 1752, 1152)

FPS = 25
# 線在左右兩端停的位置（寬度比例）。不貼邊，握把才不會被切掉。
EDGE = 0.04
# (起點, 終點, 秒)；起點等於終點就是停住
TIMELINE = [('right', 'right', 1.0), ('right', 'left', 2.0),
            ('left', 'left', 1.6), ('left', 'right', 2.0)]

GRIP = 46      # 握把直徑
SUPERSAMPLE = 4


def grip():
    """照抄網站的 .compare__grip：白色圓形，中間一對左右箭頭。"""
    d = GRIP * SUPERSAMPLE
    big = Image.new('RGBA', (d, d), (0, 0, 0, 0))
    draw = ImageDraw.Draw(big)
    draw.ellipse((0, 0, d - 1, d - 1), fill=(255, 255, 255, 240))
    s = d / 24.0
    for pts in ([(10, 8.5), (6.5, 12), (10, 15.5)], [(14, 8.5), (17.5, 12), (14, 15.5)]):
        draw.line([(x * s, y * s) for x, y in pts], fill=(13, 22, 34, 255),
                  width=int(2.2 * s), joint='curve')
    return big.resize((GRIP, GRIP), Image.LANCZOS)


def main():
    for path in (BEFORE, AFTER):
        if not os.path.exists(path):
            raise SystemExit('缺少 %s' % os.path.relpath(path, ROOT))
    before = Image.open(BEFORE).convert('RGB').crop(CROP)
    after = Image.open(AFTER).convert('RGB').crop(CROP)
    w, h = before.size

    # 一組調色盤給兩張圖共用，底下補一條白色，分隔線與握把才有準確的白
    sheet = Image.new('RGB', (w * 2, h + 8), (255, 255, 255))
    sheet.paste(before, (0, 0))
    sheet.paste(after, (w, 0))
    palette = sheet.quantize(256, method=Image.Quantize.MEDIANCUT)
    before = before.quantize(palette=palette, dither=Image.Dither.NONE).convert('RGB')
    after = after.quantize(palette=palette, dither=Image.Dither.NONE).convert('RGB')
    handle = grip()

    def frame(x):
        img = before.copy()
        img.paste(after.crop((x, 0, w, h)), (x, 0))
        draw = ImageDraw.Draw(img, 'RGBA')
        draw.rectangle((x - 2, 0, x + 2, h), fill=(0, 0, 0, 50))
        draw.rectangle((x - 1, 0, x, h), fill=(255, 255, 255, 235))
        img.paste(handle, (x - GRIP // 2, h // 2 - GRIP // 2), handle)
        return img.quantize(palette=palette, dither=Image.Dither.NONE)

    pos = {'left': int(w * EDGE), 'right': int(w * (1 - EDGE))}
    frames, durations = [], []
    for start, end, sec in TIMELINE:
        a, b = pos[start], pos[end]
        if a == b:
            frames.append(frame(a))
            durations.append(int(sec * 1000))
            continue
        n = int(sec * FPS)
        for i in range(1, n + 1):
            t = 0.5 - 0.5 * math.cos(math.pi * i / n)
            frames.append(frame(int(round(a + (b - a) * t))))
            durations.append(int(round(1000.0 / FPS)))

    frames[0].save(OUT, save_all=True, append_images=frames[1:],
                   duration=durations, loop=0, optimize=True)
    sys.stdout.write('  readme-hero.png  %dx%d  %d 格  %.2f MB\n'
                     % (w, h, len(frames), os.path.getsize(OUT) / 1048576.0))


if __name__ == '__main__':
    main()
