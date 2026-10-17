using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shellvis.Contracts;
using Shellvis.Core.Mail;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Exercises the Thunderbird bridge with the probe playing BOTH ends.
///
/// Thunderbird is not installed on this machine, so the extension side is a mock -- but a
/// mock that speaks the real native messaging protocol over the real host's real stdio,
/// while the real client talks to the real named pipe. What is being tested is the relay
/// and the framing, and those are exactly the parts with silent failure modes: a wrong
/// length prefix does not produce an error, it produces a hang.
///
/// What is NOT tested: the extension's own JavaScript against Thunderbird's APIs. That
/// needs Thunderbird, and saying so is better than implying otherwise.
/// </summary>
internal static class ThunderbirdProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("=== Thunderbird bridge ===");
        Console.WriteLine();

        failures += Framing();
        failures += await AbsentAsync().ConfigureAwait(false);

        string exe = FindHost();

        if (exe.Length == 0)
        {
            Console.WriteLine("  FAIL the host executable was not found; build Shellvis.Thunderbird.Host.");
            return failures + 1;
        }

        Console.WriteLine($"  starting {Path.GetFileName(exe)} as Thunderbird would");

        using Process? host = Start(exe);

        if (host is null)
        {
            Console.WriteLine("  FAIL the host did not start.");
            return failures + 1;
        }

        try
        {
            failures += await RelayAsync(host).ConfigureAwait(false);
            failures += await ProviderAsync(host).ConfigureAwait(false);
            failures += ToolSurface();
        }
        finally
        {
            try
            {
                host.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: the relay and its framing work end to end. The extension's own JS needs Thunderbird."
            : $"{failures} Thunderbird check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The framing, on its own.
    ///
    /// Four bytes little-endian then exactly that many bytes of UTF-8. Getting it wrong is
    /// a hang rather than an error, which is why it is worth testing in isolation before
    /// any process is involved.
    /// </summary>
    private static int Framing()
    {
        Console.WriteLine("-- native message framing --");
        int failures = 0;

        using var buffer = new MemoryStream();

        var request = new MailRequest(
            MailOperation.ListMessages,
            new Dictionary<string, string> { ["limit"] = "5", ["unreadOnly"] = "true" },
            "abc12345");

        ThunderbirdProtocol.WriteAsync(buffer, request).GetAwaiter().GetResult();

        byte[] written = buffer.ToArray();
        int declared = BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(0, 4));

        Console.WriteLine($"    {written.Length} bytes on the wire, prefix says {declared}");

        failures += Check("the prefix is four bytes", written.Length == declared + 4);

        failures += Check(
            "and it is little-endian",
            written[0] == (byte)(declared & 0xFF));

        failures += Check(
            "the payload is UTF-8 JSON",
            Encoding.UTF8.GetString(written, 4, declared).StartsWith('{'));

        buffer.Position = 0;

        MailRequest? read = ThunderbirdProtocol
            .ReadAsync<MailRequest>(buffer).GetAwaiter().GetResult();

        failures += Check("a frame round-trips", read?.RequestId == "abc12345");
        failures += Check("with its arguments", read?.Get("limit") == "5");

        // Enum names travel as snake_case, matching what the JavaScript switch compares
        // against. A mismatch here would make every operation "unknown".
        failures += Check(
            "the operation travels as snake_case, matching background.js",
            Encoding.UTF8.GetString(written, 4, declared).Contains("\"list_messages\""));

        // A nonsense length is how a desynchronised stream first shows itself, and reading
        // it would allocate whatever the bytes happened to say.
        using var corrupt = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);

        try
        {
            ThunderbirdProtocol.ReadAsync<MailRequest>(corrupt).GetAwaiter().GetResult();
            failures += Check("an absurd length prefix is rejected", false);
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine("    " + ex.Message);
            failures += Check("an absurd length prefix is rejected, not allocated", true);
        }

        // Thunderbird disconnects the port on an oversized message rather than truncating,
        // so writing one has to be refused here.
        using var big = new MemoryStream();

        try
        {
            ThunderbirdProtocol.WriteAsync(big, new MailResponse(
                true, new string('x', ThunderbirdProtocol.MaxMessageBytes + 100), null, "x"))
                .GetAwaiter().GetResult();

            failures += Check("an oversized message is refused", false);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("    " + ex.Message.ReplaceLineEndings(" "));
            failures += Check("an oversized message is refused before it is written", true);
        }

        using var empty = new MemoryStream();

        failures += Check(
            "an empty stream reads as end, not as an error",
            ThunderbirdProtocol.ReadAsync<MailRequest>(empty).GetAwaiter().GetResult() is null);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> AbsentAsync()
    {
        Console.WriteLine("-- with no bridge running --");
        int failures = 0;

        var provider = new ThunderbirdProvider();
        var clock = Stopwatch.StartNew();

        bool available = await provider.IsAvailableAsync().ConfigureAwait(false);
        clock.Stop();

        failures += Check("it reports unavailable", !available);
        failures += Check($"quickly ({clock.ElapsedMilliseconds} ms)", clock.Elapsed.TotalSeconds < 5);

        string read = await provider.ReadMessageAsync("1").ConfigureAwait(false);
        Console.WriteLine("    " + read);

        // The message names the actual precondition. "Connection failed" would send the
        // user looking at the network.
        failures += Check(
            "and the message says Thunderbird and the extension have to be running",
            read.Contains("Thunderbird") && read.Contains("extension"));

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// The relay: probe as Shellvis on the pipe, probe as Thunderbird on the host's stdio.
    /// </summary>
    private static async Task<int> RelayAsync(Process host)
    {
        Console.WriteLine("-- relay --");
        int failures = 0;

        // Answer as the extension would, in the background, so the client call below can
        // complete.
        Task<MailRequest?> served = ServeOnceAsync(host, request => new MailResponse(
            true,
            JsonSerializer.Serialize(new { thunderbird = "128.0" }),
            null,
            request.RequestId));

        var provider = new ThunderbirdProvider();

        bool available = await provider.IsAvailableAsync().ConfigureAwait(false);

        MailRequest? seen = await served.ConfigureAwait(false);

        failures += Check("a ping reaches the mock extension", seen?.Operation == MailOperation.Ping);
        failures += Check("and its answer reaches the client", available);

        // The correlation id has to survive the round trip: it is what ties a line in the
        // host's log to the call that caused it.
        failures += Check("the request id is carried through", seen?.RequestId.Length == 8);

        Task<MailRequest?> folders = ServeOnceAsync(host, request => new MailResponse(
            true,
            JsonSerializer.Serialize(new[]
            {
                new { id = "/INBOX", name = "work/INBOX", total = 812, unread = 3 },
                new { id = "/Drafts", name = "work/Drafts", total = 2, unread = 0 },
            }),
            null,
            request.RequestId));

        IReadOnlyList<MailFolder> list = await provider.ListFoldersAsync().ConfigureAwait(false);
        await folders.ConfigureAwait(false);

        Console.WriteLine("    " + string.Join(" | ", list));

        failures += Check("folders are parsed", list.Count == 2);
        failures += Check("with their unread counts", list[0].Unread == 3 && list[0].Total == 812);

        // An error from the extension must arrive as an error, not as an empty result that
        // reads like "there is nothing there".
        Task<MailRequest?> failing = ServeOnceAsync(host, request =>
            MailResponse.Failed("'99' is not a message id", request.RequestId));

        string read = await provider.ReadMessageAsync("99").ConfigureAwait(false);
        await failing.ConfigureAwait(false);

        Console.WriteLine("    " + read);
        failures += Check("an extension error reaches the client verbatim", read.Contains("not a message id"));

        // The host must survive the extension failing, or one bad mail would end the
        // bridge for the rest of the Thunderbird session.
        Task<MailRequest?> after = ServeOnceAsync(host, request => new MailResponse(
            true, JsonSerializer.Serialize(new { thunderbird = "128.0" }), null, request.RequestId));

        bool stillThere = await provider.IsAvailableAsync().ConfigureAwait(false);
        await after.ConfigureAwait(false);

        failures += Check("the bridge survives an error and serves the next call", stillThere);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ProviderAsync(Process host)
    {
        Console.WriteLine("-- provider semantics --");
        int failures = 0;

        var provider = new ThunderbirdProvider();

        Task<MailRequest?> listing = ServeOnceAsync(host, request => new MailResponse(
            true,
            JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = 4711,
                    subject = "Drucker klemmt",
                    author = "Meier <meier@example.org>",
                    date = "2026-08-24T09:12:00+02:00",
                    unread = true,
                },
            }),
            null,
            request.RequestId));

        IReadOnlyList<MailMessage> messages = await provider
            .ListMessagesAsync(unreadOnly: true, limit: 5)
            .ConfigureAwait(false);

        MailRequest? asked = await listing.ConfigureAwait(false);

        Console.WriteLine("    " + string.Join("\n    ", messages));

        failures += Check("messages are parsed", messages.Count == 1);

        // Thunderbird hands out a numeric id; it is carried as a string so the model
        // passes it back verbatim instead of doing arithmetic on something opaque.
        failures += Check("a numeric id is carried as a string", messages[0].Id == "4711");

        failures += Check("the date is parsed", messages[0].Received.Hour == 9);

        // Both fields labelled and quoted -- the WindowInfo lesson, applied again.
        failures += Check(
            "the line labels sender and subject separately",
            messages[0].ToString().Contains("from \"Meier") && messages[0].ToString().Contains("\"Drucker klemmt\""));

        failures += Check("the filter is passed through", asked?.Get("unreadOnly") == "true");
        failures += Check("and the limit", asked?.Get("limit") == "5");

        // A body arriving as a quoted JSON string would be double-encoded -- the same
        // mistake already found once with tool results.
        Task<MailRequest?> body = ServeOnceAsync(host, request => new MailResponse(
            true,
            JsonSerializer.Serialize("From: Meier\nSubject: Drucker klemmt\n\nEr klemmt wieder."),
            null,
            request.RequestId));

        string text = await provider.ReadMessageAsync("4711").ConfigureAwait(false);
        await body.ConfigureAwait(false);

        Console.WriteLine("    body: " + text.ReplaceLineEndings(" / "));

        failures += Check(
            "a body is plain text, not a quoted JSON string",
            !text.StartsWith('"') && text.Contains("Er klemmt wieder."));

        // The rule that shapes the whole surface: it never sends.
        Task<MailRequest?> reply = ServeOnceAsync(host, request => new MailResponse(
            true, JsonSerializer.Serialize(new { saved = "draft" }), null, request.RequestId));

        string drafted = await provider.DraftReplyAsync("4711", "Ich schaue morgen.").ConfigureAwait(false);
        MailRequest? replyRequest = await reply.ConfigureAwait(false);

        Console.WriteLine("    " + drafted);

        failures += Check("a reply is drafted", replyRequest?.Operation == MailOperation.DraftReply);
        failures += Check("the body is passed through", replyRequest?.Get("body") == "Ich schaue morgen.");

        // Said explicitly, because a model that reads "reply created" as "reply sent" tells
        // the user something untrue about an irreversible action.
        failures += Check(
            "and the result says plainly that nothing was sent",
            drafted.Contains("draft") && drafted.Contains("Nothing was sent"));

        Console.WriteLine();
        return failures;
    }

    private static int ToolSurface()
    {
        Console.WriteLine("-- tool surface --");
        int failures = 0;

        var registry = new ToolRegistry();
        registry.RegisterFrom(new MailTools(new ThunderbirdProvider()));

        failures += Check("five mail tools register", registry.Count == 5);

        // Named for the capability, not the product: the model should ask for mail, not
        // for a mail client.
        failures += Check(
            "they are named mail_*, not thunderbird_*",
            registry.Tools.All(t => t.Name.StartsWith("mail_")));

        failures += Check(
            "reading is read-only, drafting is mutating",
            registry.Tools.First(t => t.Name == "mail_read").SideEffect == SideEffect.ReadOnly
                && registry.Tools.First(t => t.Name == "mail_messages").SideEffect == SideEffect.ReadOnly
                && registry.Tools.First(t => t.Name == "mail_reply_draft").SideEffect == SideEffect.Mutating);

        // There is no send tool at all -- not a guarded one, none. That is the point.
        failures += Check(
            "there is no send tool of any kind",
            !registry.Tools.Any(t => t.Name.Contains("send")));

        failures += Check(
            "the draft tools say in their description that nothing is sent",
            registry.Tools
                .Where(t => t.Name.Contains("draft"))
                .All(t => t.Description.Contains("never sent")));

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// Play Thunderbird for exactly one exchange: read the host's stdout, answer on stdin.
    /// </summary>
    private static Task<MailRequest?> ServeOnceAsync(
        Process host, Func<MailRequest, MailResponse> answer)
    {
        return Task.Run(async () =>
        {
            Stream fromHost = host.StandardOutput.BaseStream;
            Stream toHost = host.StandardInput.BaseStream;

            MailRequest? request = await ThunderbirdProtocol
                .ReadAsync<MailRequest>(fromHost)
                .ConfigureAwait(false);

            if (request is null)
                return null;

            await ThunderbirdProtocol.WriteAsync(toHost, answer(request)).ConfigureAwait(false);

            return request;
        });
    }

    private static Process? Start(string exe)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Thunderbird passes the extension origin as the first argument.
        startInfo.ArgumentList.Add("shellvis-bridge@ippen.media");

        Process? process = Process.Start(startInfo);

        // stderr drained so a full pipe cannot block the host. stdout is NOT drained: it
        // is the native messaging channel this probe reads as Thunderbird.
        _ = process?.StandardError.ReadToEndAsync();

        return process;
    }

    private static string FindHost()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "Shellvis.Thunderbird.Host", "bin");

            if (Directory.Exists(candidate))
            {
                string[] found = Directory.GetFiles(
                    candidate, "Shellvis.Thunderbird.Host.exe", SearchOption.AllDirectories);

                if (found.Length > 0)
                    return found.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }

            directory = directory.Parent;
        }

        return string.Empty;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
