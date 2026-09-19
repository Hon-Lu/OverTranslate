#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""從 docs/index.html（繁體中文正本）產生各語言的靜態頁面。

為什麼需要這一步：以前是瀏覽器載入後才用 JS 把文字換成對應語言，
但每個網址的原始碼都是繁中，搜尋引擎不一定會等 JS 跑完，
結果各語言在搜尋結果裡都顯示中文標題。改成每個語言各有一份實體 HTML，
原始碼就是該語言，任何爬蟲都不必執行 JS。

用法：
    python tools/build-site.py           產生頁面
    python tools/build-site.py --check   只檢查是否為最新，不寫檔（CI 用）
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, 'docs')
SOURCE = os.path.join(DOCS, 'index.html')
I18N_DIR = os.path.join(ROOT, 'tools', 'site-i18n')
BASE_URL = 'https://asd880921.github.io/OverTranslate/'

# 每個語言：輸出目錄、<html lang>、截圖後綴、下載統計圖卡、README 與 Ollama 教學
LANGS = [
    ('zh-TW',   'zh-TW',   'zh-Hant',  '',          'overtranslate-downloads-history.zh-TW.svg',
     'README.md',                'docs/guides/OLLAMA_GUIDE.md'),
    ('en',      'en',      'en',       '_en',       'overtranslate-downloads-history.svg',
     'docs/README.en.md',        'docs/guides/OLLAMA_GUIDE.en.md'),
    ('zh-Hans', 'zh-Hans', 'zh-Hans',  '_zh-Hans',  'overtranslate-downloads-history.svg',
     'docs/README.zh-Hans.md',   'docs/guides/OLLAMA_GUIDE.zh-Hans.md'),
    ('ja',      'ja',      'ja',       '_jp',       'overtranslate-downloads-history.svg',
     'docs/README.ja.md',        'docs/guides/OLLAMA_GUIDE.ja.md'),
    ('ko',      'ko',      'ko',       '_ko',       'overtranslate-downloads-history.svg',
     'docs/README.ko.md',        'docs/guides/OLLAMA_GUIDE.ko.md'),
]
LANG_LABEL = {'en': 'English', 'zh-TW': '繁體中文', 'zh-Hans': '简体中文',
              'ja': '日本語', 'ko': '한국어'}
MENU_ORDER = ['en', 'zh-TW', 'zh-Hans', 'ja', 'ko']
REPO_BLOB = 'https://github.com/asd880921/OverTranslate/blob/main/'
CARDS = 'https://raw.githubusercontent.com/asd880921/github-statcards/main/cards/'


# ---------------------------------------------------------------- 讀取翻譯

def read_locale(lang):
    """解析 tools/site-i18n/<lang>.js 裡的 otLocale(...) 字典。"""
    path = os.path.join(I18N_DIR, lang + '.js')
    text = io.open(path, encoding='utf-8').read()
    out = {}
    for m in re.finditer(r"^  '([^']+)': '(.*?)',?$", text, re.M):
        value = m.group(2)
        value = value.replace(chr(92) + "'", "'").replace(chr(92) * 2, chr(92))
        out[m.group(1)] = value
    if not out:
        raise SystemExit('%s 解析不到任何 key' % path)
    return out


def source_strings(html):
    """從正本抽出繁中文案，讓 zh-TW 也能走同一條產生流程。"""
    out = {}
    for m in re.finditer(r'data-i18n="([^"]+)"[^>]*>([^<]*)', html):
        out.setdefault(m.group(1), m.group(2))
    for tag in ('figcaption', 'span'):
        pat = r'<%s[^>]*data-i18n-html="([^"]+)"[^>]*>(.*?)</%s>' % (tag, tag)
        for m in re.finditer(pat, html, re.S):
            out.setdefault(m.group(1), m.group(2))
    for m in re.finditer(r'<[a-z]+[^>]*data-i18n-attr="[^"]+"[^>]*>', html):
        t = m.group(0)
        for pair in re.search(r'data-i18n-attr="([^"]+)"', t).group(1).split(';'):
            attr, key = [x.strip() for x in pair.split('|')]
            got = re.search(attr + r'="([^"]*)"', t)
            if got:
                out.setdefault(key, got.group(1))
    out.setdefault('meta.title', re.search(r'<title>([^<]*)</title>', html).group(1))
    out.setdefault('meta.description',
                   re.search(r'<meta name="description" content="([^"]*)"', html).group(1))
    return out


