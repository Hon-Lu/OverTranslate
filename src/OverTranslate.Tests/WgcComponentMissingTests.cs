using System.Runtime.InteropServices;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A Windows without the capture component (8X5-KDH: REGDB_E_CLASSNOTREG from CreateForWindow) is
/// told apart from a window that merely refused, so the user hears "this Windows cannot" instead
/// of display-mode advice that cannot help.
/// </summary>
public class WgcComponentMissingTests
{
    private const int ClassNotRegistered = unchecked((int)0x80040154);

    [Fact]
    public void Class_not_registered_is_a_missing_component()
    {
        Assert.True(WgcWindowCaptureBackend.IsComponentMissing(new COMException("Class not registered", ClassNotRegistered)));
    }

    [Fact]
    public void Class_not_registered_is_found_inside_a_wrapper()
    {
        var wrapped = new InvalidOperationException("attach failed", new COMException("Class not registered", ClassNotRegistered));
        Assert.True(WgcWindowCaptureBackend.IsComponentMissing(wrapped));
    }

    [Fact]
    public void Other_failures_are_not()
    {
        Assert.False(WgcWindowCaptureBackend.IsComponentMissing(new COMException("Access denied", unchecked((int)0x80070005))));
        Assert.False(WgcWindowCaptureBackend.IsComponentMissing(new InvalidOperationException("the window refused capture")));
        Assert.False(WgcWindowCaptureBackend.IsComponentMissing(null));
    }
}
