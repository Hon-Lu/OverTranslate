# 打包與發布

發版由 CI 負責：**壓一個 tag，GitHub Actions 打包並建立一個 pre-release**。
把它轉成正式版是唯一會推給使用者的動作，而且必須由人手動做。

本專案以 **自封式（self-contained）** 發布，使用者**不需另外安裝 .NET 8 Runtime**。

---

## 一、正常流程

### 1. 調版號並推上 main

```
src\OverTranslate\OverTranslate.csproj  →  <Version>2.0.0</Version>
```

> CI 的版號其實**以 tag 為準**，csproj 不參與（不一致只警告）。這一步是為了讓程式碼裡的版號
> 與發出去的版本一致；純測打包流程時可以跳過，直接壓一個不同的 tag。

### 2. 壓 tag

```powershell
git tag 2.0.0
git push origin 2.0.0
```

tag **不加 `v` 前綴**（沿用本倉慣例）。觸發條件是 `[0-9]*` 或 `v[0-9]*`，
所以標記用的 tag 不會誤觸發一次 370 MB 的打包。

### 3. 等 CI（約 2～10 分鐘）

[`.github/workflows/release.yml`](../../.github/workflows/release.yml) 會：

- 用 `vpk download github` 抓線上最新**正式版**的 full 包當 delta 基準（runner 每次都是全新的）
- clone 並建置自用 fork 的 vpk（`--noStub`，見[五、啟動器 stub 已經拿掉了](#五啟動器-stub-已經拿掉了210)）
- `dotnet publish`（自封式）+ `vpk pack`，一併產出 portable
- 比對 stub 與 `Update.exe` 的雜湊，變了就在 run 摘要頁留一條黃色警示 —— **只提醒，不會擋住發布**
- 建立 **pre-release**，附上 `releases.win.json`、`-full.nupkg`、`-delta.nupkg`、
  `Setup.exe`、`Portable.zip`

這一步失敗最常見的原因是**版號沒有比線上最新正式版高** —— `vpk` 會拒絕打包：

```
[FTL] There is a release in channel win which is equal or greater to the current version
```

### 4. 驗更新

見 [TEST-UPDATE-PRERELEASE.md](TEST-UPDATE-PRERELEASE.md)。一般使用者看不到 pre-release，
要在自己機器上看到得設 `OVERTRANSLATE_UPDATE_PRERELEASE=1`。

### 5. 轉成正式版

到那個 release 按 **Edit release**，取消勾選 *Set as a pre-release*。

**使用者拿到的就是你剛剛驗過的同一批檔案**，不是重打的另一包。

---

## 二、版號規則

```
1.6.0  <  1.6.1-beta.1  <  1.6.1  <  1.6.2
```

- 版號**只能往上**。客戶端的 `AllowVersionDowngrade` 是 `False`，`vpk pack` 也拒絕打包
  比既有版本低的版號。
- 預發行版的 base 版號要**等於你打算發的那個正式版號**。走到 `2.0.0-beta.5` 之後才改主意
  想發 `1.8.0`，停在那個版本的機器就再也升不上去了（`1.8.0 < 2.0.0-beta.5`），
  而且不會報錯，只顯示「已是最新版本」。
- ⚠️ 預發行版千萬別直接用不帶後綴的版號（例如拿 `2.0.0` 當測試版），
  否則之後就無法再發「更新的正式 2.0.0」。

---

## 三、手動發版（CI 掛掉時的退路）

腳本用 `$PSScriptRoot` 解析相對路徑，**不看當前工作目錄**，從哪裡呼叫都可以。

> **先決條件**：打包走的是自用 fork 的 vpk，不是 `dotnet tool install -g vpk` 裝的那顆
> （為什麼見[五、啟動器 stub 已經拿掉了](#五啟動器-stub-已經拿掉了210)）。腳本預設找
> `..\velopack\build\Release\net10.0\vpk.exe`，也就是 fork 與本倉並排 clone 的位置；
> 放在別處就用 `-VpkPath` 指，或設環境變數 `OVERTRANSLATE_VPK_PATH`。找不到會直接失敗，
> 不會安靜地退回官方版。

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1
```

- 版號取自 csproj 的 `<Version>`，或用 `-Version` 覆寫
- 會先清空 `src\OverTranslate\bin\Publish` 再自封式 publish，最後 `vpk pack`
- `appsettings.json` 不會被打包進去（會覆蓋使用者既有設定）
- 一併產出 `OverTranslate-<channel>-Portable.zip`。portable 與安裝版**共用同一條更新 feed** ——
  `releases.<channel>.json` 完全不因 portable 而改變 —— 所以它一樣能自動更新，
  前提是 zip 有跟其他檔案一起上傳到同一個 Release
- 已手動 publish、只想重新打包時加 `-SkipPublish`
- 打包完可以跑 `.\check-release-hashes.ps1` 看 stub 與 `Update.exe` 的雜湊有沒有變（CI 會自動跑）

輸出在 `artifacts\releases\`（未版控）。**不要期待它一直在** —— Velopack 靠裡面的舊 full 包
產生 delta，換機器或誤刪之後要先抓回來：

```powershell
vpk download github --repoUrl https://github.com/asd880921/OverTranslate --channel win --outputDir .\artifacts\releases
```

上傳到 GitHub Release 的檔案（**勾選 Set as a pre-release**，確認後再取消）：

- `releases.win.json`
- `OverTranslate-win-Setup.exe`
- `OverTranslate-win-Portable.zip`
- `OverTranslate-<版本>-full.nupkg`（及 `-delta.nupkg`，若有）

---

## 四、beta channel（現在很少用到）

除了 `win`，還有一條獨立的 `beta` 管線，由環境變數切換：

```powershell
[Environment]::SetEnvironmentVariable("OVERTRANSLATE_CHANNEL", "beta", "User")   # 訂閱
[Environment]::SetEnvironmentVariable("OVERTRANSLATE_CHANNEL", $null, "User")    # 退出
```

打包時加 `-Channel beta` 並用 `-Version` 指定帶後綴的版號：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 -Channel beta -Version 2.0.0-beta.1
```

**但 CI 的 pre-release 流程已經涵蓋了它原本的用途，而且更好** ——
CI 打的是 `win` channel，你測到的就是使用者會拿到的同一批二進位；
走 beta channel 測的是 `-beta-full.nupkg`，內容相同但 SHA 與 delta 基準都不一樣，
嚴格說沒測到同一顆。

> ### ⚠️ 發正式版**不會**讓 beta 訂閱者升上去
>
> 兩條 channel 是各自獨立的 feed：訂在 beta 的機器只讀 `releases.beta.json`，
> 而正式發布只上傳 `releases.win.json`。版號再大也沒用，**它根本看不到那個包**。
>
> 實例：`releases.win.json` 的最新是 `1.7.0`，而 `releases.beta.json` 還停在 `1.6.1-beta.1`
> —— 訂在 beta 的機器從來沒收到過 1.7.0。
>
> 要讓 beta 訂閱者跟上，發正式版時**兩條都打**，兩組檔案上傳到同一個 Release：
>
> ```powershell
> powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1
> powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 `
>     -SkipPublish -Channel beta -Version 2.0.0
> ```
>
> （腳本會警告「beta channel 但版號不含預發行後綴」，這裡是刻意的，忽略即可。）

---

## 五、啟動器 stub 已經拿掉了（#210）

以前免安裝包根目錄長這樣，前兩顆是 Velopack 的產物、沒有受信任 CA 的簽章，也是 Defender 會報
`Trojan:Win32/Wacatac.B!ml` 的那兩顆：

```
OverTranslate-win-Portable.zip
├── OverTranslate.exe      ← Velopack 的啟動器 stub   ← 已換掉
├── Update.exe             ← 自動更新
└── current\
    └── OverTranslate.exe  ← 程式本體
```

stub 是誤判最集中的地方（實測：公司電腦下載免安裝版、解壓縮當下就被攔）。它的內容是
「vendor 的 stub 二進位 + 主程式的整棵資源樹」，一顆沒人見過的未簽章原生啟動器 —— 正是
AV 啟發式最愛的形狀。與其想辦法讓它累積信譽，不如讓它不存在。

### 現在的樣子

打包帶 `--noStub`，Velopack 不再產生它的 stub。頂替它位置的是**我們自己的啟動器** ——
它在 `vpk pack` 之前就以 Velopack 約定的檔名 `OverTranslate_ExecutionStub.exe` 放進 packDir，
於是拿到那個檔名帶來的三個行為，而這三個剛好都是我們要的：

- 它會被打進 `.nupkg`，所以**自動更新送得到它**
- 安裝與**每一次**套用更新，更新器都會無條件把它解回**安裝根目錄**、改名成 `OverTranslate.exe`
  （`Bundle.extract_stubs_to_dir`）—— 使用者手上那顆舊的 Velopack stub 會被**直接覆蓋掉**
- 解 `current\` 的那條路徑會跳過這個檔名（`Bundle.extract_lib_contents_to_path`），
  所以 `current\` 裡不會多出一份

| 使用情境 | 根目錄那顆 `OverTranslate.exe` 是誰放的 |
|---|---|
| 安裝版（Setup） | 安裝時由更新器從套件裡解出來 |
| 免安裝版（Portable） | 打包腳本直接放進 zip 根目錄（免安裝包沒有經過更新器） |
| 更新後（兩者相同） | 更新器每一版都重解一次，等於每次更新都把根目錄那顆校正回我們這顆 |

安裝版的捷徑本來就指向 `current\OverTranslate.exe`，不受這件事影響。

### 我們自己的啟動器

原始碼與編好的二進位都在 [`tools/launcher/`](../../tools/launcher/README.md)，它只做一件事：
把 `current\OverTranslate.exe` 叫起來。與 Velopack stub 的差別是整件事的重點：

| | Velopack stub | 我們這顆 |
|---|---|---|
| 版本資源 | 出廠全空，打包時塞進**主程式的**身分 | 編譯期寫死，誠實說自己是 Launcher |
| 誰放進去的 | 打包工具事後改寫二進位 | 編譯器，之後沒有任何工具再碰它 |
| 跨版本位元組 | 每次發版都變 | 永遠不變 |
| VirusTotal | —— | **0/71**（含 Microsoft），未簽章版 1/71（SecureAge，那家對任何不認識的未簽章檔都報毒） |

二進位跟著原始碼一起進版控，所以 CI 不需要 Rust 工具鏈；建置是可重現的（`/Brepro`），任何人都能
自己編一顆對雜湊。`publish-velopack.ps1` 在 `vpk pack` 之前把它複製進 packDir（vpk 會連同其他 PE
一起簽），打包完再用同一支 signtool 簽一份放進免安裝包根目錄 —— 簽章不加時戳，所以兩邊簽出來是
**同樣的位元組**（已實測）。

> **換應用程式圖示時要記得重編它**，否則它會帶著舊圖示 —— 沒有其他檢查抓得到，因為它的雜湊
> 不會因為 `app.ico` 被換掉而改變。打包腳本會比對 `app.ico` 與 `tools/launcher/dist/build-info.txt`
> 裡記錄的雜湊，對不上就警告。重編步驟見該資料夾的 README。

免安裝包還有一個收尾動作：vpk 是把整個 packDir 複製進 zip 的 `current\`，原本會再把 stub 搬到
根目錄，而 `--noStub` 把那個搬移跳掉了，所以打包腳本要把 `current\` 裡那份多餘的刪掉。安裝版
沒有這個問題（更新器解 `current\` 時本來就會跳過 `*_ExecutionStub.exe`）。

免安裝 zip 不在任何校驗鏈裡（`releases.<channel>.json` 只記 nupkg 的 SHA256），所以打包後往裡面
塞檔案是安全的。

### 實測（2026-09-23 本機）

| 情境 | 結果 |
|---|---|
| 打包 | `Skipping launcher stub, --noStub was specified.` |
| `.nupkg` | 有 `lib/app/OverTranslate_ExecutionStub.exe`，347,304 bytes，`1dd826ad…` —— 與免安裝包根目錄那顆**位元組相同** |
| 免安裝包 | root 是 `.portable`、`Update.exe`、我們的 `OverTranslate.exe`；整包沒有任何 `_ExecutionStub` |
| 從「根目錄還是舊 Velopack stub」的安裝版更新上來 | log：`Extracting stub 'OverTranslate_ExecutionStub.exe' to '…\OverTranslate.exe'`，根目錄那顆**被換成 `1dd826ad…`**；`current\` 裡沒有 `_ExecutionStub`（log：`Skipped Stub (obsolete)`） |
| 只換版號（1.6.1 → 1.6.2）再打一次 | 啟動器與 `Update.exe` 的雜湊完全相同 |

> **舊使用者手上那顆孤兒 stub 會自己消失**：更新器每次套用更新都會把套件裡的啟動器解回根目錄、
> 覆蓋同名檔案，所以只要更新過一次，那顆誤判來源就不在硬碟上了 —— 不需要使用者自己去刪，也不
> 需要現場的 `Update.exe` 先更新（這個行為本來就寫在舊版更新器裡）。

### 還在的那顆：`Update.exe`

它留下來了（自動更新要靠它），內容是「vendor 的 update.exe + 應用程式圖示 + 我們的自簽章」，
與版號、commit 都無關，所以雜湊很穩定。`check-release-hashes.ps1` 在打包後比對三個值，
**只提醒、不擋**：

1. 免安裝包根目錄 `OverTranslate.exe` —— 對不上要嘛是啟動器重編過，要嘛是 `--noStub` 失效、
   Velopack 的 stub 又跑回來佔了這個檔名
2. 免安裝包根目錄 `Update.exe`
3. 套件裡的 `OverTranslate_ExecutionStub.exe` —— **現有使用者更新後實際拿到的就是它**，
   要與第 1 項一致

順便檢查免安裝包的 `current\` 裡沒有殘留的 `_ExecutionStub`。

基準值（2026-09-23 本機實測，含自簽章，見[第七節](#七自簽憑證)）：

| 檔案 | 大小 | SHA256 |
|---|---|---|
| 啟動器（免安裝包根目錄 ＆ 套件內） | 347,304 | `1dd826ad50481ec7bffa57ad9cafe7c6dad79541009129dca2d1e4266a7a76bf` |
| `Update.exe` | 3,973,288 | `ae4a116a15e5cda0e423e8fca5f1b02326b502553397d709fc6a7090bc958e9d` |

會讓它們重算的只有三件事：換應用程式圖示、換 vpk 版本（vendor 二進位）、換簽章金鑰。
啟動器還多一個：重編 `tools/launcher`（含換 rustc）。

`Setup.exe` 每次都內嵌整包 nupkg，沒辦法穩定化，不在這個範圍內。

## 六、產物的來源證明（provenance attestation）

每次發版，CI 會對這幾個檔各產生一份 [GitHub artifact attestation](https://docs.github.com/actions/security-guides/using-artifact-attestations)：

- `OverTranslate-<版本>-full.nupkg`（與 `-delta.nupkg`，若有）
- `OverTranslate-win-Setup.exe`
- `OverTranslate-win-Portable.zip`
- `releases.win.json`
- 使用者硬碟上真正會被掃到的那兩顆：`Update.exe` 與 `current\OverTranslate.exe`
  （從免安裝包裡取出來單獨簽，因為證明了容器不等於證明裡面解出來的檔）

簽的是**檔案的 SHA256**，簽章者是 GitHub 的 OIDC 身分，記錄進 Sigstore 的公開透明日誌。
任何人下載後都能驗它是不是這個 repo 的這條 workflow 建出來的：

```powershell
gh attestation verify .\OverTranslate-win-Portable.zip --repo asd880921/OverTranslate
```

驗得出來的是「這一顆確實由 `asd880921/OverTranslate` 的 `release.yml` 在某個 commit 上產生」，
檔案被動過一個位元組就對不上。

### 它不是什麼

- **不是代碼簽章**。Windows 那邊完全不受影響，該跳的 SmartScreen 與「未知發行者」照跳。
  那要受信任 CA 簽發的憑證，是 #210 的階段 B。
- **自簽憑證不是替代方案**。自簽的根憑證不被信任，使用者看到的是簽章驗證失敗
  （`A certificate chain processed, but terminated in a root certificate which is not trusted`），
  與「檔案被竄改」長得一樣；而且竄改者可以自己做一張同名的自簽憑證重簽，名字證明不了事。
  attestation 的身分是 GitHub，不是自己宣稱的。

### 為什麼排在「發布 pre-release」之前

attestation 簽的是本機那幾個檔，跟上不上傳無關。排在建立 release 之前，萬一這一步掛掉，
release 還沒建出來，重跑整個 job 就好；排在後面的話 release 已經存在，重跑會被
「這個版號是否已經發布過」擋下來，那一版就永遠補不上 attestation 了。

---

## 七、自簽憑證

打包時會用一張**自簽**的代碼簽章憑證簽掉這些檔：

| 檔案 | 簽？ |
|---|---|
| `Update.exe`、`current\OverTranslate.exe` 與其他自家 DLL | 是（15 個） |
| 免安裝包根目錄的啟動器 | 是，但由 `publish-velopack.ps1` 在打包後單獨簽 |
| 微軟簽的 .NET 執行檔 | 否，vpk 會自動跳過已被信任簽章的檔 |
| `Setup.exe` | 是 |
| `.zip` / `.nupkg` / `releases.win.json` | 不能簽（非 PE），由 attestation 涵蓋 |

### 它不是什麼

**不會讓 Windows 少跳任何警告。** 自簽的根憑證不被信任，使用者看到的狀態是
`A certificate chain processed, but terminated in a root certificate which is not trusted`。
它的用途只有一個：**日後要判斷「使用者手上那顆是不是我出的」時，可以離線、不靠 GitHub 直接看**。
要讓作業系統自動信任，只有受信任 CA 一條路（#210 階段 B）。

### 為什麼不加時戳

時戳會讓相同內容每次簽出不同的位元組，stub 與 `Update.exe` 的雜湊就會每版重算一次，
[第五節](#五啟動器-stub-已經拿掉了210)講的 `Update.exe` 雜湊穩定性就沒了。不加時戳的代價是**憑證到期後，
過去所有版本的簽章會一起失效**，所以那張憑證的效期一次拉到 2049（X.509 的日期編碼分界，
再往後有相容性風險）。

### 憑證怎麼來的

私鑰只在本機產生，上傳到 GitHub 的是加密過的 PFX：

```powershell
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=Hon.Lu, O=OverTranslate" `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyAlgorithm RSA -KeyLength 4096 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date "2049-12-31")

$pfxPwd = Read-Host "PFX 密碼" -AsSecureString
Export-PfxCertificate -Cert $cert -FilePath "$HOME\overtranslate-signing.pfx" -Password $pfxPwd

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("$HOME\overtranslate-signing.pfx"))
gh secret set SIGNING_PFX_BASE64 --repo asd880921/OverTranslate --body $b64
gh secret set SIGNING_PFX_PASSWORD --repo asd880921/OverTranslate
```

`--body` 是刻意的：貼進終端機或網頁欄位容易混進 BOM、引號或被截斷，而那顆 secret 有七千多字，
肉眼看不出哪裡壞了。CI 端會先清掉空白、引號、BOM 與 PEM 標頭再解碼，解不開時會印出長度與開頭
（PKCS#12 的 base64 一定是 `MII` 開頭）好讓人判斷是哪一種壞法。

CI 會把 secret 還原成暫存 `.pfx`、匯入憑證存放區、立刻刪檔，之後只用**指紋**叫 signtool，
密碼不會出現在任何命令列上。**缺 secret 時 job 會直接失敗**——不要安靜地發一包沒簽的出去，
那包的雜湊會跟基準值對不上，使用者拿到的東西也與前一版不一致。

### 兩件必須記住的事

- **PFX 弄丟 = 換金鑰 = stub 與 `Update.exe` 的雜湊再重置一次**（而且舊版簽章與新版對不起來）。
  備份到離線的地方，別只留在桌面。
- **指紋要公開**。它是「事先的公開承諾」，日後要主張某顆檔不是我出的，靠的就是它。
  指紋不是秘密，可以直接寫在 README 或這份文件裡。

### 本機打包

不帶 `-CertThumbprint` 就不簽，隨手打包不需要動到憑證：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 -CertThumbprint <指紋>
```

### 目前這張憑證

```
Subject    : CN=Hon.Lu, O=OverTranslate
Thumbprint : 5817F971FCC333251C488FC90C4AF8F9208E9E1C
NotAfter   : 2049-12-31
Key        : RSA 4096 / SHA256
```

指紋不是秘密，**就是要公開的**：日後要主張某顆檔不是我們出的，靠的是它事先被公布過。

### 實測（2026-09-23，整條流程跑過四種情境）

| 情境 | stub | `Update.exe` |
|---|---|---|
| app 2.4.0 | 基準 | 基準 |
| app 2.5.0（改版號重建） | 相同 | 相同 |
| app 2.5.0 + 不同 commit | 相同 | 相同 |
| 憑證砍掉、從 PFX 重新匯入再簽 | 相同 | 相同 |

`current\OverTranslate.exe` 每版都會變——版號就寫在它裡面，本來就該變。
簽章後 stub 的版本資源仍然是凍結的 `1.0.0`，nupkg 裡那顆與免安裝包根目錄仍是同一顆。
