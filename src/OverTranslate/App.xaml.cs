using System.Threading;
using System.Windows;
using NLog;
using OverTranslate.Services;
using OverTranslate.Models;
using System.Threading.Tasks;
using Velopack;
using OverTranslate.Views;
using OverTranslate.Views.Shell;
using OverTranslate.Views.Translation;

namespace OverTranslate;

public partial class App
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string ActivateEventName = "OverTranslate_Activate_9F3C7B2E";

    private EventWaitHandle? _activateEvent;

    [STAThread]
    private static void Main(string[] args)
    {
        // Uninstalling takes everything the app wrote with it — settings, logs, models — and the
        // sign-in entry. Runs in a process of its own that exits straight after; see UninstallCleanup.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unhandled UI exception");

            // A crash during a capture session used to take the process down while its full-screen
            // dim window stayed painted over the desktop — unclosable, because the code that owned
            // it was gone. Tear the session down first; if there was one, its state is now fully
            // reset (windows closed, fields cleared, session id bumped), so keeping the app alive in
            // its tray-idle state is strictly better than dying with the overlay stuck on screen.
            // Exceptions outside a capture session keep the original fail-fast behaviour.
            if ((MainWindow as Views.MainWindow)?.ForceCloseOverlays() == true)
                ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Fatal(ex.ExceptionObject as Exception, "Unhandled domain exception");
            LogManager.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unobserved Task exception");
            ex.SetObserved();
        };
        _activateEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, ActivateEventName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — signal it to show the window and exit
            _activateEvent.Set();
            _activateEvent.Dispose();
            Shutdown();
            return;
        }

        // Background thread listens for activation signals from future launch attempts
        new Thread(MonitorActivationRequests) { IsBackground = true }.Start();

        // Before the first line worth keeping is written, and early enough that a user who ticked
        // 記錄詳細資訊 gets the detail from startup onwards rather than from their next toggle.
        LogLevelService.Apply(SettingsService.Instance.Current.VerboseLogging);

        // Straight after the log level and before anything else runs, so the identity is on the
        // first lines of every session's log rather than turning up partway down whichever of them
        // happened to reach it first.
        AppIdentityService.Initialize();

        ThemeService.Apply(SettingsService.Instance.Current.Theme);

        // Before any window is built, so nothing is ever constructed against the wrong dictionary.
        LocalizationService.Apply(LocalizationService.Current);

        // Kept in the shipped log: the process's DPI awareness is fixed at launch, and a misreported
        // monitor size is invisible to every managed API, so without this a display-geometry report
        // cannot be diagnosed from a log at all.
        DisplayDiagnostics.LogSnapshot("startup", level: LogLevel.Info);

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Past the single-instance check for the reason given below, and before any window: the
        // card and the hints read the switch this puts in step.
        MangaModelOptions.Attach();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.InitializeApp();

        UpdateNotifier.StartPolling();
        _ = PromptForUpdateAsync();

        // A manga model download the app was closed in the middle of is not kept. Here, past the
        // single-instance check, rather than when the store is built: a second launch must not clear
        // the folder the running instance is downloading into.
        _ = Task.Run(AppServices.MangaModels.DiscardIncomplete);
    }

    /// <summary>
    /// The one update check allowed to put a window on the screen.
    /// </summary>
    /// <remarks>
    /// Every later check — see <see cref="UpdateNotifier.StartPolling"/> — only lights up the nav
    /// rail. This used to be the only check there was, which meant the dialog was the sole way an
    /// update could ever be announced, which in turn meant it had to reappear on every single launch
    /// until the user gave in. It no longer has to carry that alone: 跳過此版本 turns it off for a
    /// release without taking the rail's entry with it.
    /// </remarks>
    private static async Task PromptForUpdateAsync()
    {
        var info = await UpdateNotifier.CheckAsync();
        if (info is null || UpdateNotifier.IsSkipped(info)) return;
        Current.Dispatcher.Invoke(() => UpdateWindow.ShowOrActivate(info));
    }

    private void MonitorActivationRequests()
    {
        try
        {
            while (_activateEvent!.WaitOne())
            {
                if (Dispatcher.HasShutdownStarted) break;
                Dispatcher.Invoke(ShowOrActivateShell);
            }
        }
        catch { /* event disposed on exit — normal shutdown */ }
    }

    internal static void ShowOrActivateShell() => ShellWindow.ShowOrActivate();

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        base.OnExit(e);
    }
}
