<div align="center">
  <p>
    🌐
    <strong><a href="README.en.md">English</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="../README.md">繁體中文</a></strong>
    &nbsp;｜&nbsp;
    <strong>简体中文 ✓</strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.ja.md">日本語</a></strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.ko.md">한국어</a></strong>
  </p>

  <h1>
    <img src="images/icon.svg" width="180" alt="OverTranslate Icon"/>
    <br/>
    OverTranslate
  </h1>
  <p>一款适合日常使用、漫画、影音与游戏的 Windows 屏幕翻译工具，支持截图翻译、实时翻译等多种翻译功能，翻译结果可直接显示在原画面上。</p>

  <p>
    <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-GPL--3.0-22C55E?style=for-the-badge" alt="License GPL-3.0" />
  </p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe"><img src="images/ui/btn-setup.zh-Hans.svg" alt="下载 Windows 安装版（推荐）" /></a>
    &nbsp;
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Portable.zip"><img src="images/ui/btn-portable.zh-Hans.svg" alt="下载免安装版（Portable）" /></a>
  </p>

  <p>
    <a href="https://github.com/asd880921/github-statcards"><img src="https://raw.githubusercontent.com/asd880921/github-statcards/main/cards/overtranslate-downloads-history.svg" alt="累计下载次数与每日新增" /></a>
  </p>

</div>

---

## 翻译功能
> 🌐 **OverTranslate 支持多语言界面。README 中的应用界面截图统一以繁体中文显示，实际界面可切换为其他语言。**

OverTranslate 目前提供了五种翻译功能，可依不同使用场景快速选择：

