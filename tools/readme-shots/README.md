# README 截圖

拍 README 與官網用的 app 畫面，輸出未壓縮的原圖，交給 `docs/images/originals/`。壓縮後放進 `docs/images/` 的那一步由人處理。

| 輸出 | 內容 |
|------|------|
| `設定頁{,_zh-Hans,_en,_jp,_ko}.png` | 設定頁，捲在最上面 |
| `即時翻譯視窗預覽{,_zh-Hans,_en,_jp,_ko}.png` | 即時翻譯頁，捲到「顯示外觀」card 在最上面 |
| `備援.png` | 截圖工具列的 ⚡備援 標籤與它的 ToolTip |

主視窗拍 DWM 外框（不含看不見的縮放邊框），所以圓角外是背後的東西。

## 拍之前

- **主螢幕放一個純黑的視窗**（例如全螢幕的小畫家，畫布填黑），主視窗的圓角外與工具列四周才會是黑的。
- 截圖翻譯的快捷鍵要是預設的 `Ctrl+Alt+A`：備援那張是用它開框選的。
- 有副螢幕的話，滑鼠拍照時會停在副螢幕，主視窗在拍備援時也移過去；沒有也能拍。

## 執行

```powershell
powershell -ExecutionPolicy Bypass -File tools\readme-shots\shoot.ps1 -Build
```

| 參數 | 說明 |
|------|------|
| `-Build` | 重新建置拍照用的 app。第一次、或程式改過之後要加；沒建過也會自動建 |
| `-Phase main` / `backup` | 只拍主視窗那十張，或只拍備援那張；預設 `all` |
| `-Langs zh-Hant,en` | 只拍這幾種介面語言；預設五種 |
| `-CopyToOriginals` | 拍完直接覆蓋 `docs/images/originals/` 裡的同名檔 |

輸出與 log 在 `artifacts/readme-shots/out/`（不進版控）。拍完一定要看過：`run.log` 會記下捲動位置，`backup-full.png` 是裁切前的整個主螢幕。

## 它會動到的東西

- **正在跑的 OverTranslate 會先被關掉**（包括使用者自己開的那個），因為只能開一個。拍完不會幫你開回來。
- **設定檔**（`%APPDATA%\OverTranslate\appsettings.json`）先備份，拍的過程中改介面語言，備援那張另外把翻譯服務改成 Microsoft、開自動翻譯，結束時原樣還原（log 最後一行會比對雜湊）。所以畫面上其他的值（快捷鍵、文字大小、漫畫模型狀態……）都是當下這台電腦的設定。
- **原始碼不會被改到**：`-Build` 會先套上 `backup-badge.patch`、建到 `artifacts/readme-shots/app/`，建完立刻還原。

## 為什麼要補丁

⚡備援 標籤只有在選定的翻譯服務真的翻不完、改由其他服務接手時才會出現，拍照時沒辦法穩定觸發。補丁讓 `ResilientProvider` 在環境變數 `OT_SHOT_BACKUP=1` 時，把結果當成最後兩塊由第一個備援服務翻譯；腳本只在拍這張時設這個變數。

補丁對不上（`ResilientProvider.cs` 改過）時，建置會停下來。重做的方法：手動把同樣的幾行加回 `bool fallbackUsed = ...` 之後，`git diff src/OverTranslate/Services/Providers/ResilientProvider.cs > tools/readme-shots/backup-badge.patch`，再把原始碼還原。

## 已知的坑

- 視窗大小用程式預設的 1280×760 DIP，在 125% 的螢幕上是 1600×950 px。換了縮放比例，圖的大小會跟著變，官網 `<img>` 的 `width`／`height` 要一起改。
- 「顯示外觀」card 的捲動位置，是用 UI Automation 找到該語言的標題字串（`S.Realtime.Appearance`）來對齊的。改了這個字串或頁面結構，要回來看 `Scroll-ToAppearance`。
- 備援那張的裁切範圍，是從翻譯服務下拉框往右的固定 DIP 範圍，依照目前繁中工具列的寬度定的。工具列改過版面，就要對著 `backup-full.png` 重新調 `shoot.ps1` 裡的數字。
