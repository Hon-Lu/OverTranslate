using OverTranslate.Services.Ocr;

namespace OverTranslate.Models;

/// <summary>
/// Everything 截圖翻譯 keeps between capture sessions, under one key.
/// </summary>
public class CaptureSettings
{
    /// <summary>
    /// Whether the capture toolbar last translated text written downwards in columns. False means
    /// the ordinary horizontal layout and remains the default for existing settings files.
    /// </summary>
    public bool VerticalText { get; set; } = false;

    /// <summary>
    /// What the capture toolbar last said the framed material is. Standard is the default, which is
    /// also what every settings file written before this switch existed reads as.
    /// </summary>
    /// <remarks>
    /// <para>Stored as the enum's name rather than as a flag. 標準 and 漫畫・文章 are very unlikely
    /// to be the last two answers — realtime already has more than two — and a bool would have to
    /// change data format the day a third arrives, taking every existing settings file with it.</para>
    ///
    /// <para>A name this build does not know reads as Standard: the settings reader keeps a
    /// property's default when a value will not deserialize, and logs the field it dropped. That is
    /// the whole of the unknown-value handling, and it belongs there rather than here — every enum
    /// in the file gets it for free.</para>
    /// </remarks>
    public CaptureLayoutMode LayoutMode { get; set; } = CaptureLayoutMode.General;

    /// <summary>
    /// Whether the capture toolbar grows its footer saying which model will read the selection.
    /// On by default; set from the toolbar's 顯示更多 menu.
    /// </summary>
    /// <remarks>
    /// The capture toolbar's alone. The line under the source picker on the realtime page is always
    /// shown when it has something to say: that page is where a session is set up, not a bar laid
    /// over what is being read.
    /// </remarks>
    public bool ShowModelHint { get; set; } = true;

    /// <summary>How the 標記 tools were last set up.</summary>
    /// <remarks>
    /// The user's preferences for the tools, not the state of a drawing: the colour, each tool's
    /// width, how faint a highlight is and which shape 形狀 gives. Which tool is in hand is not kept —
    /// every capture starts on the pen, because one that started on the eraser would be a drag that
    /// draws nothing and looks broken. The marks themselves belong to the capture they were drawn on.
    /// </remarks>
    public CaptureAnnotationSettings Annotation { get; set; } = new();
}

/// <summary>The 標記 preferences kept between captures. See <see cref="CaptureSettings.Annotation"/>.</summary>
/// <remarks>
/// The widths and the opacity are slider positions from 0 to 1, not DIP: what each position means is
/// the panel's to decide, and keeping the position is what lets that change without every settings
/// file written before it coming back at a width nobody chose.
/// </remarks>
public class CaptureAnnotationSettings
{
    /// <summary>The ink colour as #RRGGBB. One the palette does not offer reads as its first colour.</summary>
    public string Color { get; set; } = "#000000";

    public double PenThickness { get; set; } = 0.5;

    public double HighlighterThickness { get; set; } = 0.5;

    /// <summary>One width for all three shapes: they are one tool behind one button.</summary>
    public double ShapeThickness { get; set; } = 0.5;

    public double EraserSize { get; set; } = 0.5;

    public double HighlighterOpacity { get; set; } = 0.5;

    /// <summary>What 形狀 gives when pressed. Anything but one of the three shapes reads as Rectangle.</summary>
    public AnnotationTool Shape { get; set; } = AnnotationTool.Rectangle;
}