# ---------------------------------------------------------------- 套用翻譯

def apply_text(html, strings):
    # data-i18n：開始標籤之後的那段純文字
    def repl_leaf(m):
        key = m.group(2)
        if key not in strings:
            raise SystemExit('缺少翻譯：%s' % key)
        return m.group(1) + strings[key]
    html = re.sub(r'(data-i18n="([^"]+)"[^>]*>)[^<]*', repl_leaf, html)

    # data-i18n-html：內容含連結，整段換掉
    for tag in ('figcaption', 'span'):
        pat = re.compile(r'(<%s[^>]*data-i18n-html="([^"]+)"[^>]*>).*?(</%s>)' % (tag, tag), re.S)

        def repl_html(m):
            key = m.group(2)
            if key not in strings:
                raise SystemExit('缺少翻譯：%s' % key)
            return m.group(1) + strings[key] + m.group(3)
        html = pat.sub(repl_html, html)

    # data-i18n-attr：改寫同一個標籤內的屬性
    def repl_attr(m):
        tag = m.group(0)
        spec = re.search(r'data-i18n-attr="([^"]+)"', tag).group(1)
        for pair in spec.split(';'):
            attr, key = [x.strip() for x in pair.split('|')]
            if key not in strings:
                raise SystemExit('缺少翻譯：%s' % key)
            tag = re.sub(attr + r'="[^"]*"', lambda _: '%s="%s"' % (attr, strings[key]), tag, count=1)
        return tag
    html = re.sub(r'<[a-z]+[^>]*data-i18n-attr="[^"]+"[^>]*>', repl_attr, html)
    return html


def apply_assets(html, suffix, statcard, readme, ollama):
    def repl_img(m):
        tag = m.group(0)
        name = re.search(r'data-loc-img="([^"]+)"', tag).group(1)
        return re.sub(r'src="[^"]*"', 'src="images/%s%s.png"' % (name, suffix), tag, count=1)
    html = re.sub(r'<img[^>]*data-loc-img="[^"]+"[^>]*>', repl_img, html)

    def repl_src(m):
        return re.sub(r'src="[^"]*"', 'src="%s%s"' % (CARDS, statcard), m.group(0), count=1)
    html = re.sub(r'<img[^>]*data-loc-src="statcard"[^>]*>', repl_src, html)

    def repl_href(m):
        tag = m.group(0)
        kind = re.search(r'data-loc-href="([^"]+)"', tag).group(1)
        target = ollama if kind == 'ollama' else readme
        return re.sub(r'href="[^"]*"', 'href="%s%s"' % (REPO_BLOB, target), tag, count=1)
    return re.sub(r'<a[^>]*data-loc-href="[^"]+"[^>]*>', repl_href, html)


# ---------------------------------------------------------------- 頁面層級

def apply_head(html, lang, html_lang, canonical, og_url, og_image, is_root=False):
    # data-site-root 只留給根目錄那一份，語言頁不轉址
    root_attr = ' data-site-root' if is_root else ''
    html = re.sub(r'<html lang="[^"]*" data-lang="[^"]*"[^>]*>',
                  '<html lang="%s" data-lang="%s"%s>' % (html_lang, lang, root_attr),
                  html, count=1)
    html = re.sub(r'<link rel="canonical" href="[^"]*" />',
                  '<link rel="canonical" href="%s" />' % canonical, html, count=1)
    html = re.sub(r'<meta property="og:url" content="[^"]*" />',
                  '<meta property="og:url" content="%s" />' % og_url, html, count=1)
    html = re.sub(r'<meta property="og:image" content="[^"]*" />',
                  '<meta property="og:image" content="%s" />' % og_image, html, count=1)

    alternates = ''.join(
        '<link rel="alternate" hreflang="%s" href="%s%s/" />\n' % (hl, BASE_URL, code)
        for code, _, hl, _, _, _, _ in LANGS)
    alternates += '<link rel="alternate" hreflang="x-default" href="%s" />' % BASE_URL
    html = re.sub(r'<link rel="alternate" hreflang="zh-Hant".*?hreflang="x-default"[^>]*/>',
                  alternates, html, count=1, flags=re.S)
    return html


