using FlaUI.Core.AutomationElements;
using Shellvis.Core.Desktop;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Manual harness for the desktop layer.
///
/// This exists because the desktop tools cannot be meaningfully unit tested: their
/// whole job is to reach into other running processes. A probe that drives a real
/// application end to end is the only honest verification, and it doubles as the
/// script an agent-driven regression test will later automate.
///
/// Usage:
///   probe windows              list visible top-level windows
///   probe tree [title]         snapshot the foreground window, or the first whose
///                              title contains [title]
///   probe drive                launch Notepad, type into it, read the text back
///   probe tools                list the tool catalog and drive it through the registry
///   probe agent [url] [model]  run one agent turn; no url means a stubbed transport
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "windows";

        try
        {
            return command switch
            {
                "windows" => ListWindows(),
                "tree" => ShowTree(args.Length > 1 ? args[1] : null),
                "drive" => await DriveNotepadAsync().ConfigureAwait(false),
                "launch" => await LaunchProbe.RunAsync().ConfigureAwait(false),
                "reflect" => await ReflectProbe.RunAsync().ConfigureAwait(false),
                "remote" => await RemoteProbe.RunAsync().ConfigureAwait(false),
                "endpoint" => EndpointProbe.Run(),
                "audiobridge" => StreamedAudioProbe.Run(),

                // The hosted recognisers, against a local stub.
                "speech" => await SpeechCloudProbe.RunAsync().ConfigureAwait(false),

                // When the bar steps out of the way of the foreground window.
                "topmost" => TopmostProbe.Run(),

                // Which microphone Windows actually hands over, and what it hears. Needed
                // because "the default recording device" is two different devices in Windows.
                "mic" => MicrophoneProbe.Run(
                    args.Length > 1 && int.TryParse(args[1], out int secs) ? secs : 3),

                // --fetch downloads the model. Off by default: a harness that pulls half a
                // gigabyte because someone ran the suite is a harness people stop running.
                "whisper" => WhisperProbe.RunAsync(
                    args.Any(a => a.Equals("--fetch", StringComparison.OrdinalIgnoreCase)),
                    args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)
                        && !a.Equals("whisper", StringComparison.OrdinalIgnoreCase)))
                    .GetAwaiter().GetResult(),
                "tools" => await ToolProbe.RunAsync().ConfigureAwait(false),
                "classify" => ClassifierProbe.Run(),
                "config" => ConfigProbe.Run(),
                "skills" => SkillProbe.Run(),
                "sessions" => SessionProbe.Run(),
                "history" => HistoryProbe.Run(args.Length > 1 ? args[1] : null),
                "compaction" => await CompactionProbe.RunAsync().ConfigureAwait(false),
                "office" => OfficeProbe.Run(),
                "outlook" => await OutlookProbe.RunAsync().ConfigureAwait(false),
                "providers" => ProviderProbe.Run(),
                "hooks" => await HookProbe.RunAsync().ConfigureAwait(false),
                "cron" => await CronProbe.RunAsync().ConfigureAwait(false),
                "broker" => await BrokerProbe.RunAsync().ConfigureAwait(false),
                "thunderbird" => await ThunderbirdProbe.RunAsync().ConfigureAwait(false),
                "voice" => VoiceProbe.Run(),
                "stream" => await StreamProbe.RunAsync().ConfigureAwait(false),
                "officelive" => await OfficeComProbe.RunAsync().ConfigureAwait(false),
                "browser" => await BrowserProbe.RunAsync(args.Length > 1 && args[1] == "--headless").ConfigureAwait(false),
                "hass" => await HomeAssistantProbe.RunAsync().ConfigureAwait(false),
                "mcp" => await McpProbe.RunAsync(args.Length > 1 ? args[1] : null).ConfigureAwait(false),
                "agent" => await AgentProbe.RunAsync(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null,
                    args.Length > 3 ? string.Join(' ', args[3..]) : null).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            // A probe should report the failure plainly rather than dumping a stack
            // trace that says nothing about which desktop assumption broke.
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine(
            "usage: probe [windows | tree [title] | drive | launch | reflect | remote | endpoint | audiobridge | tools | agent [baseUrl] [model] [task] | classify | office | outlook | mcp | config | skills | sessions | history | compaction | hass | browser [--headless] | providers | hooks | cron | broker | thunderbird | voice | whisper [--fetch] | mic | topmost | speech | stream | officelive]");
        return 2;
    }

    private static int ListWindows()
    {
        IReadOnlyList<WindowInfo> windows = WindowInspector.ListWindows();

        Console.WriteLine($"{windows.Count} visible top-level windows, front to back:");
        foreach (WindowInfo w in windows.Take(20))
            Console.WriteLine($"  {w}");

        return 0;
    }

    private static int ShowTree(string? titleFilter)
    {
        using var analyzer = new DesktopAnalyzer();

        WindowInfo? target = titleFilter is null
            ? WindowInspector.Foreground()
            : WindowInspector.ListWindows()
                .FirstOrDefault(w => w.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            Console.Error.WriteLine(
                titleFilter is null
                    ? "no foreground window"
                    : $"no visible window whose title contains '{titleFilter}'");
            return 1;
        }

        DesktopSnapshot snapshot = analyzer.Capture(target.Handle, maxDepth: 8);

        Console.WriteLine($"snapshot {snapshot.SnapshotId}, {snapshot.ElementCount} elements"
            + (snapshot.WasTruncated ? " (truncated)" : string.Empty));
        Console.WriteLine(snapshot.ToPromptText());
        return 0;
    }

    /// <summary>
    /// The real test: open a program, understand its UI, act inside it, and verify the
    /// effect by reading the state back rather than trusting the action result.
    /// </summary>
    private static async Task<int> DriveNotepadAsync()
    {
        const string message = "Shellvis has entered the building.";

        Console.WriteLine("1. launching notepad...");
        LaunchResult launch = await ProgramLauncher.LaunchAsync("notepad.exe").ConfigureAwait(false);
        Console.WriteLine($"   {launch}");

        if (!launch.Succeeded || launch.MainWindow is null)
        {
            Console.Error.WriteLine("   no window to drive");
            return 1;
        }

        using var analyzer = new DesktopAnalyzer();

        Console.WriteLine("2. capturing the UI tree...");
        DesktopSnapshot snapshot = analyzer.Capture(launch.MainWindow.Handle, maxDepth: 8);
        Console.WriteLine($"   snapshot {snapshot.SnapshotId}, {snapshot.ElementCount} elements");

        Console.WriteLine("3. locating the text surface...");
        UiElement? editor = FindFirst(
            snapshot.Root,
            e => e.ControlType is "Edit" or "Document" && e.Actions.Contains("SetValue"))
            ?? FindFirst(snapshot.Root, e => e.ControlType is "Edit" or "Document");

        if (editor is null)
        {
            Console.Error.WriteLine("   no Edit or Document element found. Tree was:");
            Console.Error.WriteLine(snapshot.ToPromptText());
            return 1;
        }

        Console.WriteLine($"   @{editor.Ref} {editor.ControlType} \"{editor.Name}\" "
            + $"[{string.Join(',', editor.Actions)}]");

        Console.WriteLine("4. writing text...");
        AutomationElement live = analyzer.Resolve(snapshot.SnapshotId, editor.Ref);
        ActionResult typed = DesktopActions.SetText(live, message);
        Console.WriteLine($"   {typed}");

        Console.WriteLine("5. reading it back...");
        // Give the app a moment: a typed string lands asynchronously, and reading
        // immediately is the classic way to get a false negative here.
        await Task.Delay(400).ConfigureAwait(false);
        string readBack = DesktopActions.ReadText(live);
        Console.WriteLine($"   got: \"{readBack.Trim()}\"");

        bool verified = readBack.Contains(message, StringComparison.Ordinal);
        Console.WriteLine(verified
            ? "\nVERIFIED: launched a program, read its UI, acted in it, and confirmed the effect."
            : "\nNOT VERIFIED: the text did not arrive.");

        return verified ? 0 : 1;
    }

    private static UiElement? FindFirst(UiElement root, Func<UiElement, bool> predicate)
    {
        if (predicate(root))
            return root;

        foreach (UiElement child in root.Children)
        {
            UiElement? hit = FindFirst(child, predicate);
            if (hit is not null)
                return hit;
        }

        return null;
    }
}














