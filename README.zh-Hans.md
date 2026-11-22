<div align="center">
  <p>
    🌐
    <strong><a href="README.md">English</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.zh-Hant.md">繁體中文</a></strong>
    &nbsp;｜&nbsp;
    <strong>简体中文 ✓</strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.ja.md">日本語</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.ko.md">한국어</a></strong>
  </p>

  <br/>

  <p>
    <a href="https://hon-lu.github.io/OverTranslate/"><img src="docs/images/ui/btn-site.zh-Hans.svg" alt="前往官方网站" /></a>
  </p>

  <h1>
    <img src="docs/images/icon.svg" width="180" alt="OverTranslate Icon"/>
    <br/>
    OverTranslate
  </h1>
  <p>一款适合游戏、漫画、影音与日常使用的 Windows 屏幕翻译工具，支持截图翻译、实时翻译等功能，译文可直接覆盖在原文上。</p>

  <p>
    <img src="https://img.shields.io/github/v/release/Hon-Lu/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-MIT-22C55E?style=for-the-badge" alt="License MIT" />
    <img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/Hon-Lu/github-statcards/main/badges/overtranslate-downloads.json" alt="Total downloads" />
  </p>

  <p>
    <a href="https://github.com/Hon-Lu/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe"><img src="docs/images/ui/btn-setup.zh-Hans.svg" alt="下载 Windows 安装版（推荐）" /></a>
    &nbsp;
    <a href="https://github.com/Hon-Lu/OverTranslate/releases/latest/download/OverTranslate-win-Portable.zip"><img src="docs/images/ui/btn-portable.zh-Hans.svg" alt="下载免安装版（Portable）" /></a>
  </p>

  <p>
    <img src="docs/images/readme-hero.png" alt="翻译前后对照动画：译文直接覆盖在原文上" />
    <br/>
    <sub>画面来源：<a href="https://godotengine.org/blog/">Godot Engine Blog</a></sub>
  </p>

</div>

---

## 翻译功能

OverTranslate 目前提供了五种翻译功能，可依不同使用场景快速选择：

