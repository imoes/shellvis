using Shellvis.Core.Config;
using Shellvis.Core.Voice;

namespace Shellvis.Setup;

/// <summary>
/// Fetching the speech model as part of installing, rather than at the first dictation.
///
/// <b>Why this is a separate program and not a step inside the .msi.</b> Two reasons, and the
/// first one decides it.
///
/// The model belongs to a PERSON, not to the machine: it lands in
/// <c>%LOCALAPPDATA%\Shellvis\Models</c>. A custom action inside the install transaction runs
/// either as LocalSystem, which would put a 1.5 GB file in the service profile where nobody
/// will ever find it, or as the account that elevated the install, which on a managed desktop
/// is frequently not the person who is going to dictate. Running afterwards, in the session
/// that clicked Finish, is the only way the file ends up in the right profile.
///
/// And a download is not something to do inside a transaction. It takes minutes on a slow
/// connection, during which Windows Installer's progress bar would sit still with no
/// explanation and no cancel; a failure would have to either roll the installation back or be
/// swallowed. Afterwards, it is a console window that can be closed, retried, or ignored
/// entirely without the installation being any less complete.
///
/// <b>The intelligence is here rather than in the dialog.</b> The checkbox on the last page is
/// unconditional, because MSI conditions cannot cheaply express "a model was chosen and is not
/// already on disk". So this decides: it says what it is doing and does nothing when there is
/// nothing to do. Ticking the box with the model already present costs a sentence.
/// </summary>
internal static class ModelFetch
{
    public static async Task<int> RunAsync(string? requested)
    {
        // config.yaml first, exactly as Shellvis itself resolves it: a choice made in the
        // installer months ago must not overrule the file the user has since edited. The
        // installer's own answer is the fallback, which is what WhisperModelStore.Configured
        // already implements -- so this asks the same question the application asks rather
        // than a similar one.
        string? fromConfig = requested;
        string? warning = null;

        if (fromConfig is null)
        {
            try
            {
                fromConfig = ConfigStore.Load().Config.Voice?.WhisperModel;
            }
            catch (Exception ex)
            {
                // A missing or unreadable config is not a reason to refuse: the registry
                // answer from setup is still there, and that is the common case on a fresh
                // machine where no config.yaml exists yet.
                Console.WriteLine($"could not read config.yaml ({ex.Message}); using the setup choice.");
            }
        }

        WhisperModel model = WhisperModelStore.Configured(fromConfig, out warning);

        if (warning is { Length: > 0 })
            Console.WriteLine(warning);

        // "None" is a real answer and has to be honoured, or the box would download 1.5 GB
        // to somebody who said they did not want it.
        if (model.Id.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "no speech model was chosen, so nothing is downloaded. Dictation will use the "
                + "Windows recogniser. Set voice.whisperModel in config.yaml to change that.");

            return 0;
        }

        if (WhisperModelStore.IsPresent(model))
        {
            Console.WriteLine(
                $"'{model.Id}' is already in {WhisperModelStore.Directory} -- nothing to do. "
                + "The model folder lives outside the installation and is untouched by "
                + "installing or uninstalling.");

            return 0;
        }

        Console.WriteLine($"downloading the '{model.Id}' speech model ({model.SizeText}).");
        Console.WriteLine($"into {WhisperModelStore.Directory}");
        Console.WriteLine("This runs on this machine only; nothing is sent anywhere.");
        Console.WriteLine();

        int lastShown = -1;

        // NULL MEANS SUCCESS. The string is the reason it failed, not the path it wrote, and
        // reading it the other way round is how the first version of this reported a
        // completed 74 MB download as "the download did not finish" -- after printing 100%,
        // with the file correctly on disk. Named 'problem' so the next reader cannot invert
        // it as easily as I did.
        string? problem = await WhisperModelStore.DownloadAsync(
            model,
            progress: percent =>
            {
                // Every five per cent, on one line. A per-cent-per-line log of a 1.5 GB
                // download is a hundred lines nobody reads, and a spinner in a window that
                // may be closed at any moment is worse than a number.
                if (percent / 5 == lastShown / 5)
                    return;

                lastShown = percent;
                Console.WriteLine($"  {percent,3}%");
            }).ConfigureAwait(false);

        Console.WriteLine();

        if (problem is { Length: > 0 })
        {
            // Not a failed installation, and said so. This window is the only place it will
            // be said, and the fallback is genuinely fine.
            Console.WriteLine(problem);
            Console.WriteLine(
                "Nothing is broken: Shellvis fetches the model the first time you dictate, "
                + "and uses the Windows recogniser until then.");

            return 1;
        }

        Console.WriteLine($"done: {WhisperModelStore.PathFor(model)}");
        return 0;
    }
}
