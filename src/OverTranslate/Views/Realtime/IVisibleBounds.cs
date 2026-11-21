namespace OverTranslate.Views.Realtime;

/// <summary>
/// A window of ours whose visible part is smaller than the window itself — one sized for the most it
/// may ever show and transparent around it.
/// </summary>
/// <remarks>
/// For the edit layer, which keeps its lists of modes clear of the windows over it: taken whole, the
/// 模式 panel's window walled off most of the screen beside it (RealtimeEditWindow.OwnedWindowBounds).
/// </remarks>
internal interface IVisibleBounds
{
    /// <summary>What the window shows, in physical screen pixels; null while nothing is shown.</summary>
    System.Windows.Rect? VisibleScreenBounds();
}
