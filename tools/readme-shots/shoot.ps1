# README／官網用的 app 截圖：主螢幕置中、五種介面語言。用法見同資料夾的 README.md。
param(
    [ValidateSet('all', 'main', 'backup')][string]$Phase = 'all',
    [string[]]$Langs = @('zh-Hant', 'zh-Hans', 'en', 'ja', 'ko'),
    [switch]$Build,
    [switch]$CopyToOriginals
)
$ErrorActionPreference = 'Stop'
# Through -File, "-Langs en,ja" arrives as the one string "en,ja".
$Langs = @($Langs | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -Namespace RS -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr c);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, System.UIntPtr e);
[DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, System.UIntPtr e);
[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr after, int x, int y, int cx, int cy, uint f);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(System.IntPtr h);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr h, int a, out RECT r, int size);
public struct RECT { public int L, T, R, B; }
'@
# Per-monitor aware, so every rectangle below is in physical pixels on whichever monitor it is on.
[void][RS.W]::SetProcessDpiAwarenessContext([IntPtr]-4)
$A = [System.Windows.Automation.AutomationElement]

$Root = (Resolve-Path "$PSScriptRoot\..\..").Path
$Work = "$Root\artifacts\readme-shots"
$AppDir = "$Work\app"
$Exe = "$AppDir\OverTranslate.exe"
$Out = "$Work\out"
New-Item -ItemType Directory -Force $Out | Out-Null
$Settings = "$env:APPDATA\OverTranslate\appsettings.json"
$Backup = "$Work\appsettings.backup.json"
$Originals = "$Root\docs\images\originals"

$Pri = ([System.Windows.Forms.Screen]::AllScreens | Where-Object Primary).Bounds
$SecScreen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
$Sec = if ($SecScreen) { $SecScreen.Bounds } else { $null }
$PCX = $Pri.X + [int]($Pri.Width / 2); $PCY = $Pri.Y + [int]($Pri.Height / 2)

# File names follow docs/images: zh-Hant has no suffix, Japanese is _jp.
$Suffix = @{ 'zh-Hant' = ''; 'zh-Hans' = '_zh-Hans'; 'en' = '_en'; 'ja' = '_jp'; 'ko' = '_ko' }
$SettingsName = -join ([char[]](0x8A2D, 0x5B9A, 0x9801))                                   # 設定頁
$RealtimeName = -join ([char[]](0x5373, 0x6642, 0x7FFB, 0x8B6F, 0x8996, 0x7A97, 0x9810, 0x89BD)) # 即時翻譯視窗預覽
$BackupName = -join ([char[]](0x5099, 0x63F4))                                              # 備援

function Mark([string]$t) { $l = "$(Get-Date -Format 'HH:mm:ss') $t"; Add-Content "$Out\run.log" $l -Encoding UTF8; Write-Host $l }
# Off the primary monitor, so no hover state lands in a picture.
function Park { if ($Sec) { [void][RS.W]::SetCursorPos($Sec.X + 5, $Sec.Y + $Sec.Height - 5) } else { [void][RS.W]::SetCursorPos($Pri.X + 2, $Pri.Y + $Pri.Height - 2) } }

# ── 建置：套上備援標籤的截圖補丁，建到 artifacts，原始碼立刻還原 ──
if ($Build -or -not (Test-Path $Exe)) {
    $patch = "$PSScriptRoot\backup-badge.patch"
    Push-Location $Root
    try {
        git apply $patch
        if ($LASTEXITCODE) { throw 'backup-badge.patch does not apply: refresh it against the current ResilientProvider.cs (see README)' }
        try {
            Get-Process OverTranslate -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force
            dotnet build src\OverTranslate\OverTranslate.csproj -c Release -o $AppDir -nologo -v q
            if ($LASTEXITCODE) { throw 'build failed' }
        } finally {
            git apply -R $patch
            if ($LASTEXITCODE) { throw 'could not take backup-badge.patch back out: check git status' }
        }
    } finally { Pop-Location }
    Mark "built $Exe"
}

