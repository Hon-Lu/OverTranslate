# 截圖翻譯氣泡底板驗證

把 `CaptureBubbleBackdrop` 產出的底板直接合成回真實截圖，用來判斷「氣泡看不看得出是一張貼上去的卡」。對每張圖輸出三個變體：

- `00-original` —— 原圖
- `10-solid` —— 舊行為，整塊填 `SourceTextColorSampler` 取到的純色
- `40-shipping` —— 正式路徑：抹字修補 → 模糊 → 底色 wash → 文字／底板對比調整 → 邊緣羽化

`40-shipping` 完全走產品程式碼，這裡只負責把回傳的 BGRA 以 source-over 疊回去，也就是 WPF 拿到同一支筆刷會做的事。譯文是固定的中文佔位字串，用 GDI+ 以 `SourceFontScale` 算出的字級畫上去，只為了判斷可讀性，不代表實際排版。

## 執行

在儲存庫根目錄：

```powershell
dotnet run --project tools/CaptureBubbleProbe/CaptureBubbleProbe.csproj -c Release
dotnet run --project tools/CaptureBubbleProbe/CaptureBubbleProbe.csproj -c Release -- <圖片路徑> ...
```

不給參數時跑內建的六張：兩張遊戲面板、兩張漫畫、一張日文網頁、一張深色說明頁。需要本機 `.ai/test-images/`（不進版控）。

輸出在 `artifacts/capture-bubble/<集合>-<檔名>/`。OCR 結果快取在同資料夾的 `ocr.json`，刪掉才會重跑辨識。

## 已經量過的

- 六張樣本上，`40-shipping` 的模糊半徑＝字高 ×0.30、羽化寬＝字高 ×0.35、wash 0.25。0.15 與 0.30 的模糊肉眼難分，0.50 開始發軟。
- **羽化是決定性的**：同樣參數但硬邊，遊戲畫面上仍看得到矩形輪廓。
- 平底（深色網頁、漫畫黑框）三個變體看不出差別，不需要另做複雜度分流。
- wash 往取樣背景色疊不會增加對比，那是文字顏色與 lift 的工作。
- 整張畫面修補 29–144ms（870×1236 至 2365×1265，3–70 個區塊），在背景執行緒跑一次。
