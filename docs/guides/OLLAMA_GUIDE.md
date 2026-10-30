# 本地 LLM 架設教學（以 Ollama 為例）

> **Language：** **繁體中文 ✓** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻譯服務支援 OpenAI API 相容格式，因此也可以搭配 [Ollama](https://ollama.com/)，直接在自己的電腦上執行本地模型。

使用本地模型不需要另外支付 API 費用，翻譯內容也不會傳送到外部伺服器。

> 這篇以 Ollama 示範。LM Studio、OpenRouter 等其他 OpenAI 相容服務也能使用，設定方式見文末〈4. 使用其他 OpenAI 相容服務〉。

這篇教學會以 `hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS` 為例。Hy-MT2 是騰訊推出的翻譯專用模型，支援 33 種語言互譯；這裡使用的是由 Unsloth 重新打包、體積較小的版本。

> **電腦需要什麼規格？**
>
> 本地模型會使用電腦的 CPU 與 GPU。為了讓翻譯速度更順暢，建議使用有獨立顯示卡的電腦。
>
> 這篇教學使用的模型檔案約 **3.1 GB**，載入後大約會占用 **4.1 GB VRAM**，因此建議顯示卡至少有 **6 GB VRAM**。
>
> 如果你會一邊玩遊戲、一邊使用 OverTranslate，遊戲本身也會占用 VRAM，建議使用 **8 GB 以上**的顯示卡。
>
> 實際使用量仍會受到 Ollama、翻譯內容長度，以及其他程式的 GPU 使用狀況影響。
>
> 如果 VRAM 不夠，模型的一部分可能會改由 CPU 執行，翻譯速度會明顯變慢。遇到這種情況，可以改用下面介紹的較小版本。

## 1. 安裝 Ollama

1. 前往 [Ollama 官網](https://ollama.com/download)，下載適合你作業系統的版本
2. 執行安裝程式，依照預設選項完成安裝即可

安裝完成後，開啟 Ollama。

Ollama 預設的 API 位址是：

`http://localhost:11434`

如果你沒有特別修改過 Ollama 的設定，使用預設值即可。

## 2. 下載翻譯模型

開啟 Windows 的「命令提示字元」或「PowerShell」，輸入：

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

接著等待模型下載完成。

這個版本大約 **3.1 GB**，實際需要多久會依你的網路速度而定。

> **想使用其他大小的版本？**
>
> 可以前往 [unsloth/Hy-MT2-7B-GGUF](https://huggingface.co/unsloth/Hy-MT2-7B-GGUF)，選擇同一個模型的其他量化版本。
>
> 找到想使用的版本後，把指令改成：
>
> `hf.co/unsloth/Hy-MT2-7B-GGUF:<版本名稱>`
>
> 一般來說，檔案越小，需要的硬體資源越少，執行也會更輕量；相對地，翻譯品質也可能有所下降。

你也可以從其他地方尋找適合的模型：

- [Ollama Models](https://ollama.com/search)：可以直接尋找 Ollama 已整理好的模型，使用起來最簡單。
- [Hugging Face](https://huggingface.co/models)：模型選擇更多。如果要搭配 Ollama 使用，建議優先找**已提供 GGUF 版本**的模型。這類模型的名稱通常會帶有 `GGUF`，例如以 `-GGUF` 結尾。

如果 Hugging Face 上只有原始模型，例如 Safetensors，而沒有提供 GGUF 版本，就需要先透過 [llama.cpp](https://github.com/ggml-org/llama.cpp) 將相容的模型轉換成 GGUF，再匯入 Ollama。

如果不確定該選哪一個，直接使用這篇教學推薦的模型即可；另外也建議選擇**不使用思考模式**的模型，會比較適合 OverTranslate 這類即時翻譯用途。如果模型預設會先思考再回答，可以在模型設定的 **進階** 勾選 **關閉思考**。

## 3. 在 OverTranslate 中設定

模型下載完成後，回到 OverTranslate：

1. 開啟 **設定**
2. 在翻譯服務中選擇 **OpenAI**
3. **API 位址**與 **API Key** 都可以直接留空
4. **模型設定**維持預設的推薦設定
5. 關閉設定頁後，就可以開始使用本地模型翻譯

API 位址留空時，OverTranslate 會使用 Ollama 預設的本機位址；而 Ollama 在本機執行時也不需要 API Key。

OverTranslate 內建的推薦設定已經使用這篇教學中的模型：

`hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS`

對應的提示詞與進階參數也已經設定好，因此如果只是想直接使用，**不需要另外修改模型設定**。

> **想改用其他模型？**
>
> 在 **設定清單**中按下 **新增設定**，填入完整的模型名稱，儲存後再選擇這份設定即可。提示詞可以先使用預設內容，需要時再依該模型的說明調整。
>
> 如果模型來自 Hugging Face，模型名稱請連同開頭的 `hf.co/` 一起填入。
>
> 每一份模型設定都包含：
>
> - 模型名稱
> - 進階參數（Temperature、Top P、Seed、關閉思考）
> - 翻譯模式（標準 / 整批）
> - 提示詞
>
> 切換模型設定時，這些內容會一起切換，不需要逐項重新設定。

> **關於提示詞與進階參數**
>
> OverTranslate 內建的提示詞會依照目前的介面語言切換，並已針對推薦的 Hy-MT2 模型調整。
>
> 每份模型設定只有一份提示詞，不使用 System Prompt，這是依照推薦模型官方文件提供的使用方式設定。
>
> 不同模型需要的提示詞格式可能不同，自行加入其他模型時，可以按照該模型的說明調整。
>
> 推薦模型的進階參數預設為：
>
> - Temperature：`0.7`
> - Top P：`0.6`
> - Seed：`42`
>
> 這些數值依照推薦模型官方文件的建議設定。Seed 固定後，同一段原文在相同設定下，可以維持較一致的翻譯結果。

## 4. 使用其他 OpenAI 相容服務

LM Studio、OpenRouter 等提供 OpenAI 相容 API 的服務也可以使用。把該服務的 API 位址與 API Key 填入設定，再新增一份模型設定填入它的模型名稱即可，詳細請參考該服務的說明文件。
