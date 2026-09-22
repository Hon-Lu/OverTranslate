# OcrHarness

直排 OCR 重構、官方裁圖方向、離線比較與限制：見 [vertical-ocr.md](vertical-ocr.md)。

## 固定輸入的分組實驗台

`--group-prototype` 保留供後續測試，不隨 app 發佈，呼叫正式 OCR、墨跡量測與分組函式。
app 目前固定一般模式，工具仍可測 general/interface，不受 UI 的模式鎖定影響。

```powershell
$h = 'tools/OcrHarness/bin/Release/net8.0-windows10.0.26100.0/win-x64/OcrHarness.exe'
& $h --group-prototype capture .test-artifacts/prose-inputs.json image.png
& $h --group-prototype enrich .test-artifacts/prose-inputs.json .test-artifacts/prose-ink.json
& $h --group-prototype replay .test-artifacts/prose-ink.json .test-artifacts/prose-result.json
& $h --group-prototype ink .test-artifacts/prose-inputs.json
```

- `capture`：一次 OCR，保存完整文字、框與估值；拒絕覆寫既有輸入。接受多圖或 `@list.txt`。
  清單每行為路徑，可加 Tab 分隔的 flow（`screenshot`／`realtime`）和語言（如 `KO`），預設 screenshot／EN。
- `enrich`：從原圖加入正式墨跡量測，須保留圖片路徑；即時輸入不加此量測。
- `replay`：重新建置後可反覆重播，不跑 OCR、不讀圖片。截圖輸入跑 general/interface，即時輸入只跑 realtime。
  輸出含每組原始 `bN` 成員、完整文字與逐項判定。`bN` 只在同一份 OCR 輸入內穩定。
- `ink`：顯示正式墨跡量測；null 代表不適用。沒有第二份估尺公式。

比較時使用同一輸入並保留建置版本／diff；組數相同不代表成員相同。
重播不取代實際 ROI／尺度變動或翻譯疊圖驗證；realtime 使用 Subtitle 偵測尺寸，不代表直排流程。
產物建議放在 `.test-artifacts/`，不要把圖片加入測試 Fixtures。

## Panel 分組與譯文疊圖

固定輸入清單可用「圖片路徑<Tab>panel<Tab>語言」（EN／JA／KO／ZH-HANT）。
capture 依正式 Panel 偵測尺寸辨識一次；replay 以 realtime 門檻重播，輸出原始成員 ID 和來源行框。

    & $h --group-prototype capture .test-artifacts/panel-inputs.json '@.test-artifacts/panel-list.txt'
    & $h --group-prototype replay .test-artifacts/panel-inputs.json .test-artifacts/panel-groups.json
    & $h --panel-layout .test-artifacts/panel-inputs.json .test-artifacts/panel-translations.json .test-artifacts/panel-rendered

panel-translations.json 是 { "完整分組原文": "完整譯文" }，可填人工核對或先前保存的供應商結果。
疊圖工具不呼叫翻譯服務，未提供的字串沿用原文。每張圖都輸出：

- `<圖>-Groups.png`：原圖加分組標註，不需要譯文。
- `<圖>-Overlay.png`／`-Overlay-Debug.png`／`.json`：至少命中一筆譯文時才輸出，使用正式 RealtimeBlockWindow。

標註方式：白色虛線是 OCR 原始框；同色實線框與 `#n` 是同一個翻譯單位，`#n xk` 表示由 k 行組成。
工具不顯示視窗，也不擷取正在執行的遊戲；自然背景與取色另需實際回歸。

### 外部 OCR 圖片測試

測試專案不再附帶截圖。舊的真實像素回歸保留；需要執行時，將六張原始素材放在外部資料夾，
設定 `OVERTRANSLATE_TEST_IMAGES` 為該目錄，再跑 `dotnet test`。素材名稱列在 `ScreenshotFact`／`ScreenshotTheory` 屬性中。
未提供素材時，這些測試明確顯示略過；不以人造圖冒充原本的真實 OCR 回歸。
一般分組測試仍使用 JSON OCR 框，墨跡單元測試在記憶體中產生像素，不依賴圖片檔案。

OverTranslate 的 **OCR + 分組 + 翻譯** 離線測試工具。給人或 AI 在改動 OCR / 文字分組邏輯後，**不必啟動 WPF app、不必手動框選**，就能在真實截圖上重現並驗證結果。

## 它做什麼

把一張（或多張）截圖 PNG 餵進**真實 pipeline**：

