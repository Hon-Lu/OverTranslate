# MangaModelExport

產生漫畫直排模型（B3）中「我們自己匯出」的那幾個檔。app 讀的是下載來的成品，這裡只負責重做成品。
對應清單 `src/OverTranslate/ocrmodels/manga-vertical.json` 的 **version 1**。

量測與選型的來龍去脈見 `tools/OcrHarness/vertical-ocr.md` 最後一節，以及 `.ai/vertical-ja3-handoff/vertical-ocr-bench-results-2.md`。

## 清單 v1 的每個檔從哪裡來

| 清單裡的檔名 | 來源 | 怎麼產生 |
|---|---|---|
| `ocr-encoder.fp16.onnx` | [kha-white/manga-ocr-base](https://huggingface.co/kha-white/manga-ocr-base) 的 PyTorch 權重 | `python export_fp16.py <out>` → `<out>/encoder.onnx` |
| `ocr-decoder-cross.fp16.onnx` | 同上 | `HALF=1 python export_kv.py <out>/mo-kv-fp16` → `<out>/mo-kv-fp16-small/decoder_cross.onnx` |
| `ocr-decoder-step.fp16.onnx` | 同上 | 同一次執行 → `<out>/mo-kv-fp16-small/decoder_step.onnx` |
| `vocab.txt` | kha-white/manga-ocr-base 的 `vocab.txt` | 原檔，不改 |
| `detector.fp16.onnx` | [ogkalu/comic-text-and-bubble-detector](https://huggingface.co/ogkalu/comic-text-and-bubble-detector) 的 `detector.onnx` | **沒有腳本**，步驟見下 |

檔名改成清單裡的名字後，計算 SHA-256 與大小，寫回清單。

## 兩支腳本做什麼

- **`export_kv.py`**：把 manga-ocr 的兩層 BERT decoder 改寫成帶 KV cache 的圖。
  - HF 上所有標成 "merged" 的 manga-ocr 匯出其實都沒有 past 輸入，optimum 也拒絕匯出 BERT decoder，所以只能自己寫。
  - 輸出兩組：
    - `<out>/`：`decoder_init`＋`decoder_step`，兩圖各帶一份權重，app 不用；
    - `<out>-small/`：`decoder_cross`＋`decoder_step`，app 用的是這組。
  - fp32 版在最後會對拍 HF 原模型 8 步，印出 logits 最大誤差（實測 1.3e-5）。
- **`export_fp16.py`**：把 encoder 以 `half()` 直接從 PyTorch 匯出（權重 fp16、圖的輸入輸出仍是 fp32），並印出與 fp32 的最大差異。

兩支都只用 CPU，會自動從 HF 下載 `kha-white/manga-ocr-base`。

## 相依

量出現行檔案時用的版本（Python 3.11）：

```text
torch 2.14.0+cpu
transformers 4.57.6
onnx 1.23.0
onnxruntime 1.24.4   # 只有偵測器轉檔需要（onnxruntime.transformers.float16）
```

```bash
python -m venv venv-export
venv-export/Scripts/pip install torch --index-url https://download.pytorch.org/whl/cpu
venv-export/Scripts/pip install transformers onnx
```

C 槽很緊，venv 和輸出目錄請開在 D 槽。

## 怎麼跑

```bash
cd tools/MangaModelExport
../../venv-export/Scripts/python export_fp16.py D:/temp/manga-export/mo-fp16
HALF=1 ../../venv-export/Scripts/python export_kv.py D:/temp/manga-export/mo-kv-fp16
```

不給參數時輸出到目前目錄下的 `out/`。

## 偵測器 fp16（手動步驟，沒有腳本）

照 `vertical-ocr-bench-results-2.md` 第 2 項：

1. 用 `onnxruntime.transformers.float16.convert_float_to_float16` 轉 `detector.onnx`，`keep_io_types=True`，
   `op_block_list` 只擋 `TopK`、`GatherElements`、`Cast`。多擋 `Gather` 或算術類會讓偵測變慢 27ms，還會在 ja2 掉一句。
2. 清掉圖裡的 `value_info`。
3. 刪掉轉換工具插入的重複 Cast 節點（原檔上是 49 個），並把重名節點改名，否則 DirectML 載入失敗或輸出重複。

**驗收**：接上完整後處理，只有 ja2 一句從 whole 變成 split。重做時請用抄本重新計分，不要只比對雜湊。
onnxconverter-common 的轉法會產生重名節點與重複輸出，已否決。

## 版本

模型的輸入輸出是 `MangaTextDetector` 與 `MangaTextRecognizer` 寫死的契約。只要有任何一個檔的內容變了，就要：

- 把清單的 `version` 加一；
- 上傳到新的託管位置；
- 用 `OcrHarness` probe 重跑三組抄本，確認分數。

app 只讀自己那一版的清單，舊版目錄會在新版下載完成後刪除。
