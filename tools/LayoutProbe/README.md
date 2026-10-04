# LayoutProbe

截圖翻譯的**疊圖**與**工具列**的版面量測工具。它把「文字被畫在框裡的哪個位置」「兩個開關的兩半是不是等寬」「那顆藥丸滑多遠」這種**只能把視窗真的排出來才知道**的問題，變成一行指令與一組數字。

## 為什麼需要它

`OverlayWindow` 與 `ToolbarWindow` 的 XAML 都用了 `StaticResource`，載入時就需要 `Application.Current`，而單元測試專案刻意不引入行程層級狀態（理由寫在 `src/OverTranslate.Tests/AssemblyInfo.cs`）。**所以使用者真正看到的那兩個視窗，版面完全沒有自動化覆蓋。**

在這支工具進版控之前，每次要回答這類問題的做法都是「在暫存目錄開一支拋棄式 WPF exe，量完丟掉」——同一個工作流裡做了兩次（工具列版面一次、疊圖對齊一次），而丟掉的那兩支的內容就是這裡的全部。

它走的是主專案真正的 `OverlayWindow` / `ToolbarWindow`，不是另一份複製品。

## 它**不**做的事

- **不判斷對錯。** 只印值，不印「通過／失敗」。什麼數字才算對，屬於引用它的那份報告，不屬於量尺本身。這一點與 `WgcProbe` 一致。
- **不是回歸測試。** 沒有基準檔、沒有比對。要看 before/after，就自己改程式、各跑一次、比兩份輸出——本輪 Step 15 就是這樣用的。
- **不驗顏色、不驗畫面。** 它讀的是視覺樹上的數字（位置、大小、字級、對齊列舉值），不是像素。字有沒有被裁掉、底色配得對不對，它答不了。
- **不驗分組。** 那是 `OcrHarness --group-explain` 的工作。
- **不涵蓋標記（annotation）層、拖曳、動畫中途的任何一格。** 藥丸的行程是等動畫**結束**之後讀的。

## 使用方式

```bash
dotnet build tools/LayoutProbe/LayoutProbe.csproj -c Debug
tools/LayoutProbe/bin/Debug/net8.0-windows10.0.26100.0/win-x64/LayoutProbe.exe overlay
tools/LayoutProbe/bin/Debug/net8.0-windows10.0.26100.0/win-x64/LayoutProbe.exe toolbar
```

### `overlay` — 疊圖把譯文畫在哪裡

三個 fixture：

| fixture | 它能回答什麼 |
|---|---|
| `intent` | **兩個幾何完全相同、文字完全相同的框**，一個 `Default` 一個 `GroupReflow`。任何差異只能來自 intent，不必相信任何人的眼睛。框刻意比文字高很多——在文字塞滿的框裡，靠上與置中是同一個位置，看不出差別 |
| `room` | 同一對，但右邊留白，而且譯文長到單行放不下。**「不向右擴張」只有在有地方可以擴張的時候才看得見** |
| `vertical` | 直排擷取，走的是另一個 renderer（`BuildVerticalOverlay`）。放在這裡是為了讓「橫排的改動沒有碰到直排」變成量出來的，而不是讀 diff 讀出來的 |

輸出的每一欄：

```
[0] intent=GroupReflow box w=244.0 h=104.8 left=2078.0 top=30.0
    fontSize=25.4 textH=33.8 textTopInBox=35.5 valign=Center talign=Left wrap=Wrap
```

- `box w/h`、`left/top` —— 氣泡本身的大小與落點
- `textTopInBox` —— **文字在自己框裡的起始位置**。靠上會等於上內距；置中會等於 `(框高 − 上下內距 − 文字高) / 2 + 上內距`
- `fontSize` —— 塞不下時是縮字級還是換行，從這裡看得出來
- `valign` / `talign` / `wrap` —— 直接讀回 `TextBlock` 的屬性，省得從位置反推

### `toolbar` — 工具列的兩個 segmented control

印視窗尺寸、開機停在哪一個模式、四個半邊的寬度與 tooltip，以及兩顆藥丸的位置；然後**按下另一半**、等動畫結束、再印一次。

- 兩個 tray 各自的兩半應該等寬（欄寬是共享的），而那個寬度不是寫死的數字——它由當前介面語言的標籤文字決定，所以只能量
- 藥丸的**一次完整行程等於一欄寬**，也就是輸出裡 `LayoutModeThumb w=` 的那個值；`shiftX` 在兩次列印之間應該在 `0` 與那個值之間移動

> **它會按按鈕，但會把設定放回去。** 工具列是在「被按下」時把選擇寫進設定檔的，所以這支 probe 一開始會記下 `Capture.LayoutMode` 與 `Capture.VerticalText`，結束時寫回原值並在最後一行印出來。**量尺不該移動它量的東西。**

## 已知限制

- **視窗會真的被開出來。** 工具列開在 `(-4000, -4000)`；疊圖橫跨整個虛擬桌面，會在畫面上閃一下。
- **數字與 DPI 有關。** 這支工具用了與應用程式相同的 `app.manifest`（PerMonitorV2），所以它量到的座標系與應用程式一致；但不同縮放比例的機器上絕對值本來就不同，**跨機器比對絕對值是沒有意義的**，要比就在同一台機器上比 before/after。
- **`overlay` 的 fixture 是手寫的，不是真的 OCR 結果。** 它要回答的是排版政策，不是辨識品質。
