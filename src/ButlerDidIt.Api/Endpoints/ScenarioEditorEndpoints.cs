using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record MyMystery(
    string Id, string Title, string ThemeSlug, ScenarioSource Source, ContentRating ContentRating,
    DateTimeOffset UpdatedAt, int TimesPlayed, bool InUse, bool CanEdit);

public sealed record EditableScenario(string Id, ScenarioSource Source, bool CanEdit, JsonElement Document);
public sealed record ScenarioDocumentRequest(JsonElement Document);
public sealed record ValidationResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>
/// "My mysteries" and the scenario editor.
///
/// Who may do what:
/// <list type="bullet">
/// <item>A host sees, edits and deletes the mysteries they own (AI-generated ones, and copies).</item>
/// <item>The admin can do that for everyone's mysteries, and can read and duplicate the hand-written ones.</item>
/// <item>Hand-written mysteries are never edited in place: the seeder reloads them from
/// <c>content/</c> at every start-up and would silently undo the edit. "Duplicate" makes an
/// editable copy instead.</item>
/// </list>
/// </summary>
public static class ScenarioEditorEndpoints
{
    public static void MapScenarioEditorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scenarios").RequireAuthorization(AuthPolicies.Host);

        group.MapGet("/mine", async (ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var rows = await db.Scenarios.AsNoTracking()
                .Where(s => s.ArchivedAt == null && s.VariantOf == null && (s.OwnerUserId == user.Id || (user.IsAdmin && s.Source == ScenarioSource.Handwritten)))
                .OrderBy(s => s.Source).ThenBy(s => s.Title)
                .Select(s => new { s.Id, s.Title, s.ThemeSlug, s.Source, s.ContentRating, s.UpdatedAt, s.OwnerUserId })
                .ToListAsync(ct);
            var ids = rows.Select(r => r.Id).ToList();
            var plays = await db.Parties.AsNoTracking().Where(p => ids.Contains(p.ScenarioId))
                .GroupBy(p => p.ScenarioId)
                .Select(g => new { g.Key, Played = g.Count(p => p.Status == PartyStatus.Finished), Active = g.Count(p => p.Status != PartyStatus.Finished) })
                .ToDictionaryAsync(x => x.Key, ct);
            return Results.Ok(rows.Select(r => new MyMystery(r.Id, r.Title, r.ThemeSlug, r.Source, r.ContentRating, r.UpdatedAt,
                plays.GetValueOrDefault(r.Id)?.Played ?? 0, (plays.GetValueOrDefault(r.Id)?.Active ?? 0) > 0,
                r.Source != ScenarioSource.Handwritten)));
        });

        // The whole mystery, solution included. The editor hides it behind a "Spoilers!" warning.
        group.MapGet("/{id}", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        {
            var (user, row) = await LoadAsync(id, principal, users, db, ct);
            if (row is null || !CanRead(user, row)) return Results.NotFound();
            return Results.Ok(new EditableScenario(row.Id, row.Source, CanEdit(user, row), JsonDocument.Parse(row.Document).RootElement));
        });

        // Live feedback while typing. Nothing is saved.
        group.MapPost("/validate", async (ScenarioDocumentRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var (_, errors) = await CheckAsync(req.Document, db, ct);
            return new ValidationResult(errors.Count == 0, errors);
        });

        group.MapPut("/{id}", async (string id, ScenarioDocumentRequest req, ClaimsPrincipal principal, UserManager<AppUser> users,
            AppDbContext db, ContentCatalog catalog, MediaGateway media, TimeProvider clock, CancellationToken ct) =>
        {
            var (user, row) = await LoadAsync(id, principal, users, db, ct);
            if (row is null || !CanRead(user, row)) return Results.NotFound();
            if (!CanEdit(user, row))
                return Results.Problem("Hand-written mysteries can't be edited in place. Use Duplicate to make your own copy.", statusCode: 403);
            if (await BusyPartyAsync(db, id, ct) is { } code)
                return Results.Problem($"Party {code} is using this mystery right now. Finish it first, or duplicate the mystery and edit the copy.", statusCode: 409);

            var (scenario, errors) = await CheckAsync(req.Document, db, ct);
            if (scenario is null || errors.Count > 0) return Results.BadRequest(new ValidationResult(false, errors));
            if (scenario.Id != id) return Results.BadRequest(new ValidationResult(false, ["The id can't be changed. Duplicate the mystery instead."]));

            var old = GameJson.Deserialize<Scenario>(row.Document);
            row.Title = scenario.Title;
            row.ThemeSlug = scenario.ThemeSlug;
            row.MinPlayers = scenario.MinPlayers;
            row.MaxPlayers = scenario.MaxPlayers;
            row.ContentRating = scenario.ContentRating;
            row.Document = GameJson.Serialize(scenario);
            row.UpdatedAt = clock.GetUtcNow();

            // Voices and pictures whose words changed are now wrong (a voice clip still says the old
            // line). Forget just those; everything else keeps its media. The next preparation job
            // makes the new ones, and anything unchanged is found in the cache for free.
            var stale = await StaleMediaKeysAsync(db, old, scenario, ct);
            await db.ScenarioMedia.Where(m => m.ScenarioId == id && stale.Contains(m.Key)).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            catalog.Invalidate(id);
            if (stale.Count > 0 && (await media.VoicesConfiguredAsync(ct) || await media.ImagesConfiguredAsync(ct)))
                await MediaWorker.EnqueueAsync(db, id, user!.Id, clock, ct);
            return Results.Ok(new ValidationResult(true, []));
        });

