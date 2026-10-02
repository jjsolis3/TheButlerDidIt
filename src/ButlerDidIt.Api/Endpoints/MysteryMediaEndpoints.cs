using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

/// <summary>One place in a mystery for a picture, a video or a sound.</summary>
/// <param name="Section">"mystery" (the whole evening), "cast", "acts" or "clues".</param>
/// <param name="ItemId">The character, act or clue it belongs to; null for the whole mystery.</param>
/// <param name="ItemTitle">That character's name, act's title or clue's title (or the victim's name).</param>
/// <param name="Url">What's there now, or null when it's empty.</param>
/// <param name="Uploaded">Someone uploaded it, rather than the AI painting it.</param>
public sealed record MysteryMediaSlot(string Key, MediaKind Kind, string Section, string? ItemId, string? ItemTitle, string? Url, bool Uploaded, long? SizeBytes);

/// <param name="BuiltIn">A hand-written mystery: only the admin can change its media, and every host's games get it.</param>
/// <param name="HasVersions">It has versions with other killers, which all play these files.</param>
public sealed record MysteryMediaView(string ScenarioId, bool CanEdit, bool BuiltIn, bool HasVersions, IReadOnlyList<MysteryMediaSlot> Slots, MediaLimits Limits);

/// <summary>
/// A mystery's own pictures, videos and music (#124, step 2 of #110, after the escape rooms' #118): a cover, the victim, each
/// character's portrait and each clue's picture; a video for the opening scene, each act and the finale (which
/// replaces that scene's narration and pictures); and background music for the evening and for each act.
///
/// They're stored like the AI's media (see <see cref="MediaUploads"/>), under the original mystery's id, and
/// <see cref="ContentCatalog.GetScenarioAsync"/> plays them in every version of it. Only the host who owns a mystery
/// can change its media, or the admin; hand-written mysteries only the admin, like the editor.
/// </summary>
public static class MysteryMediaEndpoints
{
    public sealed record Place(string Key, MediaKind Kind, string Section, string? ItemId, string? ItemTitle);

    /// <summary>Every place in the mystery for a picture, a video or a sound.</summary>
    public static IReadOnlyList<Place> Slots(Scenario s) =>
    [
        new(MediaOverlay.Setting, MediaKind.Image, "mystery", null, s.Setting.Place),
        new(MediaOverlay.Victim, MediaKind.Image, "mystery", null, s.Victim.Name),
        new(MediaOverlay.Video(MediaOverlay.Prologue), MediaKind.Video, "mystery", null, null),
        new(MediaOverlay.Music, MediaKind.Audio, "mystery", null, null),
        new(MediaOverlay.Video(MediaOverlay.Finale), MediaKind.Video, "mystery", null, null),
        .. s.Characters.Select(c => new Place(MediaOverlay.Portrait(c.Id), MediaKind.Image, "cast", c.Id, c.Name)),
        .. s.Acts.SelectMany(a => new Place[]
        {
            new(MediaOverlay.Video(a.Id), MediaKind.Video, "acts", a.Id, a.Title),
            new(MediaOverlay.ActMusic(a.Id), MediaKind.Audio, "acts", a.Id, a.Title),
        }),
        // A clue's picture belongs to its title (see MediaOverlay.ClueImage): renaming the clue needs a new one.
        .. s.Clues.Select(c => new Place(MediaOverlay.ClueImage(c.Id, c.Title), MediaKind.Image, "clues", c.Id, c.Title)),
    ];

    public static void MapMysteryMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scenarios/{id}/media").RequireAuthorization(AuthPolicies.Host);

        group.MapGet("", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            if (await FindAsync(id, principal, users, db, ct) is not { } found) return Results.NotFound();
            return Results.Ok(await ViewAsync(db, found.User, found.Row, options.Value, ct));
        });

        // The body is the file, as for the escape rooms. Keys have slashes ("portrait/finch"), hence the catch-all.
        group.MapPost("/{**key}", async (string id, string key, HttpContext http, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            MediaService media, ContentCatalog catalog, PartyRuntime runtime, IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            if (await FindAsync(id, principal, users, db, ct) is not { } found) return Results.NotFound();
            if (Slots(GameJson.Deserialize<Scenario>(found.Row.Document)).FirstOrDefault(s => s.Key == key) is not { } slot) return Results.NotFound();
            if (await MediaUploads.ReceiveAsync(http, db, media, found.User, options.Value, id, key, slot.Kind, ct) is { } refused) return refused;
            await ChangedAsync(db, catalog, runtime, id, ct);
            return Results.Ok(await ViewAsync(db, found.User, found.Row, options.Value, ct));
        });

        // Empties a place. A picture is painted by the AI again on the next game with the AI on.
        group.MapDelete("/{**key}", async (string id, string key, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            MediaService media, ContentCatalog catalog, PartyRuntime runtime, IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            if (await FindAsync(id, principal, users, db, ct) is not { } found) return Results.NotFound();
            if (!Slots(GameJson.Deserialize<Scenario>(found.Row.Document)).Any(s => s.Key == key)) return Results.NotFound();
            await MediaUploads.RemoveAsync(db, media, id, key, ct);
            await ChangedAsync(db, catalog, runtime, id, ct);
            return Results.Ok(await ViewAsync(db, found.User, found.Row, options.Value, ct));
        });
    }

    private sealed record Found(AppUser User, ScenarioEntity Row);

    /// <summary>
    /// A mystery this host may change the media of: their own, or (for the admin) any, hand-written ones included.
    /// Only an original: its versions play its media. Anyone else's looks like no mystery at all (404).
    /// </summary>
    private static async Task<Found?> FindAsync(string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return null;
        var row = await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.ArchivedAt == null && s.VariantOf == null, ct);
        return row is not null && ScenarioEditorEndpoints.CanRead(user, row) ? new Found(user, row) : null;
    }

    /// <summary>The mystery and every version of it play the new media: forget them all, and update the screens of parties playing them.</summary>
    private static async Task ChangedAsync(AppDbContext db, ContentCatalog catalog, PartyRuntime runtime, string id, CancellationToken ct)
    {
        catalog.InvalidateFamily(id);
        var family = await db.Scenarios.AsNoTracking().Where(s => s.Id == id || s.VariantOf == id).Select(s => s.Id).ToListAsync(ct);
        foreach (var scenarioId in family) await runtime.RefreshAsync(scenarioId, ct);
    }

    private static async Task<MysteryMediaView> ViewAsync(AppDbContext db, AppUser user, ScenarioEntity row, MediaOptions o, CancellationToken ct)
    {
        var places = await MediaUploads.PlacesAsync(db, row.Id, ct);
        var slots = Slots(GameJson.Deserialize<Scenario>(row.Document)).Select(s =>
        {
            var found = places.TryGetValue(s.Key, out var p);
            return new MysteryMediaSlot(s.Key, s.Kind, s.Section, s.ItemId, s.ItemTitle, found ? MediaStore.Url(p.AssetId) : null,
                Uploaded: found && p.Uploaded, SizeBytes: found ? p.SizeBytes : null);
        }).ToList();
        var hasVersions = await db.Scenarios.AnyAsync(s => s.VariantOf == row.Id && s.ArchivedAt == null, ct);
        return new MysteryMediaView(row.Id, CanEdit: true, BuiltIn: row.Source == ScenarioSource.Handwritten, hasVersions, slots,
            await MediaUploads.LimitsAsync(db, user, o, ct));
    }
}
