using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.UI;
using WinRT;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The window exclusion list: telling a capture session to leave named windows out of its frames,
/// which is what lets a monitor capture serve a source that OverTranslate is drawing its own
/// overlays on top of.
/// </summary>
/// <remarks>
/// Hand-written for the same reason as <see cref="WgcInterop"/>, and one more. The API lives on
/// <c>IDisplayGraphicsCaptureSession</c>, an interface the metadata does not declare
/// <see cref="GraphicsCaptureSession"/> as implementing — a session gets it only when its item is a
/// display, and only by asking for it at runtime. And the C# projection this project compiles
/// against (Windows SDK 10.0.26100) does not have the interface at all: the first projection that
/// does ships for net9.0 upwards, and the .NET SDK still resolves 10.0.26100.57 for net10.0 — so
/// reaching it means going to the ABI by hand. The system underneath has it regardless —
/// <see cref="WgcCapability.SupportsWindowExclusion"/> asks the machine, not the projection.
///
/// Two things travel back from the ABI that the whole design rests on. The set call returns a
/// <i>configuration iteration</i>, and every frame carries the iteration it was produced under
/// (<c>IDirect3D11CaptureFrame3</c>): a frame numbered below the one the exclusion was accepted at
/// was composed before it took effect and may still hold the overlays. That is the difference
/// between this and <c>WDA_EXCLUDEFROMCAPTURE</c> — the request is not the promise, the frame is —
/// and it is exactly what #94 got wrong.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal static class WgcWindowExclusion
{
    // Windows.Graphics.Capture.IDisplayGraphicsCaptureSession, read out of the system's own
    // Windows.Graphics.winmd rather than copied from a header, because nothing in the SDK this
    // builds against declares it.
    private static readonly Guid IidDisplayGraphicsCaptureSession = new("BB91F61B-218A-587D-8580-2701A74C0525");

    // Windows.Graphics.Capture.IDirect3D11CaptureFrame3 — one property, the frame's iteration.
    private static readonly Guid IidDirect3D11CaptureFrame3 = new("71616DC8-FEA5-5741-A3D8-591ACC39A9EE");

    // Slots past IInspectable (IUnknown's three, then GetIids/GetRuntimeClassName/GetTrustLevel),
    // in the order the interface declares them.
    private const int SlotSetWindowExclusionList = 6;
    private const int SlotGetWindowExclusionList = 7;
    private const int SlotGetConfigurationIteration = 6;

    /// <summary>
    /// Asks <paramref name="session"/> to keep <paramref name="windows"/> out of its frames, and
    /// returns the configuration iteration from which that holds — or null when the session refused,
    /// which on a display session means this system does not have the API.
    /// </summary>
    public static unsafe ulong? TrySet(
        GraphicsCaptureSession session, IReadOnlyCollection<IntPtr> windows, out string detail)
    {
        var sessionAbi = IntPtr.Zero;
        var display = IntPtr.Zero;
        var iterable = IntPtr.Zero;
        try
        {
            sessionAbi = MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
            var iid = IidDisplayGraphicsCaptureSession;
            var hr = Marshal.QueryInterface(sessionAbi, in iid, out display);
            if (hr < 0)
            {
                detail = $"no display capture session (0x{hr:X8})";
                return null;
            }

            // A window handle is a window id, one to one, which is the whole of the conversion —
            // and the reason the list can be built here rather than travelling as WinRT types.
            var ids = windows.Select(hwnd => new WindowId((ulong)hwnd.ToInt64())).ToList();
            iterable = MarshalInterface<IEnumerable<WindowId>>.FromManaged(ids);

            ulong iteration;
            var vtable = *(void***)display;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong*, int>)vtable[SlotSetWindowExclusionList])(
                display, iterable, &iteration);

            if (hr < 0)
            {
                detail = $"refused (0x{hr:X8})";
                return null;
            }

            detail = $"applied from iteration {iteration} for {ids.Count} window(s)";
            return iteration;
        }
        catch (Exception ex)
        {
            // Every way this can fail is a fact about the system, and the caller's response to all
            // of them is the same: do not claim isolation.
            detail = $"failed ({ex.GetType().Name}: {ex.Message.Trim()})";
            return null;
        }
        finally
        {
            if (iterable != IntPtr.Zero) Marshal.Release(iterable);
            if (display != IntPtr.Zero) Marshal.Release(display);
            if (sessionAbi != IntPtr.Zero) Marshal.Release(sessionAbi);
        }
    }

    /// <summary>
    /// What the session says is currently excluded, which is not the same question as what was
    /// asked for — the system is free to have taken fewer.
    /// </summary>
    public static unsafe IReadOnlyList<IntPtr> GetApplied(GraphicsCaptureSession session)
    {
        var sessionAbi = IntPtr.Zero;
        var display = IntPtr.Zero;
        var view = IntPtr.Zero;
        try
        {
            sessionAbi = MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
            var iid = IidDisplayGraphicsCaptureSession;
            if (Marshal.QueryInterface(sessionAbi, in iid, out display) < 0) return [];

            var vtable = *(void***)display;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtable[SlotGetWindowExclusionList])(
                display, &view);
            if (hr < 0 || view == IntPtr.Zero) return [];

            var ids = MarshalInterface<IReadOnlyList<WindowId>>.FromAbi(view);
            return ids.Select(id => new IntPtr((long)id.Value)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            if (view != IntPtr.Zero) Marshal.Release(view);
            if (display != IntPtr.Zero) Marshal.Release(display);
            if (sessionAbi != IntPtr.Zero) Marshal.Release(sessionAbi);
        }
    }

    /// <summary>
    /// Which configuration this frame was composed under, or null on a system whose frames do not
    /// carry one — where no exclusion could have been set either.
    /// </summary>
    public static unsafe ulong? TryGetIteration(Direct3D11CaptureFrame frame)
    {
        var frameAbi = IntPtr.Zero;
        var frame3 = IntPtr.Zero;
        try
        {
            frameAbi = MarshalInspectable<Direct3D11CaptureFrame>.FromManaged(frame);
            var iid = IidDirect3D11CaptureFrame3;
            if (Marshal.QueryInterface(frameAbi, in iid, out frame3) < 0) return null;

            ulong iteration;
            var vtable = *(void***)frame3;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int>)vtable[SlotGetConfigurationIteration])(
                frame3, &iteration);
            return hr < 0 ? null : iteration;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (frame3 != IntPtr.Zero) Marshal.Release(frame3);
            if (frameAbi != IntPtr.Zero) Marshal.Release(frameAbi);
        }
    }
}
