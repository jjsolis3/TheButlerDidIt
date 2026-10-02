using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Escape;

/// <summary>One place in a room for a picture, a video or a sound.</summary>
/// <param name="StageId">The stage it belongs to, or null for the whole room (the cover, the intro video, the room's sound).</param>
/// <param name="Url">What's there now, or null when it's empty.</param>
/// <param name="Uploaded">Someone uploaded it, rather than the AI painting it.</param>
public sealed record RoomMediaSlot(string Key, MediaKind Kind, string? StageId, string? StageTitle, string? Url, bool Uploaded, long? SizeBytes);

/// <param name="AllowanceBytes">How much this host can upload in all, or null for no limit (the admin).</param>
/// <param name="UsedBytes">How much they've uploaded so far, across all their rooms.</param>
public sealed record RoomMediaLimits(long ImageBytes, long VideoBytes, long AudioBytes, long? AllowanceBytes, long UsedBytes);

/// <param name="CanEdit">The owner or the admin; for a built-in room, only the admin.</param>
public sealed record RoomMediaView(string RoomId, bool CanEdit, bool BuiltIn, IReadOnlyList<RoomMediaSlot> Slots, RoomMediaLimits Limits);

/// <summary>
/// A room's own pictures, videos and sounds (#118, step 2 of #110). Each one is a <see cref="ScenarioMediaEntity"/> row for the
/// room, under an <see cref="EscapeArt"/> key, pointing at a <see cref="MediaAsset"/>: exactly how the AI's pictures
/// are kept. So an uploaded cover shows on the shelf, the TV, the phones and the recap with nothing else to change,
/// and the AI never paints over it (it only paints keys that are empty).
///
/// <list type="bullet">
/// <item>The owner of a room (or the admin) can change its media. Built-in rooms only the admin, and then everyone sees it.</item>
/// <item>Files are checked by their first bytes, never by their name or the type the browser claims. Pictures are
/// re-encoded (dropping hidden details such as where a phone photo was taken), videos and sounds are kept as they are.</item>
/// <item>The body is the file itself, not a form. It's copied to a temporary file as it arrives, so a 100 MB
/// video never sits in memory, and it isn't a request another website could make with the host's cookie.</item>
/// <item>Copies of a room share its files, so a file is deleted only when no room uses it any more.</item>
/// </list>
/// </summary>
public static class EscapeMediaEndpoints
{
    private const long MB = 1024 * 1024;
    public const long MaxImageBytes = MediaApi.MaxPhotoBytes;
    public const long MaxAudioBytes = 20 * MB;

