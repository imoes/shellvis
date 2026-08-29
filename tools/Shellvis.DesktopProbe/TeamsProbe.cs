using Shellvis.Core.Desktop;
using Shellvis.Core.Teams;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The Teams links, without opening Teams.
///
/// <b>Nothing here launches anything, and that is the point.</b> A harness that opened a
/// chat would put a window in front of whoever is at the machine on every regression run,
/// and the thing being verified is the link that gets built and the refusal that happens
/// before a launch, not Teams itself.
///
/// The refusal matters more than it looks. On a machine without Teams, an <c>msteams:</c>
/// URL handed to ShellExecute does not fail: it raises the "choose an app" dialog and blocks
/// until somebody closes it. That is exactly the hang a fabricated <c>calc://</c> produced
/// here once, for the full fifteen seconds of the window budget, and the guard built then is
/// what covers this now.
/// </summary>
internal static class TeamsProbe
{
    public static int Run()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        Console.WriteLine("teams: the links, without opening anything\n");

        string chat = TeamsLinks.Chat(["a.meier@example.com"]);

        Check("a chat link uses the registered scheme",
            chat.StartsWith("msteams:/l/chat/0/0?", StringComparison.Ordinal), chat);
        Check("and carries the address", chat.Contains("a.meier%40example.com", StringComparison.Ordinal));

        string many = TeamsLinks.Chat(["a@x.de", "b@x.de"], topic: "Q3");
        Check("several people are comma separated",
            many.Contains("a%40x.de%2Cb%40x.de", StringComparison.Ordinal), many);
        Check("a topic is carried", many.Contains("topicName=Q3", StringComparison.Ordinal));

        // The escaping is not cosmetic: a half-escaped ampersand turns the rest of the
        // message into query parameters Teams then ignores, and the failure is silent.
        string tricky = TeamsLinks.Chat(
            ["a@x.de"],
            message: "Angebot & Preis? 50% bis Fr. #wichtig");

        Check("an ampersand in the message is escaped",
            !tricky[(tricky.IndexOf("message=", StringComparison.Ordinal))..]
                .Contains('&', StringComparison.Ordinal),
            tricky);
        Check("a hash does not truncate the link",
            !tricky.Contains('#', StringComparison.Ordinal));
        Check("a percent sign survives", tricky.Contains("50%25", StringComparison.Ordinal));

        Console.WriteLine("\naddresses, not names:");

        Check("an address is usable", TeamsLinks.LooksAddressable("a.meier@example.com"));
        Check("a display name is not", !TeamsLinks.LooksAddressable("Meier, Anna"));
        Check("and neither is a name with an address after it",
            !TeamsLinks.LooksAddressable("Meier, Anna <a@x.de>"));
        Check("nor a bare domain", !TeamsLinks.LooksAddressable("@example.com"));
        Check("nor an address with nothing after the at", !TeamsLinks.LooksAddressable("anna@"));

        Console.WriteLine("\nfinding a join link in an invitation:");

        // Localised bodies: the German wording differs from the English, so the link is
        // found by its own shape rather than by the words around it.
        const string German =
            "________________________________________\r\n"
            + "Microsoft Teams-Besprechung\r\n"
            + "Nehmen Sie auf dem Computer teil\r\n"
            + "https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZTk@thread.v2/0?context=%7b%22Tid%22%3a%22x%22%7d\r\n"
            + "Besprechungs-ID: 123 456\r\n";

        string? found = TeamsLinks.JoinUrlIn(German);

        Check("a German invitation yields its link", found is not null, found ?? "(none)");
        Check("and the link is not truncated at the first percent",
            found?.Contains("context=", StringComparison.Ordinal) == true);

        Check("a full stop after the link is not part of it",
            TeamsLinks.JoinUrlIn("Join at https://teams.microsoft.com/l/meetup-join/abc.")
                ?.EndsWith("abc", StringComparison.Ordinal) == true);

