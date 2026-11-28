# 備援那張的取景素材：主螢幕整片純黑，上方畫一塊有幾行英文的面板讓截圖翻譯有字可讀。
# 面板在螢幕上的 x,y,w,h 寫到 $Out，shoot.ps1 照著框選。
# 給 -Image 時改成即時翻譯那張的取景：整片純黑，圖以 100% 原尺寸畫在中央偏左，右邊留給面板。
# -Blank 只鋪整片純黑，給主視窗那幾張當背景（圓角外才是黑的）。
param([string]$Out, [int]$Seconds = 300, [string]$Image, [double]$Scale = 1, [switch]$Blank, [ValidateSet('primary', 'secondary')][string]$Screen = 'primary')
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type 'using System.Runtime.InteropServices; public class RSV { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr c); }'
[void][RSV]::SetProcessDpiAwarenessContext([IntPtr]-4)
$b = ([System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.Primary -eq ($Screen -eq 'primary') } | Select-Object -First 1).Bounds
$lines = @(
    'Welcome back! Your weekly summary is ready.',
    'Three new chapters were added to the series you follow.',
    'The next episode will be released on Friday at 8 PM.',
    'Tap here to continue reading where you left off.'
)
$w = [int]($b.Width * 0.5); $h = [int]($b.Height * 0.35)
$x = $b.X + [int](($b.Width - $w) / 2); $y = $b.Y + [int]($b.Height * 0.12)
$f = New-Object System.Windows.Forms.Form
$f.Text = 'readme-shots'; $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.ShowInTaskbar = $false
$f.BackColor = [System.Drawing.Color]::Black
$f.Bounds = $b
$p = New-Object System.Windows.Forms.Panel
$p.BackColor = [System.Drawing.Color]::FromArgb(36, 39, 46)
$p.SetBounds($x - $b.X, $y - $b.Y, $w, $h)
$p.Add_Paint({
    param($sender, $e)
    $font = New-Object System.Drawing.Font('Segoe UI', [single]($h / 16), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $e.Graphics.TextRenderingHint = 'AntiAliasGridFit'
    $ly = [int]($h * 0.14)
    foreach ($l in $lines) { $e.Graphics.DrawString($l, $font, [System.Drawing.Brushes]::WhiteSmoke, [single]($w * 0.06), [single]$ly); $ly += [int]($h * 0.2) }
    $font.Dispose()
})
if ($Image) {
    $img = [System.Drawing.Image]::FromFile($Image)
    # 放大用最近鄰，點陣畫面才不會糊。
    $w = [int]($img.Width * $Scale); $h = [int]($img.Height * $Scale)
    $x = $b.X + [int]($b.Width / 2) - $w + [int]($w * 0.15); $y = $b.Y + [int](($b.Height - $h) / 2) - [int]($h * 0.25)
    $big = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.DrawImage($img, 0, 0, $w, $h); $g.Dispose()
    $pb = New-Object System.Windows.Forms.PictureBox
    $pb.SizeMode = 'Normal'; $pb.Image = $big; $pb.SetBounds($x - $b.X, $y - $b.Y, $w, $h)
    $f.Controls.Add($pb)
    # 不是純黑：深灰底才像一般桌面，合成時的畫布也用同一個顏色（shoot.ps1 的 $SceneBg）。
    $f.BackColor = [System.Drawing.Color]::FromArgb(30, 30, 32)
} elseif (-not $Blank) { $f.Controls.Add($p) }
# 置頂才蓋得住使用者開著的程式（遊戲、瀏覽器）；拍主視窗時不置頂，主視窗才在它上面。
$f.TopMost = -not $Blank
$t = New-Object System.Windows.Forms.Timer; $t.Interval = $Seconds * 1000; $t.Add_Tick({ $f.Close() }); $t.Start()
$f.Add_Shown({ Set-Content -Path $Out -Value "$x,$y,$w,$h" -Encoding ASCII })
[void]$f.ShowDialog()
