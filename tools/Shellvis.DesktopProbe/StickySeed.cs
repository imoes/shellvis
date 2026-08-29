using Shellvis.Core.Notes;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Put a note on the real desktop, so the window can be looked at.
///
/// A separate command rather than part of `probe notes`, because that harness deliberately
/// works in a temporary database and must never touch the user's. This one does touch it:
/// its whole purpose is to make a window appear in the application on the next start.
/// </summary>
internal static class StickySeed
{
    public static int Run(string? text, string? colour)
    {
        using var store = new NoteStore();

        Sticky stuck = store.Stick(
            text ?? "Rosen kaufen -- Freitag ist der Hochzeitstag.",
            NoteStore.ParseColour(colour ?? "yellow"));

        Console.WriteLine($"stuck note {stuck.Id} ({stuck.Colour}) at {stuck.X},{stuck.Y}");
        Console.WriteLine($"{store.Stickies().Count} note(s) now on the desktop");

        return 0;
    }
}
