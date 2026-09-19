# -*- coding: utf-8 -*-
"""產生各語言的社群分享縮圖（og:image）。

縮圖沿用首頁的視覺：同樣的標題、同樣的標語、同樣那個左右對照，
字級直接抄 styles.css 在 1200px 寬時的實際值，不要在這裡另外調。
版面是為 1200x630 重排的 —— 直接截首頁會因為比例不對而切掉東西。

對照的部分是「一張畫面切一半」，不是左右各擺一張圖：
左半取未翻譯的那張、右半取翻譯後的那張，中間一條白線。
兩張是同一個畫面的前後，中間那條線才有意義。

原始畫面：用瀏覽器開 tools/og-source.html，縮放 100%，
截一張原文、再用 OverTranslate 翻譯後截一張，存成

    docs/images/og/og-src-before.png
    docs/images/og/og-src-after.png

整個視窗截沒關係，這支會靠畫面裡那圈桃紅色的框自己定位並裁掉框本身。

    python tools/build-og.py

需要 Edge 與 Pillow（只有產圖時要，CI 不會跑這支）。產出的 PNG 會 commit 進版控。
"""
import io
import os
import re
import subprocess
import sys
import tempfile

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, 'docs', 'images', 'og')
BEFORE = os.path.join(OUT_DIR, 'og-src-before.png')
AFTER = os.path.join(OUT_DIR, 'og-src-after.png')

EDGE = [
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
]

TC = '"Microsoft JhengHei", "PingFang TC", "Noto Sans TC"'
SC = '"Microsoft YaHei", "PingFang SC", "Noto Sans SC"'
JP = '"Yu Gothic UI", "Hiragino Sans", "Noto Sans JP"'
KR = '"Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans KR"'

# 檔名後綴沿用 build-site.py 的 LANGS，換語言的圖才是同一套規則
LANGS = [
    ('zh-TW',   'og.png',         'docs/index.html',         TC),
    ('en',      'og_en.png',      'docs/en/index.html',      TC),
    ('zh-Hans', 'og_zh-Hans.png', 'docs/zh-Hans/index.html', SC),
    ('ja',      'og_jp.png',      'docs/ja/index.html',      JP),
    ('ko',      'og_ko.png',      'docs/ko/index.html',      KR),
]

# 縮圖只放得下一行，所以取 hero.lede 的第一句。改了首頁那句記得一起改。
LEDE = {
    'zh-TW':   u'適用於漫畫、影音與遊戲的 Windows 螢幕翻譯工具。',
    'en':      u'A Windows screen translator for comics, video and games.',
    'zh-Hans': u'适用于漫画、影音与游戏的 Windows 屏幕翻译工具。',
    'ja':      u'漫画・動画・ゲームで使える Windows 用の画面翻訳ツール。',
    'ko':      u'만화·영상·게임에 쓸 수 있는 Windows 화면 번역 도구.',
}

# og-source.html 裡那塊畫布的尺寸
CANVAS_W, CANVAS_H = 1104, 384
# 縮圖上實際露出多少。底部那截是畫布的留白，切掉不會少看到東西
SHOT_W, SHOT_H = 1104, 368

TEMPLATE = u'''<!doctype html>
<meta charset="utf-8">
<style>
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{
    width: 1200px; height: 630px; position: relative; overflow: hidden;
    background: #070c14; color: #e9eef6;
    font-family: system-ui, -apple-system, "Segoe UI", {font}, sans-serif;
    -webkit-font-smoothing: antialiased;
  }}
  .glow {{
    position: absolute; top: -300px; left: 50%; translate: -50% 0;
    width: 1200px; height: 760px; filter: blur(18px);
    background:
      radial-gradient(48% 50% at 50% 50%, rgba(82, 196, 250, 0.22), transparent 70%),
      radial-gradient(38% 42% at 72% 42%, rgba(98, 239, 246, 0.16), transparent 70%);
  }}
  .stack {{ position: relative; padding-top: 28px; text-align: center; }}
  .eyebrow {{
    display: inline-flex; align-items: center; gap: 9px;
    padding: 6px 14px; border: 1px solid rgba(255, 255, 255, 0.10);
    border-radius: 999px; background: rgba(255, 255, 255, 0.045);
    color: #9aabc0; font-size: 13.12px; letter-spacing: 0.01em;
  }}
  .eyebrow .os {{ display: inline-flex; align-items: center; gap: 7px; color: #e9eef6; font-weight: 600; }}
  .eyebrow svg {{ width: 15px; height: 15px; fill: #52c4fa; }}
  .eyebrow .dot {{ color: #6d7f95; }}
  .name {{
    /* 網站 .hero__name 在 1200px 寬時的實際值，不要自己再調 */
    margin-top: 18px; font-size: 73.6px; line-height: 1.04; font-weight: 700;
    letter-spacing: -0.035em;
    background: linear-gradient(140deg, #e9eef6 30%, #7fe4f4);
    -webkit-background-clip: text; background-clip: text; color: transparent;
  }}
  .claim {{
    /* 網站 .hero__claim，與標題之間的 14px 也照抄 */
    margin-top: 14px; font-size: 27.52px; line-height: 1.32; font-weight: 600;
    letter-spacing: -0.018em; color: #e9eef6;
  }}
  .lede {{
    /* 網站 .hero__lede 在 1200px 寬時的實際值 */
    margin-top: 12px; font-size: 17.28px; line-height: 1.75; color: #9aabc0;
  }}
  .shot {{
    position: absolute; left: 50%; translate: -50% 0; bottom: 0;
    width: {shot_w}px; height: {shot_h}px;
    border: 1px solid rgba(255, 255, 255, 0.10); border-bottom: 0;
    border-radius: 18px 18px 0 0; overflow: hidden;
    box-shadow: 0 -20px 60px -30px rgba(0, 0, 0, 0.9);
  }}
  .shot img {{ display: block; width: {shot_w}px; height: {shot_h}px; }}
</style>
<div class="glow"></div>
<div class="stack">
  <p class="eyebrow">
    <span class="os">
      <svg viewBox="0 0 24 24"><path d="M3 5.4 10.4 4.3v7.2H3V5.4Zm8.6-1.3L21 3v8.5h-9.4V4.1ZM3 12.7h7.4v7.2L3 18.8v-6.1Zm8.6 0H21V21l-9.4-1.1v-7.2Z"/></svg>
      Windows 10 / 11
    </span>
    <span class="dot">·</span>
    <span>{free}</span>
  </p>
  <h1 class="name">OverTranslate</h1>
  <p class="claim">{claim}</p>
  <p class="lede">{lede}</p>
</div>
<div class="shot">
  <img src="file:///{shot}" alt="">
</div>
'''


