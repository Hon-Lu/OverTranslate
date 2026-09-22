<#
    檢查發布產物裡 stub 與 Update.exe 的 SHA256 有沒有變。

    這兩顆帶的是自簽章，沒有受信任 CA 背書，所以累積不到發行者信譽；Defender 能給它們的
    只有**按檔案雜湊**累積的信譽（見 #210）。雜湊一換，信譽就從零開始，那幾天最容易被報
    Trojan:Win32/Wacatac.B!ml。`--stableStub` 讓 stub 不再跟著版號與 commit 變，但圖示、
    app.manifest、組件資訊字串、vendor 二進位、簽章金鑰這幾項只要改了，它照樣會重算。

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
# 把這次印出來的新值貼回來即可：
#   stub        ← 應用程式圖示、app.manifest、AssemblyCompany / Product / Description 等資訊字串、
#                 vendor 的 stub.exe（換 vpk 版本就會換）、簽章金鑰
#   Update.exe  ← 應用程式圖示、vendor 的 update.exe、簽章金鑰
# 版號與 commit 不在上面任何一條裡，那正是 --stableStub 凍掉的東西。
# 兩個值都是 2026-09-23 在本機量的，用的是憑證 7F1340D9C8084D3EACB2A9E76096C3EA48554F48
# （見 docs/ops/PUBLISH.md 第七節）。導入 --stableStub 與自簽之前，stub 每次發版都不一樣
# ——8 次建置量到 8 顆不同雜湊——而 Update.exe 停在 9a1e4194… 沒動過。
$expected = [ordered]@{
    $MainExe     = "357bf9e26e7fcaa1912c6eb804a2f7d2aa11272289250ffe7939256f2c27442c"
    "Update.exe" = "bd85bec6cde2dc4f8dbef802fcfe257d39b19f88802eddc5360b16871f03b88a"
}

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
        foreach ($name in $expected.Keys) {
            $actual = Get-ZipEntryHash -Archive $archive -EntryName $name
            $status = if ($null -eq $actual) { "missing" }
                      elseif ($actual -eq $expected[$name]) { "same" }
                      else { "changed" }

            $results += [pscustomobject]@{
                File     = $name
                Status   = $status
                Actual   = $actual
                Expected = $expected[$name]
            }
        }
    }
    finally { $archive.Dispose() }
}
catch {
    # 這一步不該擋住發版。讀不到就說讀不到，包還是要發得出去。
    Write-CiWarning "雜湊檢查沒跑完" "讀取 $zipPath 時出錯：$($_.Exception.Message)"
    return
}

Write-Host ""
Write-Host "免安裝包根目錄那兩顆原生檔：" -ForegroundColor Cyan
foreach ($r in $results) {
    $mark = switch ($r.Status) { "same" { "不變" } "changed" { "已改變" } default { "不在包裡" } }
    $color = switch ($r.Status) { "same" { "Green" } default { "Yellow" } }
    Write-Host ("  {0,-18} {1,-8} {2}" -f $r.File, $mark, $r.Actual) -ForegroundColor $color
}
Write-Host ""

foreach ($r in $results | Where-Object { $_.Status -eq "changed" }) {
    Write-CiWarning "$($r.File) 的雜湊變了" `
        "$($r.Actual)（原本 $($r.Expected)）。這顆檔的 Defender 信譽會從零開始累積。確認是預期中的改動（換圖示、改 app.manifest、改組件資訊字串、換 vpk）之後，把新值更新到 check-release-hashes.ps1。"
}

foreach ($r in $results | Where-Object { $_.Status -eq "missing" }) {
    Write-CiWarning "找不到 $($r.File)" "免安裝包根目錄沒有這個檔，無法比對雜湊。"
}

if ($isCi -and $env:GITHUB_STEP_SUMMARY) {
    # 沒變也寫進摘要。要能一眼看出「這一版跟上一版是同一顆檔」，不能只在出事時才有東西看。
    $lines = @(
        "### stub 與 Update.exe 的雜湊"
        ""
        "| 檔案 | 狀態 | SHA256 |"
        "| --- | --- | --- |"
    )
    foreach ($r in $results) {
        $mark = switch ($r.Status) { "same" { "✅ 不變" } "changed" { "⚠️ 已改變" } default { "⚠️ 不在包裡" } }
        $lines += "| ``$($r.File)`` | $mark | ``$($r.Actual)`` |"
    }
    if ($results | Where-Object { $_.Status -eq "changed" }) {
        $lines += ""
        $lines += "改變的檔案，其 Defender 檔案信譽會從零開始累積（#210）。確認是預期中的改動後，把新值更新到 ``check-release-hashes.ps1``。"
    }
    $lines -join "`n" | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
}
