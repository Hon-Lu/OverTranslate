# Local LLM Setup Guide (using Ollama as the example)

> **Language:** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **English ✓** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate's **OpenAI** translation service supports OpenAI API-compatible endpoints, which means you can also use [Ollama](https://ollama.com/) to run a model directly on your own computer.

With a local model, there are no separate API fees, and the text you translate does not need to be sent to an external server.

> This guide uses Ollama as the example. Other OpenAI-compatible services such as LM Studio and OpenRouter work too — see "4. Using other OpenAI-compatible services" at the end.

This guide uses `hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS` as the example. Hy-MT2 is a translation-focused model from Tencent that supports translation between 33 languages. The version used here is a smaller repackaged build provided by Unsloth.

> **What kind of hardware do I need?**
>
> Local models use your computer's CPU and GPU. For smoother translation speed, a computer with a dedicated graphics card is recommended.
>
> The model used in this guide is about **3.1 GB** to download and uses roughly **4.1 GB of VRAM** once loaded, so a graphics card with at least **6 GB of VRAM** is recommended.
>
> If you plan to use OverTranslate while gaming, keep in mind that the game will also use VRAM. In that case, **8 GB or more** is a better target.
>
> Actual usage can vary depending on Ollama, the length of the text being translated, and how much GPU memory other applications are using.
>
> If you run out of VRAM, part of the model may fall back to the CPU and translation can become much slower. If that happens, try one of the smaller versions described below.

## 1. Install Ollama

1. Go to the [Ollama website](https://ollama.com/download) and download the version for your operating system
2. Run the installer and complete the installation using the default options

After installation, open Ollama.

Its default API endpoint is:

`http://localhost:11434`

If you have not changed Ollama's settings, you can simply leave it at the default.

## 2. Download the translation model

Open Command Prompt or PowerShell and run:

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

Then wait for the download to finish.

This version is about **3.1 GB**, so the download time depends on your connection speed.

> **Want a different-sized version?**
>
> Visit [unsloth/Hy-MT2-7B-GGUF](https://huggingface.co/unsloth/Hy-MT2-7B-GGUF) and choose another quantized build of the same model.
>
> Once you have picked one, use:
>
> `hf.co/unsloth/Hy-MT2-7B-GGUF:<build name>`
>
> In general, smaller files need fewer hardware resources and are lighter to run, but translation quality may also decrease.
>
> If your graphics card only has **4 GB of VRAM**, the smaller `maternion/hy-mt2:1.8b` model is recommended. Its model file is about **1.1 GB**.

You can also look for other suitable models here:

- [Ollama Models](https://ollama.com/search): the easiest option, with models already prepared for use with Ollama.
- [Hugging Face](https://huggingface.co/models): offers a much wider selection. For use with Ollama, look for a model that **already provides a GGUF version**. These repositories often include `GGUF` in their name, for example names ending in `-GGUF`.

If a Hugging Face repository only provides the original model files, such as Safetensors, and no GGUF build is available, you will first need to convert a compatible model to GGUF with [llama.cpp](https://github.com/ggml-org/llama.cpp), then import it into Ollama.

If you are not sure what to choose, using the model recommended in this guide is the simplest option. Models that **do not use a thinking/reasoning mode** are also generally a better fit for real-time translation in OverTranslate.

## 3. Configure OverTranslate

Once the model has finished downloading, return to OverTranslate:

1. Open **Settings**
2. Choose **OpenAI** as the translation service
3. Leave both **API endpoint** and **API Key** empty
4. Keep the **model setting** on the built-in recommended configuration
5. Close Settings and start translating with the local model

When the API endpoint is left blank, OverTranslate uses Ollama's default local address. A locally running Ollama server also does not require an API key.

OverTranslate's built-in recommended configuration already uses the model from this guide:

`hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS`

The matching prompts and advanced parameters are already configured as well, so if you simply want to start using it, **there is nothing else you need to change**.

> **Want to use a different model?**
>
> In the **settings list**, choose **Add**, enter the full model name and the prompts required by that model, then save and select the new configuration.
>
> If the model comes from Hugging Face, include the full name starting with `hf.co/`.
>
> Each model configuration includes:
>
> - Model name
> - Temperature
> - System / User prompts used for automatic language detection
> - System / User prompts used when the source language is specified
>
> When you switch configurations, all of these settings switch together.

> **About prompts and advanced parameters**
>
> OverTranslate's built-in prompts follow the current interface language and are already adjusted for the recommended Hy-MT2 model.
>
> This model uses a **User Prompt** without a **System Prompt** by default, following the usage format shown in the model's documentation.
>
> Other models may require a different prompt format. When adding your own model, follow that model's instructions. You do not need to fill in both the System Prompt and User Prompt; at least one of them is enough.
>
> The recommended model uses these advanced parameters by default:
>
> - Temperature: `0.7`
> - Top P: `0.6`
> - Seed: `42`
>
> These values follow the recommended settings in the model documentation. With a fixed seed, the same source text under the same settings can produce more consistent translations.

## 4. Using other OpenAI-compatible services

Other services that offer an OpenAI-compatible API, such as LM Studio and OpenRouter, work too. Enter the service's API endpoint and API key in Settings, then add a model setting with its model name. See that service's documentation for details.