- **[截图翻译](#截图翻译)** — 框选画面内容，识别并将译文直接显示在原画面
- **[实时翻译](#实时翻译)** — 框选视频、游戏或漫画的内容，持续识别并将译文显示在原画面
- **[取词翻译](#取词翻译)** — 选取文字后打开精简翻译小窗口，也可固定常驻使用
- **[替换翻译](#替换翻译)** — 选取文字后按下快捷键，直接翻译并替换原文
- **[文字翻译](#文字翻译)** — 使用完整翻译窗口，支持文字输入、语言互换与文字朗读

> 🌐 **OverTranslate 支持多语言界面。README 中的应用界面截图统一以繁体中文显示，实际界面可切换为其他语言。**

---

## 截图翻译

**程序可以直接关闭主窗口并常驻在系统托盘**，平常不需要一直把窗口开着，  
需要翻译时按下快捷键（默认 Ctrl + Alt + A），框选想翻译的画面即可。
> 适用于网页、PDF、图片、漫画、影音、游戏界面等各种无法直接选取文字的画面。

![翻译比对图.png](docs/images/翻譯比對圖.png)

| 原文 | 翻译结果 |
|------|----------|
| ![截图翻译0-前.png](docs/images/截圖翻譯0-前.png) | ![截图翻译0-后.png](docs/images/截圖翻譯0-後.png) |
| ![截图翻译1-前.png](docs/images/截圖翻譯1-前.png) | ![截图翻译1-后.png](docs/images/截圖翻譯1-後.png) |
| ![截图翻译2-前.png](docs/images/截圖翻譯2-前.png) | ![截图翻译2-后.png](docs/images/截圖翻譯2-後.png) |

> 以上翻译对比均为 OverTranslate 实机运行结果。

> 图片来源：第一组漫画原图为 AI 生成；第二组漫画取自 [こうしす！＠IT支線](https://atmarkit.itmedia.co.jp/ait/articles/2604/02/news010.html)；第三组取自 [Closers 官网](https://www.naddic.co.kr/ko/cls/index)。

### 工具栏 - 其他功能

框选完成后，可通过工具栏使用以下功能：

- **复制文字**：直接将识别出的文字复制到剪贴板，无需进行翻译
- **截图**：将框选画面复制到剪贴板，也可在设置中开启「保存截图」，同步保存图片文件
- **标记**：可使用画笔、文字等工具直接在截图上标记，并通过截图功能复制或保存画面内容
- **设置**：自动翻译、截图后自动关闭、保存截图等设置

![截圖翻譯-標記.png](docs/images/截圖翻譯-標記.png)

---

## 实时翻译

适合 **视频字幕、游戏画面、漫画阅读** 等需要持续翻译的场景。框选需要翻译的区域后，会持续识别画面内容，  
文字变化时自动更新译文并显示在原本的位置。

画面来源分为 **屏幕捕获** 与 **窗口捕获** 两种模式：  
屏幕捕获需 Windows 11 24H2 以上，窗口捕获需 Windows 10 1903 以上。

![实时翻译功能预览.png](docs/images/即時翻譯功能預覽_zh-Hans.png)

### 翻译区块（框选）

> 实时翻译快捷键（默认 Ctrl + Alt + S）：未启动时按下开始框选，框选时再按一次开始翻译；翻译进行中可暂停 / 继续，  
> 遇到不需要翻译的画面、或想直接看原文时可先暂停，之后再恢复，不必关闭实时翻译。

以下按画面类型展示：
#### 字幕・漫画
影音字幕、游戏剧情对话、漫画文字等连续阅读的内容。

| 框选 | 翻译结果 |
|------|----------|
| ![实时翻译-漫画框.png](docs/images/即時翻譯-漫畫框.png) | ![实时翻译-漫画翻译.png](docs/images/即時翻譯-漫畫翻譯.png) |
| ![实时翻译1-漫画框.png](docs/images/即時翻譯1-漫畫框.png) | ![实时翻译1-漫画翻译.png](docs/images/即時翻譯1-漫畫翻譯.png) |
| ![实时翻译1-对话游戏框.png](docs/images/即時翻譯1-對話遊戲框.png) | ![实时翻译1-对话游戏翻译.png](docs/images/即時翻譯1-對話遊戲翻譯.png) |
| ![实时翻译2-对话游戏框.png](docs/images/即時翻譯2-對話遊戲框.png) | ![实时翻译2-对话游戏翻译.png](docs/images/即時翻譯2-對話遊戲翻譯.png) |

> 以上翻译对比均为 OverTranslate 实机运行结果。

> 图片来源：第一组漫画原图为 AI 生成；第二组四格漫画原作出自 [@kiyo_mariari](https://x.com/kiyo_mariari/status/2101991547877429535?s=20)；第三组游戏画面来源：[Hades](https://store.steampowered.com/app/1145360/)；第四组游戏画面来源：[《魔法少女的魔女审判》](https://store.steampowered.com/app/3101040)。

#### 游戏・界面
游戏画面、聊天室、菜单与界面文字。

![实时翻译-聊天室对比图.png](docs/images/即時翻譯-聊天室比對圖.png)

| 框选 | 翻译结果 |
|------|----------|
| ![实时翻译1-游戏翻译框.png](docs/images/即時翻譯1-遊戲翻譯框.png) | ![实时翻译1-游戏翻译.png](docs/images/即時翻譯1-遊戲翻譯.png) |
| ![实时翻译-游戏翻译框.png](docs/images/即時翻譯-遊戲翻譯框.png) | ![实时翻译-游戏翻译.png](docs/images/即時翻譯-遊戲翻譯.png) |

> 图片来源：聊天室游戏画面来源：[Phantasy Star Online 2 New Genesis](https://store.steampowered.com/app/1056640/Phantasy_Star_Online_2_New_Genesis/)；菜单游戏画面来源：[FINAL FANTASY IV Complete Collection](https://game.watch.impress.co.jp/docs/news/417727.html)。

### 原文对照

通过浮动窗口栏的「原文对照」功能，可保留原始画面，并将译文显示在独立的对照框中，方便比对原文与翻译结果。
> 对照框可自由拖动、缩放或单独关闭。

| 框选 | 翻译结果 |
|------|----------|
| ![实时翻译-原文对照0-前.png](docs/images/即時翻譯-原文對照0-前.png) | ![实时翻译-原文对照0-后.png](docs/images/即時翻譯-原文對照0-後.png) |

> 视频来源：[millsage「everscape」](https://www.youtube.com/watch?v=nPv1dBYawZQ)

---

## 取词翻译
> 快捷键（默认 `Ctrl + C + C`，按住 Ctrl 连按两下 C）可在任何画面上直接打开。

选取文字后按下快捷键，会自动带入并翻译；未选取文字时，也可以直接输入内容。  
切换到其他窗口时会自动关闭；若需要持续显示，可将窗口固定。

![取词翻译.png](docs/images/選詞翻譯.png)

---

## 替换翻译
> 快捷键（默认 `Ctrl + Alt + E`），使用翻译时不会打开任何窗口。

选取文字后按下快捷键，翻译结果会直接粘贴替换原文（仅适用于可输入文字的字段，非输入区域则无法粘贴）。

![替换翻译.png](docs/images/快速翻譯.png)

---

## 文字翻译

**输入文字后即可翻译**，源语言与目标语言可快速互换；  
内置文字转语音（TTS），支持朗读原文与翻译结果。

![翻译窗口预览.png](docs/images/翻譯視窗預覽_zh-Hans.png)

---

## 设置

![设置页.png](docs/images/設定頁_zh-Hans.png)

- **界面语言**：繁體中文 / 简体中文 / English / 日本語 / 한국어 / Русский，首次启动时依 Windows 显示语言决定
- **快捷键**：每个功能都可自定义，支持组合键、单个按键（F1 ~ F24、Pause、Scroll Lock）、连按两下（Ctrl + Ctrl、Ctrl + C + C）、鼠标中键／侧键与游戏手柄按键；另有 **打开翻译窗口**（默认 `Ctrl + Alt + W`）可随时呼出主窗口
- **识别模型**：管理需要另外下载的 [识别语言](#识别语言ocr) 与漫画竖排模型
- **翻译服务**：选择要使用的翻译服务；DeepL、AI 翻译 (本地) 与 OpenAI 也可在此进行设置
- **模型存放位置**：下载的识别模型与 AI 模型可改存到其他磁盘，位于 **高级设置**

> Log 仅保存在本机，不会自动上传；开启 **记录详细信息** 后的内容同样只会保留在本机。反馈问题时，可在设置页按下 **导出诊断信息** 与 **上传**。

---

### 翻译 API

**一般服务**：安装后即可使用，不需注册或申请密钥。

- Google 翻译 (标准)
- Google 翻译 (Beta)
- Bing 翻译
- Microsoft 翻译 **（默认）**
- 有道翻译
- 腾讯翻译
- Yandex 翻译

**可自定义服务**：需自备密钥、服务地址，或下载模型（见下方「系统需求」）。

- DeepL
- AI 翻译 (本地)
- OpenAI
  
提供「自动备用」机制（适用于 **截图翻译**、**实时翻译**、**取词翻译** 与 **替换翻译**）：  
翻译失败或响应过慢时，会先用同一个服务重试，仍然无法翻译才切换到其他可用的翻译服务。实际使用的引擎显示于工具栏。
![备用.png](docs/images/備援.png)

> 使用 **DeepL** 与 AI 相关翻译服务时，不会触发备用机制。

### AI 翻译

使用 AI 模型翻译，会参考之前的翻译内容，让连续字幕与对话中的人名、用词及语气更加一致。

![AI翻译.png](docs/images/AI翻譯_zh-Hans.png)

- **AI 翻译 (本地)**：翻译功能内置于 OverTranslate，在独立显卡上运行，首次使用时下载模型即可，无需安装其他工具或申请密钥，翻译过程不经过网络。可按需选择专用翻译模型或通用语言模型，也能指定翻译风格。
- **OpenAI**：可连接 OpenAI 兼容 API，使用 Ollama、LM Studio 等本地工具，或 OpenRouter 等云端服务，并自行选择模型与设置提示词；本地搭建方式可参考 [本地 LLM 搭建教程](docs/guides/OLLAMA_GUIDE.zh-Hans.md)。
- **词汇表**：翻译时优先使用指定词汇，让专有名词保持一致。

### 识别语言（OCR）

OCR 全程在本机运行，不会上传图片。中文、英文、日文已内置；其他语言第一次选择时会询问是否下载模型，之后可在设置页的 **识别模型** 管理。

| 识别语言 | 模型 | 获取方式 |
|----------|------|----------|
| 中文（繁／简）、英文、日文 | `PP-OCRv6_small_rec` | 内置 |
| 韩文 | `korean_PP-OCRv5_rec_mobile` | 下载（约 14 MB） |
| 俄文、乌克兰文 | `cyrillic_PP-OCRv5_rec_mobile` | 下载（约 8 MB） |
| 日文竖排（漫画） | [comic-text-and-bubble-detector](https://huggingface.co/ogkalu/comic-text-and-bubble-detector)、[CRAFT](https://github.com/clovaai/CRAFT-pytorch)（MIT）、[manga-ocr-base-CPT-flex](https://huggingface.co/muscgab/manga-ocr-base-CPT-flex)（CC BY-NC-SA 4.0） | 下载（约 342 MB，需显卡） |

所有语言共用文字检测（`PP-OCRv6_det_tiny`）与方向分类（`cls`）模型，由 RapidOcrNet 以 ONNX 运行；漫画竖排模型使用显卡，其余使用 CPU。

---

## 系统需求

- **操作系统**：Windows 10 / 11
- **运行环境**：安装文件已内含所需环境，不需另外安装 .NET Runtime

使用以下翻译服务时，需另外准备：

- **DeepL**：需到 [DeepL 官网](https://www.deepl.com/pro-api) 申请 API Key
- **AI 翻译 (本地)**：需要独立显卡；模型（约 2 ~ 15 GB）与运行组件在设置页下载
- **OpenAI**：需自备 OpenAI API 兼容服务，Ollama、LM Studio 等本地 LLM 或 OpenRouter 等云端服务均可；本地 LLM 搭建方式可参考 [本地 LLM 搭建教程](docs/guides/OLLAMA_GUIDE.zh-Hans.md)

---

## ☕ 支持项目

OverTranslate 是免费提供的 Windows 翻译工具。  
如果这个项目对你有帮助，欢迎通过 [Buy Me a Coffee](https://buymeacoffee.com/hon.lu) 请我喝杯咖啡，支持后续开发与维护。

<p align="center">
  <a href="https://github.com/Hon-Lu/github-statcards"><img src="https://raw.githubusercontent.com/Hon-Lu/github-statcards/main/cards/overtranslate-downloads-history.svg" alt="累计下载次数与每日新增" /></a>
</p>

---

## 许可

本项目采用 [MIT License](https://opensource.org/license/mit) 许可。  
你可以自由使用、修改、分发本软件，也可用于商业用途；主要要求是在复制或分发时保留原始的版权声明与 MIT 许可条款。  
完整许可内容请参阅 [LICENSE](LICENSE)。
