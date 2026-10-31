# 使用 Ollama 运行本地翻译模型

> **Language：** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **简体中文 ✓** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻译服务支持 OpenAI API 兼容格式，可以搭配 [Ollama](https://ollama.com/) 在自己的电脑上运行翻译模型。翻译内容会留在本机，不需要支付外部翻译 API 的费用。

本篇以 Ollama 搭配 Hy-MT2 翻译模型为例，按照以下步骤安装并设置即可开始使用。也可以改用 LM Studio、OpenRouter 等其他 OpenAI 兼容服务，见文末〈使用其他 OpenAI 兼容服务〉。

## 电脑需求

本篇使用的模型约 **3.1 GB**，加载后约占用 **4.1 GB 显存**，建议使用至少 **6 GB 显存**的独立显卡。如果同时运行游戏或其他 GPU 程序，建议有 **8 GB 显存以上**。

实际资源用量会因翻译内容及其他程序而异。显存不足时，部分运算可能改由 CPU 执行，速度也会变慢，可以改用较小的版本（见〈使用其他模型〉）。

## 1. 安装 Ollama

前往 [Ollama 官网](https://ollama.com/download) 下载并安装，使用默认选项即可。安装完成后打开 Ollama，使用 OverTranslate 翻译时需要让它保持运行。

## 2. 下载翻译模型

打开 Windows 的「命令提示符」或「PowerShell」，执行以下命令：

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

等待下载完成即可。模型文件约 **3.1 GB**，所需时间取决于网络速度。

Hy-MT2 是腾讯推出的翻译专用模型，支持 33 种语言互译；这里使用的是由 Unsloth 重新打包、体积较小的版本。

## 3. 设置 OverTranslate

1. 打开 OverTranslate 的 **设置**。
2. 在翻译服务中选择 **OpenAI**。
3. 将 **API 地址**与 **API Key** 留空。
4. **模型设置**保持默认的 **系统默认**。

设置完成后即可开始翻译。API 地址留空时，OverTranslate 会使用 Ollama 的本机地址 `http://localhost:11434/v1`；本机 Ollama 不需要 API Key。系统默认已包含本篇使用的模型、提示词及高级参数，不必另外修改。

## 使用其他模型

### 同一个模型的其他大小

到 [unsloth/Hy-MT2-7B-GGUF](https://huggingface.co/unsloth/Hy-MT2-7B-GGUF) 选择其他量化版本，把命令最后的版本名称换掉即可：

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:<版本名称>
```

文件越小，需要的硬件资源越少，但翻译质量也可能下降。

### 其他模型

可以在 [Ollama Models](https://ollama.com/search) 或 [Hugging Face](https://huggingface.co/models) 寻找。

使用 Hugging Face 上的模型时，建议选择已提供 **GGUF** 版本的模型。若只有 Safetensors 等原始格式，通常需要先确认模型是否能转换，再通过 [llama.cpp](https://github.com/ggml-org/llama.cpp) 转成 GGUF，才能导入 Ollama。

### 更换模型设置

1. 在 OverTranslate 的 **模型设置**中选择 **新增设置**。
2. 填入模型名称并保存，再选择新建的设置。
3. 如有需要，再按照模型文档调整提示词与高级参数。

从 Hugging Face 下载的模型，名称需包含 `hf.co/` 开头。例如：

```text
hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

每份模型设置会一并保存模型名称、高级参数、翻译模式与提示词。切换设置时，这些项目也会一起切换。

不同模型适用的提示词与参数可能不同，建议先参考模型本身的使用说明。OverTranslate 内置的提示词会跟随界面语言切换，并已针对推荐的 Hy-MT2 模型调整。

系统默认的高级参数为：

- Temperature：`0.7`
- Top P：`0.6`
- Seed：`42`

这些数值按照推荐模型的官方文档设置。固定 Seed 有助于在相同设置下得到较一致的结果。若模型会先进入思考模式才输出译文，可在模型设置的 **高级**中勾选 **关闭思考**；实时翻译通常更适合直接输出译文的模型。

## 使用其他 OpenAI 兼容服务

LM Studio、OpenRouter 等服务也能搭配 OverTranslate 使用。在翻译服务设置中填入该服务提供的 **API 地址**与 **API Key**，再新增模型设置并填入模型名称即可。其他设置方式请参考各服务的文档。
