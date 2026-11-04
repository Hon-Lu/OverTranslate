# 測試更新提醒（不碰真實 GitHub Release）

更新提醒的行為 —— 啟動彈窗、跳過此版本、側邊欄的「有新版本」、每小時輪詢 —— 全都在**下載任何東西之前**就決定完了。
要驗證它們，需要有一個「新版本」可以反應，但發一個真的 Release 會推給正在使用的使用者。

因此 `UpdateService` 內建兩個環境變數鉤子，可以憑空捏造一個版本，完全不連網。
**不設就完全不生效**，正式版行為不受影響。

> 這不是可有可無的。`UpdateService.CheckAsync()` 第一件事是 `if (!manager.IsInstalled) return null;`，
> 而 `dotnet run` 或 F5 跑出來的 build 永遠不是 Velopack 安裝版。
> 沒有這些鉤子，開發環境下連彈窗都看不到。

---

## 環境變數

| 變數 | 值 | 作用 |
|------|----|------|
| `OVERTRANSLATE_FAKE_UPDATE` | 版本號，例如 `99.0.0` | 假裝找到這個版本。未設 = 走真實檢查 |
| `OVERTRANSLATE_FAKE_UPDATE_AFTER` | 次數，例如 `1` | 前 N 次檢查裝作沒有，第 N+1 次才「發現」。未設 = 第一次就有 |
| `OVERTRANSLATE_UPDATE_POLL_SECONDS` | 秒數，例如 `10` | 把輪詢間隔從 1 小時縮短。未設 = 1 小時 |
| `OVERTRANSLATE_FAKE_UPDATE_FROM` | 版本號，例如 `2.7.0` | 假裝目前是這個版本，用來看跨多版的更新內容（內容仍從 GitHub 抓）。未設 = 實際版本 |
| `OVERTRANSLATE_SHOW_RELEASE_HISTORY` | 任意值，例如 `1` | 啟動時開啟「更新紀錄」畫面（目前還沒有入口）。未設 = 不開 |

以下指令都用 **User 層級**（`'User'`），所以重開終端機、從 Visual Studio 啟動、直接雙擊 exe 都吃得到。

> **設定後要開一個新的 PowerShell 視窗才會生效**，已開著的 process 不會重新讀取。Visual Studio 要整個重啟。
>
> **測完務必跑最後的「清除」**，否則之後每次啟動都會看到假更新。

---

## 情境一：啟動彈窗 + 跳過此版本

```powershell
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_FAKE_UPDATE', '99.0.0', 'User')
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_FAKE_UPDATE_AFTER', $null, 'User')
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_UPDATE_POLL_SECONDS', $null, 'User')
```

新開 PowerShell 視窗後：

```powershell
cd C:\Users\asd88\Desktop\Code\OverTranslate
dotnet run --project src\OverTranslate
```

驗收：

1. 啟動後彈出更新視窗，顯示「目前版本 vs 99.0.0」。
2. 按「跳過此版本，不要再提醒我」→ 視窗關閉。
3. 關掉程式重跑 → **不該再彈窗**。
4. 但打開主視窗，側邊欄「關於」上方要有「有新版本 99.0.0」，點下去能重新開啟更新視窗。
   （跳過只關掉打擾，不關掉入口。）

接著驗證「只跳過該版及更舊」：

```powershell
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_FAKE_UPDATE', '99.1.0', 'User')
```

新開視窗重跑 → **應該又彈窗**，因為 99.1.0 比跳過的 99.0.0 新。

---

## 情境二：輪詢靜默亮起側邊欄

這是這次改動的核心行為：**程式開著不關的使用者也能得知新版，但過程完全不打擾**。

```powershell
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_FAKE_UPDATE', '99.0.0', 'User')
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_FAKE_UPDATE_AFTER', '1', 'User')
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_UPDATE_POLL_SECONDS', '10', 'User')
```

跑之前**先確認「跳過此版本」是空的**（見下方重置），否則情境一的殘留會讓結果看起來像壞掉。

新開視窗跑起來後，驗收：

1. 啟動時**不該有任何彈窗**（第一次檢查被 `_AFTER=1` 擋掉，什麼都沒找到）。
2. 打開主視窗停在那裡，約 10 秒後「有新版本 99.0.0」自己出現在「關於」上方。
3. 全程**沒有**彈窗、toast 或任何主動提示。
4. 點那一列，才開啟更新視窗。

---

## 重置「跳過此版本」

只改這一個欄位，不動 API Key 等其他設定。**執行前請先關閉 OverTranslate**，否則程式結束時可能把記憶體中的舊值寫回去。

```powershell
$p = "$env:APPDATA\OverTranslate\appsettings.json"
$j = Get-Content $p -Raw | ConvertFrom-Json
$j | Add-Member -NotePropertyName SkippedUpdateVersion -NotePropertyValue '' -Force
[IO.File]::WriteAllText($p, ($j | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
```

> 不要用「刪掉整個 `appsettings.json`」的做法 —— 那會把 API Key、快捷鍵、翻譯來源全部一起清掉。

---

## 清除（測完必跑）

```powershell
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_FAKE_UPDATE', $null, 'User')
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_FAKE_UPDATE_AFTER', $null, 'User')
[Environment]::SetEnvironmentVariable('OVERTRANSLATE_UPDATE_POLL_SECONDS', $null, 'User')
```

再跑一次上面的「重置『跳過此版本』」，設定檔就跟沒測過一樣。

---

## 確認目前狀態

```powershell
'OVERTRANSLATE_FAKE_UPDATE','OVERTRANSLATE_FAKE_UPDATE_AFTER','OVERTRANSLATE_UPDATE_POLL_SECONDS' |
    ForEach-Object { "{0} = {1}" -f $_, [Environment]::GetEnvironmentVariable($_, 'User') }
(Get-Content "$env:APPDATA\OverTranslate\appsettings.json" -Raw | ConvertFrom-Json).SkippedUpdateVersion
```

四行都空白 = 環境乾淨。

---

## 這套方法測不到什麼

**實際的下載與套用**。假版本背後沒有真的 package，按「立即更新」會失敗，並在視窗內顯示錯誤訊息 —— 這是預期的。

要驗證那段流程（`UpdateService.DownloadAndApplyAsync`、進度條、`ApplyUpdatesAndRestart`），
需要一個真的 package。壓一個 tag 讓 CI 發出 pre-release，再用 `OVERTRANSLATE_UPDATE_PRERELEASE`
看到它即可，一般使用者全程看不到 —— 見 [TEST-UPDATE-PRERELEASE.md](TEST-UPDATE-PRERELEASE.md)。

發布流程見 [PUBLISH.md](PUBLISH.md)。
