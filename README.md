<div align="center">
  <p>
    🌐
    <strong><a href="docs/README.en.md">English</a></strong>
    &nbsp;｜&nbsp;
    <strong>繁體中文 ✓</strong>
    &nbsp;｜&nbsp;
    <strong><a href="docs/README.zh-Hans.md">简体中文</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="docs/README.ja.md">日本語</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="docs/README.ko.md">한국어</a></strong>
  </p>

  <br/>

  <p>
    <a href="https://asd880921.github.io/OverTranslate/"><img src="docs/images/ui/btn-site.svg" alt="前往官方網站" /></a>
  </p>

  <h1>
    <img src="docs/images/icon.svg" width="180" alt="OverTranslate Icon"/>
    <br/>
    OverTranslate
  </h1>
  <p>一款適合日常使用、漫畫、影音與遊戲的 Windows 螢幕翻譯工具，支援截圖翻譯、即時翻譯等多種翻譯功能，翻譯結果可直接顯示在原畫面上。</p>

  <p>
    <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-MIT-22C55E?style=for-the-badge" alt="License MIT" />
  </p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe"><img src="docs/images/ui/btn-setup.svg" alt="下載 Windows 安裝版（推薦）" /></a>
    &nbsp;
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Portable.zip"><img src="docs/images/ui/btn-portable.svg" alt="下載免安裝版（Portable）" /></a>
  </p>

  <p>
    <a href="https://github.com/asd880921/github-statcards"><img src="https://raw.githubusercontent.com/asd880921/github-statcards/main/cards/overtranslate-downloads-history.zh-TW.svg" alt="累積下載次數與每日新增" /></a>
  </p>

</div>

---

## 翻譯功能

> 🌐 **OverTranslate 支援多國語言介面。README 中的應用程式圖片統一以繁體中文顯示，實際介面可切換不同語言。**

OverTranslate 目前提供了五種翻譯功能，可依不同使用情境快速選擇：

