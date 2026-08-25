using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Shellvis.Core.Hooks;
using Shellvis.Core.Permissions;
using Shellvis.Core.Sessions;
using Shellvis.Core.Tools;

namespace Shellvis.Core.Agent;

/// <summary>Knobs for one agent session.</summary>
/// <param name="MaxIterations">
/// How many model round trips a single user turn may consume. A runaway loop is the
/// characteristic failure mode of tool-using agents, and the cost is real money and
/// real side effects, so the ceiling is mandatory rather than advisory.
/// </param>
/// <param name="SystemPrompt">The persona and operating rules.</param>
/// <param name="Stream">
/// Whether to stream the model's answer. On by default: a local llama.cpp endpoint takes
/// tens of seconds for a long answer, and a console that stays blank for all of it is the
/// exact opacity this project exists to remove. Turning it off is useful when a provider's
/// streaming is broken, which happens.
/// </param>
/// <param name="StallTimeoutSeconds">
/// How long a stream may go silent before it is treated as dead.
///
/// Streaming needs this and non-streaming does not: a request that never answers fails on
/// the HTTP timeout, but a stream that stops mid-sentence keeps a live connection open and
/// would otherwise wait forever. Hermes solves the same problem with a stale-response
/// detector, and for the same reason -- local inference servers do stop mid-answer.
/// </param>
public sealed record AgentOptions(
    int MaxIterations = 25,
    string? SystemPrompt = null,
    bool Stream = true,
    int StallTimeoutSeconds = 90);

