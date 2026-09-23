# 發版與維運文件

| 檔案 | 什麼時候看 |
|------|-----------|
| [PUBLISH.md](PUBLISH.md) | 要發版。CI 的正常流程，以及 CI 掛掉時的手動退路 |
| [TEST-UPDATE-PRERELEASE.md](TEST-UPDATE-PRERELEASE.md) | 要驗「使用者能不能順利更新到下一版」 |
| [TEST-UPDATE-NOTIFICATION.md](TEST-UPDATE-NOTIFICATION.md) | 要改更新提醒的 UI，需要一個假的新版本來觸發它 |
| [TEST-UPDATE-STAGING.md](TEST-UPDATE-STAGING.md) | 極少用。要改發布流程本身，且不希望主倉出現任何 release |

## 一句話版本

```
改 csproj 版號 → push → 壓 tag → CI 自動打包並建立 pre-release
                                        ↓
                            設環境變數，在自己機器上驗更新
                                        ↓
                    到 GitHub 取消勾選 pre-release ← 唯一會推給使用者的動作，必須由人做
```

## 相關檔案

- [`publish-velopack.ps1`](../../publish-velopack.ps1) —— 打包腳本，CI 與手動共用同一份
- [`check-release-hashes.ps1`](../../check-release-hashes.ps1) —— 比對啟動器（免安裝包根目錄與套件內）與 `Update.exe` 的雜湊，
  只提醒不擋（見 [PUBLISH.md 第五節](PUBLISH.md#五啟動器-stub-已經拿掉了210)）
- [`src/OverTranslate.Launcher/`](../../src/OverTranslate.Launcher/README.md) —— 安裝版與免安裝包根目錄那顆啟動器的原始碼與編好的二進位
- [`.github/workflows/release.yml`](../../.github/workflows/release.yml) —— 壓 tag 觸發的自動打包
- `artifacts/releases/` —— 本機打包輸出，未版控。**GitHub Release 才是唯一真相**，
  換機器或誤刪都能用 `vpk download github` 抓回來
