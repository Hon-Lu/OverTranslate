using System.Runtime.CompilerServices;
using System.Windows;

[assembly: InternalsVisibleTo("OverTranslate.Tests")]
[assembly: InternalsVisibleTo("SceneBackgroundProbe")]
// The offline OCR harness, so a measurement can ask the real RealtimeDetectorSize what the app
// would pick rather than keeping a second copy of that rule that could drift out of step with it.
[assembly: InternalsVisibleTo("OcrHarness")]
// The capture probe, for the same reason: the questions it answers — what this machine's
// Windows.Graphics.Capture will do, whether an exclusion list actually took — are only worth asking
// against the interop the application itself runs on.
[assembly: InternalsVisibleTo("WgcProbe")]
// The capture-bubble background probe: it composes candidate bubble backgrounds out of the
// same glyph repair the realtime path runs, so it has to see those internals.
[assembly: InternalsVisibleTo("CaptureBubbleProbe")]
// The overlay layout probe: it shows a real OverlayWindow and measures where the bubbles landed,
// so it constructs one — and that constructor went internal when the bubble backdrop, an internal
// type, became an optional parameter of it.
[assembly: InternalsVisibleTo("LayoutProbe")]
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