        Check("an HTML body stops at the quote",
            TeamsLinks.JoinUrlIn("<a href=\"https://teams.microsoft.com/l/meetup-join/abc\">Join</a>")
                == "https://teams.microsoft.com/l/meetup-join/abc");

        Check("a meeting in a room yields nothing",
            TeamsLinks.JoinUrlIn("Raum 3.14, bitte pünktlich sein.") is null);
        Check("an empty body yields nothing", TeamsLinks.JoinUrlIn("") is null);
        Check("a null body yields nothing", TeamsLinks.JoinUrlIn(null) is null);
        Check("some other Teams URL is not a join link",
            TeamsLinks.JoinUrlIn("https://teams.microsoft.com/l/channel/19%3aabc/General") is null);

        Console.WriteLine("\nwhen Teams is not installed:");

        // The guard that turns a fifteen-second hang into a sentence. Checked WITHOUT
        // launching, which is why ProgramLauncher exposes the decision separately.
        bool registered = !ProgramLauncher.WouldRefuse("msteams:/l/chat/0/0?users=a@x.de", out string? why);

        Console.WriteLine($"       msteams: is registered on this machine: {registered}");

        if (registered)
        {
            Console.WriteLine(
                "  ..   Teams IS installed here, so the refusal path cannot be exercised;");
            Console.WriteLine(
                "       a made-up scheme stands in for it.");
        }

        // Either way, a scheme nothing handles must be refused rather than handed to
        // ShellExecute, which would raise the "choose an app" dialog and block.
        Check("an unregistered scheme is refused before any launch",
            ProgramLauncher.WouldRefuse("msteamsnope:/l/chat/0/0?users=a@x.de", out string? reason),
            reason ?? string.Empty);

        Check("and the refusal explains itself rather than just failing",
            (reason ?? string.Empty).Contains("no handler registered", StringComparison.Ordinal));

        Console.WriteLine("\nin the catalog:");

        var registry = new ToolRegistry();
        registry.RegisterFrom(new TeamsTools());

        foreach (string name in new[] { "teams_chat_open", "teams_meeting_join" })
        {
            ToolEntry? entry = registry.Tools.FirstOrDefault(t => t.Name == name);

            Check($"{name} is registered", entry is not null);

            // Mutating, not read-only: both put something in front of the user, and joining
            // a meeting is visible to everyone else in it.
            Check($"{name} prompts before acting", entry?.SideEffect == SideEffect.Mutating);
        }

        Check("no Teams tool can send a message",
            registry.Tools.All(t => !t.Name.Contains("send", StringComparison.OrdinalIgnoreCase)));

        Console.WriteLine("\nthe tools refuse before they reach a link:");

        var tools = new TeamsTools();

        Check("no recipient is refused",
            tools.OpenChat("").GetAwaiter().GetResult()
                .StartsWith("error:", StringComparison.Ordinal));

        string byName = tools.OpenChat("Meier, Anna").GetAwaiter().GetResult();

        Check("a display name is refused rather than opening an empty search",
            byName.StartsWith("error:", StringComparison.Ordinal), byName);
        Check("and the refusal says where to get the address",
            byName.Contains("mail_history", StringComparison.Ordinal));

        // A name containing a comma must not be split into two nonexistent people, or the
        // refusal reads as though the user asked for two of them.
        Check("a name with a comma is reported as one name, not two",
            byName.Contains("'Meier, Anna' is a display name", StringComparison.Ordinal), byName);

        string mixed = tools.OpenChat("a@x.de, Meier Anna").GetAwaiter().GetResult();

        Check("one bad entry among good ones is named on its own",
            mixed.Contains("'Meier Anna' is not an email address", StringComparison.Ordinal), mixed);

        Check("no appointment id is refused",
            tools.JoinMeeting("").GetAwaiter().GetResult().Length > 0);

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: links are built and escaped correctly, a display name is refused\n"
                + "instead of opening an empty search, a join link is found in a localised\n"
                + "invitation, and an unhandled scheme is refused before it can hang.\n"
                + "NOT checked here: that Teams then does the right thing with the link."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }
}
