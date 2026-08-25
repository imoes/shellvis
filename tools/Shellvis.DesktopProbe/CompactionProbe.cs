using System.Net;
using System.Text;
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using Shellvis.Core.Sessions;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Verifies context compaction, and above all that it never breaks a tool exchange.
///
/// This is the part where being wrong is expensive in a way the user sees immediately:
/// a history where an assistant message announces a tool call whose result is missing
/// gets rejected by the provider outright. The turn fails with an opaque 400 and the
/// conversation is unusable until it is cleared. Every naive "keep the last N messages"
/// implementation produces exactly that the first time a turn ends mid-tool-call, so
/// the boundary cases get pinned down explicitly.
///
/// The summariser is stubbed rather than live: what is under test is where the cut
/// falls, and a real model would make the outcome depend on its mood.
/// </summary>
internal static class CompactionProbe
{
    public static async Task<int> RunAsync()
    {
        var compactor = new ContextCompactor(
            StubSummariser(),
            new CompactionOptions(ContextTokens: 2_000, Threshold: 0.5, ProtectRecent: 4));

        int failures = 0;

        // ---------------------------------------------- nothing to do when short
        List<ChatMessage> shortHistory = [
            new(ChatRole.System, "You are Shellvis."),
            new(ChatRole.User, "hello"),
        ];

        CompactionResult none = await compactor.CompactAsync(shortHistory).ConfigureAwait(false);
        failures += Expect(!none.Compacted, "a short history is left alone");
        failures += Expect(shortHistory.Count == 2, "and is not modified");

        // ------------------------------------------------------ the system message
        List<ChatMessage> longHistory = BuildLongHistory(withTrailingToolCall: false);
        int originalCount = longHistory.Count;

        CompactionResult done = await compactor.CompactAsync(longHistory).ConfigureAwait(false);
        Console.WriteLine($"       {done.Detail}");

        failures += Expect(done.Compacted, "a long history is compacted");
        failures += Expect(longHistory.Count < originalCount, "it actually got shorter");
        failures += Expect(
            longHistory[0].Role == ChatRole.System,
            "the system message survives at position 0");
        failures += Expect(
            longHistory[0].Text?.Contains("Shellvis", StringComparison.Ordinal) == true,
            "and still carries the persona rather than a summary");
        failures += Expect(
            longHistory[1].Text?.Contains("summarised", StringComparison.OrdinalIgnoreCase) == true,
            "the summary is inserted right after it");

        // ------------------------------------------------- tool-pair integrity
        // The awkward case: the protected tail begins exactly on a tool RESULT, so a
        // naive cut would leave the result without its call.
        for (int trailing = 0; trailing <= 6; trailing++)
        {
            List<ChatMessage> history = BuildHistoryEndingInToolExchange(trailing);
            await compactor.CompactAsync(history).ConfigureAwait(false);

            failures += Expect(
                IsIntact(history, out string problem),
                $"history stays intact with {trailing} trailing message(s)"
                    + (problem.Length > 0 ? $" -> {problem}" : string.Empty));
        }

        // ------------------------------------------------- the summariser failing
        var failing = new ContextCompactor(
            FailingSummariser(),
            new CompactionOptions(ContextTokens: 2_000, Threshold: 0.5, ProtectRecent: 4));

        List<ChatMessage> fallbackHistory = BuildLongHistory(withTrailingToolCall: false);
        CompactionResult fallback = await failing.CompactAsync(fallbackHistory).ConfigureAwait(false);

        failures += Expect(
            fallback.Compacted,
            "a failing summariser still compacts rather than stalling the turn");
        failures += Expect(
            fallbackHistory[1].Text?.Contains("mechanical summary", StringComparison.Ordinal) == true,
            "and says plainly that the summary is mechanical");
        failures += Expect(
            fallbackHistory[1].Text?.Contains("printer", StringComparison.OrdinalIgnoreCase) == true,
            "the fallback keeps the user's own words");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: compaction shortens history and never orphans a tool call."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Check that every tool call has its result and every result has its call.
    ///
    /// This is the invariant the provider enforces, so it is the one worth asserting
    /// rather than inspecting indices by hand.
    /// </summary>
    private static bool IsIntact(List<ChatMessage> history, out string problem)
    {
        var announced = new HashSet<string>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);

        foreach (ChatMessage message in history)
        {
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        announced.Add(call.CallId);
                        break;

                    case FunctionResultContent result:
                        answered.Add(result.CallId);
                        break;
                }
            }
        }

        string[] unanswered = announced.Except(answered).ToArray();
        string[] orphaned = answered.Except(announced).ToArray();

        if (unanswered.Length > 0)
        {
            problem = $"call(s) with no result: {string.Join(", ", unanswered)}";
            return false;
        }

        if (orphaned.Length > 0)
        {
            problem = $"result(s) with no call: {string.Join(", ", orphaned)}";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static List<ChatMessage> BuildLongHistory(bool withTrailingToolCall)
    {
        var history = new List<ChatMessage> { new(ChatRole.System, "You are Shellvis.") };

        // Enough bulk to cross the threshold. The printer wording is deliberate: the
        // fallback summary is checked for it.
        for (int i = 0; i < 12; i++)
        {
            history.Add(new ChatMessage(ChatRole.User,
                $"Turn {i}: the printer keeps jamming. " + new string('x', 300)));
            history.Add(new ChatMessage(ChatRole.Assistant,
                $"Reply {i}: looking at the spooler. " + new string('y', 300)));
        }

        if (withTrailingToolCall)
            AppendToolExchange(history, "trailing");

        return history;
    }

    /// <summary>
    /// A history whose tail contains a tool exchange, with a variable number of plain
    /// messages after it. Sweeping that count walks the exchange across the protected
    /// boundary, which is where a naive cut breaks.
    /// </summary>
    private static List<ChatMessage> BuildHistoryEndingInToolExchange(int trailingPlainMessages)
    {
        List<ChatMessage> history = BuildLongHistory(withTrailingToolCall: false);

        AppendToolExchange(history, $"call-{trailingPlainMessages}");

        for (int i = 0; i < trailingPlainMessages; i++)
        {
            history.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"follow-up {i}"));
        }

        return history;
    }

    private static void AppendToolExchange(List<ChatMessage> history, string callId)
    {
        history.Add(new ChatMessage(ChatRole.Assistant,
            (IList<AIContent>)[new FunctionCallContent(callId, "powershell_run",
                new Dictionary<string, object?> { ["script"] = "Get-Service Spooler" })]));

        history.Add(new ChatMessage(ChatRole.Tool,
            (IList<AIContent>)[new FunctionResultContent(callId, "Spooler: Running")]));
    }

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    private static IChatClient StubSummariser() => Client(new StubHandler(
        """{"id":"x","object":"chat.completion","created":1,"model":"stub","choices":[{"index":0,"message":{"role":"assistant","content":"The user reported a jamming printer; the spooler was investigated."},"finish_reason":"stop"}]}"""));

    private static IChatClient FailingSummariser() => Client(new FailingHandler());

    private static IChatClient Client(HttpMessageHandler handler)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://stub.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        };

        return new OpenAIClient(new ApiKeyCredential("stub"), options)
            .GetChatClient("stub")
            .AsIChatClient();
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("the summarising endpoint is unreachable");
    }
}
