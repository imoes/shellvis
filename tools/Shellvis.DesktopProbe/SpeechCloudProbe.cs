using System.Net;
using System.Text;
using System.Text.Json;

using Shellvis.Core.Voice;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The hosted recognisers, against a stub that speaks their real response shapes.
///
/// <b>Why a stub and not the real services.</b> Both need a paid account and a key, so a suite
/// that called them would fail on every machine that has neither -- for a reason unrelated to
/// this code. What can be checked without them is everything that has actually gone wrong in
/// this project's HTTP clients: what lands in the request, and how an unexpected answer is
/// read. The response bodies below are the documented shapes, including the two that arrive as
/// HTTP 200 while meaning "nothing was said".
///
/// <b>What is deliberately checked on the wire.</b> That the audio is sent as a RIFF file and
/// not raw PCM, that the key travels in a header for Azure rather than in a URL, and that the
/// language reaches the service. Each of those is invisible from the outside: a wrong one
/// produces a plausible failure much later, at someone else's expense.
/// </summary>
internal static class SpeechCloudProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("-- what gets sent, and what comes back --");

        // A real-ish recording: two seconds, above the minimum length and above the noise gate.
        byte[] pcm = Tone(seconds: 2);

        using var stub = new SpeechStub();
        stub.Start();

        failures += await AzureChecks(stub, pcm).ConfigureAwait(false);
        failures += await GoogleChecks(stub, pcm).ConfigureAwait(false);
        failures += KeyChecks();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: both hosted recognisers send what they should and read every documented answer."
            : $"{failures} check(s) failed.");

        Console.WriteLine();
        Console.WriteLine("NOT covered: the real services. They need a paid key, and a suite that");
        Console.WriteLine("required one would fail everywhere for the wrong reason. What is checked");
        Console.WriteLine("is the request and the parsing, which is where the defects live.");

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> AzureChecks(SpeechStub stub, byte[] pcm)
    {
        int failures = 0;

        stub.Reply = ("""{"RecognitionStatus":"Success","DisplayText":"Welche Termine liegen heute an?"}""", 200);

        using var azure = new CloudTranscriber(
            CloudTranscriber.Service.Azure, "test-key", "westeurope")
        {
            EndpointOverride = stub.Url + "azure",
        };

        WhisperResult ok = await azure.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);

        Console.WriteLine($"    azure success -> \"{ok.Text}\"");

        failures += Check("azure reads DisplayText out of a success", ok.Text.StartsWith("Welche", StringComparison.Ordinal));
        failures += Check("and reports no problem", ok.Problem is null);

        failures += Check(
            "the key travels in a header, never in the URL",
            stub.LastKeyHeader == "test-key" && !stub.LastUrl.Contains("test-key", StringComparison.Ordinal));

        failures += Check(
            "the language reaches the service",
            stub.LastUrl.Contains("language=de-DE", StringComparison.Ordinal));

        // The framing check. Azure accepts raw PCM only with a content type spelling out every
        // audio parameter; sending a WAV instead is the choice this code makes, and a
        // regression to raw bytes would fail against the real service and pass any test that
        // only looked at the text.
        failures += Check(
            "the body is a RIFF/WAVE file rather than raw PCM",
            stub.LastBody.Length > 44
            && Encoding.ASCII.GetString(stub.LastBody, 0, 4) == "RIFF"
            && Encoding.ASCII.GetString(stub.LastBody, 8, 4) == "WAVE");

        failures += Check(
            "and the header declares 16 kHz mono 16-bit",
            BitConverter.ToInt32(stub.LastBody, 24) == 16000
            && BitConverter.ToInt16(stub.LastBody, 22) == 1
            && BitConverter.ToInt16(stub.LastBody, 34) == 16);

        // "Nothing was said" arrives as a 200. Treated as an error it would put a red warning
        // in the console every time somebody pressed the key and then did not speak.
        stub.Reply = ("""{"RecognitionStatus":"NoMatch","DisplayText":""}""", 200);
        WhisperResult noMatch = await azure.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);

        failures += Check(
            "NoMatch is silence, not a failure",
            noMatch.Text.Length == 0 && noMatch.Problem is null);

        stub.Reply = ("""{"RecognitionStatus":"InitialSilenceTimeout"}""", 200);
        WhisperResult silence = await azure.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);

        failures += Check(
            "and so is InitialSilenceTimeout",
            silence.Text.Length == 0 && silence.Problem is null);

        // The status codes with different remedies have to be told apart, or the user is sent
        // to a search engine instead of to their key.
        stub.Reply = ("""{"error":"unauthorized"}""", 401);
        WhisperResult unauthorised = await azure.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);

        Console.WriteLine($"    azure 401     -> {unauthorised.Problem}");

        failures += Check(
            "a rejected key says so, and mentions the region",
            unauthorised.Problem?.Contains("key", StringComparison.OrdinalIgnoreCase) == true
            && unauthorised.Problem.Contains("westeurope", StringComparison.Ordinal));

        stub.Reply = ("""{"error":"boom"}""", 500);
        WhisperResult broken = await azure.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);

        failures += Check(
            "a server error suggests retrying rather than blaming the key",
            broken.Problem?.Contains("retry", StringComparison.OrdinalIgnoreCase) == true);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> GoogleChecks(SpeechStub stub, byte[] pcm)
    {
        int failures = 0;

        stub.Reply = ("""
            {"results":[{"alternatives":[{"transcript":"Welche Termine liegen"}]},
                        {"alternatives":[{"transcript":"diese Woche noch an?"}]}]}
            """, 200);

        using var google = new CloudTranscriber(
            CloudTranscriber.Service.Google, "goog-key", string.Empty)
        {
            EndpointOverride = stub.Url + "google",
        };

        WhisperResult ok = await google.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);

        Console.WriteLine($"    google success -> \"{ok.Text}\"");

        // Two results, joined. Google splits a longer utterance and reading only the first is
        // an easy mistake that loses the second half of a sentence.
        failures += Check(
            "google joins every result rather than reading only the first",
            ok.Text.Contains("Termine", StringComparison.Ordinal)
            && ok.Text.Contains("noch an", StringComparison.Ordinal));

        using JsonDocument sent = JsonDocument.Parse(Encoding.UTF8.GetString(stub.LastBody));
        JsonElement config = sent.RootElement.GetProperty("config");

        failures += Check(
            "the request declares LINEAR16 at 16 kHz mono",
            config.GetProperty("encoding").GetString() == "LINEAR16"
            && config.GetProperty("sampleRateHertz").GetInt32() == 16000
            && config.GetProperty("audioChannelCount").GetInt32() == 1);

        failures += Check(
            "the language is passed through",
            config.GetProperty("languageCode").GetString() == "de-DE");

        // Punctuation is off by default at Google, and the result is one long clause in a box a
        // person is about to read.
        failures += Check(
            "automatic punctuation is asked for",
            config.GetProperty("enableAutomaticPunctuation").GetBoolean());

        failures += Check(
            "the audio is base64 in the body, not a file upload",
            sent.RootElement.GetProperty("audio").GetProperty("content").GetString()!.Length > 100);

        // Google says "nothing" by omitting the property entirely rather than sending an empty
        // array, which a naive reader treats as malformed.
        stub.Reply = ("""{}""", 200);
        WhisperResult nothing = await google.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);

        failures += Check(
            "an answer with no results at all is silence, not an error",
            nothing.Text.Length == 0 && nothing.Problem is null);

        Console.WriteLine();
        return failures;
    }

    private static int KeyChecks()
    {
        Console.WriteLine("-- without a key --");
        int failures = 0;

        using var keyless = new CloudTranscriber(
            CloudTranscriber.Service.Azure, string.Empty, "westeurope");

        failures += Check("a transcriber with no key is not loaded", !keyless.IsLoaded);

        WhisperResult result = keyless.TranscribeAsync(new byte[64000], "de-DE")
            .GetAwaiter().GetResult();

        failures += Check(
            "and asking it says what to do rather than throwing",
            result.Problem?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true);

        // The property the UI reads to decide what it prints. A cloud transcriber that claimed
        // to be local would make the application state something untrue about the user's audio.
        failures += Check("a hosted recogniser declares itself remote", keyless.IsRemote);

        using var local = new WhisperRecognizer();
        failures += Check("and the local one declares itself local", !local.IsRemote);

        failures += Check(
            "engine names map to services, and nothing else does",
            CloudTranscriber.ServiceFor("azure") == CloudTranscriber.Service.Azure
            && CloudTranscriber.ServiceFor("google") == CloudTranscriber.Service.Google
            && CloudTranscriber.ServiceFor("auto") is null
            && CloudTranscriber.ServiceFor("whisper") is null
            && CloudTranscriber.ServiceFor(null) is null);

        return failures;
    }

    /// <summary>Two seconds of a tone, loud enough to pass the minimum-length checks.</summary>
    private static byte[] Tone(int seconds)
    {
        byte[] pcm = new byte[16000 * 2 * seconds];

        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short sample = (short)(8000 * Math.Sin(i / 2.0 * 0.05));
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return pcm;
    }

    private static int Check(string what, bool condition)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    /// <summary>A local listener that records the request and answers whatever it is told to.</summary>
    private sealed class SpeechStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int _port = 18099;

        public string Url => $"http://127.0.0.1:{_port}/";

        public (string Body, int Status) Reply { get; set; } = ("{}", 200);

        public byte[] LastBody { get; private set; } = [];

        public string LastUrl { get; private set; } = string.Empty;

        public string? LastKeyHeader { get; private set; }

        public void Start()
        {
            _listener.Prefixes.Add(Url);
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;

                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    LastUrl = context.Request.Url?.ToString() ?? string.Empty;
                    LastKeyHeader = context.Request.Headers["Ocp-Apim-Subscription-Key"];

                    using var buffer = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(buffer).ConfigureAwait(false);
                    LastBody = buffer.ToArray();

                    (string body, int status) = Reply;
                    byte[] payload = Encoding.UTF8.GetBytes(body);

                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = payload.Length;

                    await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                    context.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
            }
        }
    }
}
