using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Escape;

/// <summary>One place in a room for a picture, a video or a sound.</summary>
/// <param name="StageId">The stage it belongs to, or null for the whole room (the cover, the intro video, the room's sound).</param>
/// <param name="Url">What's there now, or null when it's empty.</param>
/// <param name="Uploaded">Someone uploaded it, rather than the AI painting it.</param>
public sealed record RoomMediaSlot(string Key, MediaKind Kind, string? StageId, string? StageTitle, string? Url, bool Uploaded, long? SizeBytes);

/// <param name="CanEdit">The owner or the admin; for a built-in room, only the admin.</param>
public sealed record RoomMediaView(string RoomId, bool CanEdit, bool BuiltIn, IReadOnlyList<RoomMediaSlot> Slots, MediaLimits Limits);

/// <summary>
/// A room's own pictures, videos and sounds (#118, step 2 of #110). Each one is a <see cref="ScenarioMediaEntity"/> row for the
/// room, under an <see cref="EscapeArt"/> key, pointing at a <see cref="MediaAsset"/>: exactly how the AI's pictures
/// are kept. So an uploaded cover shows on the shelf, the TV, the phones and the recap with nothing else to change,
/// and the AI never paints over it (it only paints keys that are empty).
///
/// The owner of a room (or the admin) can change its media. Built-in rooms only the admin, and then everyone sees it.
/// How a file is received, checked and stored is <see cref="MediaUploads"/>, shared with the mysteries.
/// </summary>
public static class EscapeMediaEndpoints
{
    /// <summary>Every place in the room for a picture, a video or a sound: three for the room, three for each stage.</summary>
    public static IReadOnlyList<(string Key, MediaKind Kind, EscapeStage? Stage)> Slots(EscapeRoom room) =>
    [
        (EscapeArt.Cover, MediaKind.Image, null),
        (EscapeArt.IntroVideo, MediaKind.Video, null),
        (EscapeArt.Ambience, MediaKind.Audio, null),
        .. room.Stages.SelectMany(s => new (string, MediaKind, EscapeStage?)[]
        {
            (EscapeArt.Stage(s.Id), MediaKind.Image, s),
            (EscapeArt.StageVideo(s.Id), MediaKind.Video, s),
            (EscapeArt.StageAmbience(s.Id), MediaKind.Audio, s),
        }),
    ];

    public static void MapEscapeMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/escape-rooms/{id}/media").RequireAuthorization(AuthPolicies.Host);

