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

    // 標記 deliberately keeps nothing here. Which pen is in hand lasts exactly as long as the
    // capture it was picked up for: every new capture starts on the black pen at the middle width,
    // and the choice only has to survive closing and reopening the panel inside that one session —
    // see MainWindow's annotation session state.
}
