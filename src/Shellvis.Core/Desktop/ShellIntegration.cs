using Microsoft.Win32;

namespace Shellvis.Core.Desktop;

/// <summary>
/// Shellvis in Explorer and behind its own links: the <c>shellvis:</c> scheme and two entries
/// in the context menu of every file.
///
/// <b>Why per user, in HKCU.</b> No administrator is needed, nothing is written for other
/// accounts, and the entries point at the executable of the person who runs it. A machine-wide
/// installation still registers per user, for the reason its autostart entry does: the
/// interactive application is per user even when the binaries are shared.
///
/// <b>Why the application registers itself and not only the installer.</b> A build that was
/// never installed -- the one a developer tests with -- would otherwise have no menu and no
/// links, and the feature would be untestable exactly where it is developed. The rule that
/// keeps the two from fighting: an entry is written only when it is missing or points at a
/// file that no longer exists. A working registration, from the installer or from another
/// copy, is left alone.
///
/// <b>What a link can do is decided elsewhere, and stays small.</b> Registering a scheme lets
/// any web page or document hand Shellvis a <c>shellvis:</c> address. The address reaches the
/// same closed set of link actions an answer's link does -- today, opening a mail -- and never
/// a prompt: a page that could make the agent ask itself something would be a remote control.
/// </summary>
public static class ShellIntegration
{
    /// <summary>The scheme, without the colon.</summary>
    public const string Scheme = "shellvis";

    /// <summary>Where the per-user classes live. A probe passes its own root instead.</summary>
    public const string UserClasses = @"Software\Classes";

    /// <summary>The verb key of "ask about this file".</summary>
    public const string AskVerb = "Shellvis.Ask";

    /// <summary>The verb key of "file this with Shellvis".</summary>
    public const string SortVerb = "Shellvis.Sort";

    /// <summary>The switch that opens a <c>shellvis:</c> link.</summary>
    public const string OpenArgument = "--open";

    /// <summary>The switch that names a file.</summary>
    public const string FileArgument = "--file";

    /// <summary>The switch for "open the workbench with this file, and let me ask".</summary>
    public const string AskArgument = "--ask";

    /// <summary>The switch for "work out what this file belongs to".</summary>
    public const string SortArgument = "--sort";

    /// <summary>One value to write: a key below the classes root, a value name (empty for the default), the data.</summary>
    public sealed record Entry(string Key, string Name, string Value);

    /// <summary>
    /// Everything that is written, for one executable, in one language.
    ///
    /// A list rather than code that writes as it goes, so the probe can compare the plan with
    /// what landed in the registry, and a dry run can show it.
    /// </summary>
    public static IReadOnlyList<Entry> Entries(string executable, bool german)
    {
        string exe = Quote(executable);
        string askVerb = $@"*\shell\{AskVerb}";
        string sortVerb = $@"*\shell\{SortVerb}";

        return
        [
            new(Scheme, string.Empty, "URL:Shellvis"),
            new(Scheme, "URL Protocol", string.Empty),
            new($@"{Scheme}\DefaultIcon", string.Empty, $"{exe},0"),
            new($@"{Scheme}\shell\open\command", string.Empty, $"{exe} {OpenArgument} \"%1\""),

            new(askVerb, "MUIVerb", german ? "Shellvis fragen" : "Ask Shellvis"),
            new(askVerb, "Icon", $"{exe},0"),
            new($@"{askVerb}\command", string.Empty, $"{exe} {AskArgument} {FileArgument} \"%1\""),

            new(sortVerb, "MUIVerb", german ? "Mit Shellvis einordnen" : "File with Shellvis"),
            new(sortVerb, "Icon", $"{exe},0"),
            new($@"{sortVerb}\command", string.Empty, $"{exe} {SortArgument} {FileArgument} \"%1\""),
        ];
    }

    /// <summary>
    /// Register when nothing usable is registered, and say what happened.
    /// </summary>
    /// <returns>A sentence when something was written, null when the existing registration was kept.</returns>
    public static string? EnsureRegistered(string executable, bool german, string root = UserClasses)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return null;

        string? current = RegisteredExecutable(root);

        if (current is not null && File.Exists(current) && AllPresent(root))
            return null;

        Write(Entries(executable, german), root);

        return current is null
            ? "Explorer now offers \"Ask Shellvis\" on files, and shellvis: links open here."
            : $"the Explorer entries pointed at {current}, which is gone; they point here now.";
    }

    /// <summary>Write the entries, whatever is there.</summary>
    public static void Write(IReadOnlyList<Entry> entries, string root = UserClasses)
    {
        foreach (Entry entry in entries)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{root}\{entry.Key}", writable: true);
            key.SetValue(entry.Name, entry.Value, RegistryValueKind.String);
        }
    }

    /// <summary>Remove everything this class writes. Used by the probe, and by anyone undoing it by hand.</summary>
    public static void Remove(string root = UserClasses)
    {
        foreach (string key in new[] { Scheme, $@"*\shell\{AskVerb}", $@"*\shell\{SortVerb}" })
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{key}", throwOnMissingSubKey: false);
    }

    /// <summary>The executable the scheme opens today, or null when none is registered.</summary>
    public static string? RegisteredExecutable(string root = UserClasses)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{root}\{Scheme}\shell\open\command");

        return key?.GetValue(string.Empty) is string command ? ExecutableOf(command) : null;
    }

    /// <summary>The executable at the front of a command line, quoted or not.</summary>
    public static string? ExecutableOf(string command)
    {
        string text = command.Trim();

        if (text.Length == 0)
            return null;

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }

        int space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? text : text[..space];
    }

    private static bool AllPresent(string root)
    {
        foreach (string key in new[]
        {
            $@"{Scheme}\shell\open\command",
            $@"*\shell\{AskVerb}\command",
            $@"*\shell\{SortVerb}\command",
        })
        {
            using RegistryKey? found = Registry.CurrentUser.OpenSubKey($@"{root}\{key}");

            if (found?.GetValue(string.Empty) is not string { Length: > 0 })
                return false;
        }

        return true;
    }

    private static string Quote(string path) => "\"" + path.Trim('"') + "\"";
}
