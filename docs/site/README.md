# GitHub Pages 網站

OverTranslate 的官方網站，由 GitHub Pages 從 `main` 分支的 `/docs` 資料夾直接發布。

## 網址與檔案

| 網址 | 檔案 | 說明 |
|------|------|------|
| `/` | `docs/index.html` | **繁體中文正本**，所有語言都從這份產生。非繁中的訪客會被轉到對應語言 |
| `/zh-TW/` | `docs/zh-TW/index.html` | 繁中的正式網址，`/` 的 canonical 指向這裡 |
| `/en/` `/zh-Hans/` `/ja/` `/ko/` | 各自的 `index.html` | 產生的檔案 |

共用資源：`docs/site/styles.css`、`docs/site/app.js`、`docs/images/`。網站直接引用既有的截圖，不另外複製一份。

## 改完內容一定要重新產生

```bash
python tools/build-site.py
```

**`docs/index.html` 以外的 `index.html` 都是產生出來的，不要手動編輯**，下次執行產生器就會被覆蓋。忘了跑的話 GitHub Actions（`.github/workflows/site.yml`）會在 push 時擋下來。

本機預覽：直接用瀏覽器開 `docs/index.html`，或在 `docs/` 執行 `python -m http.server`。

## 為什麼要產生而不是用 JS 換字

以前是載入後才用 JS 把文字換成對應語言，但這樣每個網址的**原始碼都是繁中**。搜尋引擎不保證會等 JS 跑完，結果各語言在搜尋結果裡都顯示中文標題。現在每個語言都有實體 HTML，原始碼就是該語言，任何爬蟲都不必執行 JS。

## 社群分享縮圖

```bash
python tools/build-og.py
```

`docs/images/og/og*.png`（1200x630）是分享連結時各平台抓的那張圖，由 `tools/build-og.py`
用 Edge 無頭渲染出來，版面沿用首頁但為 1.91:1 重排過。標語和「原文／譯文」的字是直接從
各語言的 `index.html` 撈的，不另外維護一份。

**改了 `hero.claim`、`hero.free` 或 `compare.before/after` 之後要重跑**，否則縮圖上的字
會停在舊版。這支需要本機有 Edge，CI 不會跑它，圖是 commit 進版控的。

## 翻譯放哪裡

| 內容 | 位置 |
|------|------|
| 繁體中文 | `docs/index.html` 裡的文字本身 |
| 其他四種語言 | `tools/site-i18n/{en,zh-Hans,ja,ko}.js` |

這四個檔案只是建置的輸入，不會被瀏覽器載入，所以放在 `tools/` 而不是 `docs/`。

- **改繁中**：直接改 `docs/index.html` 的文字
- **改其他語言**：找到該元素的 `data-i18n` 名稱，改對應語言檔裡同名的項目
- **新增一段文字**：在元素上加 `data-i18n="區塊.名稱"`（含連結的用 `data-i18n-html`，alt 或 aria-label 用 `data-i18n-attr="alt|區塊.名稱"`），四個語言檔各補一行。**少一個 key 產生器就會停下來報錯**，不會默默產生半成品。

產生器還會檢查每個字串裡的 `href` 是否與正本一致——譯文改動連結是最容易發生又最難發現的錯誤。

## 會依語言換掉的東西

除了文字以外：

- `data-loc-img="設定頁"` → `設定頁_en.png`、`設定頁_jp.png`、`設定頁_ko.png`、`設定頁_zh-Hans.png`。要讓新截圖跟著換語言，五種語言的檔案都要備齊，後綴對應寫在 `tools/build-site.py` 的 `LANGS`
- `data-loc-href="readme"` / `"ollama"` → 對應語言的 README 與 Ollama 教學
- `data-loc-src="statcard"` → 下載統計圖卡
- 字型堆疊：`docs/site/styles.css` 裡的 `html[data-lang="ja"]` 之類

## 語言怎麼決定

`/` 的 `<head>` 會在畫面繪製前判斷，順序是：**上次主動選過的語言（localStorage）→ 瀏覽器語言 → 英文**，判斷完就轉到對應目錄。繁中不轉，直接顯示。

`/ja/` 這類語言頁進來就是那個語言，不會再自作主張轉址。使用者從選單點過語言會記在 localStorage，之後回到 `/` 就直接送去同一個語言。

## 新增一種語言

1. 複製 `tools/site-i18n/en.js` 改成新的語言代碼並翻譯
2. 在 `tools/build-site.py` 的 `LANGS` 與 `LANG_LABEL`、`MENU_ORDER` 各加一筆
3. `docs/sitemap.xml` 補上新的 `<url>` 與所有 `hreflang`
4. 若需要不同字型，在 `docs/site/styles.css` 補 `html[data-lang="..."]`
5. 在 `tools/build-og.py` 的 `LANGS` 加一筆，縮圖才會有那個語言的版本
6. 執行 `python tools/build-site.py` 與 `python tools/build-og.py`