```
OnnxOcrEngine（真 ONNX OCR）
  → OcrTextBlockGrouper（同行/換行分組，多數 bug 出在這）
  → GTranslateProvider(MicrosoftTranslator)（EN → 繁中，免金鑰、走網路）
```

然後印出：
- 每個**分組後的區塊**（送去翻譯的單位）的 `bounds`、行數、原文 —— 直接看出「同一行有沒有被切開」或「不同元件有沒有被誤併」。
- 每個區塊的 **Microsoft 翻譯結果**。

> 為什麼用螢幕截圖？OverTranslate 本身就是擷取**螢幕像素**做 OCR（不是讀網頁 DOM），所以截圖是與正式流程**完全相同**的輸入。

## 使用方式

前置：**先關閉正在執行的 OverTranslate app**，否則 `OverTranslate.exe` 被鎖、無法建置。

```bash
# 1) 建置（會一併把 ocrmodels 複製到輸出）
dotnet build tools/OcrHarness/OcrHarness.csproj -c Debug

# 2) 對截圖跑 OCR + 分組 + 翻譯
tools/OcrHarness/bin/Debug/net8.0-windows10.0.26100.0/win-x64/OcrHarness.exe 圖1.png 圖2.png
```

目前固定以來源語言 `EN`、目標 `ZH-HANT`、Microsoft provider 執行（測英翻中用）。要換語言/provider 直接改 `Program.cs`。

### 量測模式（不翻譯、不連網）

改動 OCR 前後要有數字才知道有沒有變好，這兩個模式就是拿來產生那些數字的：

```bash
# 同一張圖掃過每個偵測尺寸（0.30–1.00，步進 0.05），看哪些尺寸讀得到、讀成什麼
OcrHarness.exe --scale-sweep 圖.png [更多.png ...]

# 同一張圖、同一個尺寸，只換辨識模型（cjk vs korean）
OcrHarness.exe --compare-models 圖.png [更多.png ...]

# 同一張圖、app 會用的尺寸，只換送進偵測器前加的白邊（0–96）
OcrHarness.exe --pad-sweep 圖.png [更多.png ...]
```

`--pad-sweep` 的白邊以前不是獨立變因：它參與 `AlignForDetector` 的對齊算式，會連帶改變偵測器
輸入被壓扁多少，所以舊那張「50 最高分、兩側都比它差」的表其實是在排「哪個白邊剛好落在扭曲最少
的幾何上」。偵測器輸入幾何修好之後（`OnnxOcrEngine.CreateDetectorFrame`）白邊才第一次只是白邊，
重掃的結果是 **8 最好**，也是現行值——0 在語料上跟 8 打平且更快，但在「框整個對話框」
這種實際框選型態上差很多（同一畫面挪 ±8px 共 45 種框法，讀到行首詞 22/45 對 44/45）。
細節與數字記在 `OnnxOcrEngine.DetectorPadding`
與 `.ai/realtime-dialogue/ocr-detector-geometry.md`。

`--scale-sweep` 會一併印出 `RealtimeDetectorSize` 對該尺寸區塊會挑的 primary 與 fallback，
所以掃描結果可以直接對照 app 真正會用的尺寸來讀。它走的是主專案的 `OnnxOcrEngine`，
量到的就是 app 實際在跑的東西。每一列都帶 `chars=` 與耗時：命中率與成本要一起看，
只挑命中率最高的尺寸會付出不成比例的代價（#22 量到 0.95 的成本是 0.5 的四倍）。

列上的框數是**即時翻譯真的會採用的**：塌陷框（高度達區塊 90%）與過短讀取在
`RealtimeTranslationSession` 被丟掉，掃描一併套用，丟掉幾個記在 `dropped=`。
不套用的話，掃描會把 app 其實判定為「讀不到」的尺寸算成成功 —— 實測 v5 有 5 張幀
正好落在這個差別上。

### 拆開「比例」與「絕對尺寸」

```bash
# CSV：同一段字幕切成多種留白，每種留白再掃過每個偵測尺度
OcrHarness.exe --margin-scale-grid 全螢幕.png [更多.png ...]
```

`--margin-series` 是「固定尺寸、變留白」，`--scale-sweep` 是「固定留白、變尺寸」，兩個都拆不開變因 ——
而且 `region-` 那幾集全是約 1820 寬，比例與絕對尺寸在資料裡本來就綁死。把整張螢幕依不同留白裁切，
同一組字就能在同一個比例下對應到不同的絕對尺寸，這樣才拆得開（issue #89）。

