using System.Text.Json.Nodes;
using Shellvis.Core.Config;
using Shellvis.Core.Hooks;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Runs real hook processes.
///
/// A mock would prove nothing here: the whole mechanism is a child process, a pipe and
/// a timeout, and every interesting failure lives in that plumbing -- a hook that
/// ignores stdin, one that writes megabytes, one that never exits. The hooks are written
/// as batch files into the temp directory and executed the way a user's would be.
/// </summary>
internal static class HookProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("=== Hooks ===");
        Console.WriteLine();

        string dir = Path.Combine(Path.GetTempPath(), "shellvis-hook-probe");
        Directory.CreateDirectory(dir);

        failures += Naming();
        failures += Loading();
        failures += await ProtocolAsync(dir).ConfigureAwait(false);
        failures += await BlockingAsync(dir).ConfigureAwait(false);
        failures += await RobustnessAsync(dir).ConfigureAwait(false);
        failures += await ConsentAsync(dir).ConfigureAwait(false);
        failures += await MatchingAsync(dir).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: hooks fire, can block, can rewrite, and cannot hang the turn."
            : $"{failures} hook check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int Naming()
    {
        Console.WriteLine("-- events --");
        int failures = 0;

        failures += Check("all thirteen protocol events exist", HookCatalog.AllNames.Count == 13);

        // Every name has to round-trip, or a config entry that looks right is silently
        // unreachable.
        foreach (HookEvent value in Enum.GetValues<HookEvent>())
        {
            if (HookCatalog.Parse(HookCatalog.NameOf(value)) != value)
                failures += Check($"{value} round-trips through its wire name", false);
        }

        failures += Check("every event round-trips through its wire name", failures == 0);
        failures += Check("names are snake_case", HookCatalog.NameOf(HookEvent.PreToolCall) == "pre_tool_call");
        failures += Check("parsing ignores case", HookCatalog.Parse("PRE_TOOL_CALL") == HookEvent.PreToolCall);
        failures += Check("an unknown name does not parse", HookCatalog.Parse("on_coffee_break") is null);

        int fires = Enum.GetValues<HookEvent>().Count(HookCatalog.Fires);
        Console.WriteLine($"    {fires} of 13 events are actually raised by this build");

        failures += Check(
            "the events with a wiring site are marked as firing",
            HookCatalog.Fires(HookEvent.PreToolCall)
                && HookCatalog.Fires(HookEvent.PostToolCall)
                && HookCatalog.Fires(HookEvent.TransformToolResult)
                && HookCatalog.Fires(HookEvent.PreLlmCall)
                && HookCatalog.Fires(HookEvent.OnSessionStart));

        // The honesty check: an event with nowhere to fire from must NOT claim it does.
        failures += Check(
            "the events with no wiring site are marked as not firing",
            !HookCatalog.Fires(HookEvent.SubagentStop)
                && !HookCatalog.Fires(HookEvent.PreApiRequest)
                && !HookCatalog.Fires(HookEvent.OnSessionFinalize));

        Console.WriteLine();
        return failures;
    }

    private static int Loading()
    {
        Console.WriteLine("-- config loading --");
        int failures = 0;

        var warnings = new List<string>();

        var configured = new Dictionary<string, List<HookSection>>
        {
            ["pre_tool_call"] = [new HookSection { Command = "echo one", Matcher = "^powershell_" }],
            ["post_tool_call"] = [new HookSection { Command = "echo two", TimeoutSeconds = 9000 }],
            ["on_coffee_break"] = [new HookSection { Command = "echo three" }],
            ["subagent_stop"] = [new HookSection { Command = "echo four" }],
            ["pre_llm_call"] = [new HookSection { Command = "echo five", Matcher = "[unclosed" }],
            ["on_session_start"] = [new HookSection { Command = "   " }],
        };

        IReadOnlyList<HookDefinition> hooks = HookLoader.Load(configured, warnings);

        foreach (string warning in warnings)
            Console.WriteLine("    ! " + warning);

        // Three, not two: the subagent_stop entry is syntactically fine and IS loaded,
        // it just has no site to fire from. Loading it keeps the loader's job to
        // "is this valid" and the firing decision in one place, so a future build that
        // raises the event needs no loader change.
        failures += Check("valid entries load", hooks.Count == 3);

        failures += Check(
            "a hook on a never-raised event is loaded but its event does not fire",
            hooks.Any(h => h.Event == HookEvent.SubagentStop)
                && !HookCatalog.Fires(HookEvent.SubagentStop));

        failures += Check(
            "an unknown event name is reported with the list of real ones",
            warnings.Any(w => w.Contains("on_coffee_break") && w.Contains("pre_tool_call")));

        // The point of the whole Fires table: a hook that can never run must say so.
        failures += Check(
            "an event this build never raises is reported rather than accepted in silence",
            warnings.Any(w => w.Contains("subagent_stop") && w.Contains("will not run")));

        failures += Check(
            "a bad regex is reported and the hook skipped",
            warnings.Any(w => w.Contains("not a") && w.Contains("regex"))
                && !hooks.Any(h => h.Event == HookEvent.PreLlmCall));

        failures += Check(
            "an entry with no command is reported and skipped",
            warnings.Any(w => w.Contains("no command")));

        // Capped rather than rejected: the intent is clear, and honouring 9000s
        // literally would be an agent that can hang for two and a half hours.
        HookDefinition? capped = hooks.FirstOrDefault(h => h.Event == HookEvent.PostToolCall);

        failures += Check(
            $"an excessive timeout is capped at {HookDefinition.MaxTimeoutSeconds}s",
            capped?.TimeoutSeconds == HookDefinition.MaxTimeoutSeconds
                && warnings.Any(w => w.Contains("capped")));

        HookDefinition? matched = hooks.FirstOrDefault(h => h.Event == HookEvent.PreToolCall);

        failures += Check(
            "a matcher narrows to the tools it names",
            matched?.Matches("powershell_run") == true && matched?.Matches("window_list") == false);

        failures += Check(
            "no matcher means every tool",
            capped?.Matches("anything") == true);

        failures += Check("an empty config loads nothing", HookLoader.Load(null, warnings).Count == 0);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ProtocolAsync(string dir)
    {
        Console.WriteLine("-- protocol --");
        int failures = 0;

        // Writes the JSON it was handed to a file, so what the hook RECEIVED can be
        // asserted rather than assumed.
        string capture = Path.Combine(dir, "received.json");
        string echoScript = Write(dir, "capture.cmd", $"""
        @echo off
        findstr /r ".*" > "{capture}"
        """);

        if (File.Exists(capture))
            File.Delete(capture);

        var runner = new HookRunner(
            [new HookDefinition(HookEvent.PreToolCall, echoScript)],
            AllowAllHooks.Instance,
            "s-probe-1");

        HookOutcome outcome = await runner.FireAsync(
            HookEvent.PreToolCall,
            "powershell_run",
            new JsonObject { ["tool_input"] = "command = Get-Date" }).ConfigureAwait(false);

        failures += Check("a silent hook is a no-op", outcome.IsEmpty);
        failures += Check("the hook actually ran", File.Exists(capture));

        if (File.Exists(capture))
        {
            string json = File.ReadAllText(capture);
            Console.WriteLine("    received: " + json.Trim());

            JsonNode? node = JsonNode.Parse(json);

            failures += Check(
                "the payload names the event",
                node?["hook_event_name"]?.GetValue<string>() == "pre_tool_call");

            failures += Check(
                "the payload carries the tool name",
                node?["tool_name"]?.GetValue<string>() == "powershell_run");

            // Without this a hook cannot correlate its own log across a conversation.
            failures += Check(
                "the payload carries the session id",
                node?["session_id"]?.GetValue<string>() == "s-probe-1");

            failures += Check("the payload carries cwd", node?["cwd"] is not null);

            failures += Check(
                "event-specific fields are merged in",
                node?["tool_input"]?.GetValue<string>() == "command = Get-Date");
        }

        // The common case: a hook that logs and says nothing must not be treated as a
        // protocol violation.
        var chatty = new HookRunner(
            [new HookDefinition(HookEvent.PostToolCall, "echo just logging, nothing to report")],
            AllowAllHooks.Instance);

        HookOutcome noise = await chatty.FireAsync(HookEvent.PostToolCall, "window_list")
            .ConfigureAwait(false);

        failures += Check("plain text output is a no-op, not an error", noise.IsEmpty);
        failures += Check("and it produces no diagnostic noise", chatty.DrainNotes().Count == 0);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> BlockingAsync(string dir)
    {
        Console.WriteLine("-- blocking and rewriting --");
        int failures = 0;

        string blocker = Write(dir, "block.cmd", """
        @echo off
        echo {"decision":"block","reason":"the probe forbids this tool"}
        """);

        var runner = new HookRunner(
            [new HookDefinition(HookEvent.PreToolCall, blocker)], AllowAllHooks.Instance);

        HookOutcome blocked = await runner.FireAsync(HookEvent.PreToolCall, "ui_click")
            .ConfigureAwait(false);

        failures += Check("a hook can block an action", blocked.Blocked);

        // The reason is what the model is told, so it can propose something else instead
        // of assuming the click happened.
        failures += Check(
            "and its reason is carried through",
            blocked.Reason?.Contains("probe forbids") == true);

        string contextual = Write(dir, "context.cmd", """
        @echo off
        echo {"context":"this machine is a build agent"}
        """);

        var informer = new HookRunner(
            [new HookDefinition(HookEvent.PostToolCall, contextual)], AllowAllHooks.Instance);

        HookOutcome informed = await informer.FireAsync(HookEvent.PostToolCall, "window_list")
            .ConfigureAwait(false);

        failures += Check(
            "a hook can add context without blocking",
            !informed.Blocked && informed.Context?.Contains("build agent") == true);

        string rewriter = Write(dir, "rewrite.cmd", """
        @echo off
        echo {"replacement":"REDACTED BY HOOK"}
        """);

        var transformer = new HookRunner(
            [new HookDefinition(HookEvent.TransformToolResult, rewriter)], AllowAllHooks.Instance);

        HookOutcome rewritten = await transformer.FireAsync(
            HookEvent.TransformToolResult,
            "powershell_run",
            new JsonObject { ["tool_result"] = "secret output" }).ConfigureAwait(false);

        failures += Check(
            "a hook can rewrite a tool result",
            rewritten.Replacement == "REDACTED BY HOOK");

        // Chaining, so two filters compose instead of the last one winning. Verified by
        // having the second hook report what it was handed.
        string secondCapture = Path.Combine(dir, "chained.json");
        // Placeholder rather than interpolation: the JSON braces and a raw interpolated
        // string's delimiters fight, and the escaping would be the least readable part.
        string chainReader = Write(dir, "chain2.cmd", """
        @echo off
        findstr /r ".*" > "__CAPTURE__"
        echo {"replacement":"SECOND"}
        """.Replace("__CAPTURE__", secondCapture, StringComparison.Ordinal));

        if (File.Exists(secondCapture))
            File.Delete(secondCapture);

        var chain = new HookRunner(
            [
                new HookDefinition(HookEvent.TransformToolResult, rewriter),
                new HookDefinition(HookEvent.TransformToolResult, chainReader),
            ],
            AllowAllHooks.Instance);

        HookOutcome chained = await chain.FireAsync(
            HookEvent.TransformToolResult,
            "powershell_run",
            new JsonObject { ["tool_result"] = "original" }).ConfigureAwait(false);

        failures += Check("the last transform in a chain wins", chained.Replacement == "SECOND");

        if (File.Exists(secondCapture))
        {
            JsonNode? seen = JsonNode.Parse(File.ReadAllText(secondCapture));

            failures += Check(
                "and each transform sees the previous one's output, so filters compose",
                seen?["tool_result"]?.GetValue<string>() == "REDACTED BY HOOK");
        }
        else
        {
            failures += Check("the second transform ran", false);
        }

        // A veto must stop the rest: later context describing an action that will not
        // happen would be worse than no context.
        string neverRuns = Path.Combine(dir, "after-block.txt");

        if (File.Exists(neverRuns))
            File.Delete(neverRuns);

        var shortCircuit = new HookRunner(
            [
                new HookDefinition(HookEvent.PreToolCall, blocker),
                new HookDefinition(HookEvent.PreToolCall, $"echo ran > \"{neverRuns}\""),
            ],
            AllowAllHooks.Instance);

        await shortCircuit.FireAsync(HookEvent.PreToolCall, "ui_click").ConfigureAwait(false);

        failures += Check("a block short-circuits the remaining hooks", !File.Exists(neverRuns));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> RobustnessAsync(string dir)
    {
        Console.WriteLine("-- robustness --");
        int failures = 0;

        // The most important property of the whole subsystem: a broken hook must not be
        // able to disable the agent.
        string hang = Write(dir, "hang.cmd", """
        @echo off
        ping -n 30 127.0.0.1 > nul
        echo {"decision":"block","reason":"too late"}
        """);

        var slow = new HookRunner(
            [new HookDefinition(HookEvent.PreToolCall, hang, TimeoutSeconds: 2)],
            AllowAllHooks.Instance);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        HookOutcome timedOut = await slow.FireAsync(HookEvent.PreToolCall, "ui_click")
            .ConfigureAwait(false);
        clock.Stop();

        Console.WriteLine($"    returned after {clock.ElapsedMilliseconds} ms");

        failures += Check("a hanging hook is abandoned at its timeout", clock.Elapsed.TotalSeconds < 10);

        // NOT treated as a block: a slow hook silently disabling every tool would be a
        // worse failure than the hook not running.
        failures += Check("a timed-out hook does not block the action", !timedOut.Blocked);

        IReadOnlyList<string> notes = slow.DrainNotes();
        Console.WriteLine("    note: " + string.Join(" | ", notes));
        failures += Check("and the timeout is reported", notes.Any(n => n.Contains("timed out")));

        var missing = new HookRunner(
            [new HookDefinition(HookEvent.PreToolCall, "this-command-does-not-exist-xyz")],
            AllowAllHooks.Instance);

        HookOutcome gone = await missing.FireAsync(HookEvent.PreToolCall, "ui_click")
            .ConfigureAwait(false);

        failures += Check("a missing command does not block", !gone.Blocked);
        failures += Check("and is reported", missing.DrainNotes().Count > 0);

        string malformed = Write(dir, "malformed.cmd", """
        @echo off
        echo {"decision":"blo
        """);

        var broken = new HookRunner(
            [new HookDefinition(HookEvent.PreToolCall, malformed)], AllowAllHooks.Instance);

        HookOutcome garbled = await broken.FireAsync(HookEvent.PreToolCall, "ui_click")
            .ConfigureAwait(false);

        failures += Check("malformed JSON does not block", !garbled.Blocked);

        // Output that STARTS like JSON but does not parse means the hook meant to say
        // something and failed -- unlike plain log output, that is worth reporting.
        failures += Check(
            "and is reported, unlike plain text output",
            broken.DrainNotes().Any(n => n.Contains("does not parse")));

        // A hook that never reads stdin closes the pipe; writing to it then throws.
        string ignoresInput = Write(dir, "ignore.cmd", """
        @echo off
        echo {"context":"I never read stdin"}
        """);

        var deaf = new HookRunner(
            [new HookDefinition(HookEvent.PostToolCall, ignoresInput)], AllowAllHooks.Instance);

        HookOutcome heard = await deaf.FireAsync(
            HookEvent.PostToolCall,
            "powershell_run",
            new JsonObject { ["tool_result"] = new string('x', 200_000) }).ConfigureAwait(false);

        failures += Check(
            "a hook that ignores a large stdin still works",
            heard.Context?.Contains("never read") == true);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ConsentAsync(string dir)
    {
        Console.WriteLine("-- consent --");
        int failures = 0;

        string ran = Path.Combine(dir, "consent-ran.txt");

        if (File.Exists(ran))
            File.Delete(ran);

        var counter = new CountingConsent(allow: false);

        var denied = new HookRunner(
            [new HookDefinition(HookEvent.PreToolCall, $"echo ran > \"{ran}\"")], counter);

        // Three calls, to prove the answer is cached: being asked again after saying no
        // is worse than the original question.
        for (int i = 0; i < 3; i++)
            await denied.FireAsync(HookEvent.PreToolCall, "ui_click").ConfigureAwait(false);

        failures += Check("a denied hook does not run", !File.Exists(ran));
        failures += Check("and is asked about exactly once", counter.Asked == 1);
        failures += Check("and the denial is reported", denied.DrainNotes().Count > 0);

        var granting = new CountingConsent(allow: true);

        var allowed = new HookRunner(
            [new HookDefinition(HookEvent.PreToolCall, $"echo ran > \"{ran}\"")], granting);

        for (int i = 0; i < 3; i++)
            await allowed.FireAsync(HookEvent.PreToolCall, "ui_click").ConfigureAwait(false);

        failures += Check("an allowed hook runs", File.Exists(ran));

        // Consent is per hook, not per call: a pre_tool_call hook fires on every tool,
        // and asking each time would make the feature unusable.
        failures += Check("and is also asked about only once", granting.Asked == 1);

        // The same script on a different event is a different grant, because the data it
        // sees and the power it holds differ.
        var pre = new HookDefinition(HookEvent.PreToolCall, "echo x");
        var post = new HookDefinition(HookEvent.PostToolCall, "echo x");

        failures += Check(
            "consent identity covers event and command together",
            pre.ConsentKey != post.ConsentKey);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> MatchingAsync(string dir)
    {
        Console.WriteLine("-- matching --");
        int failures = 0;

        string marker = Path.Combine(dir, "matched.txt");

        if (File.Exists(marker))
            File.Delete(marker);

        var runner = new HookRunner(
            [
                new HookDefinition(
                    HookEvent.PreToolCall,
                    $"echo ran > \"{marker}\"",
                    new System.Text.RegularExpressions.Regex("^powershell_")),
            ],
            AllowAllHooks.Instance);

        await runner.FireAsync(HookEvent.PreToolCall, "window_list").ConfigureAwait(false);
        failures += Check("a non-matching tool does not fire the hook", !File.Exists(marker));

        await runner.FireAsync(HookEvent.PreToolCall, "powershell_run").ConfigureAwait(false);
        failures += Check("a matching tool does", File.Exists(marker));

        var other = new HookRunner(
            [new HookDefinition(HookEvent.PostToolCall, "echo x")], AllowAllHooks.Instance);

        failures += Check("Has reports only configured events",
            other.Has(HookEvent.PostToolCall) && !other.Has(HookEvent.PreToolCall));

        HookOutcome unrelated = await other.FireAsync(HookEvent.PreToolCall, "anything")
            .ConfigureAwait(false);

        failures += Check("firing an unconfigured event is a no-op", unrelated.IsEmpty);

        Console.WriteLine();
        return failures;
    }

    /// <summary>Counts how often consent was requested, to prove the answer is cached.</summary>
    private sealed class CountingConsent(bool allow) : IHookConsent
    {
        public int Asked { get; private set; }

        public Task<bool> AllowAsync(HookDefinition hook, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(allow);
        }
    }

    private static string Write(string dir, string name, string body)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, body);

        // Quoted: the temp path contains the user name, which on a domain machine can
        // contain characters cmd would otherwise split on.
        return $"\"{path}\"";
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
