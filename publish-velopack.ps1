param(
    # 相對路徑一律以 $PSScriptRoot（本腳本所在的專案根目錄）為基準，不是當前工作目錄，
    # 所以從哪裡呼叫都可以——CI 的工作目錄與人在本機的習慣不一定相同。
    [string]$ProjectPath = ".\src\OverTranslate\OverTranslate.csproj",
    [string]$PublishDir = ".\src\OverTranslate\bin\Publish",
    [string]$OutputDir = ".\artifacts\releases",
    [string]$PackId = "OverTranslate",
    [string]$PackTitle = "OverTranslate",
    [string]$PackAuthors = "Hon.Lu",
    [string]$MainExe = "OverTranslate.exe",
    [string]$IconPath = ".\src\OverTranslate\icons\app.ico",
    [string]$Channel = "win",
    [string]$PublishProfile = "FolderProfile",
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [string]$Version,
    # 打包用的是 fork 版 vpk（asd880921/velopack 的 fork/no-stub-1.2.0），不是 nuget 上的官方
    # 版本 —— 只有它認得 --noStub。取得與建置方式見該倉的 FORK-APPS.md。
    # 沒給就用這台機器的慣例位置（fork 與本倉並排 clone），CI 則明確傳進來。
    [string]$VpkPath = $env:OVERTRANSLATE_VPK_PATH,
    # 自簽憑證的指紋。給了才簽，沒給就照常打包不簽——本機隨手打包不需要動到憑證。
    # CI 會先把憑證匯入存放區，再把指紋傳進來，私鑰不會出現在任何命令列上。
    [string]$CertThumbprint = $env:OVERTRANSLATE_SIGN_THUMBPRINT
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([string]$PathValue)
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PathValue))
}

function Clear-DirectoryContents {
    param([string]$DirectoryPath)

    if (-not (Test-Path $DirectoryPath)) {
        New-Item -ItemType Directory -Force -Path $DirectoryPath | Out-Null
        return
    }

    Get-ChildItem -LiteralPath $DirectoryPath -Force | Remove-Item -Recurse -Force
}

function Get-VersionFromCsproj {
    param([string]$CsprojPath)

    $csprojPath = Resolve-FullPath $CsprojPath
    if (-not (Test-Path $csprojPath)) {
        throw "找不到 csproj：$csprojPath"
    }

    [xml]$csproj = Get-Content $csprojPath
    $versionNode = $csproj.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "csproj 內沒有 <Version>。"
    }

    return $versionNode.Trim()
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-VersionFromCsproj -CsprojPath $ProjectPath
}

# Channel 與版號後綴防呆：beta 應帶預發行後綴（如 -beta.1），stable 不應帶。
$isPrerelease = $Version -match '-'
if ($Channel -ne 'win' -and -not $isPrerelease) {
    Write-Warning "Channel='$Channel' 但版本 '$Version' 不含預發行後綴（如 1.6.1-beta.1）。確認這是你要的。"
}
if ($Channel -eq 'win' -and $isPrerelease) {
    Write-Warning "穩定 channel 'win' 但版本 '$Version' 含預發行後綴。穩定版通常不應帶 -beta 後綴。"
}

$projectFullPath = Resolve-FullPath $ProjectPath
$publishFullPath = Resolve-FullPath $PublishDir
$outputFullPath = Resolve-FullPath $OutputDir
$iconFullPath = Resolve-FullPath $IconPath
$mainExeFullPath = Join-Path $publishFullPath $MainExe
$appSettingsPublishPath = Join-Path $publishFullPath "appsettings.json"

if (-not (Test-Path $projectFullPath)) {
    throw "找不到專案檔：$projectFullPath"
}

