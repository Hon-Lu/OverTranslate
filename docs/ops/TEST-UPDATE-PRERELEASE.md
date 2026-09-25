# 測試 CI 發出來的 pre-release

壓一個版號 tag 之後，[release.yml](../../.github/workflows/release.yml)
會自動打包並建立一個 **pre-release**。

一般使用者看不到它——他們的更新來源是 `GithubSource(prerelease: false)`，pre-release 被過濾掉。
要在自己機器上看到，就設下面這個環境變數。

確認沒問題後，到那個 release 按 **Edit release** 取消勾選 *Set as a pre-release*，
使用者才會收到。**你測到的與他們收到的是同一批檔案**，不是重打的另一包。

---

## 環境變數

| 變數 | 值 | 作用 |
|------|----|------|
| `OVERTRANSLATE_UPDATE_PRERELEASE` | `1`（`0` 以外的任何值） | 看得到 pre-release，**channel 維持 `win`** |

### 為什麼不是用 `OVERTRANSLATE_CHANNEL=beta`

因為那個變數同時動了兩件事：

```csharp
new GithubSource(repoUrl, token, prerelease: seesPrerelease)
//   channel = beta 會一次改掉 channel 與 prerelease 兩者
```

CI 打的是 **`win` channel**，release 裡放的是 `releases.win.json` 與 `OverTranslate-<版本>-full.nupkg`。
設 `OVERTRANSLATE_CHANNEL=beta` 的客戶端會去找 `releases.beta.json`——那個 release 裡沒有這個檔，
所以**什麼都不會發生**。

分開之後，你測的就是使用者取消勾選 pre-release 後拿到的同一批二進位（同樣的 SHA、同樣的 delta 基準）。

---

## 設定

以下都是 **User 層級**，所以重開終端機、從開始功能表啟動、直接雙擊 exe 都吃得到。

```powershell
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_UPDATE_PRERELEASE', '1', 'User')
```

查詢目前狀態：

```powershell
[Environment]::GetEnvironmentVariable('OVERTRANSLATE_UPDATE_PRERELEASE', 'User')
```

**清除（測完務必執行）：**

```powershell
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_UPDATE_PRERELEASE', $null, 'User')
```

> **設定後要開一個新的終端機視窗才會生效**，已經在跑的 process 不會重新讀取。
> 程式也要完全關掉再開（含系統匣圖示）——它有單一實例機制，沒關乾淨的話新啟動的
> process 只會叫醒舊的那個，環境變數不會生效。

> ⚠️ **忘了清掉的後果**：這台機器會一直看得到 pre-release。哪天 CI 發了一個還沒驗過的測試包，
> 你自己的機器就會跳出更新提示並裝上去。測完就清。

只想在單次測試中生效、不想留在系統裡的話，改成只設在當前視窗：

```powershell
$env:OVERTRANSLATE_UPDATE_PRERELEASE = "1"
& "$env:LocalAppData\OverTranslate\current\OverTranslate.exe"
```

更新後 Velopack 重啟的 process 會繼承這個變數，所以測試過程不受影響；
關掉那個視窗就自動失效，不必記得清。

---

## 流程

### 1. 準備一個「舊版」起點

要驗的是**使用者從他們手上那一版升上來會不會有問題**，所以起點應該是線上最新的穩定版，
而不是今天新建的 build。真正執行那次更新的是使用者手上那份舊程式碼，不是新寫的。

那一版沒有 `OVERTRANSLATE_UPDATE_PRERELEASE`，所以要用它的 commit 重建一份、只加上這個開關：

```powershell
# 以 1.7.0 為例，b0bbef9 是它的 commit
git worktree add --detach D:\wt-170 b0bbef9
# 在 D:\wt-170 的 UpdateService.CreateManager 加上 prerelease 覆寫那幾行，然後：
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 `
    -ProjectPath D:\wt-170\src\OverTranslate\OverTranslate.csproj `
    -PublishDir  D:\wt-170\src\OverTranslate\bin\Publish `
    -IconPath    D:\wt-170\src\OverTranslate\icons\icon_256.ico `
    -Version 1.7.0 -OutputDir D:\ot-test
```