def apply_lang_links(html, depth, current):
    """語言切換改成實際連結：各語言一個目錄，不再靠 JS 換字。"""
    up = '../' * depth
    html_lang_of = dict((x[0], x[2]) for x in LANGS)

    def link(code):
        return up + code + '/'

    def mark(code):
        return ' aria-current="true"' if code == current else ''

    # 觸發按鈕要顯示這一頁的語言，不是正本的繁中
    html = re.sub(r'(<span data-lang-label>)[^<]*(</span>)',
                  lambda m: m.group(1) + LANG_LABEL[current] + m.group(2), html, count=1)

    menu = ''.join(
        '          <a role="menuitem" href="%s" data-set-lang="%s" lang="%s"%s>%s</a>\n'
        % (link(c), c, html_lang_of[c], mark(c), LANG_LABEL[c])
        for c in MENU_ORDER)
    html = re.sub(r'(<div class="menu__pop"[^>]*>\n).*?(\n        </div>)',
                  lambda m: m.group(1) + menu.rstrip('\n') + m.group(2), html, count=1, flags=re.S)

    foot = ''.join(
        '      <a href="%s" data-set-lang="%s" lang="%s"%s>%s</a>\n'
        % (link(c), c, html_lang_of[c], mark(c), LANG_LABEL[c])
        for c in MENU_ORDER)
    html = re.sub(r'(<span class="foot__langlabel"[^>]*>[^<]*</span>\n).*?(\n    </div>)',
                  lambda m: m.group(1) + foot.rstrip('\n') + m.group(2), html, count=1, flags=re.S)
    return html


def apply_prefix(html, depth):
    """子目錄的頁面要往上找共用資源。"""
    if not depth:
        return html
    up = '../' * depth
    for attr in ('href', 'src'):
        for folder in ('site/', 'images/'):
            html = html.replace('%s="%s' % (attr, folder), '%s="%s%s' % (attr, up, folder))
    return html


# ---------------------------------------------------------------- 主流程

def build():
    src = io.open(SOURCE, encoding='utf-8').read()
    zh = source_strings(src)
    pages = {}
    for lang, folder, html_lang, suffix, statcard, readme, ollama in LANGS:
        strings = zh if lang == 'zh-TW' else read_locale(lang)
        missing = [k for k in zh if k not in strings]
        if missing:
            raise SystemExit('%s 缺少 %d 個 key：%s' % (lang, len(missing), missing[:5]))

        page = apply_text(src, strings)
        page = apply_assets(page, suffix, statcard, readme, ollama)
        page = apply_head(page, lang, html_lang,
                          '%s%s/' % (BASE_URL, folder), '%s%s/' % (BASE_URL, folder),
                          '%simages/og/og%s.png' % (BASE_URL, suffix))
        page = apply_lang_links(page, 1, lang)
        page = apply_prefix(page, 1)
        page = re.sub(r'<title>[^<]*</title>',
                      '<title>%s</title>' % strings['meta.title'], page, count=1)
        page = re.sub(r'<meta name="description" content="[^"]*" />',
                      '<meta name="description" content="%s" />' % strings['meta.description'],
                      page, count=1)
        page = re.sub(r'<meta property="og:title" content="[^"]*" />',
                      '<meta property="og:title" content="%s" />' % strings['meta.title'],
                      page, count=1)
        pages[os.path.join(DOCS, folder, 'index.html')] = page

    # 根目錄仍是繁中內容，但 canonical 指向 /zh-TW/，避免兩個網址互搶
    root = apply_head(src, 'zh-TW', 'zh-Hant', BASE_URL + 'zh-TW/', BASE_URL,
                      BASE_URL + 'images/og/og.png', is_root=True)
    root = apply_lang_links(root, 0, 'zh-TW')
    pages[SOURCE] = root
    return pages


def main():
    check = '--check' in sys.argv
    pages = build()
    stale = []
    for path, content in sorted(pages.items()):
        old = io.open(path, encoding='utf-8').read() if os.path.exists(path) else None
        rel = os.path.relpath(path, ROOT).replace(os.sep, '/')
        if old == content:
            print('  未變動  %s' % rel)
            continue
        stale.append(rel)
        if check:
            print('  過期    %s' % rel)
            continue
        os.makedirs(os.path.dirname(path), exist_ok=True)
        io.open(path, 'w', encoding='utf-8', newline='\n').write(content)
        print('  已寫入  %s' % rel)

    if check and stale:
        print('\n以下頁面與 docs/index.html 不同步，請執行 python tools/build-site.py')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