if (-not $SkipPublish) {
    if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
        throw "找不到 dotnet。請先安裝 .NET SDK，或確認終端機環境變數已更新。"
    }

    Write-Host "先清空 Publish 資料夾..." -ForegroundColor Cyan
    Clear-DirectoryContents -DirectoryPath $publishFullPath

    Write-Host "先執行 dotnet publish..." -ForegroundColor Cyan
    Write-Host "Project    : $projectFullPath"
    Write-Host "Profile    : $PublishProfile"
    Write-Host "Config     : $Configuration"
    Write-Host "PublishDir : $publishFullPath"

    # 自封式設定明寫在這裡，不依賴 publish profile。
    # FolderProfile.pubxml 被 .gitignore 排除（PublishProfiles/），所以任何拿不到它的環境
    # —— CI checkout、git worktree、新 clone —— 都沒有 <SelfContained>true</SelfContained>。
    # 而 -p:PublishProfile=... 指向不存在的檔案時 dotnet 不會報錯，只會安靜地退回框架相依建置，
    # 產出一個看起來正常、但在沒裝 .NET 8 Runtime 的機器上根本開不起來的安裝包。
    $publishArgs = @(
        "publish",
        $projectFullPath,
        "-c", $Configuration,
        "-r", "win-x64",
        "-p:SelfContained=true",
        "-p:PublishDir=$publishFullPath"
    )

    # profile 存在才傳。傳一個不存在的 profile 只會換來 NETSDK1198 警告 —— 上面那些設定
    # 已經涵蓋它的內容，警告純粹是噪音，而噪音會讓真正該看的警告被忽略。
    $profileFullPath = Join-Path (Split-Path $projectFullPath) "Properties\PublishProfiles\$PublishProfile.pubxml"
    if (Test-Path $profileFullPath) {
        $publishArgs += "-p:PublishProfile=$PublishProfile"
    }
    else {
        Write-Host "找不到 publish profile '$PublishProfile'，改用腳本內建的自封式設定。" -ForegroundColor Yellow
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失敗，exit code: $LASTEXITCODE"
    }

    # 上面那個坑安靜到 build 會成功、打包會成功、大小也只是「比較小」而已。
    # 寧可在這裡炸掉，也不要把跑不起來的東西發出去。
    if (-not (Test-Path (Join-Path $publishFullPath "coreclr.dll"))) {
        throw "Publish 輸出不是自封式（找不到 coreclr.dll）。這種包在沒有 .NET Runtime 的機器上開不起來。"
    }

    Write-Host ""
}

if ([string]::IsNullOrWhiteSpace($VpkPath)) {
    # 本機慣例：fork 與本倉並排 clone。
    $VpkPath = "..\velopack\build\Release\net10.0\vpk.exe"
}

$vpkFullPath = Resolve-FullPath $VpkPath
if (-not (Test-Path $vpkFullPath)) {
    # 刻意不退回 PATH 上的官方 vpk。官方版不認得 --noStub 會直接失敗；就算拔掉那個旗標，
    # 打出來的包就會夾著那顆未簽章的啟動器 stub —— 正是 #210 要拿掉的東西。
    throw @"
找不到 fork 版 vpk：$vpkFullPath

取得與建置方式見 fork 的 FORK-APPS.md：裝官方 vpk 1.2.0 取它的 vendor 二進位（必須沿用官方那份，
自己編的 Rust 產物會連帶換掉 Update.exe 的雜湊）、clone fork/no-stub-1.2.0、把 vendor 複製進去、
dotnet build src/vpk/Velopack.Vpk -c Release -f net10.0。

建好之後用 -VpkPath 指到 build/Release/net10.0/vpk.exe，或設環境變數 OVERTRANSLATE_VPK_PATH。
"@
}

if (-not (Test-Path $publishFullPath)) {
    throw "找不到 Publish 資料夾：$publishFullPath"
}

if (-not (Test-Path $mainExeFullPath)) {
    throw "找不到主程式：$mainExeFullPath"
}

# 在 pack 之前、且不受 -SkipPublish 影響。打包進去的 appsettings.json 會在更新時覆蓋
# 使用者既有的設定，而 -SkipPublish 的用途正是「已手動 publish、只想重新打包」——
# 那個資料夾沒有經過上面的流程，裡面就會有這個檔。
if (Test-Path $appSettingsPublishPath) {
    Remove-Item -LiteralPath $appSettingsPublishPath -Force
    Write-Host "已從 Publish 輸出移除 appsettings.json" -ForegroundColor Yellow
}

if (-not (Test-Path $iconFullPath)) {
    throw "找不到 icon：$iconFullPath"
}

# 我們自己的啟動器（tools\launcher 編出來的，二進位跟著原始碼一起進版控）。
#
# 它在 pack 之前就要放進 packDir，而且檔名必須是 Velopack 約定的
# <主程式>_ExecutionStub.exe —— 這個名字帶來的三個行為，剛好就是我們要的：
#   一、它會被打進 .nupkg，所以**自動更新送得到它**；
#   二、安裝與每一次套用更新，更新器都會無條件把套件裡的它解回**安裝根目錄**、改名成主程式的
#       名字（Bundle.extract_stubs_to_dir）——於是使用者手上那顆舊的 Velopack stub 會被直接
#       覆蓋掉，那顆誤判來源不用等使用者自己去刪；
#   三、解 current\ 的那條路徑會跳過這個檔名（Bundle.extract_lib_contents_to_path），
#       所以安裝版的 current\ 不會多出一份。
#
# 免安裝包是唯一的例外：vpk 是把整個 packDir 複製進 zip 的 current\，原本再把 stub 搬到根目錄，
# 而 --noStub 把那個搬移跳掉了，所以 zip 的 current\ 會留下一份多餘的。打包後在下面刪掉。
$launcherPath = Resolve-FullPath ".\tools\launcher\dist\OverTranslate-launcher-unsigned.exe"
if (-not (Test-Path $launcherPath)) {
    throw "找不到啟動器：$launcherPath（見 tools\launcher\README.md）"
}

