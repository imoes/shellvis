using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using Shellvis.Shell.Agent;
using Shellvis.Shell.Views;

namespace Shellvis.Shell;

public partial class App : Application
{
    internal static PillWindow? Pill { get; private set; }

    public App() => InitializeComponent();

    /// <summary>
    /// Start the bar, or carry out one errand and leave.
    ///
    /// <b>Three ways in.</b> Started normally, this puts the pill on screen. Started as
    /// <c>Shellvis.Shell.exe --job briefing</c> by a Windows task, or as
    /// <c>--prompt "what is due today"</c> by anything at all, it carries out that one errand.
    ///
    /// <b>And it hands the errand over rather than duplicating the application.</b> If a
    /// Shellvis is already running, the parameter goes to THAT instance: a second one would
    /// mean a second PowerShell runspace, a second COM apartment, a second tray icon, and a
    /// result that appears nowhere the user is looking, because the window they have open is
    /// not the one that did the work. A prompt lands in the running conversation; a job runs
    /// there as a scheduled run, which is also the only way its notification can be shown at
    /// all -- an alert needs a window to belong to.
    ///
    /// Only a machine with no Shellvis open falls back to doing it alone, silently, with the
    /// result left in the store for whoever opens the application next. The exit code is how a
    /// task's history reports it: 0 for done, 1 for failed.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (ScheduledRun.RequestedErrand() is { } errand)
        {
            // The running instance first. It has the windows, the tray icon and the alert.
            if (PromptChannel.TrySend(errand))
            {
                Environment.Exit(0);
                return;
            }

            CarryOutAloneAndExit(errand);
            return;
        }

        Pill = new PillWindow();
        Pill.Activate();
    }

    private void CarryOutAloneAndExit(Errand errand)
    {
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

        // Fire and forget deliberately: OnLaunched has to return for the message loop to run,
        // and the run needs that loop -- the COM apartment the Office and Outlook tools use is
        // pumped by it. Awaiting here would deadlock the very thing being started.
        _ = RunAsync(errand, dispatcher);
    }

    private async Task RunAsync(Errand errand, DispatcherQueue dispatcher)
    {
        int code = 1;

        try
        {
            // A ceiling on the whole run. An errand with nobody watching has no other way to
            // end: a model that never answers would leave this process alive until the machine
            // is rebooted, holding a runspace and a COM apartment.
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            string outcome = await ScheduledRun.RunAsync(errand, dispatcher, stop.Token)
                .ConfigureAwait(true);

            System.Diagnostics.Debug.WriteLine(outcome);
            Console.WriteLine(outcome);

            code = outcome.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || outcome.StartsWith("no scheduled job", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"the errand threw: {ex.Message}");
        }
        finally
        {
            Environment.Exit(code);
        }
    }
}