    /// <summary>The longest side of an uploaded picture: sharp on a 1080p TV, and quick to load.</summary>
    private const int PictureSide = 1920;

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
            var limit = LimitFor(slot.Kind, o);
            var declared = http.Request.ContentLength;
            if (declared > limit) return TooBig(slot.Kind, limit);
            var jobId = EscapeMedia.JobId(id);
            var previous = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == jobId && m.Key == key).Select(m => (Guid?)m.AssetId).FirstOrDefaultAsync(ct);
            if (await OverAllowanceAsync(db, user, previous, declared ?? 0, o, ct) is { } full) return full;

            // Kestrel turns away bodies over 30 MB unless an endpoint says otherwise. This one allows its own limit.
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = limit;

            // Copy the upload to a temporary file, counting as it comes: the declared length can't be trusted.
            // DeleteOnClose removes it however this ends.
            await using var file = new FileStream(Path.Combine(Path.GetTempPath(), $"butler-upload-{Guid.NewGuid():N}"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var buffer = new byte[81920];
            long total = 0;
            try
            {
                int read;
                while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > limit) return TooBig(slot.Kind, limit); // a server that doesn't enforce the limit itself (tests)
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                // Kestrel stopped it at the limit: say so in the same words.
                return TooBig(slot.Kind, limit);
            }
            if (total == 0) return Results.Problem("Choose a file to upload.", statusCode: 400);
            if (declared is null && await OverAllowanceAsync(db, user, previous, total, o, ct) is { } over) return over;

            // What the file really is, from its first bytes.
            file.Position = 0;
            var head = new byte[16];
            var headLength = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
            file.Position = 0;

            Guid assetId;
            if (slot.Kind == MediaKind.Image)
            {
                byte[] jpeg;
                try
                {
                    jpeg = PhotoProcessing.ToSafeJpeg(file, maxSide: PictureSide);
                }
                catch (InvalidDataException)
                {
                    return Results.Problem("That doesn't look like a picture. Use a JPEG, PNG or WebP.", statusCode: 400);
                }
                assetId = await media.SaveUploadAsync(MediaKind.Image, new MemoryStream(jpeg, writable: false), jpeg.Length, "image/jpeg", "jpg",
                    partyId: null, user.Id, ct);
            }
            else
            {
                var format = slot.Kind == MediaKind.Video ? MediaFormats.Video(head.AsSpan(0, headLength)) : MediaFormats.Audio(head.AsSpan(0, headLength));
                if (format is null)
                {
                    return Results.Problem(slot.Kind == MediaKind.Video
                        ? "That doesn't look like a video. Use an MP4 (it plays everywhere), a MOV or a WebM."
                        : "That doesn't look like a sound file. Use an MP3, M4A, OGG, WAV or WebM.", statusCode: 400);
                }
                assetId = await media.SaveUploadAsync(slot.Kind, file, total, format.ContentType, format.Extension, partyId: null, user.Id, ct);
            }

            // Point the slot at the new file: insert, or replace what was there, in one statement
            // (the AI could be painting this very picture at the same moment).
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ScenarioMedia" ("ScenarioId", "Key", "AssetId") VALUES ({jobId}, {key}, {assetId})
                ON CONFLICT ("ScenarioId", "Key") DO UPDATE SET "AssetId" = excluded."AssetId"
                """, ct);
            // Then let go of the old file, if it was an upload no other room uses. An AI picture is kept: it's shared and cached.
            if (previous is { } old) await media.DeleteUnusedUploadsAsync([old], ct);
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

            var jobId = EscapeMedia.JobId(id);
            var removed = await db.ScenarioMedia.Where(m => m.ScenarioId == jobId && m.Key == key).Select(m => m.AssetId).ToListAsync(ct);
            await db.ScenarioMedia.Where(m => m.ScenarioId == jobId && m.Key == key).ExecuteDeleteAsync(ct);
            await media.DeleteUnusedUploadsAsync(removed, ct);
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
        var jobId = EscapeMedia.JobId(room.Id);
        var rows = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == jobId)
            .Join(db.MediaAssets, m => m.AssetId, a => a.Id, (m, a) => new { m.Key, a.Id, a.Provider, a.SizeBytes })
            .ToDictionaryAsync(x => x.Key, ct);
        var slots = Slots(room).Select(s =>
        {
            var row = rows.GetValueOrDefault(s.Key);
            return new RoomMediaSlot(s.Key, s.Kind, s.Stage?.Id, s.Stage?.Title, row is null ? null : MediaStore.Url(row.Id),
                Uploaded: row?.Provider == MediaService.Upload, SizeBytes: row?.SizeBytes);
        }).ToList();
        var used = await UsedAsync(db, user, ct);
        return new RoomMediaView(room.Id, canEdit, builtIn, slots,
            new RoomMediaLimits(MaxImageBytes, LimitFor(MediaKind.Video, o), MaxAudioBytes, user.IsAdmin ? null : o.UploadQuotaMb * MB, used));
    }

    private static async Task<long> UsedAsync(AppDbContext db, AppUser user, CancellationToken ct) =>
        await db.MediaAssets.Where(a => a.OwnerUserId == user.Id && a.Provider == MediaService.Upload).SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;

    /// <summary>
    /// Refuses an upload that would take a host past their allowance. The file it replaces doesn't count, when no
    /// other room shares it (that one goes once the new one is in). The admin has no limit.
    /// </summary>
    private static async Task<IResult?> OverAllowanceAsync(AppDbContext db, AppUser user, Guid? replacing, long incoming, MediaOptions o, CancellationToken ct)
    {
        if (user.IsAdmin) return null;
        var used = await UsedAsync(db, user, ct);
        if (replacing is { } old && await db.ScenarioMedia.CountAsync(m => m.AssetId == old, ct) == 1)
            used -= await db.MediaAssets.Where(a => a.Id == old && a.OwnerUserId == user.Id && a.Provider == MediaService.Upload).Select(a => a.SizeBytes).FirstOrDefaultAsync(ct);
        var allowance = o.UploadQuotaMb * MB;
        if (used + incoming <= allowance) return null;
        return Results.Problem($"That would take you past your {o.UploadQuotaMb:N0} MB of uploads (you've used {used / (double)MB:N0} MB). " +
            "Remove a video or sound you no longer need, then try again.", statusCode: 413);
    }

    private static long LimitFor(MediaKind kind, MediaOptions o) => kind switch
    {
        MediaKind.Video => o.MaxVideoMb * MB,
        MediaKind.Audio => MaxAudioBytes,
        _ => MaxImageBytes,
    };

    private static IResult TooBig(MediaKind kind, long limit) => Results.Problem(
        $"That {(kind == MediaKind.Image ? "picture" : kind == MediaKind.Video ? "video" : "sound")} is too big ({limit / MB} MB at most)." +
        (kind == MediaKind.Video ? " A shorter clip, or one saved at 1080p, will fit." : ""),
        statusCode: 413);
}
