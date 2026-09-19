# -*- coding: utf-8 -*-
"""產生各語言的社群分享縮圖（og:image）。

縮圖沿用首頁的視覺：同樣的標題、同樣的標語、同樣那張翻譯前後對照，
但版面是為 1200x630 重排的 —— 直接截首頁會因為比例不對而切掉東西，
而且截圖縮到 1200 寬之後，對照圖裡的字就看不清楚了。

對照圖是從 2060x1160 的原圖裡「原尺寸裁切」一張卡片出來，不縮放，
所以文字是滿解析度的，縮圖被平台再縮一次也還讀得出來。

    python tools/build-og.py

需要 Edge（只有產圖時要，CI 不會跑這支）。產出的 PNG 會 commit 進版控。
"""
import io
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, 'docs', 'images', 'og')

EDGE = [
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
]

TC = '"Microsoft JhengHei", "PingFang TC", "Noto Sans TC"'
SC = '"Microsoft YaHei", "PingFang SC", "Noto Sans SC"'
JP = '"Yu Gothic UI", "Hiragino Sans", "Noto Sans JP"'
KR = '"Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans KR"'

# 語言, 產出檔名, 頁面路徑, 字型
# 檔名後綴沿用 build-site.py 的 LANGS，換語言的圖才是同一套規則
LANGS = [
    ('zh-TW',   'og.png',         'docs/index.html',         TC),
    ('en',      'og_en.png',      'docs/en/index.html',      TC),
    ('zh-Hans', 'og_zh-Hans.png', 'docs/zh-Hans/index.html', SC),
    ('ja',      'og_jp.png',      'docs/ja/index.html',      JP),
    ('ko',      'og_ko.png',      'docs/ko/index.html',      KR),
]

# 從 2060x1160 原圖裁出左上那張卡片：縮圖、標題、副標都在裡面
CROP_X, CROP_Y, CROP_W, CROP_H = 336, 44, 456, 396

TEMPLATE = u'''<!doctype html>
<meta charset="utf-8">
<base href="file:///{root}/docs/">
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
  .stack {{ position: relative; padding-top: 38px; text-align: center; }}
  .eyebrow {{
    display: inline-flex; align-items: center; gap: 10px;
    padding: 7px 16px; border: 1px solid rgba(255, 255, 255, 0.10);
    border-radius: 999px; background: rgba(255, 255, 255, 0.045);
    color: #9aabc0; font-size: 18px; letter-spacing: 0.01em;
  }}
  .eyebrow .os {{ display: inline-flex; align-items: center; gap: 8px; color: #e9eef6; font-weight: 600; }}
  .eyebrow svg {{ width: 17px; height: 17px; fill: #52c4fa; }}
  .eyebrow .dot {{ color: #6d7f95; }}
  .name {{
    margin-top: 16px; font-size: 64px; line-height: 1.04; font-weight: 700;
    letter-spacing: -0.035em;
    background: linear-gradient(140deg, #e9eef6 30%, #7fe4f4);
    -webkit-background-clip: text; background-clip: text; color: transparent;
  }}
  .claim {{
    margin-top: 14px; font-size: 31px; line-height: 1.3; font-weight: 600;
    letter-spacing: -0.018em; color: #e9eef6;
  }}
  .shot {{
    position: absolute; left: 50%; translate: -50% 0; bottom: 0;
    display: flex; width: {shot_w}px; height: {crop_h}px;
    border: 1px solid rgba(255, 255, 255, 0.10); border-bottom: 0;
    border-radius: 18px 18px 0 0; overflow: hidden;
    box-shadow: 0 -20px 60px -30px rgba(0, 0, 0, 0.9);
  }}
  .half {{ position: relative; width: {crop_w}px; height: {crop_h}px; overflow: hidden; }}
  .half img {{ position: absolute; left: -{crop_x}px; top: -{crop_y}px; display: block; }}
  .half + .half {{ border-left: 2px solid rgba(255, 255, 255, 0.55); }}
  .tag {{
    position: absolute; top: 12px; padding: 6px 14px; border-radius: 999px;
    background: rgba(8, 13, 22, 0.72); color: #fff;
    font-size: 16px; font-weight: 620; letter-spacing: 0.02em;
  }}
  .tag--before {{ left: 12px; }}
  .tag--after {{ right: 12px; }}
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
</div>
<div class="shot">
  <div class="half"><img src="images/{before}" width="2060" height="1160" alt=""><span class="tag tag--before">{tag_before}</span></div>
  <div class="half"><img src="images/{after}" width="2060" height="1160" alt=""><span class="tag tag--after">{tag_after}</span></div>
</div>
'''


def find_edge():
    for path in EDGE:
        if os.path.exists(path):
            return path
    raise SystemExit('找不到 Edge，這支只在本機產圖時會用到')


def strings(page):
    """把首頁已經翻譯好的字撈出來，不要在這裡再維護一份。"""
    html = io.open(os.path.join(ROOT, page), encoding='utf-8').read()
    want = ('hero.free', 'hero.claim', 'compare.before', 'compare.after')
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
    root = ROOT.replace('\\', '/')

    for lang, name, page, font in LANGS:
        s = strings(page)
        html = TEMPLATE.format(
            root=root, font=font,
            free=s['hero.free'], claim=s['hero.claim'],
            tag_before=s['compare.before'], tag_after=s['compare.after'],
            before=u'截圖翻譯-前.png', after=u'截圖翻譯-後.png',
            crop_x=CROP_X, crop_y=CROP_Y, crop_w=CROP_W, crop_h=CROP_H,
            shot_w=CROP_W * 2 + 2)

        fd, tmp = tempfile.mkstemp(suffix='.html')
        os.close(fd)
        io.open(tmp, 'w', encoding='utf-8').write(html)
        out = os.path.join(OUT_DIR, name)
        subprocess.call([
            edge, '--headless=new', '--disable-gpu', '--no-first-run',
            '--no-default-browser-check', '--hide-scrollbars',
            '--run-all-compositor-stages-before-draw',
            '--virtual-time-budget=6000', '--window-size=1200,630',
            '--screenshot=' + out, 'file:///' + tmp.replace('\\', '/'),
        ], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        os.remove(tmp)

        if not os.path.exists(out):
            raise SystemExit('%s 產生失敗' % name)
        sys.stdout.write('  %-16s %.0f KB\n' % (name, os.path.getsize(out) / 1024.0))


if __name__ == '__main__':
    main()
