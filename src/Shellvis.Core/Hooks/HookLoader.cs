using System.Text.RegularExpressions;
using Shellvis.Core.Config;

namespace Shellvis.Core.Hooks;

/// <summary>
/// Turns the <c>hooks:</c> block of the config into definitions, complaining about
/// anything that would silently not work.
///
/// The complaints are the point. A hook is configured by hand in a text file and its
/// only feedback is whether it fires, so every way of getting it wrong -- an event name
/// that does not exist, an event this build never raises, a regex that does not compile
/// -- has to produce a sentence rather than silence.
/// </summary>
public static class HookLoader
{
    public static IReadOnlyList<HookDefinition> Load(
        IReadOnlyDictionary<string, List<HookSection>>? configured, List<string> warnings)
    {
        var hooks = new List<HookDefinition>();

        if (configured is null || configured.Count == 0)
            return hooks;

        foreach ((string eventName, List<HookSection> entries) in configured)
        {
            HookEvent? parsed = HookCatalog.Parse(eventName);

            if (parsed is null)
            {
                warnings.Add(
                    $"hooks: '{eventName}' is not a hook event. Known: "
                    + string.Join(", ", HookCatalog.AllNames));

                continue;
            }

            HookEvent value = parsed.Value;

            // The honest warning. Accepting the entry and never firing it would leave
            // the user believing the hook is installed, which is worse than refusing it.
            if (!HookCatalog.Fires(value))
            {
                warnings.Add(
                    $"hooks: '{eventName}' is part of the protocol but this build never "
                    + "raises it, so hooks attached to it will not run.");
            }

            foreach (HookSection entry in entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Command))
                {
                    warnings.Add($"hooks: an entry under '{eventName}' has no command; ignored.");
                    continue;
                }

                Regex? matcher = null;

                if (!string.IsNullOrWhiteSpace(entry.Matcher))
                {
                    try
                    {
                        // Compiled and time-limited: the matcher runs on every tool call,
                        // and a catastrophic-backtracking pattern would otherwise stall
                        // the turn rather than fail.
                        matcher = new Regex(
                            entry.Matcher,
                            RegexOptions.Compiled | RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(100));
                    }
                    catch (ArgumentException ex)
                    {
                        warnings.Add(
                            $"hooks: matcher '{entry.Matcher}' under '{eventName}' is not a "
                            + $"valid regex ({ex.Message}); the hook was skipped.");

                        continue;
                    }
                }

                int timeout = entry.TimeoutSeconds;

                if (timeout <= 0)
                    timeout = 60;

                if (timeout > HookDefinition.MaxTimeoutSeconds)
                {
                    // Capped rather than rejected: the intent is clear and honouring it
                    // literally would mean an agent that can hang for as long as the
                    // config asks.
                    warnings.Add(
                        $"hooks: timeout {timeout}s under '{eventName}' exceeds the "
                        + $"{HookDefinition.MaxTimeoutSeconds}s ceiling and was capped.");

                    timeout = HookDefinition.MaxTimeoutSeconds;
                }

                hooks.Add(new HookDefinition(value, entry.Command.Trim(), matcher, timeout));
            }
        }

        return hooks;
    }
}