每一列帶 `detect`、`glyphDetectorPx`（字在偵測器空間裡的高度）、`occupancyPct`（文字聯集佔 ROI 的比例），
就是為了讓「哪個變因決定準確度」由資料回答。輸出**刻意用 CSV**：其他掃描為了對齊而補空白，
`chars= 78` 這種右對齊欄位在 #84 曾經讓解析器安靜地漏掉所有低分列，而那正是最關鍵的一群。

判讀這個模式踩過三個坑，都會給出漂亮但錯誤的曲線，接手前務必看：

**1. 只試一種成功判準。** 用「字數等於該圖最大值」這種最嚴的判準，會量出一條單調上升到 40–50px
的漂亮曲線；放寬到 0.85 之後，同一批資料在 40px 以上反而下降。多試幾種。

**2. 只用一個來源尺度。** 在同一批原圖上，`glyphDetectorPx` 與**實際縮放比**是綁死的——
降低 fraction 會同時讓字變小、讓圖變糊。這樣量出來會誤以為「字高就是那個變因、地板在 15px」。
把來源圖先縮到 70/50/35% 再跑（字真的變小，但重採樣很溫和）就會拆開：同樣 0–12px，
原尺寸只有 37%，縮到 35% 的卻有 76%。**兩個變因都重要而且會疊加，沒有單一變因的規則。**

**3. 每個 set 用自己的參考值。** 縮小版如果整體讀得差，它的最大值就低，等於用被降低的標準評分。
跨尺度比較時，全部要拿**原尺寸**讀到的最好結果當共同標準。

結論記在 `RealtimeDetectorSize` 的註解，不要沿用 PP-OCRv5 時代「目標約 30px」的說法。

### 框選範圍變了，結果為什麼跟著變

```bash
# 同一張圖、同一塊文字，只擴大框選範圍，逐層比對是哪一層先開始不同
OcrHarness.exe --roi-stability 圖.png --roi X,Y,W,H --grow down,right,all --steps 1,2,4,8,16,32
```

逐層順序（第一個「不同」出現在哪一層，決定了要修哪裡）：

| 層 | 看什麼 |
|---|---|
| 1 來源像素 | 兩個 ROI 重疊區來自同一個檔案，只可能因為實驗切錯而不同 |
| 2 前處理 | `CreateDetectorFrame` 產生的畫布，以及它套用的等比例縮放（函式庫不再縮放）|
| 3 偵測器輸入 | 對齊後的點陣圖在重疊區逐像素比對（`same` / `MOVED`） |
| 4 偵測框 | `DetectBoxesOnly`，在辨識與所有過濾之前 |
| 5 辨識 | 同一位置回來的文字是否相同 |
| 6 分組 | 上面的變動被放大成幾組 |

所有座標都換算回**來源影像座標**再比較；往上／往左擴張會讓每個 local 座標整體位移，直接比會報成整頁都動了。

`det =/+/-` 是配對／新增／消失的框數，`rec =/x/0` 是文字相同／改變／整塊消失。消失的框會印出它在
基準 ROI 的分數，用來分辨「分數掉到門檻以下」和「切割方式整個變了」。

`ImgResize` 是**上限不是目標**：260x200 的圖在 512／1024／2048／4096 下讀到完全一樣的框。所以小
於上限的擷取縮放比就是 1.0，中間沒有任何重取樣。

### 量「Logical ROI / Analysis ROI 分離」這個候選方案

```bash
# Analysis ROI = 把 Logical ROI 依來源影像絕對座標 snap 到格線，OCR 後再濾回 Logical ROI
OcrHarness.exe --roi-snap 圖.png --roi X,Y,W,H --grid 32,64,128 --grow down,right,all --steps 1,2,4,8,16,32,64
```

跟「把不同大小的 ROI 補成相同 canvas」是兩回事：補 canvas 只讓尺寸一樣、內容仍然不同；依**絕對
座標** snap 會讓落在同一批格子裡的兩個 Logical ROI 得到**同一個 Analysis ROI**，也就是同一張裁切圖。

輸出把三層分開，因為它們變動的原因不同：

| 欄 | 意義 |
|---|---|
| `crop` | 未跨格時 Analysis 裁切圖是否逐位元組相同（`IDENT`） |
| `det` / `ocr` | 整個 Analysis ROI 的偵測框與分組結果是否相同——與使用者框了什麼無關，這是 OCR 自身的穩定度 |
| `kept` / `rec` | 濾回 Logical ROI 後剩下什麼。這一欄本來就會隨框選邊界移動而變，那是**正確**的 |
| `strad` | 與 Logical ROI 相交但未被完全包含的區塊數，也就是濾除規則在猜的那些 |

