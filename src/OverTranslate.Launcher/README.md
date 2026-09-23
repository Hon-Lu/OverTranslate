# 根目錄的啟動器

Velopack 的版面是：安裝根目錄放 `Update.exe` 與 `current\`，主程式在 `current\` 裡面。
打包帶 `--noStub` 之後 Velopack 不再產生它自己的啟動器 stub（見
[PUBLISH.md 第五節](../../docs/ops/PUBLISH.md)），這顆就是頂替它的入口 —— 它只做一件事：
把 `current\OverTranslate.exe` 叫起來，然後自己結束。安裝版與免安裝包都有它，使用者自動更新
後也會拿到，而且會覆蓋掉舊版留在硬碟上的 Velopack stub。

## 為什麼不用 Velopack 出廠的 stub

那顆的版本資源是空的，打包時才被塞進主程式的整棵資源樹，所以它**宣稱自己是 OverTranslate、
卻是一顆在打包當下才被改寫的未簽章二進位** —— 那正是 Defender 的機器學習分類器
（`Trojan:Win32/Wacatac.B!ml`）盯上的形狀，而且不只 Defender，其他防毒也擋（#210）。

這顆反過來做：

| | Velopack stub | 這顆 |
|---|---|---|
| 版本資源 | 出廠全空，打包時塞進**主程式的**身分 | 編譯期寫死，誠實說自己是 Launcher |
| 誰放進去的 | 打包工具事後改寫二進位 | 編譯器，之後沒有任何工具再碰它 |
| 跨版本位元組 | 每次發版都變 | 永遠不變 |

實測（2026-09-23，VirusTotal）：簽章後 **0/71**，未簽章 1/71（只有 SecureAge，那家對任何不認識的
未簽章檔都報毒）。**Microsoft 兩邊都是 Undetected**。

---

## 什麼時候需要重編

平常發版**不需要**碰這裡 —— 打包腳本用的是 `dist\` 裡已經編好的那顆。只有下列情況要重編：

| 改動 | 要重編嗎 |
|---|---|
| **換應用程式圖示**（`src\OverTranslate\icons\app.ico`） | **要**，否則啟動器還帶著舊圖示 |
| 改 `src\main.rs` 的行為 | 要 |
| 改 `build.rs` 裡的版本資源字串 | 要 |
| 改 `launcher.manifest` | 要 |
| 改應用程式版號、改 commit、發新版 | 不用 |

> **換圖示這個坑沒有任何自動檢查抓得到**，因為啟動器的雜湊不會因為 `app.ico` 被換掉而改變 ——
> 它是版控裡那顆固定的二進位。所以 `publish-velopack.ps1` 會在打包時比對 `app.ico` 的雜湊與
> `dist\build-info.txt` 裡記錄的值，對不上就警告你該重編了。

## 重編的步驟

需要 Rust（msvc 工具鏈）與 Windows SDK 的 `rc.exe`（裝 Visual Studio 或 Build Tools 就有）。

```pwsh
cd src\OverTranslate.Launcher
cargo build --release

# 1. 複製產物到 dist\（版控裡那顆）
Copy-Item .\target\release\OverTranslate.exe .\dist\OverTranslate-launcher-unsigned.exe -Force

# 2. 驗證建置是可重現的：清乾淨再編一次，雜湊必須一樣
cargo clean
cargo build --release
(Get-FileHash .\target\release\OverTranslate.exe -Algorithm SHA256).Hash
(Get-FileHash .\dist\OverTranslate-launcher-unsigned.exe -Algorithm SHA256).Hash

# 3. 更新 dist\build-info.txt 裡的 rustc 版本、icon_sha、binary_sha、size
# 4. 更新 check-release-hashes.ps1 裡的啟動器基準值（簽章後的雜湊，打包一次就會印出來）
```

第 2 步不是形式 —— `.cargo\config.toml` 裡的 `/Brepro` 讓連結器以內容雜湊取代時間戳，**沒有它每次
重編出來的位元組都不同**（PE 標頭的連結時間戳）。這顆的全部價值就在於雜湊永遠不變，不可重現的
建置會讓這件事無從驗證。

> **rustc 版本會影響位元組。** 版控裡那顆是用 `dist\build-info.txt` 記錄的那個版本編的；換一個
> 版本的 rustc 重編，雜湊就會變（等於一次信譽重置）。要驗證版控裡這顆沒被動過手腳，得用同一個版本。

> **`launcher.manifest` 的換行也會影響位元組。** 它是被逐位元組嵌進資源段的，CRLF 與 LF 差 25 個
> bytes，編出來就是兩顆不同的檔。本倉 `core.autocrlf=true`，所以 `.gitattributes` 把它釘成
> `-text`（不轉換）—— **那一行拿掉，這裡的雜湊比對就會無聲地失效**。這個坑實際撞過。

建置**不受路徑影響**（已驗證：同一份原始碼在兩個不同目錄編出同一顆），所以不必把倉 clone 到特定位置。

## 為什麼編好的二進位也進版控

CI 因此不需要 Rust 工具鏈，而且沒有人能在無意間換掉這顆的位元組。建置是可重現的，所以任何人都能
自己編一顆來對雜湊，驗證版控裡這顆與原始碼相符。

## 出貨

`publish-velopack.ps1` 在 `vpk pack` **之前**把 `dist\` 那顆複製進 packDir，用 Velopack 約定的
檔名 `OverTranslate_ExecutionStub.exe` —— vpk 會連同其他 PE 一起簽（不加時戳，所以簽出來的位元組
固定），它也因此被打進 `.nupkg`。接下來分兩條路：

- **安裝與每一次更新**：更新器把套件裡的它解回安裝根目錄、改名成 `OverTranslate.exe`，
  覆蓋掉上一版留在那裡的東西（包含舊的 Velopack stub）。`current\` 不會多一份 —— 解 `current\`
  的那條路徑會跳過 `*_ExecutionStub.exe`。
- **免安裝包**：沒有經過更新器，所以打包腳本另外簽一份直接放進 zip 根目錄，並刪掉 `current\`
  裡那份多餘的（vpk 是把整個 packDir 複製進 `current\` 的）。

兩條路徑簽出來是同樣的位元組，`check-release-hashes.ps1` 會比對這件事。
