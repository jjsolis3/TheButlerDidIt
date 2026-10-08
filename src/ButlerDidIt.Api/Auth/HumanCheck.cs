using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Auth;

/// <summary>Cloudflare Turnstile's keys (#103), from the Cloudflare dashboard. Both are needed; without them the check is off.</summary>
public sealed class TurnstileOptions
{
    /// <summary>The public key the sign-up page's widget uses.</summary>
    public string SiteKey { get; set; } = "";

    /// <summary>The private key the server checks each answer with. A secret.</summary>
    public string SecretKey { get; set; } = "";

    public bool Configured => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>
/// Proves a form was filled in by a person, not a script (#103). Used on the sign-up form, which is what bots go for
/// once sign-ups are open: each fake account would get a free trial, and with it the AI budget. Behind an interface,
/// like <see cref="IEmailSender"/>, so tests swap in their own, and another service would be one class.
/// </summary>
public interface IHumanCheck
{
    /// <summary>The key the browser's widget needs, or null when the check is off.</summary>
    string? SiteKey { get; }

    /// <summary>Whether <paramref name="token"/>, the widget's answer, shows a person filled in the form.</summary>
    /// <param name="remoteIp">The visitor's address, which helps the service judge; optional.</param>
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

/// <summary>No check: the keys aren't set. Sign-up works as it always has (rate limits and email confirmation still apply).</summary>
public sealed class NoHumanCheck : IHumanCheck
{
    public string? SiteKey => null;
    public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// Cloudflare Turnstile. The widget on the page gives the browser a one-time token; the server sends it to Cloudflare
/// with the secret key, and Cloudflare says whether a person earned it. A token works once and for 5 minutes.
/// Turnstile usually needs no clicking at all, and unlike older CAPTCHAs it doesn't track people across sites.
/// </summary>
public sealed class TurnstileCheck(IHttpClientFactory http, IOptions<TurnstileOptions> options, ILogger<TurnstileCheck> log) : IHumanCheck
{
    public const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public string? SiteKey => options.Value.SiteKey;

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        // Cloudflare's tokens are at most 2048 characters: anything else is refused without asking.
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        var form = new Dictionary<string, string> { ["secret"] = options.Value.SecretKey, ["response"] = token };
        if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;
        try
        {
            using var client = http.CreateClient(nameof(TurnstileCheck));
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            response.EnsureSuccessStatusCode();
            var answer = JsonSerializer.Deserialize<Answer>(await response.Content.ReadAsStringAsync(ct));
            if (answer is { Success: true }) return true;
            log.LogInformation("Turnstile refused a sign-up: {Errors}", string.Join(", ", answer?.ErrorCodes ?? []));
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Cloudflare can't be reached: refuse, rather than let bots through whenever it's slow. The person can try again.
            log.LogWarning(ex, "Could not reach Turnstile to check a sign-up");
            return false;
        }
    }

    private sealed record Answer(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