        group.MapPost("/{id}/duplicate", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            TimeProvider clock, CancellationToken ct) =>
        {
            var (user, row) = await LoadAsync(id, principal, users, db, ct);
            if (row is null || !CanRead(user, row)) return Results.NotFound();

            var copyId = NewId(row.Title);
            // Rewrite the id and title through JSON so every other field is copied exactly.
            var node = System.Text.Json.Nodes.JsonNode.Parse(row.Document)!;
            node["id"] = copyId;
            node["title"] = $"{row.Title} (copy)";
            var copy = new ScenarioEntity
            {
                Id = copyId, ThemeSlug = row.ThemeSlug, Title = $"{row.Title} (copy)", MinPlayers = row.MinPlayers, MaxPlayers = row.MaxPlayers,
                ContentRating = row.ContentRating, Source = ScenarioSource.Custom, OwnerUserId = user!.Id,
                Document = node.ToJsonString(), UpdatedAt = clock.GetUtcNow(),
            };
            db.Scenarios.Add(copy);
            // The copy starts with the original's voices and pictures (the files are shared, not duplicated).
            var mediaRows = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == id).ToListAsync(ct);
            db.ScenarioMedia.AddRange(mediaRows.Select(m => new ScenarioMediaEntity { ScenarioId = copyId, Key = m.Key, AssetId = m.AssetId }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { Id = copyId });
        });

        group.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            ContentCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            var (user, row) = await LoadAsync(id, principal, users, db, ct);
            if (row is null || !CanRead(user, row)) return Results.NotFound();
            if (!CanEdit(user, row)) return Results.Problem("Hand-written mysteries can't be deleted here; they live in the content folder.", statusCode: 403);
            if (await BusyPartyAsync(db, id, ct) is { } code)
                return Results.Problem($"Party {code} is using this mystery. Finish that party first.", statusCode: 409);

            if (await db.Parties.AnyAsync(p => p.ScenarioId == id, ct))
            {
                // Played before: hide it, but keep it so those parties' recaps still work.
                row.ArchivedAt = clock.GetUtcNow();
            }
            else
            {
                db.Scenarios.Remove(row);
                await db.ScenarioMedia.Where(m => m.ScenarioId == id).ExecuteDeleteAsync(ct);
                await db.MediaJobs.Where(j => j.ScenarioId == id).ExecuteDeleteAsync(ct);
            }
            await db.SaveChangesAsync(ct);
            catalog.Invalidate(id);
            return Results.NoContent();
        });
    }

    private static async Task<(AppUser? User, ScenarioEntity? Row)> LoadAsync(string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        var row = await db.Scenarios.FirstOrDefaultAsync(s => s.Id == id && s.ArchivedAt == null, ct);
        return (user, row);
    }

    // Unknown and forbidden look the same (404), so nobody can probe for other hosts' mysteries.
    private static bool CanRead(AppUser? user, ScenarioEntity row) =>
        user is not null && (row.OwnerUserId == user.Id || user.IsAdmin);

    private static bool CanEdit(AppUser? user, ScenarioEntity row) =>
        CanRead(user, row) && row.Source != ScenarioSource.Handwritten;

    private static Task<string?> BusyPartyAsync(AppDbContext db, string id, CancellationToken ct) =>
        db.Parties.AsNoTracking().Where(p => p.ScenarioId == id && p.Status != PartyStatus.Finished).Select(p => p.Code).FirstOrDefaultAsync(ct);

    /// <summary>Parses and checks a document. Every problem becomes a readable message rather than a crash.</summary>
    private static async Task<(Scenario? Scenario, List<string> Errors)> CheckAsync(JsonElement document, AppDbContext db, CancellationToken ct)
    {
        Scenario scenario;
        try
        {
            scenario = GameJson.Deserialize<Scenario>(document.GetRawText());
            if (scenario is null) return (null, ["The mystery is empty."]);
        }
        catch (JsonException ex)
        {
            // e.g. "The JSON value could not be converted to ... Path: $.clues[2].act"
            return (null, [$"The mystery couldn't be read: {ex.Message}"]);
        }

        List<string> errors;
        try
        {
            errors = ScenarioValidator.Validate(scenario).ToList();
        }
        catch (NullReferenceException)
        {
            // A required section was sent as null (e.g. "solution": null).
            return (null, ["The mystery is missing a required section (setting, victim, accusation or solution)."]);
        }
        if (!await db.Themes.AnyAsync(t => t.Slug == scenario.ThemeSlug, ct)) errors.Add($"Unknown theme '{scenario.ThemeSlug}'.");
        if (string.IsNullOrWhiteSpace(scenario.Title)) errors.Add("Give the mystery a title.");
        return (scenario, errors);
    }

    private static async Task<HashSet<string>> StaleMediaKeysAsync(AppDbContext db, Scenario before, Scenario after, CancellationToken ct)
    {
        var themeRow = await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == after.ThemeSlug, ct);
        if (themeRow is null) return [];
        var theme = GameJson.Deserialize<ThemeDefinition>(themeRow.Document);
        var oldPlan = MediaPlan.For(before, theme, voices: true, images: true).ToDictionary(i => i.Key);
        var newPlan = MediaPlan.For(after, theme, voices: true, images: true).ToDictionary(i => i.Key);
        return oldPlan.Keys.Where(k => !newPlan.TryGetValue(k, out var now) || now != oldPlan[k]).ToHashSet();
    }

    /// <summary>A readable, unique id: "death-at-blackwood-manor-7f3a9c".</summary>
    private static string NewId(string title)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return $"{(slug.Length == 0 ? "mystery" : slug)}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
    }
}
