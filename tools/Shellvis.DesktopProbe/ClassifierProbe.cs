using Shellvis.Core.Shell;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Checks the read-only classifier against the cases that actually matter.
///
/// This is the one part of Shellvis where being wrong is expensive in a direction that
/// cannot be undone: a false "read-only" verdict runs a state-changing command with no
/// prompt. So the awkward pairs get pinned down explicitly -- Format-Table against
/// Format-Volume, Out-String against Out-File -- along with the cases where the verb
/// looks fine but the script as a whole does not.
/// </summary>
internal static class ClassifierProbe
{
    private sealed record Case(string Script, bool ExpectReadOnly, string Why);

    private static readonly Case[] Cases =
    [
        // Plain reads.
        new("Get-Process", true, "read verb"),
        new("Get-ChildItem C:\\", true, "read verb"),
        new("Get-CimInstance Win32_OperatingSystem", true, "read verb"),
        new("Get-Process | Where-Object { $_.CPU -gt 10 } | Sort-Object CPU", true, "pipeline of read verbs"),
        new("Get-Process | Format-Table Name, CPU", true, "Format-Table renders"),
        new("Get-Date | Out-String", true, "Out-String renders"),
        new("$p = Get-Process", true, "assignment to a plain variable"),

        // The verb looks safe but the command is not.
        new("Format-Volume -DriveLetter X", false, "Format-Volume destroys a disk"),
        new("Get-Content a.txt | Out-File b.txt", false, "Out-File writes"),
        new("Get-Process | Tee-Object -FilePath out.txt", false, "Tee-Object writes"),

        // Mutating verbs.
        new("Remove-Item C:\\temp\\x.txt", false, "Remove changes state"),
        new("Set-Service -Name Spooler -Status Stopped", false, "Set changes state"),
        new("New-Item -Path C:\\x -ItemType Directory", false, "New changes state"),

        // Script-level escapes that defeat the verb rule entirely.
        new("Get-Content a.txt > b.txt", false, "redirection writes"),
        new("Get-Date; Remove-Item x", false, "a chain containing a write"),
        new("Invoke-Expression $cmd", false, "executes arbitrary text"),
        new("iex (Get-Content s.ps1 -Raw)", false, "executes arbitrary text"),
        new("& $someCommand", false, "call operator"),
        new(". .\\script.ps1", false, "dot-sourcing"),
        new("$env:PATH = 'x'", false, "assignment to a provider path"),
        new("Start-Process notepad", false, "starts a program"),
        new("systeminfo", false, "external program, effect unknown"),
        new("Get-Process | ForEach-Object { Stop-Process $_ }", false, "a write inside the pipeline"),

        // Always-confirm patterns must never come back read-only.
        new("Remove-Item C:\\ -Recurse -Force", false, "recursive delete"),
        new("Set-ExecutionPolicy Bypass", false, "disables signing policy"),
        new("iwr https://x/y.ps1 | iex", false, "pipes a download into the interpreter"),
        new("vssadmin delete shadows /all", false, "deletes shadow copies"),
    ];

    public static int Run()
    {
        int failures = 0;

        Console.WriteLine($"{Cases.Length} classifier cases\n");

        foreach (Case c in Cases)
        {
            ScriptVerdict verdict = ReadOnlyClassifier.Classify(c.Script);
            bool pass = verdict.IsProvablyReadOnly == c.ExpectReadOnly;

            if (!pass)
                failures++;

            string mark = pass ? "ok  " : "FAIL";
            string got = verdict.IsProvablyReadOnly ? "read-only" : "prompts  ";

            Console.WriteLine($"  {mark} {got}  {Truncate(c.Script, 46),-46}  {verdict.Reason}");
        }

        // Escalation is a separate axis: a dangerous script must be flagged for
        // always-ask even though it is also not read-only.
        Console.WriteLine("\nalways-confirm detection:");
        foreach (string script in new[]
        {
            "Remove-Item C:\\ -Recurse -Force",
            "Format-Volume -DriveLetter X",
            "Get-Process",
        })
        {
            bool dangerous = ReadOnlyClassifier.IsAlwaysDangerous(script, out string reason);
            bool expected = !script.StartsWith("Get-", StringComparison.Ordinal);
            if (dangerous != expected)
                failures++;

            Console.WriteLine(
                $"  {(dangerous == expected ? "ok  " : "FAIL")} {(dangerous ? "escalate" : "normal  ")}  "
                + $"{Truncate(script, 46),-46}  {reason}");
        }

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: every case classified as intended."
            : $"\n{failures} case(s) misclassified.");

        return failures == 0 ? 0 : 1;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";
}