# 換了應用程式圖示卻忘了重編啟動器，是這裡唯一抓得到的地方 —— 啟動器的雜湊不會因為 app.ico
# 被換掉而改變（它是版控裡那顆固定的二進位），所以沒有其他檢查會發現它還帶著舊圖示。
$buildInfoPath = Join-Path (Split-Path $launcherPath) "build-info.txt"
if (Test-Path $buildInfoPath) {
    $recordedIconHash = (Select-String -Path $buildInfoPath -Pattern '^icon_sha\s*=\s*(\S+)').Matches.Groups[1].Value
    $currentIconHash = (Get-FileHash -LiteralPath $iconFullPath -Algorithm SHA256).Hash.ToLower()
    if ($recordedIconHash -and $recordedIconHash -ne $currentIconHash) {
        Write-Warning ("應用程式圖示已經換過，但 tools\launcher 還沒重編 —— 出貨的啟動器會帶著舊圖示。`n" +
                       "  目前的 app.ico : $currentIconHash`n" +
                       "  啟動器編譯時用的: $recordedIconHash`n" +
                       "  重編步驟見 tools\launcher\README.md。")
    }
}

$stubName = [System.IO.Path]::GetFileNameWithoutExtension($MainExe) + "_ExecutionStub.exe"
$stagedLauncherPath = Join-Path $publishFullPath $stubName

New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null

Write-Host "Velopack 打包開始..." -ForegroundColor Cyan
Write-Host "Version   : $Version"
Write-Host "PublishDir: $publishFullPath"
Write-Host "OutputDir : $outputFullPath"
Write-Host "vpk       : $vpkFullPath"

$packArgs = @(
    "pack",
    "--packId", $PackId,
    "--packVersion", $Version,
    "--packDir", $publishFullPath,
    "--mainExe", $MainExe,
    "--packTitle", $PackTitle,
    "--packAuthors", $PackAuthors,
    "--icon", $iconFullPath,
    "--channel", $Channel,
    "--outputDir", $outputFullPath,
    # 不要產生 Velopack 那顆啟動器 stub。它是打包當下才被塞進主程式資源的未簽章原生二進位，
    # 也就是 #210 那個 Wacatac.B!ml 誤判的主要來源。上面那顆我們自己編的啟動器會頂替它的位置
    # ——同樣的檔名約定、同樣被解到根目錄，但身分在編譯期就寫死、位元組跨版本不變。
    "--noStub"
)

if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) {
    # **刻意不加時戳（/tr）**。時戳會讓同樣的內容每次簽出不同的位元組，stub 與 Update.exe
    # 的雜湊就會每版重來一次——Update.exe 的檔案信譽就再也累積不起來。代價是憑證一到期，
    # 過去所有版本的簽章會一起失效，所以那張自簽憑證的效期一次拉到 2049。
    #
    # vpk 會簽 packDir 裡所有 PE 檔，而這時 Update.exe（以 Squirrel.exe 之名）與我們自己的
    # 啟動器（以 _ExecutionStub.exe 之名）都已經在裡面了，所以使用者硬碟上那三顆全都涵蓋。已經被信任簽章的檔（微軟簽的 .NET 執行檔）
    # 會自動跳過，不會被我們的自簽蓋掉。
    $packArgs += @("--signParams", "/sha1 $CertThumbprint /fd SHA256")
    Write-Host "簽章      : $CertThumbprint（自簽，不加時戳）"
}
else {
    Write-Host "簽章      : 無（沒給 -CertThumbprint）" -ForegroundColor Yellow
}

# 啟動器要跟著 packDir 一起被打進 .nupkg 並被 vpk 簽章，所以現在才複製進去 —— 放太早會被
# dotnet publish 清掉，放太晚就進不了套件。打包結束後一定要移除：那是 dotnet publish 的輸出
# 資料夾，留著它，下一次 -SkipPublish 打包會把這份沒簽過的再打進去一次。
Copy-Item -LiteralPath $launcherPath -Destination $stagedLauncherPath -Force
Write-Host "啟動器    : $stubName（已放進 Publish 輸出，會被打進套件並簽章）"

