# 在測試倉排練（極少用）

**先讀 [TEST-UPDATE-PRERELEASE.md](TEST-UPDATE-PRERELEASE.md)。** 平常驗更新用那一套就好 ——
CI 發的 pre-release 一般使用者看不到，測到的又是他們之後會拿到的同一批二進位。

這份文件是給一種情況用的：**你要改的是發布流程本身**（asset 命名、feed 結構、workflow 邏輯），
希望連一個 pre-release 都不要出現在主倉。那就把更新來源整個指到另一個倉。

私有倉 [OverTranslate-Ops](https://github.com/Hon-Lu/OverTranslate-Ops) 沒有任何使用者，
適合當這個角色。

---

## 環境變數

| 變數 | 值 | 說明 |
|------|----|------|
| `OVERTRANSLATE_UPDATE_REPO` | `https://github.com/Hon-Lu/OverTranslate-Ops` | 更新來源改指這個倉。**未設 = 照舊走主倉** |
| `OVERTRANSLATE_UPDATE_TOKEN` | GitHub PAT | 讀私有倉用。公開倉可省略 |

走的仍是正式的 `GithubSource` —— release 列表、asset 查找、HTTP 下載全都是出貨的程式碼，
差別只有倉庫。

已驗證：**私有倉可行，不需要公開。** Velopack 對 feed 請求與 asset 下載都帶 `Authorization`，
連跳轉到 GitHub CDN 那一段都正常。token 直接拿現成的：

```powershell
$env:OVERTRANSLATE_UPDATE_TOKEN = gh auth token
```

---

## 步驟

版號選「比主倉最新版低一階 → 主倉最新版」（例如 `1.6.9` → `1.7.0`）。
排練完機器會停在後者，跟排練前一樣，不會卡住之後的真實更新。

```powershell
# 1. 打兩個版本到同一個資料夾（第二包要靠第一包產生 delta，順序不能反）
.\publish-velopack.ps1 -Version 1.6.9 -OutputDir D:\ot-staging
.\publish-velopack.ps1 -SkipPublish -Version 1.7.0 -OutputDir D:\ot-staging

# 2. 把「新版」發到測試倉（舊版的 full 不用傳，本機安裝時就有了）
gh release create staging-1.7.0 --repo Hon-Lu/OverTranslate-Ops `
    --title "staging 1.7.0（排練用）" --notes "驗證更新流程，與使用者無關。" `
    D:\ot-staging\releases.win.json `
    D:\ot-staging\OverTranslate-1.7.0-full.nupkg `
    D:\ot-staging\OverTranslate-1.7.0-delta.nupkg

# 3. 安裝「舊版」，裝完先完全關掉（含系統匣）
#    Setup.exe 會被第二包覆蓋成新版的，要裝舊版得把 1.6.9 單獨再打一次到別的資料夾

# 4. 開新的 PowerShell，指向測試倉啟動
$env:OVERTRANSLATE_UPDATE_REPO  = "https://github.com/Hon-Lu/OverTranslate-Ops"
$env:OVERTRANSLATE_UPDATE_TOKEN = gh auth token
& "$env:LocalAppData\OverTranslate\current\OverTranslate.exe"
```

驗收與清除方式同 [TEST-UPDATE-PRERELEASE.md](TEST-UPDATE-PRERELEASE.md)，
另外記得把測試倉的 release 刪掉：

```powershell
gh release delete staging-1.7.0 --repo Hon-Lu/OverTranslate-Ops --cleanup-tag --yes
```

> 用 `$env:` 設在當前視窗就好。設成 User 層級又忘了清，測試倉的 release 一刪，
> 你的程式就會變成永遠檢查不到更新。

---

## 測不到什麼

**主倉本身。** 排練驗的是「更新機制與這一版的套件沒問題」，不是「主倉的 release 建對了」。
真正要發布時仍然照 [PUBLISH.md](PUBLISH.md) 走。
