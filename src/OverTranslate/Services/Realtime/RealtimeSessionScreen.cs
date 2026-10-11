using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Finds the session's screen again after the display has changed, and says what shape it is now.
/// </summary>
/// <remarks>
/// A session is laid out against one monitor's rectangle, taken when it started. A game switching
/// to fullscreen at another resolution changes that rectangle under it — 2560x1440 became 1280x720
/// in the case that left the control bar off the screen — and so can the user changing the display
/// settings, or a monitor being unplugged.
///
/// By device name first, because that is what stays the same while the rectangle changes. When the
/// monitor is gone, whichever screen now covers most of where it was — the same answer the capture
/// side gives for a monitor it has lost, so the bar ends up on the screen the session went on with.
/// </remarks>
public static class RealtimeSessionScreen
{
    public readonly record struct Screen(string DeviceName, Rectangle Bounds);

    public static Rectangle Resolve(string deviceName, Rectangle previous, IReadOnlyList<Screen> screens)
    {
        foreach (var screen in screens)
            if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return screen.Bounds;

        var best = previous;
        var bestArea = -1L;
        foreach (var screen in screens)
        {
            var overlap = Rectangle.Intersect(screen.Bounds, previous);
            var area = overlap.IsEmpty ? 0L : (long)overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                best = screen.Bounds;
                bestArea = area;
            }
        }

        return best;
    }
}
