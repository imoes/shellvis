using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Shellvis.Core.Agent;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Checks the streaming path against a scripted chat client.
///
/// A stub rather than a live endpoint, and deliberately: the interesting cases are a
/// stream that goes silent forever, a tool call split across chunk boundaries, and a
/// cancellation mid-answer. None of those can be produced on demand by a real provider,
/// and waiting for them to happen by chance is not testing.
/// </summary>
internal static class StreamProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("=== Streaming ===");
        Console.WriteLine();

        failures += await DeltasAsync().ConfigureAwait(false);
        failures += await SplitToolCallAsync().ConfigureAwait(false);
        failures += await StallAsync().ConfigureAwait(false);
        failures += await CancellationAsync().ConfigureAwait(false);
        failures += await NonStreamingAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: deltas arrive in order, split tool calls coalesce, and a dead stream is abandoned."
            : $"{failures} streaming check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> DeltasAsync()
    {
        Console.WriteLine("-- deltas --");
        int failures = 0;

        var client = new ScriptedClient([
            Text("Der "), Text("Rechner "), Text("heisst "), Text("TESTBOX."),
        ]);

        var loop = new AgentLoop(client, new ToolRegistry(), AutoApprove.Instance);

        var deltas = new List<string>();
        string final = string.Empty;

        await foreach (AgentEvent evt in loop.RunAsync("frage").ConfigureAwait(false))
        {
            if (evt is AgentEvent.AssistantDelta delta)
                deltas.Add(delta.Text);
            else if (evt is AgentEvent.AssistantMessage message)
                final = message.Text;
        }

        Console.WriteLine($"    {deltas.Count} delta(s): {string.Join("|", deltas)}");

        failures += Check("every chunk arrives as a delta", deltas.Count == 4);
        failures += Check("in order", string.Concat(deltas) == "Der Rechner heisst TESTBOX.");

        // The final message has to agree with the deltas, or the transcript shows one
        // thing while the model was sent another.
        failures += Check("and the final message matches them", final == string.Concat(deltas));

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// A function call arriving in fragments.
    ///
    /// This is where a hand-rolled streaming loop usually breaks: the name comes in one
    /// chunk and the arguments in several, and a loop that reads each chunk as a complete
    /// call either invokes a tool with no arguments or invents one per fragment.
    /// </summary>
    private static async Task<int> SplitToolCallAsync()
    {
        Console.WriteLine("-- a tool call split across chunks --");
        int failures = 0;

        var registry = new ToolRegistry();
        registry.RegisterFrom(new Echoer());

        // Fragmented text around the call as well, so the coalescing has to keep both
        // kinds of content apart.
        var client = new ScriptedClient(
            [
                Text("Ich schaue "), Text("nachher nach. "),
                Call("probe_echo", new Dictionary<string, object?> { ["what"] = "hallo" }),
            ],
            [Text("Fertig.")]);

        var loop = new AgentLoop(client, registry, AutoApprove.Instance);

        var calls = new List<string>();
        var results = new List<string>();
        int deltas = 0;

        await foreach (AgentEvent evt in loop.RunAsync("frage").ConfigureAwait(false))
        {
            switch (evt)
            {
                case AgentEvent.AssistantDelta:
                    deltas++;
                    break;

                case AgentEvent.ToolStarted started:
                    calls.Add(started.Tool);
                    break;

                case AgentEvent.ToolCompleted completed:
                    results.Add(completed.Result);
                    break;
            }
        }

        Console.WriteLine($"    {deltas} delta(s), calls: {string.Join(", ", calls)}");
        Console.WriteLine($"    result: {string.Join(" | ", results)}");

        // Three, not two: the two chunks before the call, plus the follow-up round's
        // "Fertig." after the tool result. Both rounds stream, which is the point --
        // an implementation that only streamed the first would still answer correctly.
        failures += Check("both rounds stream their text", deltas == 3);
        failures += Check("the call is coalesced into exactly one invocation", calls.Count == 1);
        failures += Check("with the right tool", calls.FirstOrDefault() == "probe_echo");

        // The arguments surviving is the whole point: a call whose arguments were lost
        // runs the tool with defaults and looks like it worked.
        failures += Check(
            "and its arguments survived the fragmentation",
            results.FirstOrDefault()?.Contains("hallo") == true);

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// A stream that stops sending and never closes.
    ///
    /// Not a hypothetical: a local inference server can stop mid-answer with the
    /// connection still open, and the HTTP timeout does not apply once bytes have started
    /// flowing. Without the watchdog the turn waits forever, which presents as a frozen
    /// application.
    /// </summary>
    private static async Task<int> StallAsync()
    {
        Console.WriteLine("-- a stream that goes silent --");
        int failures = 0;

        var client = new ScriptedClient([Text("Ich fange an zu antworten"), Hang()]);

        var loop = new AgentLoop(
            client,
            new ToolRegistry(),
            AutoApprove.Instance,
            // Three seconds, so the check does not take a minute and a half.
            new AgentOptions(MaxIterations: 2, StallTimeoutSeconds: 3));

        var failuresSeen = new List<string>();
        var deltas = new List<string>();
        var clock = Stopwatch.StartNew();

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await foreach (AgentEvent evt in loop.RunAsync("frage", guard.Token).ConfigureAwait(false))
        {
            if (evt is AgentEvent.AssistantDelta delta)
                deltas.Add(delta.Text);
            else if (evt is AgentEvent.Failure failure)
                failuresSeen.Add(failure.Message);
        }

        clock.Stop();

        Console.WriteLine($"    gave up after {clock.Elapsed.TotalSeconds:F1}s");

        foreach (string message in failuresSeen)
            Console.WriteLine("    " + message);

        failures += Check("the stream is abandoned near the timeout", clock.Elapsed.TotalSeconds is > 2 and < 20);
        failures += Check("and it is reported as a failure", failuresSeen.Count > 0);

        failures += Check(
            "the message says the model went quiet",
            failuresSeen.Any(m => m.Contains("stopped sending")));

        // What arrived before the silence is kept: a truncated paragraph plus a warning
        // is more use than discarding text the model already produced.
        failures += Check(
            "and what arrived before the silence was kept",
            deltas.Count > 0 && string.Concat(deltas).Contains("antworten"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> CancellationAsync()
    {
        Console.WriteLine("-- cancellation mid-stream --");
        int failures = 0;

        var client = new ScriptedClient([
            Text("eins "), Slow("zwei "), Slow("drei "), Slow("vier "), Slow("fuenf "),
        ]);

        var loop = new AgentLoop(client, new ToolRegistry(), AutoApprove.Instance);

        using var cts = new CancellationTokenSource();
        var deltas = new List<string>();
        bool threw = false;

        try
        {
            await foreach (AgentEvent evt in loop.RunAsync("frage", cts.Token).ConfigureAwait(false))
            {
                if (evt is AgentEvent.AssistantDelta delta)
                {
                    deltas.Add(delta.Text);

                    // Cancelled from inside the enumeration, which is what pressing Escape
                    // in the pill does.
                    if (deltas.Count == 2)
                        await cts.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Console.WriteLine($"    {deltas.Count} delta(s) before the stop; cancelled cleanly: {threw}");

        failures += Check("cancellation stops the stream early", deltas.Count is >= 2 and < 5);

        // A user cancel has to be distinguishable from a stall: one is intentional and
        // must propagate, the other is a fault and must be reported.
        failures += Check("and surfaces as cancellation, not as a provider failure", threw);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> NonStreamingAsync()
    {
        Console.WriteLine("-- streaming turned off --");
        int failures = 0;

        var client = new ScriptedClient([Text("Antwort ohne Streaming.")]);

        var loop = new AgentLoop(
            client, new ToolRegistry(), AutoApprove.Instance, new AgentOptions(Stream: false));

        int deltas = 0;
        string final = string.Empty;

        await foreach (AgentEvent evt in loop.RunAsync("frage").ConfigureAwait(false))
        {
            if (evt is AgentEvent.AssistantDelta)
                deltas++;
            else if (evt is AgentEvent.AssistantMessage message)
                final = message.Text;
        }

        // The escape hatch has to actually work: some providers stream badly, and the
        // setting exists so that is recoverable without a rebuild.
        failures += Check("no deltas are produced", deltas == 0);
        failures += Check("but the answer still arrives", final == "Antwort ohne Streaming.");

        Console.WriteLine();
        return failures;
    }

    // ---- scripted chunks -------------------------------------------------------

    private sealed record Chunk(string? Text, FunctionCallContent? Call, TimeSpan Delay, bool Hangs);

    private static Chunk Text(string text) => new(text, null, TimeSpan.Zero, false);

    private static Chunk Slow(string text) => new(text, null, TimeSpan.FromMilliseconds(150), false);

    private static Chunk Call(string name, Dictionary<string, object?> arguments) =>
        new(null, new FunctionCallContent(Guid.NewGuid().ToString("N")[..8], name, arguments),
            TimeSpan.Zero, false);

    /// <summary>A chunk that never arrives, leaving the stream open and silent.</summary>
    private static Chunk Hang() => new(null, null, Timeout.InfiniteTimeSpan, true);

    /// <summary>
    /// A chat client that replays scripted chunks.
    ///
    /// Implements the streaming method properly and derives the non-streaming one from it,
    /// so both paths in the loop are exercised against the same script.
    /// </summary>
    private sealed class ScriptedClient(params IReadOnlyList<Chunk>[] rounds) : IChatClient
    {
        private int _round;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IReadOnlyList<Chunk> script = _round < rounds.Length
                ? rounds[_round]
                : [Text("(script exhausted)")];

            _round++;

            foreach (Chunk chunk in script)
            {
                if (chunk.Hangs)
                {
                    // Waits for the token, which is what a wedged server looks like from
                    // this side: connection open, nothing arriving.
                    await Task.Delay(chunk.Delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (chunk.Delay > TimeSpan.Zero)
                    await Task.Delay(chunk.Delay, cancellationToken).ConfigureAwait(false);

                var update = new ChatResponseUpdate { Role = ChatRole.Assistant };

                if (chunk.Text is { Length: > 0 } text)
                    update.Contents.Add(new TextContent(text));

                if (chunk.Call is not null)
                    update.Contents.Add(chunk.Call);

                yield return update;
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var updates = new List<ChatResponseUpdate>();

            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(
                messages, options, cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Echoer
    {
        [ShellvisTool("probe_echo", SideEffect.ReadOnly, Description = "Echoes its argument.")]
        public string Echo(string what) => $"echo: {what}";
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
