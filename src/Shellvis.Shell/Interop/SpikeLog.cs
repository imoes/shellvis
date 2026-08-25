using System.Diagnostics;

namespace Shellvis.Shell.Interop;

/// <summary>
/// Throwaway diagnostics for the UI spike. A borderless always-on-top window has no
/// console and no visible error surface, so without this the only way to tell a
/// layout bug from a paint bug is guesswork.
///
/// Delete once the console renders real agent events -- at that point the transcript
/// itself is the diagnostic surface.
/// </summary>
internal static class SpikeLog
{
    private static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "shellvis-spike.log");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        string line = string.Concat(
            DateTime.Now.ToString("HH:mm:ss.fff"), "  ", message, Environment.NewLine);

        Debug.Write(line);

        try
        {
            lock (Gate)
                System.IO.File.AppendAllText(Path, line);
        }
        catch (IOException)
        {
            // Diagnostics must never take the window down with them.
        }
    }
}
