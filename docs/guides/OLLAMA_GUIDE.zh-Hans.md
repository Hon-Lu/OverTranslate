# 本地 LLM 搭建教程（以 Ollama 为例）

> **Language：** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **简体中文 ✓** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻译服务支持 OpenAI API 兼容格式，因此也可以搭配 [Ollama](https://ollama.com/)，直接在自己的电脑上运行本地模型。

使用本地模型不需要另外支付 API 费用，翻译内容也不需要发送到外部服务器。

> 这篇以 Ollama 示范。LM Studio、OpenRouter 等其他 OpenAI 兼容服务也能使用，设置方式见文末〈4. 使用其他 OpenAI 兼容服务〉。

这篇教程会以 `hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS` 为例。Hy-MT2 是腾讯推出的翻译专用模型，支持 33 种语言互译；这里使用的是由 Unsloth 重新打包、体积较小的版本。

> **电脑需要什么配置？**
>
> 本地模型会使用电脑的 CPU 和 GPU。为了获得更流畅的翻译速度，建议使用带独立显卡的电脑。
>
> 这篇教程使用的模型文件约 **3.1 GB**，加载后大约会占用 **4.1 GB 显存**，因此建议显卡至少具备 **6 GB 显存**。
>
> 如果你会一边玩游戏、一边使用 OverTranslate，游戏本身也会占用显存，此时建议使用 **8 GB 以上**的显卡。
>
> 实际占用仍会受到 Ollama、翻译内容长度，以及其他程序 GPU 使用情况的影响。
>
> 如果显存不足，模型的一部分可能会改由 CPU 运行，翻译速度会明显变慢。遇到这种情况，可以改用下面介绍的较小版本。

## 1. 安装 Ollama

1. 前往 [Ollama 官网](https://ollama.com/download)，下载适合你操作系统的版本
2. 运行安装程序，按照默认选项完成安装即可

安装完成后，打开 Ollama。

Ollama 默认的 API 地址是：

`http://localhost:11434`

如果你没有特别修改过 Ollama 的设置，直接使用默认值即可。

## 2. 下载翻译模型

打开 Windows 的“命令提示符”或“PowerShell”，输入：

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

然后等待模型下载完成。

这个版本大约 **3.1 GB**，实际需要多久会根据你的网络速度而定。

> **想使用其他大小的版本？**
>
> 可以前往 [unsloth/Hy-MT2-7B-GGUF](https://huggingface.co/unsloth/Hy-MT2-7B-GGUF)，选择同一个模型的其他量化版本。
>
> 找到想使用的版本后，把指令中的模型名称改成：
>
> `hf.co/unsloth/Hy-MT2-7B-GGUF:<版本名称>`
>
> 一般来说，文件越小，需要的硬件资源越少，运行起来也更轻量；相对地，翻译质量也可能有所下降。
>
> 如果你的显卡只有 **4 GB 显存**，建议改用较小的 `maternion/hy-mt2:1.8b`，模型文件约 **1.1 GB**。

你也可以从其他地方寻找合适的模型：

- [Ollama Models](https://ollama.com/search)：可以直接寻找已经为 Ollama 准备好的模型，使用起来最简单。
- [Hugging Face](https://huggingface.co/models)：模型选择更多。如果要搭配 Ollama 使用，建议优先找**已经提供 GGUF 版本**的模型。这类模型的名称通常会包含 `GGUF`，例如以 `-GGUF` 结尾。

如果 Hugging Face 上只有原始模型文件，例如 Safetensors，而没有提供 GGUF 版本，就需要先通过 [llama.cpp](https://github.com/ggml-org/llama.cpp) 将兼容的模型转换成 GGUF，再导入 Ollama。

如果不确定该选哪个，直接使用这篇教程推荐的模型即可。另外也建议选择**不使用思考模式**的模型，会更适合 OverTranslate 这类实时翻译用途。

## 3. 在 OverTranslate 中设置

模型下载完成后，回到 OverTranslate：

1. 打开 **设置**
2. 在翻译服务中选择 **OpenAI**
3. **API 地址**和 **API Key** 都可以直接留空
4. **模型设置**保持默认的推荐配置
5. 关闭设置页后，就可以开始使用本地模型翻译

API 地址留空时，OverTranslate 会使用 Ollama 默认的本机地址；而 Ollama 在本机运行时也不需要 API Key。

OverTranslate 内置的推荐配置已经使用这篇教程中的模型：

`hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS`

对应的提示词和高级参数也已经设置好，因此如果只是想直接使用，**不需要另外修改模型设置**。

> **想改用其他模型？**
>
> 在 **设置列表**中点击 **新增设置**，填写完整的模型名称，以及该模型需要的提示词，保存后再选择这份配置即可。
>
> 如果模型来自 Hugging Face，模型名称请连同开头的 `hf.co/` 一起填写。
>
> 每一份模型配置都包含：
>
> - 模型名称
> - Temperature
> - 自动检测语言时使用的 System / User 提示词
> - 指定源语言时使用的 System / User 提示词
>
> 切换模型配置时，这些内容会一起切换，不需要逐项重新设置。

> **关于提示词和高级参数**
>
> OverTranslate 内置的提示词会跟随当前界面语言切换，并且已经针对推荐的 Hy-MT2 模型调整。
>
> 这个模型默认只使用 **User Prompt**，不需要 **System Prompt**，这是按照模型官方文档提供的使用方式设置的。
>
> 不同模型需要的提示词格式可能不同，因此自行添加其他模型时，可以按照对应模型的说明进行设置。System Prompt 和 User Prompt 不需要两个都填写，只要至少填写其中一项即可。
>
> 推荐模型默认使用以下高级参数：
>
> - Temperature：`0.7`
> - Top P：`0.6`
> - Seed：`42`
>
> 这些数值参考推荐模型官方文档中的建议设置。Seed 固定后，同一段原文在相同设置下，可以获得更一致的翻译结果。

## 4. 使用其他 OpenAI 兼容服务

LM Studio、OpenRouter 等提供 OpenAI 兼容 API 的服务也可以使用。把该服务的 API 地址与 API Key 填入设置，再新增一份模型设置填写它的模型名称即可，详细请参考该服务的说明文档。