建完對一下 `-full.nupkg` 的大小與線上那一版差多少。差幾 KB 是正常的（新增的程式碼 + 編譯時戳），
**差幾十 MB 代表建錯了**——多半是自封式沒生效，腳本裡的 `coreclr.dll` 檢查會擋下來。

裝起來，**先不要設環境變數**。這時它的行為與真實的線上版本完全相同。

### 2. 壓 tag 讓 CI 出包

```powershell
git tag 2.0.0-beta.0
git push origin 2.0.0-beta.0
```

tag 沿用本倉慣例：**不加 `v` 前綴**（`1.7.0`、`1.6.1-beta.1`，歷來每一個都是如此）。
`v` 前綴也接受並在解析時去掉，純粹是防手滑。

> `1.6.0` 那次的 release **標題**寫成 `v1.6.0`，但 tag 本身是 `1.6.0`。標題怎麼寫不影響任何事。

觸發條件是 `[0-9]*` 或 `v[0-9]*` —— 不是 `*`，所以你壓別的 tag（標記某個 commit、備份用的）
不會誤觸發一次 370 MB 的打包。

版號以 tag 為準，csproj 的 `<Version>` 不參與（不一致時只警告）。

### 客戶端不看 tag

更新流程完全不碰 git tag：

```
Releases API 列表 → 依 prerelease 旗標過濾選出一個 release
                  → 下載該 release 的 releases.<channel>.json
                  → 版號來自打包時寫進套件的 packVersion
```

所以 tag 叫什麼都不影響使用者，也不影響舊版本能不能升上來。tag 唯一的作用是在 workflow 裡
決定餵給 `vpk` 的版號。

跑完看一下 Actions 的 log，特別是「抓上一版 full 包」那一步——它是 `continue-on-error`，
失敗不會擋住發布，但會導致這一版沒有 delta，使用者要下載完整包。

### 3. 開啟 pre-release 可見性，測更新

用上面任一種設定方式，然後啟動程式。

驗收：

- 跳出更新視窗，顯示 1.7.0 → 2.0.0-beta.0
- 按「立即更新」→ 進度條跑動 → 程式自己重啟
- 重啟後是新版本，設定（API Key、快捷鍵）都還在

```powershell
([xml][IO.File]::ReadAllText("$env:LocalAppData\OverTranslate\current\sq.version")).package.metadata |
    Select-Object id, version, channel
```

### 4. 收尾

清掉環境變數（見上），並把測試用的 pre-release 與 tag 刪掉：

```powershell
gh release delete 2.0.0-beta.0 --repo Hon-Lu/OverTranslate --cleanup-tag --yes
```

---

## 版號注意事項

- **測試版號不會影響之後的正式版**。`2.0.0-beta.0 < 2.0.0`，而防呆只擋完全相同的版號。
- 但 **beta 的 base 版號要等於你打算發的那個正式版號**。走到 `2.0.0-beta.5` 之後才改主意想發 `1.8.0`，
  停在 beta 的機器就再也升不上去了（`1.8.0 < 2.0.0-beta.5`），而且不會報錯，只顯示「已是最新版本」。
- `vpk download github` 不加 `--pre`，抓的永遠是最新**穩定版**。所以連續發 `beta.0`、`beta.1` 時，
  兩者的 delta 基準都是同一個穩定版，測試者從 `beta.0` 升 `beta.1` 會下載完整包。這是刻意的：
  加上 `--pre` 會讓正式版的 delta 基準也變成 pre-release，那更糟。

## 相關文件

- [PUBLISH.md](PUBLISH.md)：發版流程（CI 正常流程，以及 CI 掛掉時的手動退路）
- [TEST-UPDATE-NOTIFICATION.md](TEST-UPDATE-NOTIFICATION.md)：只測更新提醒 UI，不需要任何 release
- [TEST-UPDATE-STAGING.md](TEST-UPDATE-STAGING.md)：極少用。要改發布流程本身，且不希望主倉出現任何 release