- **[截图翻译](#截图翻译)** — 框选画面内容，识别并将译文直接显示在原画面
- **[实时翻译](#实时翻译)** — 实时翻译视频 / 游戏画面指定区域，并将译文显示在原画面上
- **[取词翻译](#取词翻译)** — 选取文字后打开精简翻译小窗口，也可固定常驻使用
- **[快速翻译](#快速翻译)** — 选取文字后按下快捷键，直接翻译并替换原文
- **[文字翻译](#文字翻译)** — 使用完整翻译窗口，支持文字输入、语言互换与文字朗读

---

## 截图翻译

**程序可以直接关闭主窗口并常驻在系统托盘**，平常不需要一直把窗口开着，  
需要翻译时按下快捷键（默认 Ctrl + Alt + A），框选想翻译的画面即可。
> 适用于网页、PDF、图片、漫画、影音、游戏界面等各种无法直接选取文字的画面。

![翻译比对图.png](images/翻譯比對圖.png)

| 原文 | 翻译结果 |
|------|----------|
| ![截图翻译1-前.png](images/截圖翻譯1-前.png) | ![截图翻译1-后.png](images/截圖翻譯1-後.png) |
| ![截图翻译2-前.png](images/截圖翻譯2-前.png) | ![截图翻译2-后.png](images/截圖翻譯2-後.png) |
| ![截图翻译3-前.png](images/截圖翻譯3-前.png) | ![截图翻译3-后.png](images/截圖翻譯3-後.png) |

> 比对图中的四格漫画原作出自 [@kiyo_mariari](https://x.com/kiyo_mariari/status/1980247239948988588/photo/1)。

### 工具栏 - 其他功能

框选完成后，可通过工具栏使用以下功能：

- **复制文字**：直接将识别出的文字复制到剪贴板，无需进行翻译
- **截图**：将框选画面复制到剪贴板，也可在设置中开启「保存截图」，同步保存图片文件
- **标记**：可使用画笔工具直接在截图上进行标记，并通过截图功能复制或保存画面内容

![截圖翻譯-標記.png](images/截圖翻譯-標記.png)

---

## 实时翻译

适合 **视频字幕、游戏画面** 等需要持续翻译的场景。框选需要翻译的区域后，会持续识别画面内容，  
文字变化时自动更新译文并显示在原本的位置。

画面来源分为 **屏幕捕获** 与 **窗口捕获** 两种模式：  
屏幕捕获需 Windows 11 24H2 以上，窗口捕获需 Windows 10 1903 以上。

![实时翻译窗口预览.png](images/即時翻譯視窗預覽_zh-Hans.png)

### 翻译区块模式（框选）

> 实时翻译进行中可使用快捷键（默认 Ctrl + Alt + S）暂停 / 继续翻译，  
> 遇到不需要翻译的画面、或想直接看原文时可先暂停，之后再恢复，不必关闭实时翻译。

翻译区块分为两种模式：
**字幕 / 对话**：适合影音字幕、游戏剧情对话等情境（建议使用 1 个）

| 框选 | 翻译结果 |
|------|----------|
| ![实时翻译-视频框.png](images/即時翻譯-影片框.png) | ![实时翻译-视频翻译.png](images/即時翻譯-影片翻譯.png) |
| ![实时翻译1-对话游戏框.png](images/即時翻譯1-對話遊戲框.png) | ![实时翻译1-对话游戏翻译.png](images/即時翻譯1-對話遊戲翻譯.png) |
| ![实时翻译2-对话游戏框.png](images/即時翻譯2-對話遊戲框.png) | ![实时翻译2-对话游戏翻译.png](images/即時翻譯2-對話遊戲翻譯.png) |

**游戏 / 界面**：适合游戏画面、菜单与界面文字等情境；若不属于字幕或剧情对话，建议使用此模式（建议使用 1～2 个）

| 框选 | 翻译结果 |
|------|----------|
| ![实时翻译-游戏翻译框.png](images/即時翻譯-遊戲翻譯框.png) | ![实时翻译-游戏翻译.png](images/即時翻譯-遊戲翻譯.png) |

---

## 取词翻译
> 快捷键（默认 `Ctrl + Alt + Q`）可在任何画面上直接打开。

选取文字后按下快捷键，会自动带入并翻译；未选取文字时，也可以直接输入内容。  
切换到其他窗口时会自动关闭；若需要持续显示，可将窗口固定。

![取词翻译.png](images/選詞翻譯.png)

---

## 快速翻译
> 快捷键（默认 `Ctrl + Alt + E`），使用翻译时不会打开任何窗口。

选取文字后按下快捷键，翻译结果会直接粘贴替换原文（仅适用于可输入文字的字段，非输入区域则无法粘贴）。

![快速翻译.png](images/快速翻譯.png)

---

## 文字翻译

**输入文字后即可翻译**，源语言与目标语言可快速互换；  
内置文字转语音（TTS），支持朗读原文与翻译结果。

![翻译窗口预览.png](images/翻譯視窗預覽_zh-Hans.png)

---

## 设置

![设置页.png](images/設定頁_zh-Hans.png)

| 设置项 | 说明 |
|----------|------|
| 界面语言 | 繁體中文 / 简体中文 / English / 日本語 / 한국어，切换后立即生效（首次启动时依 Windows 显示语言决定） |
| 截图翻译（快捷键） | 开始框选并翻译（默认 `Ctrl + Alt + A`） |
| 打开翻译窗口（快捷键） | 呼出主窗口并回到上次打开的页面（默认 `Ctrl + Alt + W`）；实时翻译进行中时，改为把浮动工具栏移到最上层 |
| 暂停 / 继续（快捷键） | 暂停或继续 **实时翻译**（默认 `Ctrl + Alt + S`），暂停时可以查看原文 |
| 取词翻译（快捷键） | 打开 **取词翻译** 小窗口（默认 `Ctrl + Alt + Q`），已选中的文字会自动带入 |
| 快速翻译（快捷键） | 把选中的文字直接换成译文（默认 `Ctrl + Alt + E`），可另外设置自己的源语言与目标语言 |
| 自动翻译 | 截图框选完成后立即翻译，不需再手动点击（默认关闭） |
| 开机启动 | 开机时自动启动 |
| 保存截图 | 截图时自动保存，可自定义保存位置（默认关闭） |
| 翻译服务设置 | 设置 DeepL 与 OpenAI 的密钥、地址与模型，其余服务不需设置 |
| 主题 | 浅色 / 深色 |
| 应用日志 | 记录更详细的运行信息，建议仅在排查问题时开启（默认关闭） |
| 调试工具 | 在 **截图翻译** 的结果上叠加 OCR 识别框与文字分组框（默认关闭） |

> 快捷键可以是组合键，也可以是单个按键（F1 ~ F24、Pause、Scroll Lock）、鼠标中键／侧键或游戏手柄按键。

> Log 仅保存在本机，不会自动上传；开启 **记录详细信息** 后的内容同样只会保留在本机。反馈问题时，可在设置页按下 **导出诊断信息** 与 **上传**。

---

### 翻译 API

> 除了 DeepL 与 OpenAI 以外，其他都是下载后就可以直接使用的功能。

| 服务 | 说明 |
|------|------|
| Google 翻译（RPC） | 新版 RPC 接口 |
| Google 翻译（Web） | 传统 Web 接口 |
| Bing 翻译 | 翻译质量佳 |
| Microsoft 翻译 | **（默认）** 稳定性佳、响应速度快 |
| DeepL | 需到 DeepL 官方注册并获取 API Key |
| OpenAI | 支持 OpenAI API 格式，建议使用本地 LLM，可通过 [Ollama](guides/OLLAMA_GUIDE.zh-Hans.md) 快速安装与使用 |
  
提供「自动备用」机制（备用机制适用于 **截图翻译** 与 **实时翻译**）：  
当某个翻译无法使用或响应过慢时，会自动切换到其他可用的翻译 API，实际使用的引擎显示于工具栏。
![备用.png](images/備援.png)

> 使用 **OpenAI** 时，不会触发备用机制。

### OpenAI 设置

![OpenAI.png](images/OpenAI.png)

| 项目 | 说明 |
|------|------|
| API 地址 | 留空时使用 `http://localhost:11434/v1`（Ollama 的本机默认地址） |
| API Key | 本机运行可留空 |
| 模型设置 | 一份设置 = 模型名称 + 高级参数 + **自动** 与 **指定语言** 各一组 System / User 提示词，切换设置会整组一起换 |
| 推荐设置 | 只读的那一份，使用 [Ollama 安装教程](guides/OLLAMA_GUIDE.zh-Hans.md) 推荐的模型，提示词会依界面语言切换 |
| 新增设置 | 最多 5 份；模型名称为必填，System / User 提示词至少要填写一项 |
| 高级参数 | 位于编辑页的 **高级** 区，三项都可单独关闭；关闭的参数不会发送 |

高级参数：

| 参数 | 说明 | 范围 | 默认 |
|------|------|------|------|
| Temperature | 模型对高概率结果的偏好程度，数值越高，其他可能的表达越有机会被选中 | 0.0 ~ 2.0 | 0.7 |
| Top P | 模型可选择的候选范围，数值越低，只保留概率较高的少数候选 | 0.0 ~ 1.0 | 0.6 |
| Seed | 让生成结果更容易重现；模型、提示词与其他设置相同时，相同 Seed 通常得到相同结果 | 整数 | 42 |

提示词可用参数（设置页的 **可用参数** 区块也会列出说明与示例）：

| 参数 | 说明 | 示例 |
|------|------|------|
| `{source_name}` | 源语言名称 | 英语 |
| `{source_code}` | 源语言代码 | en |
| `{target_name}` | 目标语言名称 | 日语 |
| `{target_code}` | 目标语言代码 | ja |

> 语言名称会跟着界面语言（简体中文界面代入「日语」，English 界面代入「Japanese」）；语言代码则固定是模型使用的代码。

### 多语言 OCR 识别

OCR 识别由 RapidOcrNet 搭配 ONNX 模型处理，会依语言自动使用对应模型，不需手动选择 OCR 引擎。

| 识别语言 | 识别模型（rec） |
|----------|----------------|
| **英文 / 中文（简繁）/ 日文** | PP-OCRv6 通用识别模型（`PP-OCRv6_small_rec`），单一模型支持中、英、日及多种拉丁语系，也能处理常见的中英混排 |
| **韩文** | PP-OCRv5 韩文识别模型（`korean_PP-OCRv5_rec`），用于补足 PP-OCRv6 通用模型未涵盖的韩文字符 |

所有语言共用同一套**文字检测模型**（`PP-OCRv6_det_tiny`）与**方向分类模型**（`cls`），仅识别模型会依语言切换。  
OCR 全程于本机 CPU 执行，不会将图片上传至外部服务。

---

## 系统需求

- **操作系统**：Windows 10 / 11
- **运行环境**：安装文件已内含所需环境，不需另外安装 .NET Runtime

使用以下翻译服务时，需另外准备：

- **DeepL**：需到 [DeepL 官网](https://www.deepl.com/pro-api) 申请 API Key
- **OpenAI**：需自备 OpenAI API 兼容服务，本地 LLM 搭建使用方式可参考 [Ollama 安装教程](guides/OLLAMA_GUIDE.zh-Hans.md)

---

## ☕ 支持项目

OverTranslate 是免费提供的 Windows 翻译工具。  
如果这个项目对你有帮助，欢迎通过 [Buy Me a Coffee](https://buymeacoffee.com/hon.lu) 请我喝杯咖啡，支持后续开发与维护。

---

## 许可

本项目采用 [GNU General Public License v3.0（GPL-3.0）](https://www.gnu.org/licenses/gpl-3.0.html) 许可。  
你可以自由使用、修改与分发本软件；若分发修改后的版本，需依 GPL-3.0 许可条款公开相应的源代码。  
完整许可条款请参阅 [LICENSE](../LICENSE)。