try {
    & $vpkFullPath @packArgs
    if ($LASTEXITCODE -ne 0) {
        throw "vpk pack 失敗，exit code: $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $stagedLauncherPath -Force -ErrorAction SilentlyContinue
}

# 免安裝包的根目錄只有 Update.exe 與 current\，使用者解壓之後沒有東西可以點 —— 安裝版是由
# 更新器把套件裡的啟動器解到根目錄，免安裝包沒有經過更新器，得自己補上。補的是同一顆檔案、
# 用同一張憑證簽（不加時戳，所以簽出來的位元組固定），兩邊的雜湊會一模一樣。
#
# 同時刪掉 current\ 裡那份多餘的：vpk 是把整個 packDir 複製進 zip 的 current\，原本會再把
# stub 搬到根目錄，而 --noStub 把那個搬移跳掉了。安裝版沒有這個問題 —— 更新器解 current\ 時
# 本來就會跳過 *_ExecutionStub.exe。
#
# 免安裝 zip 不在任何校驗鏈裡（releases.<channel>.json 只記 nupkg 的 SHA256），打包後改它是安全的。
function Set-PortableLauncher {
    param(
        [string]$ZipPath,
        [string]$LauncherPath,
        [string]$EntryName,
        [string]$StubName,
        [string]$CertThumbprint,
        [string]$SignToolPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # 在暫存處簽章，不要動到版控裡那顆。
    $staged = Join-Path ([System.IO.Path]::GetTempPath()) ("overtranslate-launcher-" + [guid]::NewGuid().ToString("N") + ".exe")
    Copy-Item -LiteralPath $LauncherPath -Destination $staged -Force
    try {
        if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) {
            if (-not (Test-Path $SignToolPath)) {
                throw "找不到 signtool：$SignToolPath"
            }
            & $SignToolPath sign /sha1 $CertThumbprint /fd SHA256 $staged | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "啟動器簽章失敗，exit code: $LASTEXITCODE"
            }
        }
        else {
            Write-Warning "啟動器沒有簽章（沒給 -CertThumbprint）。"
        }

        $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, "Update")
        try {
            $existing = $zip.GetEntry($EntryName)
            if ($null -ne $existing) { $existing.Delete() }
            $added = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $staged, $EntryName)
            # 其他成員的時間戳都是 1980（Velopack 正規化過），跟著對齊，免安裝包才不會每次打包都因為
            # 一個啟動器的時間而長得不一樣。
            $added.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

            # 用檔名比對而不是完整路徑：分隔符號由壓縮實作決定，不值得賭。
            $redundant = @($zip.Entries | Where-Object { $_.Name -eq $StubName })
            foreach ($entry in $redundant) {
                $full = $entry.FullName
                $entry.Delete()
                Write-Host "已從免安裝包移除多餘的 $full" -ForegroundColor DarkGray
            }
        }
        finally { $zip.Dispose() }

        $hash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLower()
        Write-Host "已在免安裝包根目錄放入啟動器 $EntryName（$hash）" -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
    }
}

$portableZipPath = Join-Path $outputFullPath "$PackId-$Channel-Portable.zip"
if (Test-Path $portableZipPath) {
    # signtool 用 vpk 內建那顆，與打包其他檔案時同一支，行為一致 —— 這點是必要的，免安裝包
    # 根目錄這顆與套件裡那顆必須是同樣的位元組。
    # vpk.exe 在 <fork>\build\Release\net10.0\ 底下，往上四層才是 fork 根目錄。
    $forkRoot = Split-Path (Split-Path (Split-Path (Split-Path $vpkFullPath)))
    $signToolPath = Join-Path $forkRoot "vendor\signing\signtool.exe"
    Set-PortableLauncher -ZipPath $portableZipPath -LauncherPath $launcherPath -EntryName $MainExe `
        -StubName $stubName -CertThumbprint $CertThumbprint -SignToolPath $signToolPath
}
else {
    Write-Warning "找不到免安裝包 $portableZipPath，沒有放入啟動器。"
}

Write-Host ""
Write-Host "打包完成，主要產物通常會在這裡：" -ForegroundColor Green
Write-Host "  Setup      : $outputFullPath\$PackId-$Channel-Setup.exe"
Write-Host "  Portable   : $outputFullPath\$PackId-$Channel-Portable.zip"
Write-Host "  Full pkg   : $outputFullPath\$PackId-$Version-full.nupkg"
Write-Host "  Releases   : $outputFullPath\releases.$Channel.json"
Write-Host ""
Write-Host "輸出資料夾內容：" -ForegroundColor Green
Get-ChildItem $outputFullPath | Select-Object Name, Length, LastWriteTime
