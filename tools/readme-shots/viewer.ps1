# 備援那張的取景素材：主螢幕整片純黑，上方畫一塊有幾行英文的面板讓截圖翻譯有字可讀。
# 面板在螢幕上的 x,y,w,h 寫到 $Out，shoot.ps1 照著框選。
param([string]$Out, [int]$Seconds = 300)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type 'using System.Runtime.InteropServices; public class RSV { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr c); }'
[void][RSV]::SetProcessDpiAwarenessContext([IntPtr]-4)
$b = ([System.Windows.Forms.Screen]::AllScreens | Where-Object Primary).Bounds
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
$f.Controls.Add($p)
$t = New-Object System.Windows.Forms.Timer; $t.Interval = $Seconds * 1000; $t.Add_Tick({ $f.Close() }); $t.Start()
$f.Add_Shown({ Set-Content -Path $Out -Value "$x,$y,$w,$h" -Encoding ASCII })
[void]$f.ShowDialog()
