using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// Voices from ElevenLabs (#33), for more expressive characters than the other providers' voices.
///
/// One HTTPS call per clip: <c>POST /v1/text-to-speech/{voice_id}</c> with the text and the model
/// (e.g. eleven_multilingual_v2), which answers with an MP3. The voice is picked from the account's own
/// list (<see cref="ElevenLabsVoices"/>), so it keeps working whichever voices the account has.
/// </summary>
internal sealed class ElevenLabsSpeech(HttpClient http, string apiKey, string model, string? baseUrl) : ITextToSpeech
{
    public const string Endpoint = "https://api.elevenlabs.io/";

    private string Root => ElevenLabsVoices.Root(baseUrl);

    public async Task<MediaFile> SpeakAsync(string text, string voice, CancellationToken ct)
    {
        var voiceId = ElevenLabsVoices.Pick(await ElevenLabsVoices.ListAsync(http, Root, apiKey, ct), voice);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=mp3_44100_128")
        {
            Content = JsonContent.Create(new JsonObject { ["text"] = text, ["model_id"] = model }),
        };
        request.Headers.Add("xi-api-key", apiKey);
        request.Headers.Accept.ParseAdd("audio/mpeg");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new AiCallFailedException($"ElevenLabs refused the voice clip ({(int)response.StatusCode}): {ElevenLabsVoices.ErrorMessage(await response.Content.ReadAsStringAsync(ct))}");
        return new MediaFile(await response.Content.ReadAsByteArrayAsync(ct), "audio/mpeg", "mp3");
    }
}

/// <summary>
/// Casts ElevenLabs voices for the app's six voice names (<see cref="VoiceCasting"/>), so a character keeps one
/// voice and deep voices stay deep.
///
/// Voice ids differ between accounts (a voice can be added, removed or cloned), so instead of a fixed table the
/// account's list (<c>GET /v1/voices</c>) is read once an hour, and each name is matched against ElevenLabs'
/// default voices by name, then by the voice's gender label. A key that may not read the list (keys can be
/// limited to text-to-speech) falls back to default voices whose ids ElevenLabs documents.
/// </summary>
public static class ElevenLabsVoices
{
    public sealed record Voice(string Id, string Name, string? Gender);

    /// <summary>ElevenLabs' default voices, best match first, for each of the app's voice names.</summary>
    private static readonly Dictionary<string, string[]> Preferred = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fable"] = ["George", "Daniel", "Brian"],        // the narrator: warm, storytelling
        ["onyx"] = ["Daniel", "Bill", "Brian", "Adam"],    // deep
        ["echo"] = ["Callum", "Liam", "Charlie", "Eric"],  // low, lively
        ["nova"] = ["Sarah", "Jessica", "Laura"],          // bright
        ["shimmer"] = ["Lily", "Alice", "Matilda"],        // light
        ["alloy"] = ["Will", "River", "Aria", "Charlotte"], // upbeat
    };

    /// <summary>Voices ElevenLabs documents by id, for a key that can't read the account's list.</summary>
    private static readonly Dictionary<string, string> Documented = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fable"] = "JBFqnCBsd6RMkjVDRZzb", // George
        ["onyx"] = "onwK4e9ZLuTAKqWW03F9",  // Daniel
        ["echo"] = "JBFqnCBsd6RMkjVDRZzb",  // George
        ["nova"] = "EXAVITQu4vr4xnSDxMaL",  // Sarah
        ["shimmer"] = "Xb7hH8MSUJpSbSDYk0k2", // Alice
        ["alloy"] = "EXAVITQu4vr4xnSDxMaL", // Sarah
    };

    private static readonly HashSet<string> Low = new(["fable", "onyx", "echo"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The voice id for one of the app's voice names. A name that isn't one of the app's (an id the admin
    /// chose) is used as it is.
    /// </summary>
    public static string Pick(IReadOnlyList<Voice> voices, string voice)
    {
        if (!Preferred.TryGetValue(voice, out var names)) return voice;
        foreach (var name in names)
            if (voices.FirstOrDefault(v => Named(v, name)) is { } match) return match.Id;
        // None of the defaults: the account's own voices, by gender, the same one every time.
        var gender = Low.Contains(voice) ? "male" : "female";
        var pool = voices.Where(v => string.Equals(v.Gender, gender, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0) pool = voices.ToList();
        return pool.Count == 0 ? Documented[voice] : pool[(int)((uint)voice.Aggregate(7, (a, c) => a * 31 + c) % (uint)pool.Count)].Id;
    }

    /// <summary>"George" matches "George" and "George - warm storyteller", never "Georgette".</summary>
    private static bool Named(Voice v, string name) =>
        v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        v.Name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase);

    internal static string Root(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? ElevenLabsSpeech.Endpoint : baseUrl.TrimEnd('/') + "/";

    // One list per account (by a hash of its key, so the key itself isn't kept as a dictionary key), for an hour.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<Voice> Voices)> Lists = new();
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    /// <summary>The account's voices; empty when the key may not list them (the caller falls back to <see cref="Documented"/>).</summary>
    public static async Task<IReadOnlyList<Voice>> ListAsync(HttpClient http, string root, string apiKey, CancellationToken ct)
    {
        var cacheKey = root + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        if (Lists.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < KeepFor) return cached.Voices;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}v1/voices");
        request.Headers.Add("xi-api-key", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return []; // not cached: a fixed key or permission is picked up next time

        var voices = new List<Voice>();
        foreach (var v in JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["voices"]?.AsArray() ?? [])
        {
            if (v?["voice_id"]?.GetValue<string>() is not { Length: > 0 } id) continue;
            voices.Add(new Voice(id, v["name"]?.GetValue<string>() ?? "", v["labels"]?["gender"]?.GetValue<string>()));
        }
        Lists[cacheKey] = (DateTimeOffset.UtcNow, voices);
        return voices;
    }

    /// <summary>ElevenLabs explains errors as <c>{"detail": {"message": …}}</c>, <c>{"detail": "…"}</c> or a list of field errors.</summary>
    internal static string ErrorMessage(string body)
    {
        try
        {
            var detail = JsonNode.Parse(body)?["detail"];
            return detail switch
            {
                JsonObject o => o["message"]?.GetValue<string>() ?? o.ToJsonString(),
                JsonArray a => a[0]?["msg"]?.GetValue<string>() ?? a.ToJsonString(),
                JsonValue s => s.GetValue<string>(),
                _ => Short(body),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Short(body);
        }
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200];
}