濾除規則用「中心點落在 Logical ROI 內」：使用者框到中間的區塊算他要的，只切到邊的通常是隔壁的，
而「完全包含」會丟掉每一條被刻意切斷的行。`strad` 就是這個選擇的代價。

### 量「偵測範圍固定成整張來源畫面」這個候選方案

```bash
OcrHarness.exe --roi-fullframe 圖.png --roi X,Y,W,H --grow down,right,all --steps 1,2,4,8,16,32
OcrHarness.exe --roi-fullframe 圖.png --explain            # 逐筆列出兩條路線讀出來的差異
OcrHarness.exe --roi-fullframe 圖.png --ff-size 880        # 壓低偵測上限，模擬高解析度螢幕
```

只有**偵測器**看整張畫面；辨識與分組仍然只跑框選留下的那些框。不是「辨識整個螢幕的文字」——
那會把無關內容送進分組，正是這個形狀要避免的事。

跟 `--roi-snap` 的差別：snap 只在框選沒跨過格線時輸入才相同，跨格就跳更大，而且 Analysis ROI
邊界會切斷文字。整張畫面沒有格子可跨，也沒有東西被裁掉。

輸出分兩張表：

| 表 | 問什麼 |
|---|---|
| A 穩定度 | 全畫面路線跟自己的基準 ROI 比。框選長大時，**本來就在框裡**的東西應該完全不動 |
| B 代價與準確度 | 同一個 ROI，全畫面路線對上今天「裁 ROI 再偵測」的行為 |

開頭的 `SEAM CHECK` 是這個模式的前提：把偵測器自己的框整批送回 `DetectionSession.Recognize`，
文字必須跟 `RecognizeAsync` **逐字相同**。不相同就代表量到的是 seam 自己的誤差，下面的數字都不用看。

| 欄 | 意義 |
|---|---|
| `kept` / `strad` | 濾回 Logical ROI 後留下的框；`strad` 是被框選邊界切到的。這裡的框是**完整的**，因為偵測器看過整行 |
| `det =/+/-` | 對照組的框有幾個對上、幾個新增、幾個消失 |
| `dH%` | 對上的框高度差，P50 / P90 / Max。**字級是從框高算出來的**，所以這一欄不是幾何潔癖 |
| `chars ff/roi` | 兩條路線讀到的非空白字數。沒有標準答案可比，但漏字是已知的失效模式，字數是它的代理指標 |

實測（5 張全畫面語料，都是 `scale=1.0`）：

- **A 表全部乾淨**：`det =/+/-` 的消失恆為 0、`dH%` 恆為 `0.0/0.0/0.0`、`rec` 恆為全同，
  +1 到 +32、往下往右往四周都一樣。變的只有「新增」——框選真的長進了新東西，那是對的。
- 300x200 的小 ROI（就是根因調查發現跳動的尺度）同樣是 `0.0/0.0/0.0`，而同一組裡對照組的
  `chars roi` 一路飄 152 → 154 → 157 → 162 → 171。今天的不穩定就在那一欄。
- **B 表的 `chars` 五張全部是全畫面路線較多**：+90 / +30 / +28 / +21 / +6，小 ROI 那組是
  225 對 152。多出來的字是 `OnnxOcrEngine` 開頭記的那個「行首被吃掉」——ROI 邊界切斷了偵測器
  的視野，看整張畫面就沒有這件事。`--explain` 看得到：`tral Andes,` → `volcanoes of the
  Central Andes,`、`ut the climate` → `It was once larger, but the climate`。
- 代價是**被切到一半的行會整條消失**：中心點落在 Logical ROI 外的框會被濾掉，而它們今天會被
  讀成半截。wikipedia 那張少了 7 筆，都是使用者從中間切過的頁首。

### 解析度：這個方案真正的風險

`ImgResize` 是長邊上限。小 ROI 幾乎都是 1.0，整個螢幕不一定。用 `--ff-size` 在現有語料上模擬
（語料裡只有一張 2288 寬會被縮，沒有 1440p / 4K 全螢幕）：

| `--ff-size` | 實得 scale | `dH%` P50/P90/Max |
|---|---|---|
| 預設 2048 | 1.00 | 8.7 / 14.8 / 23.8 |
| 1024 | 0.59（≈1440p） | 29.2 / 52.4 / 61.1 |
| 880 | 0.48（≈4K） | 39.3 / 57.1 / 84.2 |

