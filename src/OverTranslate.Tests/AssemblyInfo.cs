using Xunit;

// xunit runs test classes in parallel by default, and almost nothing this suite exercises is safe
// that way: LocalizationService.Current, SettingsService.Instance and the WPF resource dictionaries
// are all process-wide state, and several tests set one of them and then read it back.
//
// That was quietly true for a long time before it was noticed. The runs stayed green because the
// classes that fight tend to be quick and rarely overlapped — until a test that builds WPF elements
// was added, which shifted the timing enough to make the collisions show. They arrived looking like
// unrelated failures in unrelated files, which is the expensive part: two separate afternoons could
// be spent on "why does LanguageDataTests see the wrong language" before the answer turns out to be
// "because another class set it".
//
// Serialising costs a few seconds on a suite that runs in six, and buys a result that means what it
// says. The alternative — giving every class that touches a static its own xunit collection — has to
// be got right again by every test added afterwards, and is wrong silently when it is not.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
