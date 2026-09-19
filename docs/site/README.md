# GitHub Pages 網站

`docs/index.html` 是 OverTranslate 的官方網站，由 GitHub Pages 直接從 `main` 分支的 `/docs` 資料夾發布，沒有任何建置步驟。本機預覽只要用瀏覽器開啟 `docs/index.html`，或在 `docs/` 執行 `python -m http.server` 即可。

## 檔案

| 檔案 | 說明 |
|------|------|
| `index.html` | 整個頁面。文字直接寫在 HTML 裡，那份文字就是**繁體中文的正本** |
| `site/styles.css` | 樣式，深色為預設，淺色由系統設定或右上角切換 |
| `site/app.js` | 互動：前後對照拖曳、模式切換、圖片放大、語言與主題切換 |
| `site/i18n/*.js` | 其餘四種語言的翻譯 |

網站直接引用 `docs/images/` 底下既有的圖片，不另外複製一份。

## 多語系怎麼運作

繁體中文沒有語言檔：頁面載入時會先把 HTML 裡的原文記下來當成 `zh-TW` 字典，切到其他語言時才載入 `site/i18n/<語言>.js` 覆寫。

語言的決定順序是：網址的 `?lang=`、上次選過的語言（localStorage）、瀏覽器語言，都沒有就用繁體中文。

## 改文字

- **只改繁體中文**：直接改 `index.html` 裡的文字。
- **改其他語言**：找到該元素的 `data-i18n` 名稱，改 `site/i18n/<語言>.js` 裡同名的項目。
- **新增一段文字**：在 HTML 元素上加 `data-i18n="區塊.名稱"`（含連結的用 `data-i18n-html`，alt 或 aria-label 用 `data-i18n-attr="alt|區塊.名稱"`），四個語言檔各補一行。漏掉的項目會自動退回繁體中文，不會變成空白。

## 會依語言換圖的截圖

`data-loc-img="設定頁"` 這類屬性會依語言換成 `設定頁_en.png`、`設定頁_jp.png`、`設定頁_ko.png`、`設定頁_zh-Hans.png`。要讓新的截圖跟著換語言，五種語言的檔案都要備齊，檔名後綴對應寫在 `site/app.js` 的 `LANGS`。

## 新增一種語言

1. 複製一份 `site/i18n/en.js` 改成新的語言代碼並翻譯。
2. 在 `site/app.js` 的 `LANGS` 加一筆（介面語言名稱、`lang` 屬性值、截圖後綴、統計圖卡、README 與 Ollama 教學的路徑）。
3. 在 `index.html` 的語言選單、頁尾語言列、`hreflang` 與 `sitemap.xml` 各加一行。
4. 若該語言需要不同字型，在 `site/styles.css` 的 `html[data-lang="..."]` 補上字型堆疊。
