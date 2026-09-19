# -*- coding: utf-8 -*-
"""產生各語言的社群分享縮圖（og:image）。

縮圖沿用首頁的視覺：同樣的標題、同樣的標語、同樣那個左右對照，
字級直接抄 styles.css 在 1200px 寬時的實際值，不要在這裡另外調。
版面是為 1200x630 重排的 —— 直接截首頁會因為比例不對而切掉東西。

對照的部分是「一張畫面切一半」，不是左右各擺一張圖：
左半取未翻譯的那張、右半取翻譯後的那張，中間一條白線。
兩張是同一個畫面的前後，中間那條線才有意義。

原始畫面：截一張原文、再用 OverTranslate 翻譯後截一張，
兩張裁成一樣的範圍後存成

    docs/images/og/og-src-before.png
    docs/images/og/og-src-after.png

寬度會縮到 1104，高度照比例走，超過 368 就從底部切掉。

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

# 對照區在縮圖上的尺寸。高度是上限，超過就從底部切掉 ——
# 原圖上緣的留白要留著給「原文／譯文」標籤站，不能從那邊切
SHOT_W, SHOT_MAX_H = 1060, 374

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
    /* 字級照網站 .hero__lede，但行高與間距收緊 —— 這裡只有一行，
       留網站那種給多行段落用的鬆度會顯得散 */
    margin-top: 2px; font-size: 17.28px; line-height: 1.5; color: #9aabc0;
  }}
  /* 卡片後面補一圈光暈：陰影打在純黑底上等於沒打，
     要有東西襯著，邊緣和陰影才浮得起來 */
  .halo {{
    position: absolute; left: 50%; translate: -50% 0; bottom: -90px;
    width: 1180px; height: 460px; border-radius: 50%;
    background: radial-gradient(50% 50% at 50% 50%,
      rgba(82, 196, 250, 0.20), rgba(82, 196, 250, 0.06) 55%, transparent 72%);
    filter: blur(26px);
  }}
  .shot {{
    position: absolute; left: 50%; translate: -50% 0; bottom: 0;
    width: {shot_w}px; height: {shot_h}px;
    border: 1px solid rgba(255, 255, 255, 0.14);
    border-top-color: rgba(255, 255, 255, 0.26); border-bottom: 0;
    border-radius: 22px 22px 0 0; overflow: hidden; background: #0b1220;
    box-shadow: 0 -26px 60px -10px rgba(0, 0, 0, 0.85),
                inset 0 1px 0 rgba(255, 255, 255, 0.08);
  }}
  .shot img {{ display: block; width: {shot_w}px; height: {shot_h}px; }}
  /* 以下三個都照抄網站的 .compare__handle / .compare__grip / .compare__tag */
  .divider {{
    position: absolute; top: 0; bottom: 0; left: 50%; width: 2px; margin-left: -1px;
    background: rgba(255, 255, 255, 0.92);
    box-shadow: 0 0 0 1px rgba(0, 0, 0, 0.22), 0 0 18px rgba(0, 0, 0, 0.35);
  }}
  .grip {{
    position: absolute; top: 50%; left: 50%; translate: -50% -50%;
    display: grid; place-items: center; width: 42px; height: 42px;
    border-radius: 999px; background: rgba(255, 255, 255, 0.94);
    box-shadow: 0 6px 20px -6px rgba(0, 0, 0, 0.6); color: #0d1622;
  }}
  .grip svg {{
    width: 22px; height: 22px; fill: none; stroke: currentColor;
    stroke-width: 2; stroke-linecap: round; stroke-linejoin: round;
  }}
  .tag {{
    position: absolute; top: 11px; padding: 5px 13px; border-radius: 999px;
    background: rgba(8, 13, 22, 0.72); color: #fff;
    font-size: 15px; font-weight: 620; letter-spacing: 0.02em;
  }}
  .tag--before {{ left: 11px; }}
  .tag--after {{ right: 11px; }}
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
<div class="halo"></div>
<div class="shot">
  <img src="file:///{shot}" alt="">
  <span class="tag tag--before">{tag_before}</span>
  <span class="tag tag--after">{tag_after}</span>
  <span class="divider"></span>
  <span class="grip"><svg viewBox="0 0 24 24"><path d="M10 8.5 6.5 12l3.5 3.5M14 8.5l3.5 3.5L14 15.5"/></svg></span>
</div>
'''


def find_edge():
    for path in EDGE:
        if os.path.exists(path):
            return path
    raise SystemExit('找不到 Edge，這支只在本機產圖時會用到')


def compose_shot():
    """左半原文、右半譯文，中間一條白線 —— 就是首頁那個比較滑桿的定格。

    兩張圖要是同一塊畫面、同樣的裁切範圍，中間那條線才對得起來。
    """
    for path in (BEFORE, AFTER):
        if not os.path.exists(path):
            raise SystemExit('缺少 %s' % os.path.relpath(path, ROOT))

    before = Image.open(BEFORE).convert('RGB')
    after = Image.open(AFTER).convert('RGB')
    if before.size != after.size:
        raise SystemExit('兩張圖尺寸不一樣（%dx%d 與 %dx%d），請裁成一樣的範圍'
                         % (before.size + after.size))

    w, h = before.size
    mid = w // 2
    shot = before.copy()
    shot.paste(after.crop((mid, 0, w, h)), (mid, 0))

    # 分割線與握把由 HTML 疊上去，這裡只負責把兩半拼起來
    height = int(round(h * SHOT_W / float(w)))
    shot = shot.resize((SHOT_W, height), Image.LANCZOS)
    if height > SHOT_MAX_H:
        shot = shot.crop((0, 0, SHOT_W, SHOT_MAX_H))

    fd, tmp = tempfile.mkstemp(suffix='.png')
    os.close(fd)
    shot.save(tmp)
    return tmp, shot.size[1]


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
    shot, shot_h = compose_shot()

    try:
        for lang, name, page, font in LANGS:
            s = strings(page)
            html = TEMPLATE.format(
                font=font, free=s['hero.free'], claim=s['hero.claim'],
                lede=LEDE[lang],
                tag_before=s['compare.before'], tag_after=s['compare.after'],
                shot=shot.replace('\\', '/'), shot_w=SHOT_W, shot_h=shot_h)

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
