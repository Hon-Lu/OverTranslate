using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Views.Shell;

/// <summary>
/// Modal "about" panel drawn on top of the shell instead of in its own window, so the app
/// keeps a single visible surface. Dismissed by the close button, the scrim, or Escape.
/// </summary>
public partial class AboutOverlay : UserControl
{
    // The site reads the browser's language and redirects itself, so there is one URL for every
    // interface language rather than a per-locale table like DocumentationLinks keeps.
    private const string WebsiteUrl = "https://hon-lu.github.io/OverTranslate/";

    private const string GitHubUrl = "https://github.com/Hon-Lu/OverTranslate";

    private const string SponsorUrl = "https://buymeacoffee.com/hon.lu";

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    public AboutOverlay()
    {
        InitializeComponent();
        // The same embedded .ico the taskbar and the shell header use, so the card cannot end up
        // showing a stale second copy of the mark.
        AppIcon.Source = AppIconService.CreateMainIcon();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = LocalizationService.Format("S.About.Version", version);
    }

    public void Open()
    {
        Visibility = Visibility.Visible;

        // WPF switches text off pixel snapping as soon as it detects the text is being animated,
        // then ramps snapping back on over roughly a second once the motion stops — which is why
        // the card used to reach its final size and stay soft for a beat before turning sharp, and
        // there is no API to shorten or disable that ramp. Rendering the card into a bitmap cache
        // for the duration of the scale sidesteps it: the glyphs are rasterised once as static,
        // snapped text and the render thread only scales the finished bitmap, so the detector never
        // sees animating text. The cache is dropped again in ReleaseAnimations.
        Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) => ReleaseAnimations();
        BeginAnimation(OpacityProperty, fade);

        // Slight scale-up so the card reads as coming forward rather than blinking in
        var grow = new DoubleAnimation
        {
            From = 0.96, To = 1,
            Duration = FadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // Focus lets the control receive Escape without the page underneath stealing it
        Focus();
    }

    public void Close()
    {
        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            ReleaseAnimations();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // DoubleAnimation defaults to FillBehavior.HoldEnd, so the animated properties stay under the
    // animation clock's control long after the animation has visually finished, which keeps the
    // card in an intermediate composition layer indefinitely. Handing the properties back to their
    // owners drops that layer as soon as the transition is over.
    private void ReleaseAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;

        // Back to rendering the live visual tree — the cache existed only for the transition.
        Card.CacheMode = null;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside the card must not bubble up to the scrim's dismiss handler
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void WebsiteBtn_Click(object sender, RoutedEventArgs e) => OpenInBrowser(WebsiteUrl);

    private void StarBtn_Click(object sender, RoutedEventArgs e) => OpenInBrowser(GitHubUrl);

    private void SponsorBtn_Click(object sender, RoutedEventArgs e) => OpenInBrowser(SponsorUrl);

    private static void OpenInBrowser(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
