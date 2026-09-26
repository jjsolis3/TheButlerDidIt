using ButlerDidIt.Ai;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Parties;

/// <summary>
/// Starts the evening. For a "Surprise me" party this is also when the story's version is
/// dealt: everyone has a character by now, so the killer can be one of tonight's guests.
///
/// Dealing late is invisible to players. Versions of a story differ only in private sheets,
/// clues and the solution, while the lobby shows public bios and costumes, which are shared.
///
/// If no version fits (its killer would be played by the narrator) and the host allowed it,
/// the AI Storyteller writes one that does. The lobby is frozen meanwhile (BeginTailoring);
/// the GenerationWorker then calls <see cref="StartTailoredAsync"/>, or
/// <see cref="StartWithoutTailoringAsync"/> if the remix fails or the host skips it.
/// </summary>
public sealed class PartyDealer(PartyService parties, AppDbContext db, ContentCatalog catalog, AiGateway ai,
    ButlerDidIt.Ai.Media.MediaGateway media, TimeProvider clock)
{
    public async Task<PartySnapshot> StartAsync(Guid partyId, CancellationToken ct = default)
    {
        string? mediaFor = null;
        var result = await parties.ChangeAsync(partyId, async (s, now) =>
        {
            if (!s.Party.DealAtStart) return (GameEngine.Apply(s.State, s.Scenario, new StartGame(now)), s.Scenario);
            if (s.State.Tailoring) throw new GameRuleException("The mystery is still being tailored to your cast.");

            // Give everyone a character first, so the cast is final. Then check the game could
            // start at all (enough players) before spending any time or AI budget on it.
            var assigned = GameEngine.Apply(s.State, s.Scenario, new AutoAssignCharacters(now));
            GameEngine.Apply(assigned, s.Scenario, new StartGame(now));

            var cast = Cast(assigned);
            var story = s.Scenario.VariantOf ?? s.Scenario.Id;
            var deal = await DealAsync(story, s.Party.HostUserId, cast, ct);

            if (!deal.Fits && s.Party.TailorWithAi && await ai.IsConfiguredAsync(AiRole.Storyteller, ct)
                && await PickTargetAsync(story, s.Party.HostUserId, cast, ct) is { } target)
            {
                db.GenerationJobs.Add(new GenerationJobEntity
                {
                    Id = Guid.NewGuid(), Kind = GenerationKind.Remix, HostUserId = s.Party.HostUserId, ThemeSlug = s.Scenario.ThemeSlug,
                    PartyId = s.Party.Id, SourceScenarioId = story, TargetCharacterId = target,
                    Request = "{}", Status = GenerationStatus.Queued, Progress = "Waiting to start…", CreatedAt = now, UpdatedAt = now,
                });
                return (GameEngine.Apply(assigned, s.Scenario, new BeginTailoring(now)), s.Scenario);
            }

            var scenario = await catalog.GetScenarioAsync(db, deal.Id, ct);
            if (scenario.Id != s.Scenario.Id) mediaFor = scenario.Id;
            return (GameEngine.Apply(assigned, scenario, new StartGame(now)), scenario);
        }, ct: ct);

        if (mediaFor is not null) await PrepareMediaAsync(result, mediaFor, ct);
        return result;
    }

    /// <summary>The AI wrote a version for tonight's cast: start the evening with it (unless the host already started without it).</summary>
    public async Task StartTailoredAsync(Guid partyId, string versionId, CancellationToken ct = default)
    {
        var result = await parties.ChangeAsync(partyId, async (s, now) =>
        {
            if (!s.State.Tailoring) return (s.State, s.Scenario);
            var scenario = await catalog.GetScenarioAsync(db, versionId, ct);
            return (GameEngine.Apply(s.State, scenario, new StartGame(now)), scenario);
        }, ct: ct);
        if (result.Scenario.Id == versionId) await PrepareMediaAsync(result, versionId, ct);
    }

    /// <summary>The remix failed or the host skipped it: start with the best hand-written version (the narrator may play the killer).</summary>
    public async Task StartWithoutTailoringAsync(Guid partyId, CancellationToken ct = default)
    {
        string? mediaFor = null;
        var result = await parties.ChangeAsync(partyId, async (s, now) =>
        {
            if (!s.State.Tailoring) return (s.State, s.Scenario);
            var unfrozen = GameEngine.Apply(s.State, s.Scenario, new CancelTailoring(now));
            var deal = await DealAsync(s.Scenario.VariantOf ?? s.Scenario.Id, s.Party.HostUserId, Cast(unfrozen), ct);
            var scenario = await catalog.GetScenarioAsync(db, deal.Id, ct);
            if (scenario.Id != s.Scenario.Id) mediaFor = scenario.Id;
            return (GameEngine.Apply(unfrozen, scenario, new StartGame(now)), scenario);
        }, ct: ct);
        if (mediaFor is not null) await PrepareMediaAsync(result, mediaFor, ct);
    }

    private static HashSet<string> Cast(GameState s) => s.Players.Select(p => p.CharacterId).OfType<string>().ToHashSet();

    private async Task<VersionPicker.Deal> DealAsync(string story, string hostUserId, IReadOnlySet<string> cast, CancellationToken ct)
    {
        var versions = await StoryVersions.ForHostAsync(db, catalog, story, hostUserId, ct);
        var played = await StoryVersions.TimesPlayedAsync(db, hostUserId, versions.Select(v => v.Id).ToList(), ct);
        return VersionPicker.Pick(versions, played, cast, Random.Shared);
    }

    /// <summary>
    /// Who becomes the killer in the AI's version: a character a guest is playing and who may be
    /// a killer. Characters who aren't the killer in any existing version come first, so the
    /// remix is a genuinely new story rather than a variation on one the host may know.
    /// </summary>
    private async Task<string?> PickTargetAsync(string story, string hostUserId, IReadOnlySet<string> cast, CancellationToken ct)
    {
        var original = await catalog.GetScenarioAsync(db, story, ct);
        var killers = (await StoryVersions.ForHostAsync(db, catalog, story, hostUserId, ct)).Select(v => v.KillerId).ToHashSet();
        var eligible = original.Characters.Where(c => c.KillerEligible && cast.Contains(c.Id)).Select(c => c.Id).ToList();
        var fresh = eligible.Where(id => !killers.Contains(id)).ToList();
        var pool = fresh.Count > 0 ? fresh : eligible;
        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }

    /// <summary>Voices and pictures for the dealt version. Anything it shares with the original is already in the cache.</summary>
    private async Task PrepareMediaAsync(PartySnapshot s, string scenarioId, CancellationToken ct)
    {
        var ai = s.State.Ai;
        if (!(ai.NpcQuestions || ai.Hints || ai.Verdicts || ai.Voices)) return; // the host turned the AI off
        if (await media.VoicesConfiguredAsync(ct) || await media.ImagesConfiguredAsync(ct))
            await ButlerDidIt.Api.Media.MediaWorker.EnqueueAsync(db, scenarioId, s.Party.HostUserId, clock, ct);
    }
}

/// <summary>The versions of a story a host can play: the hand-written ones, plus AI versions written for their own parties.</summary>
public static class StoryVersions
{
    public static async Task<List<VersionPicker.Candidate>> ForHostAsync(AppDbContext db, ContentCatalog catalog, string story, string hostUserId, CancellationToken ct)
    {
        var ids = await db.Scenarios.AsNoTracking()
            .Where(x => (x.Id == story || x.VariantOf == story) && x.ArchivedAt == null && (x.OwnerUserId == null || x.OwnerUserId == hostUserId))
            .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        var result = new List<VersionPicker.Candidate>();
        foreach (var id in ids) result.Add(new(id, (await catalog.GetScenarioAsync(db, id, ct)).Solution.MurdererId));
        return result;
    }

    /// <summary>How often this host has played each version. Parties that never left the lobby don't count.</summary>
    public static Task<Dictionary<string, int>> TimesPlayedAsync(AppDbContext db, string hostUserId, IReadOnlyList<string> versionIds, CancellationToken ct) =>
        db.Parties.AsNoTracking()
            .Where(p => p.HostUserId == hostUserId && p.Status != PartyStatus.Lobby && versionIds.Contains(p.ScenarioId))
            .GroupBy(p => p.ScenarioId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
