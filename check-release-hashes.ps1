<#
    看緊免安裝包根目錄那兩件事：啟動器 stub 有沒有跑回來，以及 Update.exe 的 SHA256 有沒有變。

    stub 是 #210 那個 Wacatac.B!ml 誤判的主要來源，現在用 `--noStub` 整個拿掉了；它要是再
    出現，代表旗標掉了或 fork 換了分支，那一版會把誤判帶回來。`Update.exe` 則還在，它帶的是
    自簽章、沒有受信任 CA 背書，所以累積不到發行者信譽，Defender 能給它的只有**按檔案雜湊**
    累積的那種——雜湊一換就從零開始。

    所以這裡**只提醒、不擋**：對不上不代表打包壞了，代表這一版的檔案信譽要重新養 ——
    那是發版的人該當場知道、而不是幾天後從使用者回報裡才發現的事。
#>
param(
    # 相對路徑以本腳本所在的專案根目錄為基準，與 publish-velopack.ps1 一致。
    [string]$OutputDir = ".\artifacts\releases",
    [string]$PackId = "OverTranslate",
    [string]$Channel = "win",
    [string]$MainExe = "OverTranslate.exe"
)

$ErrorActionPreference = "Stop"

# 基準值 —— 是「現在應該長這樣」的紀錄，不是校驗和。改了下面任何一項之後對不上都是正常的，
# 把這次印出來的新值貼回來即可：應用程式圖示、vendor 的 update.exe（換 vpk 版本就會換）、
# 簽章金鑰。版號與 commit 都不在裡面，`Update.exe` 與它們無關。
#
# 2026-09-23 在本機量的，用的是憑證 5817F971FCC333251C488FC90C4AF8F9208E9E1C
# （見 docs/ops/PUBLISH.md 第七節）。自簽之前它停在 9a1e4194… 很久沒動。
$expectedUpdateExe = "ae4a116a15e5cda0e423e8fca5f1b02326b502553397d709fc6a7090bc958e9d"

function Resolve-FullPath {
    param([string]$PathValue)
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PathValue))
}

function Get-ZipEntryHash {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$EntryName
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) { return $null }

    # 直接讀 zip 裡的串流算，不解壓到磁碟：要比的就是這顆檔本身的位元組。
    $stream = $entry.Open()
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
        }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

$isCi = $env:GITHUB_ACTIONS -eq "true"

function Write-CiWarning {
    param([string]$Title, [string]$Message)

    if ($isCi) {
        # GitHub 的 workflow 命令：會變成 run 摘要頁最上面那排黃色警示，不影響結果狀態。
        Write-Output "::warning title=$Title::$Message"
    }
    else {
        Write-Warning "$Title —— $Message"
    }
}

$zipPath = Join-Path (Resolve-FullPath $OutputDir) "$PackId-$Channel-Portable.zip"
if (-not (Test-Path $zipPath)) {
    Write-CiWarning "跳過雜湊檢查" "找不到免安裝包 $zipPath，這一次沒有檢查 stub 與 Update.exe 的雜湊。"
    return
}

$results = @()
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        # 一、stub 應該根本不存在。它回來了就是 --noStub 沒生效——旗標掉了、fork 換了分支，
        #     或是上游改了行為。那一版會把 #210 的誤判一起帶回來，所以這裡要吵。
        $stubHash = Get-ZipEntryHash -Archive $archive -EntryName $MainExe
        $results += [pscustomobject]@{
            File   = "$MainExe（啟動器 stub）"
            Status = if ($null -eq $stubHash) { "absent" } else { "returned" }
            Actual = if ($null -eq $stubHash) { "—" } else { $stubHash }
        }

        # 二、Update.exe 還在，比雜湊。
        $updateHash = Get-ZipEntryHash -Archive $archive -EntryName "Update.exe"
        $results += [pscustomobject]@{
            File   = "Update.exe"
            Status = if ($null -eq $updateHash) { "missing" }
                     elseif ($updateHash -eq $expectedUpdateExe) { "same" }
                     else { "changed" }
            Actual = if ($null -eq $updateHash) { "—" } else { $updateHash }
        }
    }
    finally { $archive.Dispose() }
}
catch {
    # 這一步不該擋住發版。讀不到就說讀不到，包還是要發得出去。
    Write-CiWarning "雜湊檢查沒跑完" "讀取 $zipPath 時出錯：$($_.Exception.Message)"
    return
}

$label = @{
    absent   = "不存在（預期）"
    returned = "又出現了"
    same     = "不變"
    changed  = "已改變"
    missing  = "不在包裡"
}

Write-Host ""
Write-Host "免安裝包根目錄：" -ForegroundColor Cyan
foreach ($r in $results) {
    $color = if ($r.Status -in @("absent", "same")) { "Green" } else { "Yellow" }
    Write-Host ("  {0,-26} {1,-16} {2}" -f $r.File, $label[$r.Status], $r.Actual) -ForegroundColor $color
}
Write-Host ""

foreach ($r in $results | Where-Object { $_.Status -eq "returned" }) {
    Write-CiWarning "啟動器 stub 又出現了" `
        "免安裝包根目錄多了 $MainExe（$($r.Actual)）。--noStub 應該讓它完全不存在——確認打包用的是 fork 的 no-stub 分支、而且旗標還在。這顆檔是 #210 那個誤判的主要來源。"
}

foreach ($r in $results | Where-Object { $_.Status -eq "changed" }) {
    Write-CiWarning "Update.exe 的雜湊變了" `
        "$($r.Actual)（原本 $expectedUpdateExe）。這顆檔的 Defender 信譽會從零開始累積。確認是預期中的改動（換圖示、換 vpk、換簽章金鑰）之後，把新值更新到 check-release-hashes.ps1。"
}

foreach ($r in $results | Where-Object { $_.Status -eq "missing" }) {
    Write-CiWarning "找不到 Update.exe" "免安裝包根目錄沒有這個檔，無法比對雜湊。"
}

if ($isCi -and $env:GITHUB_STEP_SUMMARY) {
    # 沒變也寫進摘要。要能一眼看出「這一版跟上一版是同一顆檔」，不能只在出事時才有東西看。
    $lines = @(
        "### 免安裝包根目錄"
        ""
        "| 檔案 | 狀態 | SHA256 |"
        "| --- | --- | --- |"
    )
    foreach ($r in $results) {
        $mark = if ($r.Status -in @("absent", "same")) { "✅ " } else { "⚠️ " }
        $lines += "| ``$($r.File)`` | $mark$($label[$r.Status]) | ``$($r.Actual)`` |"
    }
    if ($results | Where-Object { $_.Status -notin @("absent", "same") }) {
        $lines += ""
        $lines += "有東西變了。stub 不該存在（#210 的誤判來源），``Update.exe`` 的雜湊一換則代表它的 Defender 檔案信譽要重新累積。確認是預期中的改動後，把新值更新到 ``check-release-hashes.ps1``。"
    }
    $lines -join "`n" | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
}
