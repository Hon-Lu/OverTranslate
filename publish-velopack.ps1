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
    # 打包用的是 fork 版 vpk（asd880921/velopack 的 fork/stable-stub-1.2.0），不是 nuget 上的官方
    # 版本 —— 只有它認得 --stableStub。取得與建置方式見該倉的 FORK-APPS.md。
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
    # 刻意不退回 PATH 上的官方 vpk。官方版不認得 --stableStub 會直接失敗；就算拔掉那個旗標，
    # 打出來的 stub 也是每次發版換一顆雜湊的那種，檔案信譽永遠從零開始 —— 正是要避免的事。
    throw @"
找不到 fork 版 vpk：$vpkFullPath

取得與建置方式見 fork 的 FORK-APPS.md：裝官方 vpk 1.2.0 取它的 vendor 二進位（必須沿用官方那份，
自己編的 Rust 產物會連帶換掉 Update.exe 的雜湊）、clone fork/stable-stub-1.2.0、把 vendor 複製進去、
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
    # 根目錄那顆啟動器 stub 沿用主程式的資源（圖示、資訊清單、公司名），但版本欄位凍結成 1.0.0，
    # 所以它的位元組不再每次發版都變。那顆檔沒有簽章，雜湊一換，Defender 的雲端信譽就重來一次
    # —— 這正是 #210 那個 Wacatac.B!ml 誤判的溫床。詳見 fork 的 FORK-APPS.md。
    "--stableStub"
)

if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) {
    # **刻意不加時戳（/tr）**。時戳會讓同樣的內容每次簽出不同的位元組，stub 與 Update.exe
    # 的雜湊就會每版重來一次——那正是上面 --stableStub 在解決的事。代價是憑證一到期，
    # 過去所有版本的簽章會一起失效，所以那張自簽憑證的效期一次拉到 2049。
    #
    # vpk 會簽 packDir 裡所有 PE 檔，而這時 Update.exe（以 Squirrel.exe 之名）與 stub 都已經
    # 在裡面了，所以使用者硬碟上那三顆全都涵蓋。已經被信任簽章的檔（微軟簽的 .NET 執行檔）
    # 會自動跳過，不會被我們的自簽蓋掉。
    $packArgs += @("--signParams", "/sha1 $CertThumbprint /fd SHA256")
    Write-Host "簽章      : $CertThumbprint（自簽，不加時戳）"
}
else {
    Write-Host "簽章      : 無（沒給 -CertThumbprint）" -ForegroundColor Yellow
}

& $vpkFullPath @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack 失敗，exit code: $LASTEXITCODE"
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
