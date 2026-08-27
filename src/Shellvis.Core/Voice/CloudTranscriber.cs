using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Shellvis.Core.Voice;

/// <summary>
/// Speech recognition by Azure or Google, for people who want it.
///
/// <b>This is the one place where audio leaves the machine, and it is off by default.</b> The
/// project's standing promise was that no cloud path exists in the code; that promise now reads
/// "unless you configure one", and the README says so. Nothing here runs unless
/// <c>voice.engine</c> names a cloud provider AND a key is present, and the console says which
/// recogniser is in use at the start of every dictation, with the fact that the recording is
/// being sent stated in the same sentence.
///
/// <b>Why it was worth adding.</b> The local models get German wrong in a specific, tiring way
/// -- "diese Woche noch an" came back as "diese Woche nach dem", the acoustics fine and an
/// unstressed word ending guessed. The larger local model fixes most of that and costs about
/// three times the compute: measured on this machine, 11.5 seconds for three seconds of speech
/// against 3.8. The hosted services are both better than the large model and faster than the
/// small one, and that combination is not something a local model can offer today.
///
/// <b>Why one class for two providers.</b> Both are a single HTTP POST of a WAV file and a JSON
/// answer, differing in the URL, the auth header and where the text sits in the response. Two
/// classes would duplicate the WAV framing, the error handling and the empty-result semantics
/// to express a difference of three fields.
/// </summary>
public sealed class CloudTranscriber : ITranscriber
{
    /// <summary>Which service to talk to.</summary>
    public enum Service
    {
        Azure,
        Google,
    }

    private readonly HttpClient _http;
    private readonly Service _service;
    private readonly string _key;
    private readonly string _region;
    private bool _disposed;

