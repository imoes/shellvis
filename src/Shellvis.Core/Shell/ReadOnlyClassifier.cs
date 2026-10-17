using System.Globalization;
using System.Management.Automation.Language;
using System.Text;
using System.Text.RegularExpressions;

namespace Shellvis.Core.Shell;

/// <summary>The classifier's verdict on a script.</summary>
/// <param name="IsProvablyReadOnly">True only when every part of the script was shown to read.</param>
/// <param name="Reason">Why, in words a human can check.</param>
public sealed record ScriptVerdict(bool IsProvablyReadOnly, string Reason);

/// <summary>
/// Decides whether a PowerShell script provably only reads.
///
/// The polarity is the entire design. The burden of proof is on READING: a script runs
/// without asking only when every command in it can be shown to be harmless. Anything
/// unclear counts as mutating. There is no "probably fine" tier, because a false
/// negative costs one confirmation prompt and a false positive silently changes the
/// user's machine.
///
/// Windows makes this tractable in a way Linux does not, because the cmdlet model
/// carries the answer. Three independent signals, all of which must agree:
///
///  1. The approved verb. Get, Find, Measure and Test read; New, Set, Remove and
///     Install do not. Noun exceptions matter though -- Format-Table renders a table,
///     Format-Volume destroys a disk.
///  2. The abstract syntax tree. Redirection, assignment to a provider path,
///     Invoke-Expression, the call operator, and any external executable all mean the
///     effect cannot be established statically.
///  3. Command shape. An unknown command name is not assumed benign.
///
/// The remaining signal, SupportsShouldProcess (a cmdlet exposing -WhatIf declares
/// itself state-changing), needs a live runspace and is applied by the caller that has
/// one; see <see cref="PowerShellRiskAssessor"/>.
/// </summary>
public static class ReadOnlyClassifier
{
    /// <summary>
    /// Verbs that only read. Deliberately conservative: a verb missing from this list
    /// is treated as mutating, which is the safe direction to be wrong in.
    /// </summary>
    private static readonly HashSet<string> ReadingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get", "Find", "Search", "Show", "Test", "Measure", "Read", "Trace",
        "Compare", "Resolve", "Select", "Where", "Sort", "Group", "Format",
        "Out", "ConvertTo", "ConvertFrom", "Convert", "Join", "Split", "ForEach",
        "Tee", "Write",
    };

    /// <summary>
    /// Commands whose verb reads but which mutate anyway. This table is the reason the
    /// verb rule is usable at all: Format-Table is a renderer, Format-Volume erases a
    /// disk, and Out-File writes where Out-String does not.
    /// </summary>
    private static readonly HashSet<string> VerbLiars = new(StringComparer.OrdinalIgnoreCase)
    {
        "Format-Volume", "Format-Disk",
        "Out-File", "Out-Printer",
        "Write-Host",          // harmless, but Write-* generally is not; see below
        "Set-Content", "Add-Content", "Clear-Content",
        "Convert-Path",        // benign, listed for completeness of the audit trail
        "Tee-Object",          // writes to a file
        "Show-Command",        // opens a UI and can execute
    };

    /// <summary>
    /// Write-* is mostly output to a stream rather than to storage, so these specific
    /// ones are allowed back in after the blanket Write exclusion above.
    /// </summary>
    private static readonly HashSet<string> AllowedWriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Write-Output", "Write-Host", "Write-Verbose", "Write-Debug",
        "Write-Information", "Write-Warning", "Write-Error", "Write-Progress",
    };

    /// <summary>
    /// Commands that execute arbitrary text and therefore defeat static analysis
    /// entirely, whatever their verb looks like.
    /// </summary>
    private static readonly HashSet<string> OpaqueCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-Expression", "iex", "Invoke-Command", "icm",
        "Start-Process", "start", "saps",
        "Invoke-Item", "ii",
        "Invoke-WebRequest", "iwr", "curl", "wget",
        "Invoke-RestMethod", "irm",
        "New-Object",          // can construct anything, including a shell
        "Add-Type",            // compiles and loads arbitrary code
    };

    /// <summary>
    /// Patterns that must always prompt regardless of anything else, and which also
    /// survive the yolo mode. Matched after Unicode normalization.
    /// </summary>
    private static readonly (Regex Pattern, string Description)[] AlwaysDangerous =
    [
        (new Regex(@"Remove-Item.*-Recurse", RegexOptions.IgnoreCase), "recursive delete"),
        (new Regex(@"\bFormat-Volume\b", RegexOptions.IgnoreCase), "formats a volume"),
        (new Regex(@"\bvssadmin\b.*\bdelete\b", RegexOptions.IgnoreCase), "deletes shadow copies"),
        (new Regex(@"\bbcdedit\b", RegexOptions.IgnoreCase), "edits the boot configuration"),
        (new Regex(@"\bcipher\b.*\/w", RegexOptions.IgnoreCase), "wipes free space"),
        (new Regex(@"Set-ExecutionPolicy.*Bypass", RegexOptions.IgnoreCase), "disables script signing policy"),
        (new Regex(@"icacls.*Everyone", RegexOptions.IgnoreCase), "grants Everyone access"),
        (new Regex(@"\breg(\.exe)?\s+delete\s+HK(LM|EY_LOCAL)", RegexOptions.IgnoreCase), "deletes a machine registry key"),
        (new Regex(@"(iwr|Invoke-WebRequest|curl).*\|\s*(iex|Invoke-Expression)", RegexOptions.IgnoreCase), "pipes a download into the interpreter"),
        (new Regex(@"Stop-(Service|Computer)", RegexOptions.IgnoreCase), "stops a service or the machine"),
    ];

    /// <summary>
    /// Whether a script matches a pattern that must always be confirmed.
    ///
    /// Normalization runs first: NFKC collapses Unicode look-alikes, so a homoglyph
    /// cannot be used to slip past the patterns. Hermes learned this one the hard way
    /// and normalizes for the same reason.
    /// </summary>
    public static bool IsAlwaysDangerous(string script, out string reason)
    {
        string normalized = Normalize(script);

        foreach ((Regex pattern, string description) in AlwaysDangerous)
        {
            if (pattern.IsMatch(normalized))
            {
                reason = description;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Decide whether a script provably only reads.
    /// </summary>
    public static ScriptVerdict Classify(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return new ScriptVerdict(false, "the script is empty");

        if (IsAlwaysDangerous(script, out string danger))
            return new ScriptVerdict(false, $"matches an always-confirm pattern ({danger})");

        ScriptBlockAst ast;
        try
        {
            ast = Parser.ParseInput(Normalize(script), out _, out ParseError[] errors);

            // A script that does not parse cannot be reasoned about. Refusing to
            // classify it is the honest answer; PowerShell will report the syntax
            // error to the user anyway.
            if (errors.Length > 0)
                return new ScriptVerdict(false, "the script does not parse cleanly");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ScriptVerdict(false, $"the script could not be analysed: {ex.Message}");
        }

        // Redirection writes to a file no matter what the command was.
        if (ast.FindAll(n => n is FileRedirectionAst, searchNestedScriptBlocks: true).Any())
            return new ScriptVerdict(false, "redirects output to a file");

        // Assignment can target a provider path (Env:, HKLM:, a file), and telling
        // those apart from a plain variable reliably is not worth the risk.
        foreach (AssignmentStatementAst assignment in ast
            .FindAll(n => n is AssignmentStatementAst, true)
            .Cast<AssignmentStatementAst>())
        {
            if (assignment.Left is not VariableExpressionAst variable)
                return new ScriptVerdict(false, "assigns to something other than a plain variable");

            string name = variable.VariablePath.UserPath;
            if (name.Contains(':', StringComparison.Ordinal))
                return new ScriptVerdict(false, $"assigns to the provider path {name}");
        }

        // The call operator and the dot-source operator both execute whatever they are
        // handed, which may be built at runtime.
        foreach (CommandAst command in ast.FindAll(n => n is CommandAst, true).Cast<CommandAst>())
        {
            if (command.InvocationOperator is TokenKind.Ampersand or TokenKind.Dot)
                return new ScriptVerdict(false, "uses the call or dot-source operator");

            string? name = command.GetCommandName();

            if (string.IsNullOrEmpty(name))
                return new ScriptVerdict(false, "builds a command name dynamically");

            ScriptVerdict verdict = ClassifyCommand(name);
            if (!verdict.IsProvablyReadOnly)
                return verdict;
        }

        return new ScriptVerdict(true, "every command reads and nothing is written");
    }

    private static ScriptVerdict ClassifyCommand(string name)
    {
        if (OpaqueCommands.Contains(name))
            return new ScriptVerdict(false, $"{name} can execute arbitrary code");

        if (VerbLiars.Contains(name) && !AllowedWriteCommands.Contains(name))
            return new ScriptVerdict(false, $"{name} writes despite its verb");

        if (AllowedWriteCommands.Contains(name))
            return new ScriptVerdict(true, string.Empty);

        int dash = name.IndexOf('-', StringComparison.Ordinal);

        // No dash means an alias, a function, or an external program. An external
        // program is entirely opaque, and resolving aliases needs a live session, so
        // this is left to the caller that has one.
        if (dash <= 0)
            return new ScriptVerdict(false, $"{name} is not a verb-noun command, so its effect is unknown");

        string verb = name[..dash];
        return ReadingVerbs.Contains(verb)
            ? new ScriptVerdict(true, string.Empty)
            : new ScriptVerdict(false, $"the verb '{verb}' in {name} changes state");
    }

    /// <summary>
    /// Collapse Unicode look-alikes so a homoglyph cannot evade the pattern list.
    ///
    /// NFKC maps compatibility characters onto their canonical forms, which is what
    /// turns a Cyrillic 'е' or a fullwidth 'Ｒ' into the ASCII letter the patterns
    /// actually match against.
    /// </summary>
    private static string Normalize(string script) =>
        script.Normalize(NormalizationForm.FormKC);
}