function Set-Prop($obj, [string]$name, $value) {
    if ($obj.PSObject.Properties.Name -contains $name) { $obj.$name = $value } else { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}
function Edit-Settings([hashtable]$Top) {
    $j = Get-Content $Settings -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($k in $Top.Keys) { Set-Prop $j $k $Top[$k] }
    [IO.File]::WriteAllText($Settings, ($j | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding($false)))
}
function Start-Hidden([string]$File) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $File; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    [System.Diagnostics.Process]::Start($psi)
}
function Start-App {
    # The main window opens on the cursor's monitor, and only on the second launch: the first sits in the tray.
    [void][RS.W]::SetCursorPos($PCX, $PCY); Start-Hidden $Exe | Out-Null; Start-Sleep 6
    [void][RS.W]::SetCursorPos($PCX, $PCY); Start-Hidden $Exe | Out-Null
    $app = $null
    for ($i = 0; $i -lt 150; $i++) { $app = Get-Process OverTranslate -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1; if ($app) { break }; Start-Sleep -Milliseconds 100 }
    Park
    if (-not $app) { throw 'main window did not appear' }
    $script:AppPid = $app.Id
    $app
}
function Stop-App { Get-Process OverTranslate -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force; Start-Sleep 1 }
function Frame([IntPtr]$h) { $r = New-Object RS.W+RECT; [void][RS.W]::DwmGetWindowAttribute($h, 9, [ref]$r, 16); $r }
function Center([IntPtr]$h) {
    $f = Frame $h; $w = New-Object RS.W+RECT; [void][RS.W]::GetWindowRect($h, [ref]$w)
    $x = $Pri.X + [int](($Pri.Width - ($f.R - $f.L)) / 2) - ($f.L - $w.L)
    $y = $Pri.Y + [int](($Pri.Height - ($f.B - $f.T)) / 2) - ($f.T - $w.T)
    [void][RS.W]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, 0, 0, 0x15)
    Start-Sleep -Milliseconds 400
}
function Shot([string]$Path, [int]$X, [int]$Y, [int]$W, [int]$H) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($X, $Y, 0, 0, $bmp.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    Mark "  shot $(Split-Path -Leaf $Path) ${W}x$H"
}
# The DWM frame: the window as drawn, without the invisible resize border around it.
function Shot-Window([IntPtr]$h, [string]$Path) { $f = Frame $h; Shot $Path $f.L $f.T ($f.R - $f.L) ($f.B - $f.T) }
function Windows-Of { $A::RootElement.FindAll('Children', (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $script:AppPid))) }
function Find-El([string]$Id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $Id)
    foreach ($w in Windows-Of) { $e = $w.FindFirst('Descendants', $c); if ($e) { return $e } }
    $null
}
function Select-Nav([string]$Id) {
    $e = Find-El $Id; if (-not $e) { throw "not found: $Id" }
    $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1500
}
function Str([string]$Lang, [string]$Key) {
    $t = [IO.File]::ReadAllText("$Root\src\OverTranslate\Resources\Strings.$Lang.xaml")
    $m = [regex]::Match($t, 'x:Key="' + [regex]::Escape($Key) + '">([^<]*)<')
    if (-not $m.Success) { throw "no $Key in $Lang" }
    $m.Groups[1].Value
}
# Brings the 顯示外觀 card to the top of the realtime page: its heading 32 DIPs under the viewport's
# top edge, which shows the card's own top edge and a margin, and none of the card above it.
function Scroll-ToAppearance([IntPtr]$h, [string]$Lang) {
    $win = $A::FromHandle($h)
    $svs = $win.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($A::IsScrollPatternAvailableProperty, $true)))
    $sv = $null
    foreach ($s in $svs) { $p = $s.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); if ($p.Current.VerticallyScrollable -and $s.Current.BoundingRectangle.Height -gt 400) { $sv = $s; $sp = $p; break } }
    if (-not $sv) { throw 'no scrollable viewer' }
    $sp.SetScrollPercent(-1, 0); Start-Sleep -Milliseconds 500
    $name = Str $Lang 'S.Realtime.Appearance'
    $hdr = $sv.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)))
    if (-not $hdr) { throw "no header $name" }
    $svr = $sv.Current.BoundingRectangle
    $view = $svr.Height; $range = $view * 100 / $sp.Current.VerticalViewSize - $view
    $scale = [RS.W]::GetDpiForWindow($h) / 96.0
    $miss = $hdr.Current.BoundingRectangle.Top - ($svr.Top + 32 * $scale)
    $pct = [math]::Max(0, [math]::Min(100, 100 * $miss / $range))
    Mark "  scroll pct=$([math]::Round($pct, 2))"
    $sp.SetScrollPercent(-1, $pct); Start-Sleep -Milliseconds 800
}

# Only one OverTranslate can run: whatever is open is closed first, the user's own copy included.
$running = Get-Process OverTranslate -ErrorAction SilentlyContinue
foreach ($p in $running) { Mark "closing running OverTranslate pid=$($p.Id) $($p.Path)"; $p | Stop-Process -Force }
if ($running) { Start-Sleep 2 }

