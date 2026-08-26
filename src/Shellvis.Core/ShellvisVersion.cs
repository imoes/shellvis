using System.Reflection;

namespace Shellvis.Core;

/// <summary>
/// The build's own version, read from the assembly rather than written down twice.
///
/// Duplicating the number in code is how a build ends up reporting a version it is not.
/// The value comes from <c>Directory.Build.props</c> at compile time and is read back
/// through the informational version, which survives as written -- AssemblyVersion is
/// normalised to four numeric parts, so a pre-release suffix would vanish from it.
/// </summary>
public static class ShellvisVersion
{
    /// <summary>The version as a bare number, e.g. "0.1.0".</summary>
    public static string Current { get; } = Read();

    /// <summary>For a status line: "Shellvis 0.1.0".</summary>
    public static string Label => "Shellvis " + Current;

    private static string Read()
    {
        Assembly assembly = typeof(ShellvisVersion).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (informational is { Length: > 0 })
        {
            // The SDK appends "+<commit sha>" when source revision is included. Cut it:
            // the sha belongs in a build log, not in a status line a person reads.
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        // A version is nice to have, not worth throwing over.
        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
