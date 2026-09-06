using System.Windows;
using System.Windows.Threading;
using NoteTaker.App.Services;

namespace NoteTaker.App;

public partial class App : Application
{
    public AppHost? Host { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception fatal)
            {
                InkTrace.LogFault("AppDomain", fatal);
                InkTrace.Dump();
            }
        };

        // Event recording is always on and costs nothing but memory, so a fault during startup
        // still lands in a trace. The Dispatcher-level probes are NOT started here — see
        // InkTrace.StartProbes for why they are opt-in.
        InkTrace.Enable();

        // A copy on disk every minute, so a crash, a force-kill, or a bug noticed ten minutes
        // late still has its trace. The ring holds only a couple of minutes of heavy input.
        InkTrace.StartAutosave();

        try
        {
            Host = await AppHost.CreateAsync();
            var window = new MainWindow(Host);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The app could not start.\n\n{ex.Message}",
                "NoteTaker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// A WPF defect this app cannot prevent and must not dramatise.
    /// </summary>
    /// <remarks>
    /// When Windows stops classifying the pen as a pen, the tip arrives as promoted mouse and
    /// WPF's own ink collection tries to commit a stroke with no points, throwing out of
    /// InkCollectionBehavior.StylusInputEnd. Reproduced in a 70-line WPF app containing an
    /// InkCanvas and nothing else, so there is no app-side cause to fix and no app-side place
    /// to catch it: it is raised from a class handler on the event route, not from any call
    /// this app makes. Swallowing the dialog is the whole remedy available — the exception is
    /// still recorded in the trace, and interrupting a student mid-sentence with a message box
    /// about someone else's bug is worse than the bug.
    /// </remarks>
    private static bool IsWpfEmptyStrokeBug(Exception exception) =>
        exception is ArgumentException { ParamName: "stylusPoints" };

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        InkTrace.LogFault("Dispatcher", e.Exception);

        if (IsWpfEmptyStrokeBug(e.Exception))
        {
            e.Handled = true;
            return;
        }


        // Written out immediately rather than at exit: the app stays alive after this, so the
        // session may well end in a way that never reaches OnExit, and the stack behind a
        // "something went wrong" is the only part of it worth having.
        var trace = InkTrace.Dump();

        MessageBox.Show(
            $"Something went wrong.\n\n{e.Exception.Message}\n\nTrace written to:\n{trace}",
            "NoteTaker",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Keep the app alive: losing unsaved ink is worse than a broken action.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        InkTrace.StopAutosave();
        InkTrace.Stop();
        InkTrace.Dump();
        Host?.Dispose();
        base.OnExit(e);
    }
}
