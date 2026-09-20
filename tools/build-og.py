# -*- coding: utf-8 -*-
"""產生各語言的社群分享縮圖（og:image）。

縮圖沿用首頁的視覺：同樣的標題、同樣的標語、同樣那個左右對照。
版面是為 1200x630 重排的 —— 直接截首頁會因為比例不對而切掉東西。

字級「不」照抄 styles.css。首頁的字是給人在 1200px 寬的螢幕上讀的，縮圖卻常常
被平台縮到三四百 px 寬顯示，照抄就會小到讀不出來，所以這裡是另一套放大過的值。

對照的部分是「一張畫面切一半」，不是左右各擺一張圖：
左半取未翻譯的那張、右半取翻譯後的那張，中間一條白線。
兩張是同一個畫面的前後，中間那條線才有意義。

原始畫面：截一張原文、再用 OverTranslate 翻譯後截一張，
兩張裁成一樣的範圍後存成

    docs/images/og/og-src-before.png
    docs/images/og/og-src-after.png

寬度會縮到 SHOT_W，高度照比例走，超過 SHOT_MAX_H 就從底部切掉。

輸出是 2 倍圖（2400x1260）。1 倍圖在高解析螢幕上、或平台把縮圖放大顯示時會糊，
而縮圖裡最需要看清楚的就是那三行字。og:image:width / height 要跟著一起改。

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
# 「免費」是後來補進來的 —— 這張圖沒有首頁那顆 Windows / 免費開源 標籤，
# 不寫在這句裡就整張圖都看不到。
LEDE = {
    'zh-TW':   u'適用於漫畫、影音與遊戲的 Windows 免費螢幕翻譯工具。',
    'en':      u'A free Windows screen translator for comics, video and games.',
    'zh-Hans': u'适用于漫画、影音与游戏的 Windows 免费屏幕翻译工具。',
    'ja':      u'漫画・動画・ゲームで使える Windows 用の無料画面翻訳ツール。',
    'ko':      u'만화·영상·게임에 쓸 수 있는 Windows 무료 화면 번역 도구.',
}

# 輸出倍率。版面仍以 1200x630 計算，只是多渲染一倍的像素。
SCALE = 2

# 對照區在縮圖上的尺寸（CSS px）。高度是上限，超過就從底部切掉 ——
# 原圖上緣的留白要留著給「原文／譯文」標籤站，不能從那邊切。
#
# 寬度決定了整張圖的版面：對照區是固定比例，窄一點高度就跟著矮，上面留給文字的
# 空間才長得出來。338 這個高度是量出來的 —— 來源圖最下面那條橘色框線在原圖的
# 87.4% 處，縮到 960 寬之後落在 316px，再留 22px 才切，框線底部就剛好看得見又
# 不貼著圖片邊界。換了來源截圖要重新量一次，不要沿用這個數字。
SHOT_W, SHOT_MAX_H = 960, 338

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
  /* 三行字級都比網站大 —— 縮圖被平台縮到三四百 px 寬是常態，照抄網站的值會
     小到讀不出來。整塊連同上方留白約 256px，剛好落在卡片上緣（y=292）之上，
     底下還留得出約 36px 的呼吸空間。改字級記得重算這筆帳。 */
  .stack {{ position: relative; padding-top: 40px; text-align: center; }}
  .name {{
    font-size: 104px; line-height: 1.04; font-weight: 700;
    letter-spacing: -0.035em;
    background: linear-gradient(140deg, #e9eef6 30%, #7fe4f4);
    -webkit-background-clip: text; background-clip: text; color: transparent;
  }}
  .claim {{
    margin-top: 16px; font-size: 38px; line-height: 1.3; font-weight: 600;
    letter-spacing: -0.018em; color: #e9eef6;
  }}
  .lede {{
    margin-top: 8px; font-size: 23px; line-height: 1.5; color: #9aabc0;
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

    # 分割線與握把由 HTML 疊上去，這裡只負責把兩半拼起來。
    # 圖本身做成 SCALE 倍，版面尺寸仍用 CSS px —— 交給瀏覽器放大只會更糊。
    out_w = SHOT_W * SCALE
    if out_w > w:
        sys.stdout.write(
            '  注意：來源截圖只有 %d px 寬，要填滿 %d px 是放大的。\n'
            '        要更銳利就重拍 og-src-before/after.png，至少 %d px 寬。\n'
            % (w, out_w, out_w))
    out_h = int(round(h * out_w / float(w)))
    shot = shot.resize((out_w, out_h), Image.LANCZOS)

    css_h = min(int(round(out_h / float(SCALE))), SHOT_MAX_H)
    if out_h > css_h * SCALE:
        shot = shot.crop((0, 0, out_w, css_h * SCALE))

    fd, tmp = tempfile.mkstemp(suffix='.png')
    os.close(fd)
    shot.save(tmp)
    return tmp, css_h


def strings(page):
    """把首頁已經翻譯好的字撈出來，不要在這裡再維護一份。"""
    html = io.open(os.path.join(ROOT, page), encoding='utf-8').read()
    want = ('hero.claim', 'compare.before', 'compare.after')
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
                font=font, claim=s['hero.claim'], lede=LEDE[lang],
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
                '--force-device-scale-factor=%d' % SCALE,
                '--screenshot=' + out, 'file:///' + tmp.replace('\\', '/'),
            ], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            os.remove(tmp)

            if not os.path.exists(out):
                raise SystemExit('%s 產生失敗' % name)
            size = Image.open(out).size
            sys.stdout.write('  %-16s %dx%d  %.0f KB\n'
                             % (name, size[0], size[1], os.path.getsize(out) / 1024.0))
    finally:
        os.remove(shot)


if __name__ == '__main__':
    main()
