<#
    看緊免安裝包根目錄那兩顆檔的 SHA256：我們自己的啟動器，以及 Update.exe。

    兩顆都帶自簽章，沒有受信任 CA 背書，所以累積不到發行者信譽；Defender 能給它們的只有
    **按檔案雜湊**累積的那種——雜湊一換就從零開始，而那幾天最容易被報 Wacatac.B!ml（#210）。
    兩顆的位元組都應該永遠不變：啟動器是版控裡編好的那顆，Update.exe 只跟 vpk 版本與圖示有關。

    啟動器那一行還有第二個用途：**確認 Velopack 的 stub 沒有跑回來**。`--noStub` 要是失效
    （旗標掉了、fork 換了分支），根目錄那顆就會變成 stub——同樣的檔名、不同的雜湊，而那顆正是
    誤判的主要來源。

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

# 基準值 —— 是「現在應該長這樣」的紀錄，不是校驗和。對不上不代表壞掉，代表信譽要重新養。
# 兩個值都是 2026-09-23 在本機量的，用的是憑證 5817F971FCC333251C488FC90C4AF8F9208E9E1C
# （見 docs/ops/PUBLISH.md 第七節）。
#
# 啟動器   ← 重編 tools/launcher（換圖示、改行為、換 rustc）、換簽章金鑰
# Update.exe ← 換應用程式圖示、換 vpk 版本（vendor 二進位）、換簽章金鑰
# 版號與 commit 兩邊都無關。
$expectedLauncher = "1dd826ad50481ec7bffa57ad9cafe7c6dad79541009129dca2d1e4266a7a76bf"
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
        # 一、根目錄那顆應該是我們自己的啟動器。對不上有兩種可能：tools/launcher 重編過，
        #     或是 --noStub 失效、Velopack 的 stub 又跑回來佔了這個檔名（那顆就是誤判來源）。
        $launcherHash = Get-ZipEntryHash -Archive $archive -EntryName $MainExe
        $results += [pscustomobject]@{
            File   = "$MainExe（啟動器）"
            Status = if ($null -eq $launcherHash) { "missing" }
                     elseif ($launcherHash -eq $expectedLauncher) { "same" }
                     else { "changed" }
            Actual = if ($null -eq $launcherHash) { "—" } else { $launcherHash }
        }

        # 二、Update.exe 比雜湊。
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
    same    = "不變"
    changed = "已改變"
    missing = "不在包裡"
}

Write-Host ""
Write-Host "免安裝包根目錄：" -ForegroundColor Cyan
foreach ($r in $results) {
    $color = if ($r.Status -eq "same") { "Green" } else { "Yellow" }
    Write-Host ("  {0,-26} {1,-12} {2}" -f $r.File, $label[$r.Status], $r.Actual) -ForegroundColor $color
}
Write-Host ""

foreach ($r in $results | Where-Object { $_.Status -eq "changed" }) {
    $expected = if ($r.File -like "$MainExe*") { $expectedLauncher } else { $expectedUpdateExe }
    $hint = if ($r.File -like "$MainExe*") {
        "如果沒有重編 tools/launcher，這顆很可能根本不是我們的啟動器，而是 Velopack 的 stub 跑回來了——先確認打包用的是 fork 的 no-stub 分支、旗標還在。"
    }
    else {
        "確認是預期中的改動（換圖示、換 vpk、換簽章金鑰）。"
    }
    Write-CiWarning "$($r.File) 的雜湊變了" `
        "$($r.Actual)（原本 $expected）。這顆檔的 Defender 信譽會從零開始累積。$hint 確認之後把新值更新到 check-release-hashes.ps1。"
}

foreach ($r in $results | Where-Object { $_.Status -eq "missing" }) {
    Write-CiWarning "找不到 $($r.File)" "免安裝包根目錄沒有這個檔，無法比對雜湊。"
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
        $mark = if ($r.Status -eq "same") { "✅ " } else { "⚠️ " }
        $lines += "| ``$($r.File)`` | $mark$($label[$r.Status]) | ``$($r.Actual)`` |"
    }
    if ($results | Where-Object { $_.Status -ne "same" }) {
        $lines += ""
        $lines += "有東西變了，這兩顆的 Defender 檔案信譽會從零開始累積（#210）。啟動器那一顆若不是重編造成的，要先確認是不是 Velopack 的 stub 跑回來了。確認之後把新值更新到 ``check-release-hashes.ps1``。"
    }
    $lines -join "`n" | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
}