Copy-Item $Settings $Backup -Force
$hash = (Get-FileHash $Settings).Hash
$made = @()
try {
    if ($Phase -in 'all', 'main') {
        foreach ($lang in $Langs) {
            Mark "== $lang"
            Edit-Settings @{ UiLanguage = $lang }
            $app = Start-App; $h = $app.MainWindowHandle
            Center $h; [void][RS.W]::SetForegroundWindow($h); Park
            Select-Nav 'SettingsNav'
            $f = "$Out\$SettingsName$($Suffix[$lang]).png"; Shot-Window $h $f; $made += $f
            Select-Nav 'RealtimeNav'
            Scroll-ToAppearance $h $lang; Park
            $f = "$Out\$RealtimeName$($Suffix[$lang]).png"; Shot-Window $h $f; $made += $f
            Stop-App
        }
    }
    if ($Phase -in 'all', 'backup') {
        Mark '== backup badge'
        Edit-Settings @{ UiLanguage = 'zh-Hant'; Provider = 'Microsoft'; AutoTranslateAfterSelection = $true }
        $env:OT_SHOT_BACKUP = '1'
        Remove-Item "$Out\viewer.txt" -ErrorAction SilentlyContinue
        $viewer = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSScriptRoot\viewer.ps1`"", '-Out', "`"$Out\viewer.txt`""
        try {
            $app = Start-App
            # The main window is not in this picture: off to the side, or minimised without a second monitor.
            if ($Sec) { [void][RS.W]::SetWindowPos($app.MainWindowHandle, [IntPtr]::Zero, $Sec.X + 20, $Sec.Y + 20, 0, 0, 0x15) }
            else { [void]$app.CloseMainWindow() }
            for ($i = 0; $i -lt 100 -and -not (Test-Path "$Out\viewer.txt"); $i++) { Start-Sleep -Milliseconds 100 }
            Start-Sleep 1
            $v = (Get-Content "$Out\viewer.txt").Split(','); $vx = [int]$v[0]; $vy = [int]$v[1]; $vw = [int]$v[2]; $vh = [int]$v[3]
            # Ctrl+Alt+A, the default 截圖翻譯 shortcut; the whole panel is selected, so the toolbar lands on black.
            [void][RS.W]::SetCursorPos($PCX, $PCY)
            foreach ($k in 0x11, 0x12, 0x41) { [RS.W]::keybd_event([byte]$k, 0, 0, [UIntPtr]::Zero) }
            foreach ($k in 0x41, 0x12, 0x11) { [RS.W]::keybd_event([byte]$k, 0, 2, [UIntPtr]::Zero) }
            Start-Sleep 2
            $x1 = $vx + 10; $y1 = $vy + 10; $x2 = $vx + $vw - 10; $y2 = $vy + $vh - 10
            [void][RS.W]::SetCursorPos($x1, $y1); Start-Sleep -Milliseconds 300
            [RS.W]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150
            for ($i = 1; $i -le 20; $i++) { [void][RS.W]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / 20), [int]($y1 + ($y2 - $y1) * $i / 20)); Start-Sleep -Milliseconds 25 }
            [RS.W]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            Park; Start-Sleep 10
            $pb = Find-El 'ProviderBox'; if (-not $pb) { throw 'no ProviderBox: the capture toolbar did not open' }
            $r = $pb.Current.BoundingRectangle
            $tw = [System.Windows.Automation.TreeWalker]::ControlViewWalker; $top = $pb
            while (($up = $tw.GetParent($top)) -and -not [System.Windows.Automation.Automation]::Compare($up, $A::RootElement)) { $top = $up }
            $s = [RS.W]::GetDpiForWindow([IntPtr]$top.Current.NativeWindowHandle) / 96.0
            if ($s -le 0) { throw 'no DPI for the toolbar window' }
            # The badge is 6 DIPs right of the provider box; hovering it opens the tooltip under it.
            [void][RS.W]::SetCursorPos([int]($r.Right + 24 * $s), [int]($r.Top + $r.Height / 2)); Start-Sleep 2
            Shot "$Out\backup-full.png" $Pri.X $Pri.Y $Pri.Width $Pri.Height
            Park
            # From the provider box to the toolbar's right edge, down to the tooltip, as the old picture was cut.
            $full = [System.Drawing.Image]::FromFile("$Out\backup-full.png")
            $cl = [int]($r.Left - $Pri.X - 5 * $s); $ct = [int]($r.Top - $Pri.Y - 25 * $s)
            $cut = New-Object System.Drawing.Rectangle $cl, $ct, ([int](716 * $s)), ([int](131 * $s))
            $crop = ([System.Drawing.Bitmap]$full).Clone($cut, $full.PixelFormat)
            $f = "$Out\$BackupName.png"; $crop.Save($f, [System.Drawing.Imaging.ImageFormat]::Png); $made += $f
            $crop.Dispose(); $full.Dispose()
            Mark "  cropped $BackupName.png $($cut.Width)x$($cut.Height)"
            [RS.W]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero); [RS.W]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
            Start-Sleep 1
        } finally {
            try { $viewer.Kill() } catch {}
            $env:OT_SHOT_BACKUP = $null
        }
    }
} finally {
    Stop-App
    Copy-Item $Backup $Settings -Force
    Mark "settings restored, hash match=$((Get-FileHash $Settings).Hash -eq $hash)"
}

if ($CopyToOriginals) {
    foreach ($f in $made) { Copy-Item $f $Originals -Force; Mark "  -> originals\$(Split-Path -Leaf $f)" }
}
