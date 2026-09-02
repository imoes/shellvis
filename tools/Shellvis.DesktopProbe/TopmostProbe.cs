using Shellvis.Core.Desktop;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Where the bar sits in the z-order, and what moves it.
///
/// <b>What this used to check, and why it is gone.</b> This suite used to pin down a geometry
/// rule: whether the foreground window covered its whole monitor, with a pixel or two of
/// tolerance. That rule was one of three the bar used to guess when to get out of the way, and
/// all three have been removed -- they were wrong in both directions, leaving the bar behind
/// ordinary windows while still showing it over a remote desktop connection. The bar is now an
/// application desktop toolbar and does what the shell tells it.
///
/// So what is worth checking is no longer arithmetic, it is the CONTRACT: the sequence of
/// appbar notifications, and where each one leaves the bar. That runs without a desktop, which
/// is the point of keeping it here.
/// </summary>
internal static class TopmostProbe
{
    public static int Run()
    {
        Console.WriteLine("-- the bar's place in the z-order --");

        int failures = 0;

        var band = new TaskbarBand();

        // The ordinary state. Topmost, because the taskbar is topmost, and the whole intent is
        // to be on its level.
        failures += Check("a fresh bar is in the topmost band", band.Position == BandPosition.Topmost);
        failures += Check("and has nothing to explain", band.Reason is null);

        // The regression this exists for. The documented sample reads ABS_ALWAYSONTOP and goes
        // topmost only when it is set; that flag lost its meaning in Windows 8, so following the
        // sample drops the bar out of the band on a modern desktop -- and for a bar lying on the
        // taskbar strip, out of the band means invisible. A state change must not move it.
        failures += Check(
            "a taskbar state change does not move the bar",
            !band.Apply(TaskbarBand.StateChange, flag: false));

        failures += Check(
            "which leaves it topmost, whatever ABS_ALWAYSONTOP says",
            band.Position == BandPosition.Topmost);

        // The taskbar moving is a placement question for the docked bar, not a z-order one.
        failures += Check(
            "the taskbar moving does not move the bar in the z-order",
            !band.Apply(TaskbarBand.PositionChanged, flag: false));

        failures += Check(
            "nor does the user tiling their windows",
            !band.Apply(TaskbarBand.WindowArrange, flag: true));

        // The one thing that does move it, reported by the shell rather than guessed at -- and
        // the same notification that moves the taskbar itself.
        failures += Check(
            "a full-screen application opening moves it",
            band.Apply(TaskbarBand.FullScreenApp, flag: true));

        failures += Check("to the bottom of the z-order", band.Position == BandPosition.Bottom);
        failures += Check("and that is said out loud", band.Reason is { Length: > 0 });

        // Reported twice, which the shell does. The bar must not report a change it did not
        // make, or the console fills with the same line.
        failures += Check(
            "a repeated notification is not a change",
            !band.Apply(TaskbarBand.FullScreenApp, flag: true));

        // Everything else while a full-screen application has the screen must leave it there:
        // coming back up over a game because the taskbar happened to resize is the defect.
        failures += Check(
            "a state change while full-screen leaves it at the bottom",
            !band.Apply(TaskbarBand.StateChange, flag: false)
                && band.Position == BandPosition.Bottom);

        failures += Check(
            "and so does the taskbar moving",
            !band.Apply(TaskbarBand.PositionChanged, flag: false)
                && band.Position == BandPosition.Bottom);

        failures += Check(
            "the last full-screen application closing brings it back",
            band.Apply(TaskbarBand.FullScreenApp, flag: false));

        failures += Check("to the topmost band", band.Position == BandPosition.Topmost);
        failures += Check("with nothing left to explain", band.Reason is null);

        // And the return is SAID. Only announcing the retreat is how a console ends up
        // reading as two disappearances and no returns -- the complaint this began with,
        // reproduced by the reporting instead of by the behaviour.
        failures += Check(
            "the return is announced too, not only the retreat",
            band.Moved is { Length: > 0 } back && back.Contains("back", StringComparison.Ordinal));

        band.Apply(TaskbarBand.FullScreenApp, flag: true);

        failures += Check(
            "and the two announcements are not the same sentence",
            band.Moved != "The full-screen application has closed, so Shellvis is back on the taskbar's level.");

        band.Apply(TaskbarBand.FullScreenApp, flag: false);

        band.Apply(TaskbarBand.FullScreenApp, flag: true);
        band.Reset();

        failures += Check(
            "a reset forgets the full-screen state, for a registration remade after Explorer restarts",
            band.Position == BandPosition.Topmost);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: only a full-screen application moves the bar, and the taskbar's own\n"
                + "state does not -- which is the regression that made it vanish."
            : $"{failures} check(s) failed.");

        Console.WriteLine();
        Console.WriteLine("NOT covered here: that the shell actually sends these notifications.");
        Console.WriteLine("ABM_NEW needs a real window and a running Explorer, so registration is");
        Console.WriteLine("checked by hand -- with a full-screen application and a remote session.");

        return failures == 0 ? 0 : 1;
    }

    private static int Check(string what, bool condition)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
