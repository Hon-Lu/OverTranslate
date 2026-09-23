<#
    看緊出貨的那幾顆原生執行檔的 SHA256：我們自己的啟動器（免安裝包根目錄那顆，以及套件裡
    那份——更新時會被解回使用者的安裝根目錄），還有 Update.exe。

    這幾顆都帶自簽章，沒有受信任 CA 背書，所以累積不到發行者信譽；Defender 能給它們的只有
    **按檔案雜湊**累積的那種——雜湊一換就從零開始，而那幾天最容易被報 Wacatac.B!ml（#210）。
    位元組都應該永遠不變：啟動器是版控裡編好的那顆，Update.exe 只跟 vpk 版本與圖示有關。

    啟動器那幾行還有第二個用途：**確認 Velopack 的 stub 沒有跑回來**。`--noStub` 要是失效
    （旗標掉了、fork 換了分支），那個位置就會變成 Velopack 自己產的 stub——同樣的檔名、
    不同的雜湊，而那顆正是誤判的主要來源。

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

$launcherHint = "如果沒有重編 tools/launcher，這顆很可能根本不是我們的啟動器，而是 Velopack 的 stub 跑回來了——先確認打包用的是 fork 的 no-stub 分支、旗標還在。"
$updateHint = "確認是預期中的改動（換圖示、換 vpk、換簽章金鑰）。"

function Resolve-FullPath {
    param([string]$PathValue)
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PathValue))
}

function Get-EntryHash {
    param([System.IO.Compression.ZipArchiveEntry]$Entry)

    if ($null -eq $Entry) { return $null }

    # 直接讀 zip 裡的串流算，不解壓到磁碟：要比的就是這顆檔本身的位元組。
    $stream = $Entry.Open()
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

Add-Type -AssemblyName System.IO.Compression.FileSystem

$outputFullPath = Resolve-FullPath $OutputDir
$stubName = [System.IO.Path]::GetFileNameWithoutExtension($MainExe) + "_ExecutionStub.exe"
$results = @()

function New-Result {
    param([string]$File, [string]$Hash, [string]$Expected, [string]$Hint)

    return [pscustomobject]@{
        File     = $File
        Status   = if ($null -eq $Hash) { "missing" } elseif ($Hash -eq $Expected) { "same" } else { "changed" }
        Actual   = if ($null -eq $Hash) { "—" } else { $Hash }
        Expected = $Expected
        Hint     = $Hint
    }
}

# 一、免安裝包。根目錄那兩顆是使用者解壓後直接面對的東西。
$zipPath = Join-Path $outputFullPath "$PackId-$Channel-Portable.zip"
if (-not (Test-Path $zipPath)) {
    Write-CiWarning "跳過免安裝包的雜湊檢查" "找不到 $zipPath。"
}
else {
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $results += New-Result -File "$MainExe（免安裝包根目錄）" -Expected $expectedLauncher -Hint $launcherHint `
                -Hash (Get-EntryHash -Entry $archive.GetEntry($MainExe))
            $results += New-Result -File "Update.exe（免安裝包根目錄）" -Expected $expectedUpdateExe -Hint $updateHint `
                -Hash (Get-EntryHash -Entry $archive.GetEntry("Update.exe"))

            # current\ 裡不該有啟動器。vpk 把整個 packDir 複製進 current\，publish-velopack.ps1
            # 會在打包後刪掉那一份；還在就表示那段沒跑到，使用者會在 current\ 看到一顆多餘的檔案。
            $strays = @($archive.Entries | Where-Object { $_.Name -eq $stubName })
            foreach ($stray in $strays) {
                Write-CiWarning "免安裝包裡有多餘的 $stubName" `
                    "$($stray.FullName) 不該出現在免安裝包裡（打包後應該被移除）。不影響執行，但使用者會看到一顆多餘的檔案。"
            }
        }
        finally { $archive.Dispose() }
    }
    catch {
        # 這一步不該擋住發版。讀不到就說讀不到，包還是要發得出去。
        Write-CiWarning "免安裝包的雜湊檢查沒跑完" "讀取 $zipPath 時出錯：$($_.Exception.Message)"
    }
}

# 二、套件裡那顆啟動器。它才是**現有使用者**更新後實際拿到的——更新器每次套用更新都會把它解回
#     安裝根目錄、改名成主程式的名字，覆蓋掉上一版留在那裡的東西。位元組必須跟免安裝包那顆一致。
$nupkgPath = Get-ChildItem -LiteralPath $outputFullPath -Filter "$PackId-*-full.nupkg" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $nupkgPath) {
    Write-CiWarning "跳過套件的雜湊檢查" "$outputFullPath 裡找不到 $PackId-*-full.nupkg。"
}
else {
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath.FullName)
        try {
            $entry = $archive.Entries | Where-Object { $_.Name -eq $stubName } | Select-Object -First 1
            $results += New-Result -File "$stubName（套件內）" -Expected $expectedLauncher `
                -Hint "套件裡沒有它，等於現有使用者更新後根目錄還是上一版的舊檔——確認 publish-velopack.ps1 有在 pack 之前把啟動器複製進 Publish 資料夾。$launcherHint" `
                -Hash (Get-EntryHash -Entry $entry)
        }
        finally { $archive.Dispose() }
    }
    catch {
        Write-CiWarning "套件的雜湊檢查沒跑完" "讀取 $($nupkgPath.Name) 時出錯：$($_.Exception.Message)"
    }
}

if ($results.Count -eq 0) { return }

$label = @{
    same    = "不變"
    changed = "已改變"
    missing = "不在包裡"
}

Write-Host ""
Write-Host "原生執行檔的雜湊：" -ForegroundColor Cyan
foreach ($r in $results) {
    $color = if ($r.Status -eq "same") { "Green" } else { "Yellow" }
    Write-Host ("  {0,-44} {1,-12} {2}" -f $r.File, $label[$r.Status], $r.Actual) -ForegroundColor $color
}
Write-Host ""

foreach ($r in $results | Where-Object { $_.Status -eq "changed" }) {
    Write-CiWarning "$($r.File) 的雜湊變了" `
        "$($r.Actual)（原本 $($r.Expected)）。這顆檔的 Defender 信譽會從零開始累積。$($r.Hint) 確認之後把新值更新到 check-release-hashes.ps1。"
}

foreach ($r in $results | Where-Object { $_.Status -eq "missing" }) {
    Write-CiWarning "找不到 $($r.File)" "包裡沒有這個檔，無法比對雜湊。$($r.Hint)"
}

if ($isCi -and $env:GITHUB_STEP_SUMMARY) {
    # 沒變也寫進摘要。要能一眼看出「這一版跟上一版是同一顆檔」，不能只在出事時才有東西看。
    $lines = @(
        "### 原生執行檔的雜湊"
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
        $lines += "有東西變了，這幾顆的 Defender 檔案信譽會從零開始累積（#210）。啟動器那幾顆若不是重編造成的，要先確認是不是 Velopack 的 stub 跑回來了。確認之後把新值更新到 ``check-release-hashes.ps1``。"
    }
    $lines -join "`n" | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
}
