# Run a Local Translation Model with Ollama

> **Language:** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **English ✓** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate's **OpenAI** translation service works with any OpenAI-compatible API, so you can pair it with [Ollama](https://ollama.com/) and run a translation model on your own computer. Your text stays on your machine, and there are no fees for an external translation API.

This guide uses Ollama with the Hy-MT2 translation model. Follow the steps below to install and set it up. You can also use other OpenAI-compatible services such as LM Studio or OpenRouter — see "Other OpenAI-compatible services" at the end.

## System requirements

The model in this guide is about **3.1 GB** and uses about **4.1 GB of VRAM** once loaded, so a dedicated graphics card with at least **6 GB of VRAM** is recommended. If you run games or other GPU programs at the same time, **8 GB of VRAM or more** is recommended.

Actual usage varies with the text being translated and other programs. When VRAM runs short, part of the work may move to the CPU and translation slows down; in that case, try a smaller version (see "Other models").

## 1. Install Ollama

Download and install it from the [Ollama website](https://ollama.com/download) with the default options. Open Ollama once it is installed, and keep it running while you translate with OverTranslate.

## 2. Download the translation model

Open Windows "Command Prompt" or "PowerShell" and run:

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

Wait for the download to finish. The model file is about **3.1 GB**, so how long it takes depends on your connection.

Hy-MT2 is a translation model from Tencent that translates between 33 languages; this is a smaller version repackaged by Unsloth.

## 3. Set up OverTranslate

1. Open **Settings** in OverTranslate.
2. Choose **OpenAI** as the translation service.
3. Leave **API URL** and **API key** empty.
4. Keep **Built-in** selected under **Model settings**.

That's all you need to start translating. With the API URL empty, OverTranslate uses Ollama's local address `http://localhost:11434/v1`, and a local Ollama needs no API key. **Built-in** already includes the model, prompt and advanced parameters used in this guide, so there is nothing else to change.

## Other models

### Other sizes of the same model

Pick another quantized version on [unsloth/Hy-MT2-7B-GGUF](https://huggingface.co/unsloth/Hy-MT2-7B-GGUF) and replace the version name at the end of the command:

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:<version name>
```

Smaller files need fewer hardware resources, but translation quality may drop.

### Different models

You can look for models on [Ollama Models](https://ollama.com/search) or [Hugging Face](https://huggingface.co/models).

On Hugging Face, choose a model that already offers a **GGUF** version. If only the original format such as Safetensors is available, first check whether the model can be converted, then convert it to GGUF with [llama.cpp](https://github.com/ggml-org/llama.cpp) before importing it into Ollama.

### Switching model settings

1. In OverTranslate's **Model settings**, choose **Add settings**.
2. Enter the model name, save, and select the new setting.
3. If needed, adjust the prompt and advanced parameters following the model's documentation.

Models downloaded from Hugging Face need the `hf.co/` prefix in their name, for example:

```text
hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

Each model setting keeps its model name, advanced parameters, translation mode and prompt together, and switching settings switches all of them.

Different models may need different prompts and parameters, so check the model's own usage notes first. OverTranslate's built-in prompt follows the interface language and is tuned for the recommended Hy-MT2 model.

The advanced parameters in **Built-in** are:

- Temperature: `0.7`
- Top P: `0.6`
- Seed: `42`

These values follow the recommended model's official documentation. A fixed seed helps keep results consistent under the same settings. If a model thinks before it outputs the translation, tick **Turn off thinking** under **Advanced** in its model settings; real-time translation usually works better with models that output the translation directly.

## Other OpenAI-compatible services

Services such as LM Studio and OpenRouter also work with OverTranslate. Enter the **API URL** and **API key** the service provides in the translation service settings, then add a model setting with its model name. For anything else, see the service's own documentation.
