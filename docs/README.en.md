<div align="center">
  <p>
    🌐
    <strong>English ✓</strong>
    &nbsp;｜&nbsp;
    <strong><a href="../README.md">繁體中文</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.zh-Hans.md">简体中文</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.ja.md">日本語</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.ko.md">한국어</a></strong>
  </p>

  <br/>

  <p>
    <a href="https://asd880921.github.io/OverTranslate/"><img src="images/ui/btn-site.en.svg" alt="Visit the website" /></a>
  </p>

  <h1>
    <img src="images/icon.svg" width="180" alt="OverTranslate Icon"/>
    <br/>
    OverTranslate
  </h1>
  <p>A Windows screen translator for everyday use, comics, video and games, with screenshot translation, real-time translation and more, showing the results right on the original screen.</p>

  <p>
    <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-MIT-22C55E?style=for-the-badge" alt="License MIT" />
  </p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe"><img src="images/ui/btn-setup.en.svg" alt="Download the Windows installer (recommended)" /></a>
    &nbsp;
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Portable.zip"><img src="images/ui/btn-portable.en.svg" alt="Download the portable version" /></a>
  </p>

  <p>
    <a href="https://github.com/asd880921/github-statcards"><img src="https://raw.githubusercontent.com/asd880921/github-statcards/main/cards/overtranslate-downloads-history.svg" alt="Total downloads and daily growth" /></a>
  </p>

</div>

---

## Translation Features

> 🌐 **OverTranslate has a multilingual interface. The screenshots in this README show it in Traditional Chinese or English; the interface language is switchable in the app.**

OverTranslate currently offers five translation features, so you can pick the one that suits what you are doing:

- **[Screenshot Translation](#screenshot-translation)** — select an area of the screen; the text is recognised and the translation is shown right on it
- **[Real-time Translation](#real-time-translation)** — select an area of a video, a game or a comic; the text is recognised continuously and the translation is shown right on it
- **[Quick Lookup](#quick-lookup)** — select some text and a compact translation popup opens; it can be pinned to stay on screen
- **[Quick Translate](#quick-translate)** — select some text and press the hotkey to translate it and replace it in place
- **[Text Translation](#text-translation)** — the full translation window, with text input, swapping languages and reading aloud

---

## Screenshot Translation

**The main window can be closed so the app just sits in the system tray**, there's no need to keep the window open all the time.  
When you need a translation, press the hotkey (default Ctrl + Alt + A) and select the area you want to translate.
> Works on web pages, PDFs, images, comics, videos, game interfaces, and any other screen where text can't be selected directly.

![Translation comparison](images/翻譯比對圖.png)

| Source text | Translation |
|------|----------|
| ![截圖翻譯1-前.png](images/截圖翻譯1-前.png) | ![截圖翻譯1-後.png](images/截圖翻譯1-後.png) |
| ![截圖翻譯-前.png](images/截圖翻譯-前.png) | ![截圖翻譯-後.png](images/截圖翻譯-後.png) |
| ![截圖翻譯2-前.png](images/截圖翻譯2-前.png) | ![截圖翻譯2-後.png](images/截圖翻譯2-後.png) |

> The comic in the comparison images comes from [こうしす！＠IT支線](https://atmarkit.itmedia.co.jp/ait/articles/2604/02/news010.html).

### Toolbar - Other Features

Once the selection is done, the toolbar offers:

- **Copy text:** copies the recognized text straight to the clipboard, no translation needed
- **Screenshot:** copies the selected area to the clipboard, and also saves an image file when "Save screenshots" is turned on in the settings
- **Annotate:** mark up the capture directly with the pen tools, then copy or save what you drew with Screenshot

![截圖翻譯-標記.png](images/截圖翻譯-標記.png)

---

## Real-time Translation

Ideal for **video subtitles, game screens and comics**, and other situations that need continuous translation. After selecting the area, the screen content is recognized continuously, and the translation updates automatically in the original position whenever the text changes.

There are two capture modes, **screen capture** and **window capture**:  
screen capture needs Windows 11 24H2 or later, window capture needs Windows 10 1903 or later.

![Real-time translation window preview](images/即時翻譯視窗預覽_en.png)

### Translation Block Modes (area selection)

> While real-time translation is running, use the hotkey (default Ctrl + Alt + S) to pause / resume translation,  
> When a screen doesn't need translating, or when you want to read the original text, pause first and resume later — there's no need to shut down real-time translation.

Translation blocks come with several modes and layouts:
#### Subtitles / Comics
For video subtitles, game story dialogue and comic lettering; use this mode when the content is writing meant to be read straight through.

| Selection | Translation result |
|-----------|--------------------|
| ![Real-time translation - comic source](images/即時翻譯-漫畫翻譯前.png) | ![Real-time translation - comic result](images/即時翻譯-漫畫翻譯後.png) |
| ![Real-time translation - video selection](images/即時翻譯-影片框.png) | ![Real-time translation - video result](images/即時翻譯-影片翻譯.png) |
| ![Real-time translation1 - dialogue game selection](images/即時翻譯1-對話遊戲框.png) | ![Real-time translation1 - dialogue game result](images/即時翻譯1-對話遊戲翻譯.png) |
| ![Real-time translation2 - dialogue game selection](images/即時翻譯2-對話遊戲框.png) | ![Real-time translation2 - dialogue game result](images/即時翻譯2-對話遊戲翻譯.png) |

> The four-panel comic in the comparison images is by [@kiyo_mariari](https://x.com/kiyo_mariari/status/1980247239948988588/photo/1).

#### Game / UI
For game screens, chat windows, menus and interface text; use this mode when the content is not subtitles, story dialogue or comic lettering.

![Real-time translation - chat comparison](images/即時翻譯-聊天室比對圖.png)

| Selection | Translation result |
|-----------|--------------------|
| ![Real-time translation - game selection](images/即時翻譯-遊戲翻譯框.png) | ![Real-time translation - game result](images/即時翻譯-遊戲翻譯.png) |

## Quick Lookup
> The hotkey (default `Ctrl + Alt + Q`) opens it on top of whatever is on screen.

Select some text and press the hotkey and it is picked up and translated straight away; with nothing selected, you can type the text in yourself.  
The window closes itself when you switch to another window; pin it if you need it to stay on screen.

![Quick lookup](images/選詞翻譯.png)

---

## Quick Translate
> The hotkey (default `Ctrl + Alt + E`); translating opens no window at all.

Select some text and press the hotkey: the translation is pasted straight over it (only in fields that accept typed text; nothing can be pasted outside an input area).

![Quick translate](images/快速翻譯.png)

---

## Text Translation

**Type text and it is translated right away**, and the source and target languages can be swapped in one click;  
built-in text to speech (TTS) reads both the original text and the translation aloud.

![Translation window preview](images/翻譯視窗預覽_en.png)

---

## Settings

![Settings page](images/設定頁_en.png)

| Setting | Description |
|---------|-------------|
| Interface language | Traditional Chinese / Simplified Chinese / English / Japanese / Korean, applied immediately (on first launch it follows your Windows display language) |
| Screenshot translation (hotkey) | Starts a selection and translates it (default `Ctrl + Alt + A`) |
| Open translation window (hotkey) | Brings up the main window on the page you left it on (default `Ctrl + Alt + W`); while real-time translation is running, it brings the floating bar to the front |
| Pause / resume (hotkey) | Pauses or resumes **real-time translation** (default `Ctrl + Alt + S`); while paused you can read the original text |
| Quick lookup (hotkey) | Opens the **quick lookup** window (default `Ctrl + Alt + Q`); any text you have selected is picked up automatically |
| Quick translate (hotkey) | Replaces the selected text with its translation (default `Ctrl + Alt + E`), and can use its own source and target language |
| Auto translate | Translates immediately once the screenshot area is selected, with nothing left to click (off by default) |
| Run at startup | Launch automatically when Windows starts |
| Save screenshots | Save captures automatically, to a folder of your choice (off by default) |
| Service setup | Set the key, endpoint, and model for DeepL and OpenAI; the other services need no setup |
| Theme | Light / Dark |
| Logging | Records more detailed information about what the app is doing; recommended only while troubleshooting (off by default) |
| Debug tools | Draws the OCR boxes and text group boxes on top of the **screenshot translation** result (off by default) |

> As well as key combinations, a shortcut can be a single key (F1 ~ F24, Pause, Scroll Lock), the middle or side mouse buttons, or a gamepad button.

> Logs are stored on your machine only and are never uploaded automatically; what **Record detailed information** adds also stays on your machine. To report a problem, press **Export diagnostics** and **Upload** on the settings page.

---

### Translation APIs

> Apart from DeepL and OpenAI, everything else works right after you download the app.

| Service | Description |
|---------|-------------|
| Google Translate (RPC) | Newer RPC interface |
| Google Translate (Web) | Traditional web interface |
| Bing Translator | Good translation quality |
| Microsoft Translator | **(default)** Stable and fast |
| DeepL | Requires registering on DeepL's site and obtaining an API key |
| OpenAI | Supports the OpenAI API format; a local LLM is recommended, which you can set up quickly with [Ollama](guides/OLLAMA_GUIDE.en.md) |
  
An "automatic fallback" mechanism is provided (it applies to both **screenshot translation** and **real-time translation**):
when a translation service is unavailable or responds too slowly, the app automatically switches to another available translation API, and the engine actually in use is shown in the toolbar.
![Fallback](images/備援.png)

> The fallback mechanism is not triggered when using **OpenAI**.

### OpenAI settings

![OpenAI settings](images/OpenAI.png)

| Setting | Description |
|---------|-------------|
| API URL | Empty uses `http://localhost:11434/v1` (Ollama's local default) |
| API Key | Can be left empty for a local server |
| Model settings | One setting = a model name, the advanced parameters, and a System / User prompt pair for each of **automatic** and **a chosen source language**. Switching settings switches all of it |
| Built-in | The read-only one, on the model the [Ollama guide](guides/OLLAMA_GUIDE.en.md) recommends, with wording that follows the interface language |
| Add | Up to five; the model name is required, and at least one of the System / User prompts has to be filled in |
| Advanced parameters | Under **Advanced** in the editor; each can be switched off on its own, and a parameter that is off is left out of the request |

Advanced parameters:

| Parameter | What it does | Range | Default |
|-----------|--------------|-------|---------|
| Temperature | How strongly the model favours the likeliest phrasing; the higher it is, the more chance other ways of saying something have | 0.0 ~ 2.0 | 0.7 |
| Top P | How many candidates the model may choose between; the lower it is, the fewer of the likeliest are kept | 0.0 ~ 1.0 | 0.6 |
| Seed | Makes a result easier to reproduce: with the same model, prompts and settings, the same seed usually gives the same output | Whole number | 42 |

Prompt parameters (the **Available parameters** block on the settings page lists them with descriptions and examples too):

| Parameter | Description | Example |
|-----------|-------------|---------|
| `{source_name}` | Source language name | English |
| `{source_code}` | Source language code | en |
| `{target_name}` | Target language name | Japanese |
| `{target_code}` | Target language code | ja |

> Language names follow the interface language (an English interface fills in "Japanese", a 繁體中文 one fills in "日文"); the codes are always the tag the model uses.

### Multi-language OCR

OCR is handled by RapidOcrNet with ONNX models. The matching model is selected automatically per language — there's no need to pick an OCR engine manually.

| Language | Recognition model (rec) |
|----------|-------------------------|
| **English / Chinese (Simplified & Traditional) / Japanese** | PP-OCRv6 general recognition model (`PP-OCRv6_small_rec`), a single model covering Chinese, English, Japanese and many Latin-script languages, including common mixed Chinese-English layouts |
| **Korean** | PP-OCRv5 Korean recognition model (`korean_PP-OCRv5_rec`), covering the Korean characters the general PP-OCRv6 model doesn't include |

All languages share the same **text detection model** (`PP-OCRv6_det_tiny`) and **orientation classification model** (`cls`); only the recognition model is switched per language.
OCR runs entirely on your local CPU, and images are never uploaded to any external service.

---

## System Requirements

- **Operating system**: Windows 10 / 11
- **Runtime**: the installer already bundles everything needed — no separate .NET Runtime installation required

The following translation services require some extra setup:

- **DeepL**: apply for an API key on the [DeepL website](https://www.deepl.com/pro-api)
- **OpenAI**: requires your own OpenAI-compatible API service; for setting up a local LLM, see the [Ollama guide](guides/OLLAMA_GUIDE.en.md)

---

## ☕ Support the Project

OverTranslate is a free Windows translation tool.  
If this project helps you, feel free to buy me a coffee on [Buy Me a Coffee](https://buymeacoffee.com/hon.lu) and support its continued development and maintenance.

---

## License

This project is licensed under the [MIT License](https://opensource.org/license/mit).
You may freely use, modify, and distribute this software, including for commercial purposes; the main requirement is that the original copyright notice and the MIT License text be retained in all copies or distributions.
See [LICENSE](../LICENSE) for the full license text.