字數幾乎不掉（1683 → 1682 → 1678），**掉的是框高**，而字級是從框高算的。

把上限提高就解得掉，而且便宜：2288 那張改 `--ff-size 4096` 後 scale 回到 1.0，偵測從 612ms
變 663ms（+8%）、記憶體 +96MB 變 +130MB，框從 97 個變 114 個、消失的框 13 個變 7 個、
`chars` 590 變 604。真正 4K 依畫素量外推約 2 秒一次，截圖翻譯付得起，即時翻譯付不起。

### 稽核信心過濾丟掉了什麼

```bash
# 每一張圖：信心過濾丟掉哪些碎片，其中哪些「本來會被併進一個夠長的行」
OcrHarness.exe --reject-audit 圖.png [更多.png ...]
```

`RejectUnconvincingBlocks` 跑在 `OcrTextBlockGrouper` **之前**，所以一行字被偵測器橫向切開時，
低信心的尾段會在合併前就消失（issue #85）。這個模式拿**未過濾**的框跑一次真正的分組器，
逐筆報告被丟掉的碎片會不會落進一個 10 字以上的行 —— 那正好就是「窄化規則」會放行的集合，
可以在寫規則**之前**先讀，而不是寫完才發現。

判讀時兩類要分開看：**真的尾巴**（併進去那行才完整）與**同排雜訊**（風景框剛好落在字幕那一列，
併進去會污染句子，而且合併後的框可能被判成 collapse 把整句帶走 —— 那正是目前這個順序存在的原因）。

實測 163 張讀到內容的字幕圖：丟掉 54 筆，只有 **1** 筆會被併進真實行（信心 0.79），其餘 53 筆
都是孤立雜訊。0.6%，與 #85 現場量到的 2/307 吻合。**不足以動那段規則**，細節記在
`OcrService.RejectUnconvincingBlocks`。

### 換偵測模型來掃

```bash
# 用別的偵測模型跑同一份掃描（省略 --det 就是出貨的那顆）
OcrHarness.exe --scale-sweep --det 別的det.onnx:half 圖.png [更多.png ...]
```

`:imagenet`（PP-OCRv5 的匯出設定，省略時的預設）與 `:half`（RapidOcrNet 的 PP-OCRv6 預設，
127.5/127.5）指的是模型訓練時的像素正規化。**這不是可調參數**：用錯的統計量餵模型，量到的
就不是那顆模型，而且失敗方式是安靜的 —— 它只是讀得比較少。換模型時務必連正規化一起換。

只換偵測模型、辨識模型維持不動，是 #22 訂下的規矩：兩顆一起換，效果就分不開了。

判讀時務必先把「畫面上根本沒有文字」的圖挑掉 —— 在那些圖上讀不到是正確行為，計入會把
誤報算成成功。實務做法是只採計讀到 10 個字元以上的結果。

升級模型或函式庫時，先用同一批圖跑一次存起來當基準，改完再跑一次比對；
PP-OCRv6 那次就是靠這個確認 `RapidOcrNet 1.0.1 → 3.0.0` 的輸出逐字元相同。

## 產生測試截圖

`capture.ps1` — 用 .NET `CopyFromScreen` 把螢幕區域存成 PNG（模擬使用者框選）：

```powershell
# 全螢幕
./capture.ps1 -Out full.png
# 指定區域 X Y W H（可做「單行＋上下殘缺」這類情境）
./capture.ps1 -Out strip.png -X 795 -Y 632 -W 960 -H 30
```

`testpage.html` — 一頁可控的英文版面（大標題、多行內文、相鄰按鈕、等寬單行），方便重現特定情境。用瀏覽器開啟後再 `capture.ps1` 擷取。

典型流程：`Start-Process msedge <url>` → 等載入 → `capture.ps1` 擷取 → `OcrHarness.exe` 跑。

## 判讀重點

- **同一句被拆成多塊** → 同行合併失敗（看 `CanJoinSameLine` / `MergeSameLineFragments`）。
- **兩個獨立元件被併成一塊** → 合併過頭（多半是垂直交疊判斷）。
- `grouped blocks: 0` 通常代表截圖太薄/對比太低，OCR 沒辨識到 —— 重抓、給足垂直邊距即可。

## 備註

- 需要 ONNX 模型（建置時從 `src/OverTranslate/ocrmodels` 連結複製到輸出）。
- 翻譯步驟需連網；OCR 步驟純離線。
- 這是開發/除錯工具，不隨 app 發佈。