    /// <param name="region">
    /// The Azure region, e.g. westeurope. Azure's speech endpoint is per-region and there is no
    /// global one, so a wrong region is a connection failure rather than an auth failure.
    /// Ignored for Google.
    /// </param>
    public CloudTranscriber(Service service, string key, string region, HttpClient? http = null)
    {
        _service = service;
        _key = key;
        _region = string.IsNullOrWhiteSpace(region) ? "westeurope" : region.Trim();

        // Injectable so the harness can point it at a local stub. The alternative is testing
        // against the real services, which needs paid accounts and network, and would make the
        // suite fail for reasons that have nothing to do with this code.
        _http = http ?? new HttpClient();

        // Dictation is short. A minute is generous for a few seconds of audio and short enough
        // that a hung request does not leave the user waiting with no idea why.
        if (http is null)
            _http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>Where the request goes. Overridable so the harness can substitute a stub.</summary>
    public string? EndpointOverride { get; set; }

    public bool IsLoaded => _key.Length > 0;

    public bool IsRemote => true;

    public string Description => _service switch
    {
        Service.Azure => $"Azure Speech ({_region})",
        _ => "Google Speech-to-Text",
    };

    public async Task<WhisperResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcm, string? language, CancellationToken cancel = default)
    {
        if (_disposed)
            return new WhisperResult(string.Empty, "the transcriber has been disposed.");

        if (_key.Length == 0)
        {
            return new WhisperResult(string.Empty,
                $"no API key for {Description}. Set it through the model menu, or put it in "
                + "the environment and restart.");
        }

        // The same floor the local recogniser uses. Sending a fragment costs a request and a
        // charge to be told what is already knowable here.
        if (pcm.Length < 16000 * 2 / 2)
            return new WhisperResult(string.Empty, null);

        string culture = language is { Length: >= 2 } ? language : "de-DE";

        try
        {
            return _service == Service.Azure
                ? await AzureAsync(pcm, culture, cancel).ConfigureAwait(false)
                : await GoogleAsync(pcm, culture, cancel).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return new WhisperResult(string.Empty,
                $"{Description} did not answer in time. Dictation fell back to nothing; the "
                + "recording was not kept.");
        }
        catch (Exception ex)
        {
            // The message is passed through, and the key never is: it is only ever in a header.
            return new WhisperResult(string.Empty, $"{Description} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Azure's short-audio REST endpoint: a WAV body, the key in a header, JSON back.
    /// </summary>
    private async Task<WhisperResult> AzureAsync(
        ReadOnlyMemory<byte> pcm, string culture, CancellationToken cancel)
    {
        string url = EndpointOverride
            ?? $"https://{_region}.stt.speech.microsoft.com/speech/recognition/conversation"
               + "/cognitiveservices/v1";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{url}?language={Uri.EscapeDataString(culture)}&format=detailed");

        request.Headers.Add("Ocp-Apim-Subscription-Key", _key);
        request.Headers.Add("Accept", "application/json");

        // A RIFF header, not raw PCM. Azure accepts raw PCM only with a content-type that
        // spells out every audio parameter; a WAV header carries the same facts in a form both
        // services already understand, and the local path needs the framing code anyway.
        request.Content = new ByteArrayContent(Wave(pcm.Span));
        request.Content.Headers.ContentType = new("audio/wav");

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancel).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new WhisperResult(string.Empty, Explain((int)response.StatusCode, body));

        using JsonDocument json = JsonDocument.Parse(body);

        // RecognitionStatus is checked rather than just reading DisplayText: "NoMatch" and
        // "InitialSilenceTimeout" both come back as HTTP 200 with an empty text, and treating
        // those as a failure would report a problem when the user simply said nothing.
        string status = json.RootElement.TryGetProperty("RecognitionStatus", out JsonElement s)
            ? s.GetString() ?? string.Empty
            : string.Empty;

        if (status is "NoMatch" or "InitialSilenceTimeout" or "BabbleTimeout")
            return new WhisperResult(string.Empty, null);

        string text = json.RootElement.TryGetProperty("DisplayText", out JsonElement d)
            ? d.GetString() ?? string.Empty
            : string.Empty;

        return new WhisperResult(text.Trim(), null);
    }

    /// <summary>
    /// Google's synchronous recognise call: base64 audio in a JSON body, the key in the query.
    /// </summary>
    private async Task<WhisperResult> GoogleAsync(
        ReadOnlyMemory<byte> pcm, string culture, CancellationToken cancel)
    {
        string url = EndpointOverride ?? "https://speech.googleapis.com/v1/speech:recognize";

        var payload = new
        {
            config = new
            {
                encoding = "LINEAR16",
                sampleRateHertz = 16000,
                languageCode = culture,
                audioChannelCount = 1,

                // Punctuation, because dictated text goes into a prompt box a person reads.
                // Google leaves it out by default and the result is one long clause.
                enableAutomaticPunctuation = true,
            },
            audio = new
            {
                content = Convert.ToBase64String(pcm.Span),
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{url}?key={Uri.EscapeDataString(_key)}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancel).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new WhisperResult(string.Empty, Explain((int)response.StatusCode, body));

        using JsonDocument json = JsonDocument.Parse(body);

        // An empty "results" is Google's way of saying it heard nothing, and it arrives as a
        // 200 with no property at all rather than an empty array.
        if (!json.RootElement.TryGetProperty("results", out JsonElement results))
            return new WhisperResult(string.Empty, null);

        var text = new StringBuilder();

        foreach (JsonElement result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("alternatives", out JsonElement alternatives)
                || alternatives.GetArrayLength() == 0)
            {
                continue;
            }

            if (!alternatives[0].TryGetProperty("transcript", out JsonElement transcript))
                continue;

            string piece = transcript.GetString()?.Trim() ?? string.Empty;

            if (piece.Length == 0)
                continue;

            if (text.Length > 0)
                text.Append(' ');

            text.Append(piece);
        }

        return new WhisperResult(text.ToString().Trim(), null);
    }

    /// <summary>
    /// Turn an HTTP failure into something a user can act on.
    ///
    /// The status codes that matter here have specific, different remedies, and "the service
    /// returned 403" sends someone to a search engine rather than to their key.
    /// </summary>
    private string Explain(int status, string body)
    {
        string detail = body.Length > 200 ? body[..200] : body;

        return status switch
        {
            401 or 403 => $"{Description} rejected the key. Check it, and for Azure that the "
                + $"region matches the one the key was issued for ('{_region}').",

            404 => $"{Description} has no endpoint at that address. For Azure this usually means "
                + $"the region is wrong: '{_region}'.",

            429 => $"{Description} is rate limiting or the quota is spent.",

            >= 500 => $"{Description} had a server error ({status}). Worth retrying.",

            _ => $"{Description} answered {status}: {detail}",
        };
    }

    /// <summary>
    /// Wrap PCM in a canonical 44-byte WAV header.
    ///
    /// Written by hand rather than pulled from NAudio because this project already assumes this
    /// exact format everywhere -- 16 kHz, mono, 16-bit -- and a dependency for 44 bytes of
    /// known-constant header is not a trade worth making.
    /// </summary>
    private static byte[] Wave(ReadOnlySpan<byte> pcm)
    {
        const int sampleRate = 16000;
        const short channels = 1;
        const short bits = 16;

        byte[] wav = new byte[44 + pcm.Length];
        using var stream = new MemoryStream(wav);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);                                  // PCM, uncompressed
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8);          // byte rate
        writer.Write((short)(channels * bits / 8));              // block align
        writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);

        return wav;
    }

    /// <summary>Parse a configured engine name into a service, or null if it is not one.</summary>
    public static Service? ServiceFor(string? engine) =>
        engine?.Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "azure" => Service.Azure,
            "google" => Service.Google,
            _ => null,
        };

    /// <summary>The secret-store name a service's key is kept under.</summary>
    public static string SecretNameFor(Service service) =>
        service == Service.Azure ? "speech.azure" : "speech.google";

    /// <summary>The environment variable that outranks the stored key.</summary>
    public static string EnvironmentVariableFor(Service service) =>
        service == Service.Azure ? "AZURE_SPEECH_KEY" : "GOOGLE_SPEECH_KEY";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
    }
}