        group.MapGet("", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, EscapeCatalog catalog,
            IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (await FindAsync(db, catalog, user, id, ct) is not { } found) return Results.NotFound();
            return Results.Ok(await ViewAsync(db, user, found.Room, found.CanEdit, found.BuiltIn, options.Value, ct));
        });

        // The body is the file. The browser's XMLHttpRequest sends a File like this, and reports upload progress.
        group.MapPost("/{key}", async (string id, string key, HttpContext http, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            EscapeCatalog catalog, MediaService media, PartyRuntime runtime, IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (await EditableAsync(db, catalog, user, id, ct) is not { } found) return Results.NotFound();
            if (found.Refusal is { } refusal) return refusal;
            var slot = Slots(found.Room).FirstOrDefault(s => s.Key == key);
            if (slot.Key is null) return Results.NotFound();

            var o = options.Value;
            if (await MediaUploads.ReceiveAsync(http, db, media, user, o, EscapeMedia.JobId(id), key, slot.Kind, ct) is { } refused) return refused;
            await ForgetClipAsync(db, id, slot.Stage, key, ct);
            await ChangedAsync(catalog, runtime, id, ct);
            return Results.Ok(await ViewAsync(db, user, found.Room, canEdit: true, found.BuiltIn, o, ct));
        });

        // Empties a slot. A picture's place is painted by the AI again on the next game with the AI on.
        group.MapDelete("/{key}", async (string id, string key, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            EscapeCatalog catalog, MediaService media, PartyRuntime runtime, IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (await EditableAsync(db, catalog, user, id, ct) is not { } found) return Results.NotFound();
            if (found.Refusal is { } refusal) return refusal;
            if (!Slots(found.Room).Any(s => s.Key == key)) return Results.NotFound();

            await MediaUploads.RemoveAsync(db, media, EscapeMedia.JobId(id), key, ct);
            await ForgetClipAsync(db, id, Slots(found.Room).First(s => s.Key == key).Stage, key, ct);
            await ChangedAsync(catalog, runtime, id, ct);
            return Results.Ok(await ViewAsync(db, user, found.Room, canEdit: true, found.BuiltIn, options.Value, ct));
        });
    }

    /// <summary>
    /// Removes a room's media rows (the room is being deleted) and then every upload no other room still uses.
    /// The caller deletes the room itself.
    /// </summary>
    public static async Task ForgetRoomAsync(AppDbContext db, MediaService media, IReadOnlyCollection<string> roomIds, CancellationToken ct)
    {
        var jobIds = roomIds.Select(EscapeMedia.JobId).ToList();
        var assets = await db.ScenarioMedia.Where(m => jobIds.Contains(m.ScenarioId)).Select(m => m.AssetId).ToListAsync(ct);
        await db.ScenarioMedia.Where(m => jobIds.Contains(m.ScenarioId)).ExecuteDeleteAsync(ct);
        await media.DeleteUnusedUploadsAsync(assets, ct);
    }

    /// <summary>
    /// A stage's picture or video changed (uploaded or removed), so the AI's clip of it goes (#110): it shows the old
    /// picture, or the host's own video now tells the stage. The next preparation with the Filmmaker role films the
    /// stage's picture again if it needs a clip. The clip's file stays: generated media is shared.
    /// </summary>
    private static async Task ForgetClipAsync(AppDbContext db, string roomId, EscapeStage? stage, string key, CancellationToken ct)
    {
        if (stage is null || (key != EscapeArt.Stage(stage.Id) && key != EscapeArt.StageVideo(stage.Id))) return;
        var (jobId, clip) = (EscapeMedia.JobId(roomId), EscapeArt.StageFilm(stage.Id));
        await db.ScenarioMedia.Where(m => m.ScenarioId == jobId && m.Key == clip).ExecuteDeleteAsync(ct);
    }

    /// <summary>New media for a room: forget the cached copy, and show it on every screen of every party playing it.</summary>
    private static async Task ChangedAsync(EscapeCatalog catalog, PartyRuntime runtime, string roomId, CancellationToken ct)
    {
        catalog.ForgetArt(roomId);
        await runtime.RefreshAsync(roomId, ct);
    }

    private sealed record Found(EscapeRoom Room, bool CanEdit, bool BuiltIn);

    /// <summary>A room this host may see the media of: any built-in room, or a room of theirs. Null for anyone else's (404, like the editor).</summary>
    private static async Task<Found?> FindAsync(AppDbContext db, EscapeCatalog catalog, AppUser user, string id, CancellationToken ct)
    {
        if (catalog.Find(id) is { } builtIn) return new Found(builtIn, CanEdit: user.IsAdmin, BuiltIn: true);
        var row = await db.EscapeRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        // A room the admin shared can be looked at by every host, and changed only by whoever can edit it.
        if (row is null || !EscapeEditorEndpoints.CanRead(user, row)) return null;
        return new Found(GameJson.Deserialize<EscapeRoom>(row.Document), CanEdit: EscapeEditorEndpoints.CanEdit(user, row), BuiltIn: false);
    }

    /// <summary>As <see cref="FindAsync"/>, with a reason to refuse when this host can see the room but not change it.</summary>
    private static async Task<(EscapeRoom Room, bool BuiltIn, IResult? Refusal)?> EditableAsync(AppDbContext db, EscapeCatalog catalog, AppUser user, string id, CancellationToken ct)
    {
        if (await FindAsync(db, catalog, user, id, ct) is not { } found) return null;
        var refusal = found.CanEdit ? null
            : Results.Problem(found.BuiltIn
                ? "Only the admin can change a built-in room's pictures, video and sound. Make your own copy to add yours."
                : "Only the admin can change this room's pictures, video and sound. Make your own copy to add yours.", statusCode: 403);
        return (found.Room, found.BuiltIn, refusal);
    }

    private static async Task<RoomMediaView> ViewAsync(AppDbContext db, AppUser user, EscapeRoom room, bool canEdit, bool builtIn, MediaOptions o, CancellationToken ct)
    {
        var places = await MediaUploads.PlacesAsync(db, EscapeMedia.JobId(room.Id), ct);
        var slots = Slots(room).Select(s =>
        {
            var found = places.TryGetValue(s.Key, out var p);
            return new RoomMediaSlot(s.Key, s.Kind, s.Stage?.Id, s.Stage?.Title, found ? MediaStore.Url(p.AssetId) : null,
                Uploaded: found && p.Uploaded, SizeBytes: found ? p.SizeBytes : null);
        }).ToList();
        return new RoomMediaView(room.Id, canEdit, builtIn, slots, await MediaUploads.LimitsAsync(db, user, o, ct));
    }
}