/// <summary>
/// The turn loop: build messages, call the model, run the tools it asks for, feed the
/// results back, repeat until it answers.
///
/// Two structural choices worth naming.
///
/// The loop drives tool calling itself rather than using the auto-invoking chat client
/// from Microsoft.Extensions.AI. That wrapper would run every tool the model asked for
/// with no chance to intervene, which is incompatible with an approval gate. Owning
/// the loop is what makes "ask before writing" possible at all.
///
/// It yields events instead of returning an answer. The interesting part of an agent
/// turn is what happens during it, and a console that stays blank for thirty seconds
/// while commands run invisibly is the exact opacity this project exists to remove.
/// </summary>
public sealed class AgentLoop(
    IChatClient client,
    ToolRegistry tools,
    IApprovalGate approvals,
    AgentOptions? options = null,
    IToolRiskAssessor? riskAssessor = null,
    ContextCompactor? compactor = null,
    HookRunner? hooks = null,
    PermissionPolicy? permissions = null)
{
    private readonly AgentOptions _options = options ?? new AgentOptions();
    private readonly IToolRiskAssessor _risk = riskAssessor ?? StaticRiskAssessor.Instance;

    /// <summary>
    /// The model this loop talks to.
    ///
    /// Settable, so switching model mid-session replaces the transport and nothing else:
    /// the history, the tool registry and the PowerShell runspace all belong to the
    /// session, not to the provider. Rebuilding the session to change model would throw
    /// away the conversation, which is the one thing a user changing model in the middle
    /// of a task wants to keep.
    /// </summary>
    public IChatClient Client { get; set; } = client;

    /// <summary>
    /// Read on every tool call, never cached, because the user can change it mid-turn.
    /// </summary>
    private readonly PermissionPolicy _permissions = permissions ?? new PermissionPolicy();
    private readonly List<ChatMessage> _history = [];

    /// <summary>Tools allowed to run without asking for the rest of this session.</summary>
    private readonly HashSet<string> _sessionAllowed = new(StringComparer.Ordinal);

    /// <summary>The conversation so far. Exposed so a session store can persist it.</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>
    /// Replace the conversation, for resuming a stored session.
    ///
    /// The system message is re-established from the current options rather than
    /// restored from storage: the persona, working rules and skill index belong to
    /// THIS build, and a session recorded before a skill existed must not resurrect a
    /// prompt that does not mention it.
    /// </summary>
    public void ReplaceHistory(IEnumerable<ChatMessage> messages)
    {
        _history.Clear();

        if (_options.SystemPrompt is { Length: > 0 } system)
            _history.Add(new ChatMessage(ChatRole.System, system));

        foreach (ChatMessage message in messages)
        {
            // A stored system message is dropped for the reason above.
            if (message.Role != ChatRole.System)
                _history.Add(message);
        }
    }

    /// <summary>
    /// Run one user turn, streaming what happens.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (_history.Count == 0 && _options.SystemPrompt is { Length: > 0 } system)
            _history.Add(new ChatMessage(ChatRole.System, system));

        _history.Add(new ChatMessage(ChatRole.User, userMessage));

        var chatOptions = new ChatOptions
        {
            Tools = tools.AsChatTools(),
            // Explicitly automatic: the model chooses whether to call a tool. Forcing
            // a call makes it invent arguments when the honest answer is plain text.
            ToolMode = ChatToolMode.Auto,
        };

        int iteration = 0;

        while (iteration < _options.MaxIterations)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield return new AgentEvent.TurnFinished(TurnEndReason.Interrupted, iteration, null);
                yield break;
            }

            iteration++;
            yield return new AgentEvent.IterationStarted(iteration, _options.MaxIterations);

            // Checked before the call, never after. Finding out the context was too
            // large because the provider rejected the request means the turn has
            // already failed, and the rejection costs a round trip to learn nothing.
            if (compactor is not null && compactor.ShouldCompact(_history))
            {
                CompactionResult compaction = await compactor
                    .CompactAsync(_history, cancellationToken)
                    .ConfigureAwait(false);

                if (compaction.Compacted)
                    yield return new AgentEvent.Compacted(compaction.Detail, compaction.Summary);
            }

            // The provider call is wrapped separately because a yield cannot live
            // inside a try/catch in C#, and a provider failure must surface as an
            // event rather than as an exception escaping the enumerator.
            if (hooks is not null && hooks.Has(HookEvent.PreLlmCall))
            {
                await hooks.FireAsync(
                    HookEvent.PreLlmCall,
                    payload: new JsonObject { ["iteration"] = iteration },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (string note in hooks.DrainNotes())
                    yield return new AgentEvent.Failure(note);
            }

            ChatResponse? response;
            string? error;

            if (_options.Stream)
            {
                // Unbounded is safe here: the producer is a network stream and the consumer
                // is the next line of this loop, so the queue holds at most the chunks that
                // arrive while one delta is being yielded.
                var deltas = Channel.CreateUnbounded<string>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

                Task<(ChatResponse? Response, string? Error)> pump =
                    PumpStreamAsync(chatOptions, deltas.Writer, cancellationToken);

                await foreach (string delta in deltas.Reader
                    .ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return new AgentEvent.AssistantDelta(delta);
                }

                (response, error) = await pump.ConfigureAwait(false);
            }
            else
            {
                (response, error) = await CallModelAsync(chatOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (hooks is not null && hooks.Has(HookEvent.PostLlmCall))
            {
                await hooks.FireAsync(
                    HookEvent.PostLlmCall,
                    payload: new JsonObject
                    {
                        ["iteration"] = iteration,
                        ["failed"] = error is not null,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (string note in hooks.DrainNotes())
                    yield return new AgentEvent.Failure(note);
            }

            if (error is not null)
            {
                yield return new AgentEvent.Failure(error);
                yield return new AgentEvent.TurnFinished(TurnEndReason.Failed, iteration, null);
                yield break;
            }

            _history.AddRange(response!.Messages);

            List<FunctionCallContent> calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                // Text with no tool calls is the model saying it is done.
                string answer = response.Text ?? string.Empty;
                if (answer.Length > 0)
                    yield return new AgentEvent.AssistantMessage(answer);

                yield return new AgentEvent.TurnFinished(TurnEndReason.Answered, iteration, answer);
                yield break;
            }

            // Any interim prose alongside the tool calls is worth showing: it is the
            // model explaining what it is about to do.
            if (response.Text is { Length: > 0 } interim)
                yield return new AgentEvent.AssistantMessage(interim);

            var results = new List<AIContent>(calls.Count);

            foreach (FunctionCallContent call in calls)
            {
                await foreach (AgentEvent evt in RunOneCallAsync(call, results, cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return evt;
                }
            }

            // Every call must produce a result message, refusals included. A tool call
            // left unanswered is the single most common cause of a provider 400 on the
            // next request.
            _history.Add(new ChatMessage(ChatRole.Tool, results));
        }

        yield return new AgentEvent.TurnFinished(TurnEndReason.BudgetExhausted, iteration, null);
    }

    private async IAsyncEnumerable<AgentEvent> RunOneCallAsync(
        FunctionCallContent call,
        List<AIContent> results,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        ToolEntry? tool = tools.Find(call.Name);

        if (tool is null)
        {
            string message = tools.DescribeUnknown(call.Name);
            results.Add(new FunctionResultContent(call.CallId, message));
            yield return new AgentEvent.ToolCompleted(call.CallId, call.Name, message, TimeSpan.Zero, false);
            yield break;
        }

        // IDictionary does not implement IReadOnlyDictionary, so materialise a
        // Dictionary, which satisfies both the preview and the invoke paths.
        Dictionary<string, object?> arguments = call.Arguments is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(call.Arguments, StringComparer.Ordinal);

        string preview = tool.Preview(arguments);

        // Hooks run BEFORE the approval prompt. A hook that blocks an action should not
        // have troubled the user with a question about it first -- a hook is the user's
        // own policy speaking, and asking them to confirm what their own rule already
        // forbids is noise.
        if (hooks is not null && hooks.Has(HookEvent.PreToolCall))
        {
            HookOutcome pre = await hooks.FireAsync(
                HookEvent.PreToolCall,
                tool.Name,
                new JsonObject { ["tool_input"] = FormatArguments(arguments) },
                cancellationToken).ConfigureAwait(false);

            foreach (string note in hooks.DrainNotes())
                yield return new AgentEvent.Failure(note);

            if (pre.Blocked)
            {
                // A result is still produced. An unanswered tool_call_id is the single
                // most common cause of a provider 400 on the next request, so a block
                // has to look like an outcome rather than like a call that never was.
                string reason = pre.Reason ?? "blocked by a hook.";
                results.Add(new FunctionResultContent(call.CallId, $"Blocked: {reason}"));
                yield return new AgentEvent.ToolRefused(call.CallId, tool.Name, reason);
                yield break;
            }
        }

        // The tool declares a ceiling; the assessor may lower it for these specific
        // arguments. This is what keeps a read-only query from prompting.
        SideEffect assessed = _risk.Assess(tool, arguments);

        // In ask mode the downgrade is withdrawn: the user has said they want to see the
        // shell commands, and "it only reads" is exactly the judgement they declined to
        // delegate. The escalation to AlwaysAsk is kept, because a mode that asks more
        // must never end up asking less.
        if (_permissions.Mode == PermissionMode.Ask && assessed != SideEffect.AlwaysAsk)
            assessed = tool.SideEffect;

        if (RequiresApproval(tool, assessed))
        {
            var request = new ApprovalRequest(
                tool,
                preview,
                FormatArguments(arguments),
                Reason(assessed));

            ApprovalDecision decision = await approvals
                .RequestAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (decision == ApprovalDecision.Deny)
            {
                // Told to the model, not just logged: it needs to know the action did
                // not happen so it can offer an alternative instead of assuming success.
                const string refusal = "The user declined this action. Do not retry it.";
                results.Add(new FunctionResultContent(call.CallId, refusal));
                yield return new AgentEvent.ToolRefused(call.CallId, tool.Name, "declined by the user");
                yield break;
            }

            // AlwaysAsk tools are never remembered. That is the whole distinction
            // between them and merely mutating ones.
            if (decision is ApprovalDecision.Session or ApprovalDecision.Always
                && assessed != SideEffect.AlwaysAsk)
            {
                _sessionAllowed.Add(tool.Name);
            }
        }

        yield return new AgentEvent.ToolStarted(call.CallId, tool.Name, preview);

        var clock = Stopwatch.StartNew();
        (string output, bool ok) = await InvokeAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        if (hooks is not null)
        {
            var after = new JsonObject
            {
                ["tool_input"] = FormatArguments(arguments),
                ["tool_result"] = output,
                ["succeeded"] = ok,
                ["duration_ms"] = (int)clock.Elapsed.TotalMilliseconds,
            };

            if (hooks.Has(HookEvent.TransformToolResult))
            {
                HookOutcome transformed = await hooks.FireAsync(
                    HookEvent.TransformToolResult, tool.Name, after, cancellationToken)
                    .ConfigureAwait(false);

                if (transformed.Replacement is { } replacement)
                {
                    output = replacement;
                    after["tool_result"] = replacement;
                }
            }

            if (hooks.Has(HookEvent.PostToolCall))
            {
                HookOutcome post = await hooks.FireAsync(
                    HookEvent.PostToolCall, tool.Name, after, cancellationToken)
                    .ConfigureAwait(false);

                // Context from a post hook is appended to what the model sees. That is
                // the only way a hook can add knowledge rather than merely observe.
                if (post.Context is { Length: > 0 } context)
                    output = output + "\n\n[hook] " + context;
            }

            foreach (string note in hooks.DrainNotes())
                yield return new AgentEvent.Failure(note);
        }

        results.Add(new FunctionResultContent(call.CallId, output));
        yield return new AgentEvent.ToolCompleted(call.CallId, tool.Name, output, clock.Elapsed, ok);
    }

    private bool RequiresApproval(ToolEntry tool, SideEffect assessed) => assessed switch
    {
        SideEffect.ReadOnly => false,

        // Never waived, in any mode. This is the entire distinction between AlwaysAsk and
        // merely mutating, and yolo would be a lie about what it covers if it swallowed
        // a gallery install or a privileged broker call.
        SideEffect.AlwaysAsk => true,

        _ => _permissions.Mode != PermissionMode.Yolo && !_sessionAllowed.Contains(tool.Name),
    };

    private static string Reason(SideEffect assessed) => assessed switch
    {
        SideEffect.AlwaysAsk =>
            "This action always needs confirmation, even in unattended mode.",
        _ => "This action changes state on your machine.",
    };

    private async Task<(string Output, bool Succeeded)> InvokeAsync(
        ToolEntry tool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = new AIFunctionArguments(arguments.ToDictionary(p => p.Key, p => p.Value));
            object? result = await tool.Function.InvokeAsync(args, cancellationToken).ConfigureAwait(false);

            string text = result switch
            {
                null => "(no output)",
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
                JsonElement e => e.ToString(),
                _ => result.ToString() ?? string.Empty,
            };

            // A tool that reports its own failure in the text is still a completed
            // call; the model reads the text either way. Flagging it lets the console
            // colour the line red.
            bool ok = !text.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
            return (text, ok);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broad by intent: tools reach into other processes, COM, the registry and
            // the network. None of that justifies ending the session.
            return ($"error: {tool.Name} failed: {ex.Message}", false);
        }
    }

    /// <summary>Carries the time of the last chunk between the reader and the watchdog.</summary>
    private sealed class StallWatch
    {
        public long Ticks = Environment.TickCount64;
    }

    /// <summary>
    /// Read a streaming response, writing text deltas to a channel as they arrive.
    ///
    /// A channel rather than yielding directly, because C# forbids <c>yield</c> inside a
    /// try/catch and a provider failure mid-stream has to be caught. So this method owns
    /// the error handling and the caller owns the iteration -- which also means the deltas
    /// reach the UI while this is still running, which is the entire point.
    /// </summary>
    private async Task<(ChatResponse? Response, string? Error)> PumpStreamAsync(
        ChatOptions chatOptions,
        ChannelWriter<string> deltas,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        var watch = new StallWatch();

        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // The watchdog cancels the stream if it goes quiet. Separate from the HTTP
            // timeout, which does not apply once bytes have started flowing.
            Task watchdog = Task.Run(async () =>
            {
                var interval = TimeSpan.FromSeconds(
                    Math.Clamp(_options.StallTimeoutSeconds / 6.0, 1, 15));

                using var timer = new PeriodicTimer(interval);

                while (await timer.WaitForNextTickAsync(stall.Token).ConfigureAwait(false))
                {
                    long idle = Environment.TickCount64 - Volatile.Read(ref watch.Ticks);

                    if (idle > _options.StallTimeoutSeconds * 1000L)
                    {
                        await stall.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }, CancellationToken.None);

            try
            {
                await foreach (ChatResponseUpdate update in Client
                    .GetStreamingResponseAsync(_history, chatOptions, stall.Token)
                    .ConfigureAwait(false))
                {
                    Volatile.Write(ref watch.Ticks, Environment.TickCount64);
                    updates.Add(update);

                    // Only the text is streamed onward. Tool calls arrive in fragments and
                    // are meaningless until coalesced, so they are accumulated and dealt
                    // with once the stream ends.
                    if (update.Text is { Length: > 0 } text)
                        await deltas.WriteAsync(text, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!stall.IsCancellationRequested)
                    await stall.CancelAsync().ConfigureAwait(false);

                // Awaited so the timer is disposed before this frame leaves; otherwise a
                // long turn accumulates one live timer per iteration.
                try
                {
                    await watchdog.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            // ToChatResponse does the coalescing, including merging a function call that
            // arrived split across chunks. Doing that by hand is where a hand-rolled
            // streaming loop usually goes wrong.
            return (updates.ToChatResponse(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not the user: the watchdog. Whatever arrived before the silence is kept and
            // returned, because a truncated answer plus a warning is more use than
            // discarding a paragraph the model already produced.
            string partial = string.Concat(updates.Select(u => u.Text));

            return (
                updates.Count > 0 ? updates.ToChatResponse() : null,
                $"the model stopped sending for {_options.StallTimeoutSeconds}s and the "
                    + $"stream was abandoned after {partial.Length} character(s).");
        }
        catch (Exception ex)
        {
            return (null, $"the model provider failed: {ex.Message}");
        }
        finally
        {
            deltas.Complete();
        }
    }

    private async Task<(ChatResponse? Response, string? Error)> CallModelAsync(
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            ChatResponse response = await Client
                .GetResponseAsync(_history, chatOptions, cancellationToken)
                .ConfigureAwait(false);

            return (response, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The message matters more than the type here: a wrong base URL, an
            // unreachable host and a rejected key all surface as different exception
            // types but need the same treatment, namely telling the user which.
            return (null, $"the model provider failed: {ex.Message}");
        }
    }

    private static string FormatArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.Count == 0)
            return "(no arguments)";

        var sb = new StringBuilder();
        foreach ((string name, object? value) in arguments)
        {
            string text = value?.ToString() ?? "null";
            if (text.Length > 200)
                text = text[..200] + "...";

            sb.Append(name).Append(" = ").AppendLine(text);
        }

        return sb.ToString().TrimEnd();
    }
}