- **[截圖翻譯](#截圖翻譯)** — 框選畫面內容，辨識並將譯文直接顯示在原畫面
- **[即時翻譯](#即時翻譯)** — 框選影片、遊戲或漫畫的內容，持續辨識並將譯文顯示在原畫面
- **[取詞翻譯](#取詞翻譯)** — 選取文字後開啟精簡翻譯小視窗，也可釘選常駐使用
- **[快速翻譯](#快速翻譯)** — 選取文字後按下快捷鍵，直接翻譯並取代原文
- **[文字翻譯](#文字翻譯)** — 使用完整翻譯視窗，支援文字輸入、語言互換與文字朗讀

---

## 截圖翻譯

**程式可以直接關閉主視窗並常駐在系統匣**，平常不需要一直把視窗開著，  
需要翻譯時按下快捷鍵（預設 Ctrl + Alt + A），框選想翻譯的畫面即可。
> 適用於網頁、PDF、圖片、漫畫、影音、遊戲介面等各種無法直接選取文字的畫面。

![翻譯比對圖.png](docs/images/翻譯比對圖.png)

| 原文 | 翻譯結果 |
|------|----------|
| ![截圖翻譯1-前.png](docs/images/截圖翻譯1-前.png) | ![截圖翻譯1-後.png](docs/images/截圖翻譯1-後.png) |
| ![截圖翻譯-前.png](docs/images/截圖翻譯-前.png) | ![截圖翻譯-後.png](docs/images/截圖翻譯-後.png) |
| ![截圖翻譯2-前.png](docs/images/截圖翻譯2-前.png) | ![截圖翻譯2-後.png](docs/images/截圖翻譯2-後.png) |

> 比對圖中的四格漫畫原作出自 [@kiyo_mariari](https://x.com/kiyo_mariari/status/1980247239948988588/photo/1)。

### 工具列 - 其他功能

框選完成後，可透過工具列使用以下功能：

- **複製文字**：直接將辨識出的文字複製到剪貼簿，無需進行翻譯
- **截圖**：將框選畫面複製到剪貼簿，也可在設定中開啟「儲存截圖」，同步保存圖片檔案
- **標記**：可使用畫筆工具直接在截圖上進行標記，並透過截圖功能複製或儲存畫面內容

![截圖翻譯-標記.png](docs/images/截圖翻譯-標記.png)

---

## 即時翻譯

適合 **影片字幕、遊戲畫面、漫畫閱讀** 等需要持續翻譯的情境。框選需要翻譯的區域後，會持續辨識畫面內容，  
文字變動時自動更新譯文並顯示在原本的位置。

畫面來源分為 **螢幕擷取** 與 **視窗擷取** 兩種模式：  
螢幕擷取需 Windows 11 24H2 以上，視窗擷取需 Windows 10 1903 以上。

![即時翻譯視窗預覽.png](docs/images/即時翻譯視窗預覽.png)

### 翻譯區塊模式 (框選)

> 即時翻譯進行中可使用快捷鍵（預設 Ctrl + Alt + S）暫停 / 繼續翻譯，  
> 遇到不需要翻譯的畫面、或想直接看原文時可先暫停，之後再恢復，不必關閉即時翻譯。

翻譯區塊提供多種模式與排列方式：
#### 字幕 / 漫畫：
適合影音字幕、遊戲劇情對話、漫畫文字等情境；若內容以連續閱讀的文字為主，建議使用此模式。

| 框選 | 翻譯結果 |
|------|----------|
| ![即時翻譯-漫畫翻譯前.png](docs/images/即時翻譯-漫畫翻譯前.png) | ![即時翻譯-漫畫翻譯後.png](docs/images/即時翻譯-漫畫翻譯後.png) |
| ![即時翻譯-影片框.png](docs/images/即時翻譯-影片框.png) | ![即時翻譯-影片翻譯.png](docs/images/即時翻譯-影片翻譯.png) |
| ![即時翻譯1-對話遊戲框.png](docs/images/即時翻譯1-對話遊戲框.png) | ![即時翻譯1-對話遊戲翻譯.png](docs/images/即時翻譯1-對話遊戲翻譯.png) |
| ![即時翻譯2-對話遊戲框.png](docs/images/即時翻譯2-對話遊戲框.png) | ![即時翻譯2-對話遊戲翻譯.png](docs/images/即時翻譯2-對話遊戲翻譯.png) |

> 比對圖中的漫畫內容取自《原神》官方漫畫 [第五話－「危険地帯」](https://genshin.hoyoverse.com/ja/manga/detail/104872?mute=1)。

#### 遊戲 / 介面：
適合遊戲畫面、聊天室、選單與介面文字等情境；若內容不是字幕、劇情對話或漫畫文字，建議使用此模式。

![即時翻譯-聊天室比對圖.png](docs/images/即時翻譯-聊天室比對圖.png)

| 框選 | 翻譯結果 |
|------|----------|
| ![即時翻譯-遊戲翻譯框.png](docs/images/即時翻譯-遊戲翻譯框.png) | ![即時翻譯-遊戲翻譯.png](docs/images/即時翻譯-遊戲翻譯.png) |

---

## 取詞翻譯
> 快捷鍵（預設 `Ctrl + Alt + Q`）可在任何畫面上直接開啟。

選取文字後按下快捷鍵，會自動帶入並翻譯；未選取文字時，也可以直接輸入內容。  
切換至其他視窗時會自動關閉；若需要持續顯示，可將視窗釘選。

![選詞翻譯.png](docs/images/選詞翻譯.png)

---

## 快速翻譯
> 快捷鍵（預設 `Ctrl + Alt + E`），使用翻譯時不會開啟任何視窗。

選取文字後按下快捷鍵，翻譯結果會直接貼上取代原文 (僅適用於可輸入文字的欄位，非輸入區域則無法貼上)。

![快速翻譯.png](docs/images/快速翻譯.png)

---

## 文字翻譯

**輸入文字後即可翻譯**，來源與目標語言可快速互換；  
內建文字轉語音（TTS），支援朗讀原文與翻譯結果。

![翻譯視窗預覽.png](docs/images/翻譯視窗預覽.png)

---

## 設定

![設定頁.png](docs/images/設定頁.png)

| 設定項目 | 說明 |
|----------|------|
| 介面語言 | 繁體中文 / 简体中文 / English / 日本語 / 한국어，切換後立即生效（首次啟動時依 Windows 顯示語言決定） |
| 截圖翻譯 (快捷鍵) | 開始框選並翻譯（預設 `Ctrl + Alt + A`） |
| 開啟翻譯視窗 (快捷鍵) | 呼叫主視窗並回到上次開啟的頁面（預設 `Ctrl + Alt + W`）；即時翻譯進行中改為把浮動工具列移到最上層 |
| 暫停 / 繼續 (快捷鍵) | 暫停或繼續 **即時翻譯**（預設 `Ctrl + Alt + S`），暫停時可以看原文 |
| 取詞翻譯 (快捷鍵) | 開啟 **取詞翻譯** 小視窗（預設 `Ctrl + Alt + Q`），已選取的文字會自動帶入 |
| 快速翻譯 (快捷鍵) | 把選取的文字直接換成譯文（預設 `Ctrl + Alt + E`），可另外設定自己的來源與目標語言 |
| 自動翻譯 | 截圖框選完成後立即翻譯，不需再手動點擊（預設關閉） |
| 開機啟動 | 開機時自動啟動 |
| 儲存截圖 | 截圖時自動存檔，可自訂儲存位置（預設關閉） |
| 翻譯服務設定 | 設定 DeepL 與 OpenAI 的金鑰、位址與模型，其餘服務不需設定 |
| 主題 | 淺色 / 深色 |
| 應用紀錄 | 記錄更詳細的執行資訊，建議只在排查問題時開啟（預設關閉） |
| 偵錯工具 | 在 **截圖翻譯** 的結果上疊出 OCR 辨識框與文字組合框（預設關閉） |

> 快捷鍵可以是組合鍵，也可以是單一按鍵（F1 ~ F24、Pause、Scroll Lock）、滑鼠中鍵／側鍵或遊戲手把按鍵。

> Log 僅儲存在本機，不會自動上傳；開啟 **記錄詳細資訊** 後的內容也同樣只會保留在本機。回報問題時，可於設定頁按下 **匯出診斷資訊** 與 **上傳**。

---

### 翻譯 API

> 除了 DeepL 與 OpenAI 以外，其他都是下載後就可以直接使用的功能。

| 服務 | 說明 |
|------|------|
| Google 翻譯（RPC） | 新版 RPC 介面 |
| Google 翻譯（Web） | 傳統 Web 介面 |
| Bing 翻譯 | 翻譯品質佳 |
| Microsoft 翻譯 | **(預設)** 穩定性佳、回應速度快 |
| DeepL | 需至 DeepL 官方註冊並取得 API Key |
| OpenAI | 支援 OpenAI API 格式，建議使用本地 LLM，可透過 [Ollama](docs/guides/OLLAMA_GUIDE.md) 快速安裝與使用 |
  
提供「自動備援」機制（備援機制適用於 **截圖翻譯** 與 **即時翻譯**）：  
當某個翻譯無法使用或回應過慢時，會自動切換到其他可用的翻譯 API，實際使用的引擎顯示於工具列。
![備援.png](docs/images/備援.png)

> 使用 **OpenAI** 時，不會觸發備援機制。

### OpenAI 設定

![OpenAI.png](docs/images/OpenAI.png)

| 項目 | 說明 |
|------|------|
| API 位址 | 留空時使用 `http://localhost:11434/v1`（Ollama 的本機預設位址） |
| API Key | 本機執行可留空 |
| 模型設定 | 一份設定 = 模型名稱 + 進階參數 + **自動** 與 **指定語言** 各一組 System / User 提示詞，切換設定會整組一起換 |
| 系統預設 | 唯讀的那一份，使用 [Ollama 安裝教學](docs/guides/OLLAMA_GUIDE.md) 推薦的模型，提示詞會依介面語言切換 |
| 新增設定 | 最多 5 份；模型名稱為必填，System / User 提示詞至少要填寫一項 |
| 進階參數 | 位於編輯頁的 **進階** 區，三項都可個別關閉；關閉的參數不會傳送 |

進階參數：

| 參數 | 說明 | 範圍 | 預設 |
|------|------|------|------|
| Temperature | 模型對高機率結果的偏好程度，數值越高，其他可能的表達越有機會被選中 | 0.0 ~ 2.0 | 0.7 |
| Top P | 模型可選擇的候選範圍，數值越低，只保留機率較高的少數候選 | 0.0 ~ 1.0 | 0.6 |
| Seed | 讓生成結果更容易重現；模型、提示詞與其他設定相同時，相同 Seed 通常得到相同結果 | 整數 | 42 |

提示詞可用參數（設定頁的 **可用參數** 區塊也會列出說明與範例）：

| 參數 | 說明 | 範例 |
|------|------|------|
| `{source_name}` | 來源語言名稱 | 英文 |
| `{source_code}` | 來源語言代碼 | en |
| `{target_name}` | 目標語言名稱 | 日文 |
| `{target_code}` | 目標語言代碼 | ja |

> 語言名稱會跟著介面語言（繁體中文介面代入「日文」，English 介面代入「Japanese」）；語言代碼則固定是模型使用的代碼。

### 多語言 OCR 辨識

OCR 辨識由 RapidOcrNet 搭配 ONNX 模型處理，會依語言自動使用對應模型，不需手動選擇 OCR 引擎。

| 辨識語言 | 辨識模型（rec） |
|----------|----------------|
| **英文 / 中文（簡繁）/ 日文** | PP-OCRv6 通用辨識模型（`PP-OCRv6_small_rec`），單一模型支援中、英、日及多種拉丁語系，也能處理常見的中英混排 |
| **韓文** | PP-OCRv5 韓文辨識模型（`korean_PP-OCRv5_rec`），用於補足 PP-OCRv6 通用模型未涵蓋的韓文字元 |

所有語言共用同一套**文字偵測模型**（`PP-OCRv6_det_tiny`）與**方向分類模型**（`cls`），僅辨識模型會依語言切換。  
OCR 全程於本機 CPU 執行，不會將圖片上傳至外部服務。

---

## 系統需求

- **作業系統**：Windows 10 / 11
- **執行環境**：安裝檔已內含所需環境，不需另外安裝 .NET Runtime

使用以下翻譯服務時，需另外準備：

- **DeepL**：需至 [DeepL 官網](https://www.deepl.com/pro-api) 申請 API Key
- **OpenAI**：需自備 OpenAI API 相容服務，本地 LLM 架設使用方式可參考 [Ollama 安裝教學](docs/guides/OLLAMA_GUIDE.md)

---

## ☕ 支持專案

OverTranslate 是免費提供的 Windows 翻譯工具。  
如果這個專案對你有幫助，歡迎透過 [Buy Me a Coffee](https://buymeacoffee.com/hon.lu) 請我喝杯咖啡，支持後續開發與維護。

---

## 授權

本專案採用 [MIT License](https://opensource.org/license/mit) 授權。  
你可以自由使用、修改、散布本軟體，亦可用於商業用途；主要要求是在複製或散布時保留原始的版權聲明與 MIT 授權條款。  
完整授權內容請參閱 [LICENSE](LICENSE)。