def find_edge():
    for path in EDGE:
        if os.path.exists(path):
            return path
    raise SystemExit('找不到 Edge，這支只在本機產圖時會用到')


def canvas_of(path):
    """裁出畫面裡那圈桃紅色定位框的內側，並還原成 1104x384。

    螢幕縮放不是 100% 的話截出來會比較大，所以最後統一縮回去；
    從比較大的圖縮下來反而更銳利，不用特別要求使用者去調縮放。
    """
    im = Image.open(path).convert('RGB')
    w, h = im.size
    px = im.load()
    xs, ys = [], []
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            if r > 200 and g < 80 and b > 200:
                xs.append(x)
                ys.append(y)
    if len(xs) < 500:
        raise SystemExit('%s 裡找不到桃紅色的定位框，是不是截到別的畫面了？'
                         % os.path.basename(path))

    scale = (max(xs) - min(xs) + 1) / float(CANVAS_W + 4)   # 框本身左右各 2px
    edge = int(round(2 * scale))
    box = (min(xs) + edge, min(ys) + edge,
           max(xs) + 1 - edge, max(ys) + 1 - edge)
    out = im.crop(box)
    if out.size != (CANVAS_W, CANVAS_H):
        out = out.resize((CANVAS_W, CANVAS_H), Image.LANCZOS)
    return out


def compose_shot():
    """左半原文、右半譯文，中間一條白線 —— 就是首頁那個比較滑桿的定格。"""
    for path in (BEFORE, AFTER):
        if not os.path.exists(path):
            raise SystemExit('缺少 %s，請先照 tools/og-source.html 的說明截圖'
                             % os.path.relpath(path, ROOT))

    before = canvas_of(BEFORE)
    after = canvas_of(AFTER)

    mid = CANVAS_W // 2
    shot = before.copy()
    shot.paste(after.crop((mid, 0, CANVAS_W, CANVAS_H)), (mid, 0))

    px = shot.load()
    for x in range(mid - 1, mid + 1):
        for y in range(CANVAS_H):
            px[x, y] = (255, 255, 255)
    shot = shot.crop((0, 0, SHOT_W, SHOT_H))

    fd, tmp = tempfile.mkstemp(suffix='.png')
    os.close(fd)
    shot.save(tmp)
    return tmp


def strings(page):
    """把首頁已經翻譯好的字撈出來，不要在這裡再維護一份。"""
    html = io.open(os.path.join(ROOT, page), encoding='utf-8').read()
    want = ('hero.free', 'hero.claim')
    out = {}
    for key in want:
        m = re.search(r'data-i18n="%s">([^<]*)<' % re.escape(key), html)
        if not m:
            raise SystemExit('%s 找不到 %s' % (page, key))
        out[key] = m.group(1)
    return out


def main():
    edge = find_edge()
    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)
    shot = compose_shot()

    try:
        for lang, name, page, font in LANGS:
            s = strings(page)
            html = TEMPLATE.format(
                font=font, free=s['hero.free'], claim=s['hero.claim'],
                lede=LEDE[lang],
                shot=shot.replace('\\', '/'), shot_w=SHOT_W, shot_h=SHOT_H)

            fd, tmp = tempfile.mkstemp(suffix='.html')
            os.close(fd)
            io.open(tmp, 'w', encoding='utf-8').write(html)
            out = os.path.join(OUT_DIR, name)
            subprocess.call([
                edge, '--headless=new', '--disable-gpu', '--no-first-run',
                '--no-default-browser-check', '--hide-scrollbars',
                '--allow-file-access-from-files',
                '--run-all-compositor-stages-before-draw',
                '--virtual-time-budget=6000', '--window-size=1200,630',
                '--screenshot=' + out, 'file:///' + tmp.replace('\\', '/'),
            ], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            os.remove(tmp)

            if not os.path.exists(out):
                raise SystemExit('%s 產生失敗' % name)
            sys.stdout.write('  %-16s %.0f KB\n' % (name, os.path.getsize(out) / 1024.0))
    finally:
        os.remove(shot)


if __name__ == '__main__':
    main()
