using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using Shellvis.Core.Config;
using Shellvis.Core.Connectors;
using Shellvis.Core.Cron;

namespace Shellvis.Shell.Views;

/// <summary>
/// The settings menu: a table of contents, not a settings page.
///
/// <b>Why this exists.</b> Ninety-nine tools and six buttons. Connectors, the scheduler and
/// the sticky notes were reachable only by saying the right sentence to the model -- which
/// means reachable only by somebody who already knew they were there. The same question
/// arrived three times over one afternoon: where do I configure the connectors, where is the
/// scheduler, where are the sticky notes. Each time the true answer was "you ask it", and
/// each time that was the wrong answer for an application with a window.
///
/// So this menu makes nothing new possible. Every item here is something the model could
/// already do; what changes is that a person can find it. That is worth a button on a bar
/// with room for six.
///
/// <b>Why a menu and not a settings window with tabs.</b> Because most of these are not
/// settings -- they are places. "Open Task Scheduler" and "sticky notes" belong in a list of
/// destinations, and a tabbed page would have to invent a home for each of them.
/// </summary>
public sealed partial class PillWindow
{
    private void ShowSettingsMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

        AddConnectorItems(menu);

        menu.Items.Add(new MenuFlyoutSeparator());
        Header(menu, "The assistant");

        Add(menu, "   Model and endpoint...", () => _ = ConfigureProviderAsync(_session?.Provider.Id));

        AddScheduleItems(menu);

        Add(menu, "   Sticky notes on the desktop", () => AskSelf("was klebt gerade auf dem Desktop?"));

        menu.Items.Add(new MenuFlyoutSeparator());
        Header(menu, "Where things live");

        // The two places, opened rather than described. A path in a sentence is something the
        // reader has to copy out; a menu item is something they can press.
        Add(menu, "   Open Task Scheduler", () => Open("taskschd.msc"));
        Add(menu, "   Open the Shellvis folder", () => Open(ShellvisPaths.Home));

        menu.ShowAt(SettingsButton);
    }

    /// <summary>
    /// One item per installed connector, saying whether it works.
    ///
    /// Listed individually rather than behind a "Connectors..." submenu: two entries do not
    /// need a submenu, and the whole point is that the state is visible without a click. A
    /// connector that is not configured says so here, which is the question that produced
    /// this menu.
    /// </summary>
    private void AddConnectorItems(MenuFlyout menu)
    {
        // A heading, because the entries alone did not answer the question.
        //
        // The first version listed each connector under its own title -- "Jira & Service
        // Desk", "Confluence" -- and the report was that the connectors were MISSING from the
        // menu. They were not missing; nothing said what they were. Somebody looking for
        // "connectors" reads two product names and moves on. A disabled first item costs one
        // line and turns a list of things into a labelled group.
        Header(menu, "Connectors");

        // Read from disk when there is no session yet, rather than saying "still starting".
        //
        // That message was the report: on a freshly installed copy the menu offered nothing
        // under Connectors but a disabled line, and the one thing this menu exists for was
        // unreachable for as long as the session took to come up. Nothing about listing or
        // configuring a connector needs a session; see InstalledConnectors.
        IReadOnlyList<ConnectorNeeds> connectors = InstalledConnectors();

        if (connectors.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = "   none installed",
                IsEnabled = false,
            });

            return;
        }

        foreach (ConnectorNeeds needs in connectors)
        {
            string label = needs.Title is { Length: > 0 } title ? title : needs.Name;

            Add(menu,
                needs.Ready ? $"   {label} — configured" : $"   {label} — not configured...",
                () => _ = ConfigureConnectorReportAsync(needs.Name));
        }
    }

    /// <summary>
    /// A group heading: a disabled item, which is what a MenuFlyout has instead of headers.
    /// </summary>
    private static void Header(MenuFlyout menu, string text) =>
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = false,
            FontSize = 11,
        });

    private async Task ConfigureConnectorReportAsync(string name)
    {
        string outcome = await ConfigureConnectorAsync(name);
        AddRow(GlyphTool, outcome, "connector");
    }

    /// <summary>The scheduled jobs, with what each one is, and a way to see the rest.</summary>
    private void AddScheduleItems(MenuFlyout menu)
    {
        IReadOnlyList<CronJob> jobs;

        try
        {
            jobs = new CronStore().Load();
        }
        catch (Exception)
        {
            // A corrupt jobs file must not take the menu down with it; the console has
            // already reported it at startup.
            jobs = [];
        }

        Add(menu,
            jobs.Count == 0
                ? "   Scheduled jobs — none yet"
                : $"   Scheduled jobs — {jobs.Count}",
            () => AskSelf("welche Jobs sind eingerichtet?"));
    }

    /// <summary>
    /// Put a question to the agent as if it had been typed.
    ///
    /// Several of these items are questions rather than dialogs -- "what is on the desktop",
    /// "which jobs are set up" -- and the honest way to offer them is to ask the same thing
    /// the user would have asked. It goes through the ordinary turn path, so it appears in the
    /// transcript and the answer lands where every answer lands. A menu item that quietly did
    /// something the conversation has no record of would be worse than no item.
    /// </summary>
    private void AskSelf(string prompt)
    {
        AddRow(GlyphPerson, Oneline(prompt), "asked");
        RecordPrompt(prompt);

        _ = RunErrandTurnAsync(prompt);
    }

    private static void Open(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,

                // Shell execute, because these are a console snap-in and a folder rather than
                // executables: Windows knows what to do with both and this code does not
                // have to.
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // A machine with the snap-in blocked by policy, or a folder that does not exist
            // yet. Nothing to recover; the item simply does nothing rather than throwing out
            // of a menu handler.
        }
    }

    private static void Add(MenuFlyout menu, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
