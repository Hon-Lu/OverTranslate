using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OverTranslate.Models;
using Image = System.Windows.Controls.Image;

namespace OverTranslate.Controls;

/// <summary>
/// A translation service's mark, drawn at <see cref="Size"/>: the one place that says which mark
/// belongs to which service and how large each is drawn beside the others.
/// </summary>
/// <remarks>
/// <para>The settings page's service cards and the running capsule's translation panel both draw
/// from here, so a service added to one cannot be missing from the other, nor drawn at another
/// weight.</para>
///
/// <para>The marks are not one size. Each is set so they read as the same size next to each other —
/// a solid square looks bigger than a thin ring of the same box, and thin diagonal strokes smaller —
/// and those sizes were settled at the cards' 28 (inside their 32 square). A smaller
/// <see cref="Size"/> scales all of them by the same factor, so the balance between them holds.</para>
///
/// <para>The marks are brand marks and are drawn as they are: nothing is laid over them.</para>
/// </remarks>
public sealed class ProviderMark : ContentControl
{
    /// <summary>The size every mark's own size below is given for.</summary>
    internal const double ReferenceSize = 28;

    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(TranslationProvider), typeof(ProviderMark),
        new PropertyMetadata(TranslationProvider.Google, (d, _) => ((ProviderMark)d).Build()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ProviderMark),
        new PropertyMetadata(ReferenceSize, (d, _) => ((ProviderMark)d).Build()));

    public ProviderMark()
    {
        Focusable = false;
        IsTabStop = false;
        Build();
    }

    public TranslationProvider Provider
    {
        get => (TranslationProvider)GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    /// <summary>
    /// The size the marks are scaled for, in DIPs. The control takes the mark's own size — a mark a
    /// little smaller than this, or for Bing a little wider — so whatever holds it sets the box.
    /// </summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>
    /// Which resource a service's mark is, and how large it is drawn at <see cref="ReferenceSize"/>.
    /// A null width or height is left to the mark's own proportions.
    /// </summary>
    internal sealed record Spec(string Resource, bool IsGeometry, string? Brush, double? Width, double? Height);

    internal static Spec For(TranslationProvider provider) => LanguageData.CurrentProvider(provider) switch
    {
        // 「(Beta)」 has the same G as 「(標準)」, as it is: the mark is not to be altered or set on, so
        // the two are told apart by their names.
        TranslationProvider.Google or TranslationProvider.GoogleChrome => new("GoogleMarkImage", false, null, 26, 26),
        TranslationProvider.Bing => new("BingMarkImage", false, null, null, 27),
        TranslationProvider.Microsoft => new("MicrosoftMarkImage", false, null, 23, 23),
        TranslationProvider.Youdao => new("YoudaoMarkImage", false, null, 28, 28),
        TranslationProvider.TranSmart => new("TranSmartMarkImage", false, null, 27, 27),
        TranslationProvider.Yandex => new("YandexMarkImage", false, null, 27, 27),
        // Sized by width: see DeepLMarkGeometry.
        TranslationProvider.DeepL => new("DeepLMarkGeometry", true, "AppDeepLMark", 22, null),
        TranslationProvider.LocalAI => new("LocalAiMarkImage", false, null, 28, 28),
        // Wider than DeepL's on purpose: thin open strokes on the diagonal look smaller than a solid
        // shape of the same box, so it gets the usual optical correction.
        TranslationProvider.OpenAI => new("OpenAiMarkGeometry", true, "AppOpenAiMark", 25, null),
        _ => new("GoogleMarkImage", false, null, 26, 26),
    };

    private void Build()
    {
        var spec = For(Provider);
        var scale = Size / ReferenceSize;

        FrameworkElement mark;
        if (spec.IsGeometry)
        {
            var path = new Path { Stretch = Stretch.Uniform };
            path.SetResourceReference(Path.DataProperty, spec.Resource);
            if (spec.Brush is { } brush) path.SetResourceReference(Shape.FillProperty, brush);
            mark = path;
        }
        else
        {
            var image = new Image();
            image.SetResourceReference(Image.SourceProperty, spec.Resource);
            mark = image;
        }

        if (spec.Width is { } width) mark.Width = width * scale;
        if (spec.Height is { } height) mark.Height = height * scale;
        mark.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        mark.VerticalAlignment = System.Windows.VerticalAlignment.Center;

        Content = mark;
    }
}
