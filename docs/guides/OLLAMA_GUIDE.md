# 使用 Ollama 執行本地翻譯模型

> **Language：** **繁體中文 ✓** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻譯服務支援 OpenAI API 相容格式，可以搭配 [Ollama](https://ollama.com/) 在自己的電腦上執行翻譯模型。翻譯內容會留在本機，不需要支付外部翻譯 API 的費用。

本篇以 Ollama 搭配 Hy-MT2 翻譯模型為例，照著以下步驟安裝並設定即可開始使用。也可以改用 LM Studio、OpenRouter 等其他 OpenAI 相容服務，見文末〈使用其他 OpenAI 相容服務〉。

## 電腦需求

本篇使用的模型約 **3.1 GB**，載入後約占用 **4.1 GB VRAM**，建議使用至少 **6 GB VRAM** 的獨立顯示卡。如果同時執行遊戲或其他 GPU 程式，建議有 **8 GB VRAM 以上**。

實際資源用量會因翻譯內容及其他程式而異。VRAM 不足時，部分運算可能改由 CPU 執行，速度也會變慢，可以改用較小的版本（見〈使用其他模型〉）。

## 1. 安裝 Ollama

前往 [Ollama 官網](https://ollama.com/download) 下載並安裝，使用預設選項即可。安裝完成後開啟 Ollama，使用 OverTranslate 翻譯時需要讓它保持執行。

## 2. 下載翻譯模型

開啟 Windows 的「命令提示字元」或「PowerShell」，執行以下指令：

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

等待下載完成即可。模型檔案約 **3.1 GB**，所需時間取決於網路速度。

Hy-MT2 是騰訊推出的翻譯專用模型，支援 33 種語言互譯；這裡使用的是由 Unsloth 重新打包、體積較小的版本。

## 3. 設定 OverTranslate

1. 開啟 OverTranslate 的 **設定**。
2. 在翻譯服務中選擇 **OpenAI**。
3. 將 **API 位址**與 **API Key** 留空。
4. **模型設定**維持預設的 **系統預設**。

設定完成後即可開始翻譯。API 位址留空時，OverTranslate 會使用 Ollama 的本機位址 `http://localhost:11434/v1`；本機 Ollama 不需要 API Key。系統預設已包含本篇使用的模型、提示詞及進階參數，不必另外修改。

## 使用其他模型

### 同一個模型的其他大小

到 [unsloth/Hy-MT2-7B-GGUF](https://huggingface.co/unsloth/Hy-MT2-7B-GGUF) 選擇其他量化版本，把指令最後的版本名稱換掉即可：

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:<版本名稱>
```

檔案越小，需要的硬體資源越少，但翻譯品質也可能下降。

### 其他模型

可以在 [Ollama Models](https://ollama.com/search) 或 [Hugging Face](https://huggingface.co/models) 尋找。

使用 Hugging Face 上的模型時，建議選擇已提供 **GGUF** 版本的模型。若只有 Safetensors 等原始格式，通常需要先確認模型是否能轉換，再透過 [llama.cpp](https://github.com/ggml-org/llama.cpp) 轉成 GGUF，才能匯入 Ollama。

### 更換模型設定

1. 在 OverTranslate 的 **模型設定**中選擇 **新增設定**。
2. 填入模型名稱並儲存，再選擇新建的設定。
3. 如有需要，再依模型文件調整提示詞與進階參數。

從 Hugging Face 下載的模型，名稱需包含 `hf.co/` 開頭。例如：

```text
hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

每份模型設定會一併保存模型名稱、進階參數、翻譯模式與提示詞。切換設定時，這些項目也會一起切換。

不同模型適用的提示詞與參數可能不同，建議先參考模型本身的使用說明。OverTranslate 內建的提示詞會依介面語言切換，並已針對推薦的 Hy-MT2 模型調整。

系統預設的進階參數為：

- Temperature：`0.7`
- Top P：`0.6`
- Seed：`42`

這些數值依照推薦模型的官方文件設定。固定 Seed 有助於在相同設定下取得較一致的結果。若模型會先進入思考模式才輸出譯文，可在模型設定的 **進階**中勾選 **關閉思考**；即時翻譯通常較適合直接輸出譯文的模型。

## 使用其他 OpenAI 相容服務

LM Studio、OpenRouter 等服務也能搭配 OverTranslate 使用。在翻譯服務設定中填入該服務提供的 **API 位址**與 **API Key**，再新增模型設定並填入模型名稱即可。其他設定方式請參考各服務的文件。
