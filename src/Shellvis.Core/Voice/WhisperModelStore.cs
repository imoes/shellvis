using System.Globalization;

namespace Shellvis.Core.Voice;

/// <summary>One Whisper model the user can choose between.</summary>
/// <param name="Id">The short name used in config.yaml and on the installer's dialog.</param>
/// <param name="File">The ggml file name, which is also the name on the model host.</param>
/// <param name="Bytes">
/// Expected size. Carried in the catalog rather than trusted from the response, because it
/// is what makes a half-finished download detectable -- see <see cref="IsPresent"/>.
/// </param>
/// <param name="Note">What the user gets for the size, for the dialog and the transcript.</param>
public sealed record WhisperModel(string Id, string File, long Bytes, string Note)
{
    public string SizeText =>
        (Bytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// Where the Whisper models live, and how one arrives.
///
/// <b>Why a download at all.</b> The models are between 74 MB and 1.5 GB, and shipping one
/// inside the installer would put a payload of up to 1.5 GB in front of every user
/// including the ones who never dictate. GitHub also refuses files over 100 MB, so the release artefact
/// could not carry it even if that were desirable. So the model is fetched once, to a
/// per-user directory, and the choice of which one is asked rather than assumed.
///
/// <b>Why not the Windows recognizer instead.</b> This machine has two engines installed:
/// the desktop SAPI one that <see cref="DictationEngine"/> can reach
/// (<c>MS-1031-80-DESK</c>, a GMM-HMM engine of Windows-Vista vintage) and a much better
/// DNN engine under <c>Speech_OneCore</c> that only the WinRT API can reach -- and that
/// API's free-form dictation goes through Microsoft's online service. Local-only was a
/// requirement, so the way past the old engine's quality is a local model, not a better
/// Windows API.
/// </summary>
public static class WhisperModelStore
{
    /// <summary>
    /// The models offered, smallest first.
    ///
    /// Deliberately short. Whisper publishes English-only and quantised variants too, and
    /// listing them would mean asking the user to compare eight things -- while the
    /// English-only ones are actively wrong here (the recognition language on this machine
    /// is German) and the quantised ones trade the accuracy this whole change exists to buy.
    /// </summary>
    public static IReadOnlyList<WhisperModel> Catalog { get; } =
    [
        new("tiny", "ggml-tiny.bin", 77_704_715,
            "fastest, roughly on a par with the old Windows engine"),
        new("base", "ggml-base.bin", 147_951_465,
            "clearly better than the Windows engine, still stumbles on names"),
        new("small", "ggml-small.bin", 487_601_967,
            "quick, but guesses unstressed word endings: \"noch an\" came back as \"nach dem\""),
        new("medium", "ggml-medium.bin", 1_533_763_059,
            "recommended: gets the endings right, about three times the CPU time"),
    ];

    public const string DefaultModelId = "medium";

    /// <summary>Resolve a configured name, falling back to the default with a reason.</summary>
    public static WhisperModel Resolve(string? id, out string? warning)
    {
        warning = null;

        if (string.IsNullOrWhiteSpace(id))
            return Find(DefaultModelId)!;

        WhisperModel? found = Find(id);

        if (found is not null)
            return found;

        // Named and not silently substituted. A misspelt model that quietly became the
        // default would be a setting that looks obeyed while something else is loaded --
        // the failure mode this project has already fixed for providers and for
        // approvals.mode.
        warning = $"unknown whisper model '{id}'. Known: "
            + string.Join(", ", Catalog.Select(m => m.Id))
            + $". Using '{DefaultModelId}'.";

        return Find(DefaultModelId)!;
    }

    /// <summary>
    /// Where the installer records the model the user chose during setup.
    ///
    /// HKCU, so it needs no privilege and works identically in both installation modes. Read
    /// as a fallback rather than as an override: once the user edits config.yaml, that is the
    /// answer, and a setup choice from months ago must not win over it.
    /// </summary>
    private const string SetupKey = @"HKEY_CURRENT_USER\Software\Shellvis";

    /// <summary>
    /// Resolve the model to use: the config's choice, else the installer's, else the default.
    /// </summary>
    public static WhisperModel Configured(string? fromConfig, out string? warning)
    {
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return Resolve(fromConfig, out warning);

        warning = null;

        try
        {
            if (Microsoft.Win32.Registry.GetValue(SetupKey, "WhisperModel", null)
                is string chosen && chosen.Length > 0)
            {
                // "none" is a real answer at setup time -- someone who declined the download
                // should not have it start behind their back on first use.
                if (chosen.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    warning = "no whisper model was selected during setup; dictation uses "
                        + "the Windows engine. Set voice.whisperModel in config.yaml to change that.";

                    return Find(DefaultModelId)!;
                }

                return Resolve(chosen, out warning);
            }
        }
        catch (Exception)
        {
            // A registry that cannot be read is not a reason to refuse to dictate.
        }

        return Find(DefaultModelId)!;
    }

    /// <summary>Whether setup explicitly declined a model.</summary>
    public static bool SetupDeclinedModel()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(SetupKey, "WhisperModel", null)
                is string chosen && chosen.Equals("none", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static WhisperModel? Find(string id) =>
        Catalog.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where models are kept.
    ///
    /// Under LOCALAPPDATA rather than beside the binaries: the per-user install has no
    /// write access to Program Files, the machine-wide one deliberately denies it, and a
    /// model is user data anyway. Not under the roaming profile either -- half a gigabyte
    /// does not belong in something that syncs.
    /// </summary>
    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shellvis", "Models");

    public static string PathFor(WhisperModel model) => Path.Combine(Directory, model.File);

    /// <summary>
    /// Whether the model is on disk and complete.
    ///
    /// The size is checked, not just the existence of the file. An interrupted download is
    /// the likely failure for a 465 MB fetch, and a truncated ggml file does not fail
    /// politely -- whisper.cpp reads a header, walks past the end and the process dies
    /// inside native code, which surfaces as Shellvis vanishing rather than as a message.
    /// A tolerance because the published sizes shift slightly between model revisions;
    /// what is being caught is a partial file, not a byte-exact match.
    /// </summary>
    public static bool IsPresent(WhisperModel model)
    {
        var file = new FileInfo(PathFor(model));

        return file.Exists && file.Length > (long)(model.Bytes * 0.98);
    }

    /// <summary>The model host. Whisper's own ggml conversions, as published upstream.</summary>
    private static string UrlFor(WhisperModel model) =>
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + model.File;

    /// <summary>
    /// Fetch a model, reporting progress as a percentage.
    ///
    /// Downloaded to a neighbouring temporary file and moved into place, so an interrupted
    /// fetch leaves nothing that looks usable. The alternative -- writing the real path
    /// directly -- means a cancelled download produces a file that passes an existence
    /// check and kills the process on load.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        WhisperModel model,
        Action<int>? progress = null,
        CancellationToken cancel = default)
    {
        string target = PathFor(model);
        string partial = target + ".partial";

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Left over from an interrupted attempt; resuming would need range support and
            // a way to trust what is already there, and starting over is honest.
            if (File.Exists(partial))
                File.Delete(partial);

            using var http = new HttpClient
            {
                // Generous: this is 465 MB over a corporate link, and the read timeout that
                // matters is the per-response one below, not the whole transfer.
                Timeout = Timeout.InfiniteTimeSpan,
            };

            using HttpResponseMessage response = await http
                .GetAsync(UrlFor(model), HttpCompletionOption.ResponseHeadersRead, cancel)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return $"the model host answered {(int)response.StatusCode} {response.ReasonPhrase}.";

            long total = response.Content.Headers.ContentLength ?? model.Bytes;
            long done = 0;
            int lastReported = -1;

            await using Stream source = await response.Content
                .ReadAsStreamAsync(cancel).ConfigureAwait(false);

            await using (var sink = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                byte[] buffer = new byte[1 << 20];
                int read;

                while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    done += read;

                    int percent = total > 0 ? (int)(done * 100 / total) : 0;

                    // Only on change, and only in whole percent: a 465 MB transfer in 1 MB
                    // blocks is roughly 465 callbacks, and each one is a UI-thread hop.
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        progress?.Invoke(percent);
                    }
                }
            }

            var fetched = new FileInfo(partial);

            if (fetched.Length < (long)(model.Bytes * 0.98))
            {
                File.Delete(partial);

                return $"the download ended early ({fetched.Length / (1024 * 1024)} MB of "
                    + $"{model.SizeText}). Nothing was installed.";
            }

            File.Move(partial, target, overwrite: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            return "the download was cancelled.";
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            return $"could not fetch the model: {ex.Message}";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
