using System.Diagnostics;
using ButlerDidIt.Game.Scenarios;
using Microsoft.Extensions.Logging;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// The door for voice and image generation, like <see cref="AiGateway"/> is for
/// chat: it looks up the provider for the role, checks the budget, makes the
/// call and records usage. Speech is logged as characters in the input-token
/// column; images as one request each.
/// </summary>
public sealed class MediaGateway(
    IAiSettingsSource settings,
    IMediaClientFactory factory,
    IAiUsageSink usage,
    IAiBudget budget,
    TimeProvider clock,
    ILogger<MediaGateway> log)
{
    public Task<bool> VoicesConfiguredAsync(CancellationToken ct = default) => Configured(AiRole.Voice, ct);
    public Task<bool> ImagesConfiguredAsync(CancellationToken ct = default) => Configured(AiRole.Illustrator, ct);
    public Task<bool> FilmsConfiguredAsync(CancellationToken ct = default) => Configured(AiRole.Filmmaker, ct);

    private async Task<bool> Configured(AiRole role, CancellationToken ct) => await settings.GetRoleAsync(role, ct) is not null;

    /// <summary>The provider/model used for a role, so the cache can tell apart results from different models.</summary>
    public async Task<string?> DescribeAsync(AiRole role, CancellationToken ct) =>
        await settings.GetRoleAsync(role, ct) is { } r ? $"{r.Provider.Kind}:{r.Model}" : null;

    public Task<MediaFile> SpeakAsync(string text, string voice, AiCallContext context, CancellationToken ct) =>
        CallAsync(AiRole.Voice, context, text.Length,
            (config) => factory.CreateSpeech(config.Provider, config.Model).SpeakAsync(text, voice, ct), ct);

    public Task<MediaFile> PaintAsync(string prompt, ImageShape shape, AiCallContext context, CancellationToken ct) =>
        CallAsync(AiRole.Illustrator, context, 0,
            (config) => factory.CreateImages(config.Provider, config.Model).PaintAsync(prompt, shape, ct), ct);

    /// <summary>A clip brought to life from <paramref name="picture"/> (#110). Logged as one request, like a picture: priced per clip.</summary>
    public Task<MediaFile> FilmAsync(MediaFile picture, string prompt, AiCallContext context, CancellationToken ct) =>
        CallAsync(AiRole.Filmmaker, context, 0,
            (config) => factory.CreateVideos(config.Provider, config.Model).AnimateAsync(picture, prompt, ct), ct);

    private async Task<MediaFile> CallAsync(AiRole role, AiCallContext context, long units, Func<AiRoleSettings, Task<MediaFile>> call, CancellationToken ct)
    {
        var config = await settings.GetRoleAsync(role, ct)
            ?? throw new AiUnavailableException($"No AI is set up for the {role} role yet.");
        await budget.EnsureWithinBudgetAsync(context.HostUserId, ct);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var file = await call(config);
            await Record(config, context, units, stopwatch, true, null, ct);
            return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiException)
        {
            log.LogWarning(ex, "{Role} call failed using {Provider}/{Model}", role, config.Provider.Name, config.Model);
            await Record(config, context, units, stopwatch, false, ex.Message, ct);
            throw new AiCallFailedException($"The {role.ToString().ToLowerInvariant()} service ({config.Provider.Name}) failed: {Trim(ex.Message)}", ex);
        }
    }

    private async Task Record(AiRoleSettings config, AiCallContext context, long units, Stopwatch sw, bool success, string? error, CancellationToken ct)
    {
        try
        {
            await usage.RecordAsync(new AiUsageRecord(clock.GetUtcNow(), config.Role, config.Provider.Kind, config.Provider.Name, config.Model,
                units, 0, sw.ElapsedMilliseconds, success, error is null ? null : Trim(error), context), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to record media usage");
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

/// <summary>One file the media pipeline should create for a scenario.</summary>
/// <param name="From">For a clip (the Filmmaker role): the key of the picture it brings to life.</param>
public sealed record MediaItem(string Key, AiRole Role, string Text, string Voice, ImageShape Shape, string? From = null);

/// <summary>
/// Works out every voice clip and picture a scenario needs, with the prompt for
/// each. It's a pure function, so it's easy to test and the same scenario always
/// produces the same list (and hits the cache).
/// </summary>
public static class MediaPlan
{
    public static IReadOnlyList<MediaItem> For(Scenario s, ThemeDefinition theme, bool voices, bool images)
    {
        var items = new List<MediaItem>();
        var style = string.IsNullOrWhiteSpace(theme.ArtStyle) ? "atmospheric illustration" : theme.ArtStyle;
        const string noText = "No text, no letters, no watermark.";

        if (images)
        {
            foreach (var c in s.Characters)
            {
                items.Add(new MediaItem(MediaOverlay.Portrait(c.Id), AiRole.Illustrator,
                    $"Head-and-shoulders portrait of {c.Name}, {c.Title} ({c.Pronouns}), a character in a murder mystery set in {s.Setting.Era}, {s.Setting.Place}. " +
                    $"{c.PublicBio} Wearing: {c.CostumeTips} Style: {style}. Dramatic lighting, looking at the viewer. {noText}",
                    "", ImageShape.Portrait));
            }
            items.Add(new MediaItem(MediaOverlay.Victim, AiRole.Illustrator,
                $"Portrait of {s.Victim.Name}, the murder victim, as they looked in life. {s.Victim.Description} Setting: {s.Setting.Era}. Style: {style}. {noText}",
                "", ImageShape.Portrait));
            items.Add(new MediaItem(MediaOverlay.Setting, AiRole.Illustrator,
                $"Establishing shot of {s.Setting.Place}, {s.Setting.Era}. {s.Setting.Description} Style: {style}. Cinematic wide shot, moody. {noText}",
                "", ImageShape.Landscape));

            // Clue cards get a still life of the object in the title, and nothing else from the clue:
            // its text (and whether it's a red herring) can differ between versions and can give the game away.
            foreach (var clue in s.Clues.Where(c => string.IsNullOrEmpty(c.Image)))
            {
                items.Add(new MediaItem(MediaOverlay.ClueImage(clue.Id, clue.Title), AiRole.Illustrator,
                    $"An evidence photograph for a murder mystery set in {s.Setting.Era}: {clue.Title}. A close-up still life of the object on a dark surface, no people. " +
                    $"Style: {style}. {noText}", "", ImageShape.Landscape));
            }
        }

        foreach (var (section, cues) in Sections(s))
        {
            for (var i = 0; i < cues.Count; i++)
            {
                var cue = cues[i];
                if (!string.IsNullOrEmpty(cue.Src) || string.IsNullOrWhiteSpace(cue.Text)) continue;
                var key = MediaOverlay.Cue(section, i);
                if (images && cue.Type == CueType.Image)
                    items.Add(new MediaItem(key, AiRole.Illustrator,
                        $"Scene from a murder mystery set in {s.Setting.Era}, {s.Setting.Place}: {cue.Text} Style: {style}. Cinematic. {noText}", "", ImageShape.Landscape));
                else if (voices && cue.Type == CueType.Narration)
                    items.Add(new MediaItem(key, AiRole.Voice, cue.Text!, VoiceCasting.Narrator, default));
                else if (voices && cue.Type == CueType.Line && cue.Speaker is not null && s.FindCharacter(cue.Speaker) is { } speaker)
                    items.Add(new MediaItem(key, AiRole.Voice, cue.Text!, VoiceCasting.For(speaker.Id, speaker.Voice), default));
            }
        }

        // Any character may end up as an NPC, so voice every scripted line.
        if (voices)
        {
            foreach (var c in s.Characters)
            {
                foreach (var (actId, lines) in c.Private.Lines)
                {
                    for (var i = 0; i < lines.Count; i++)
                        items.Add(new MediaItem(MediaOverlay.Line(c.Id, actId, i), AiRole.Voice, lines[i], VoiceCasting.For(c.Id, c.Voice), default));
                }
            }
        }
        return items;
    }

    private static IEnumerable<(string Section, List<Cue> Cues)> Sections(Scenario s)
    {
        yield return ("prologue", s.Prologue);
        foreach (var act in s.Acts) yield return (act.Id, act.Cues);
        yield return ("finale", s.Finale);
    }
}
