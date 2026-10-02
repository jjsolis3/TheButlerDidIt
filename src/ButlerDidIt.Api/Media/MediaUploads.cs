using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Media;

/// <param name="AllowanceBytes">How much this host can upload in all, or null for no limit (the admin).</param>
/// <param name="UsedBytes">How much they've uploaded so far, across all their rooms and mysteries.</param>
public sealed record MediaLimits(long ImageBytes, long VideoBytes, long AudioBytes, long? AllowanceBytes, long UsedBytes);

/// <summary>
/// A host's own pictures, videos and sounds, for both games (#118 for escape rooms, then mysteries). Each is a
/// <see cref="ScenarioMediaEntity"/> row under a key, pointing at a <see cref="MediaAsset"/>: exactly how the AI's
/// media is kept, so everything that shows the AI's pictures shows the host's too.
///
/// <list type="bullet">
/// <item>Files are checked by their first bytes, never by their name or the type the browser claims. Pictures are
/// re-encoded (dropping hidden details such as where a phone photo was taken), videos and sounds are kept as they are.</item>
/// <item>The body is the file itself, not a form. It's copied to a temporary file as it arrives, so a 100 MB
/// video never sits in memory, and it isn't a request another website could make with the host's cookie.</item>
/// <item>Copies share files, so a file is deleted only when nothing uses it any more.</item>
/// <item>Each host has an allowance across both games; the admin has none.</item>
/// </list>
/// </summary>
public static class MediaUploads
{
    private const long MB = 1024 * 1024;
    public const long MaxImageBytes = MediaApi.MaxPhotoBytes;
    public const long MaxAudioBytes = 20 * MB;

    /// <summary>The longest side of an uploaded picture: sharp on a 1080p TV, and quick to load.</summary>
    private const int PictureSide = 1920;

    /// <summary>
    /// Receives the request body into one place (<paramref name="ownerId"/>'s <paramref name="key"/>), replacing what
    /// was there and letting go of the old file if nothing else uses it. Null when it's done, or the reason it was refused.
    /// </summary>
    public static async Task<IResult?> ReceiveAsync(HttpContext http, AppDbContext db, MediaService media, AppUser user, MediaOptions o,
        string ownerId, string key, MediaKind kind, CancellationToken ct)
    {
        var limit = LimitFor(kind, o);
        var declared = http.Request.ContentLength;
        if (declared > limit) return TooBig(kind, limit);
        var previous = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == ownerId && m.Key == key).Select(m => (Guid?)m.AssetId).FirstOrDefaultAsync(ct);
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
                if (total > limit) return TooBig(kind, limit); // a server that doesn't enforce the limit itself (tests)
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel stopped it at the limit: say so in the same words.
            return TooBig(kind, limit);
        }
        if (total == 0) return Results.Problem("Choose a file to upload.", statusCode: 400);
        if (declared is null && await OverAllowanceAsync(db, user, previous, total, o, ct) is { } over) return over;

        // What the file really is, from its first bytes.
        file.Position = 0;
        var head = new byte[16];
        var headLength = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        file.Position = 0;

        Guid assetId;
        if (kind == MediaKind.Image)
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
            var format = kind == MediaKind.Video ? MediaFormats.Video(head.AsSpan(0, headLength)) : MediaFormats.Audio(head.AsSpan(0, headLength));
            if (format is null)
            {
                return Results.Problem(kind == MediaKind.Video
                    ? "That doesn't look like a video. Use an MP4 (it plays everywhere), a MOV or a WebM."
                    : "That doesn't look like a sound file. Use an MP3, M4A, OGG, WAV or WebM.", statusCode: 400);
            }
            assetId = await media.SaveUploadAsync(kind, file, total, format.ContentType, format.Extension, partyId: null, user.Id, ct);
        }

        // Point the place at the new file: insert, or replace what was there, in one statement
        // (the AI could be painting this very picture at the same moment).
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ScenarioMedia" ("ScenarioId", "Key", "AssetId") VALUES ({ownerId}, {key}, {assetId})
            ON CONFLICT ("ScenarioId", "Key") DO UPDATE SET "AssetId" = excluded."AssetId"
            """, ct);
        // Then let go of the old file, if it was an upload nothing else uses. An AI picture is kept: it's shared and cached.
        if (previous is { } old) await media.DeleteUnusedUploadsAsync([old], ct);
        return null;
    }

    /// <summary>Empties one place, and deletes its file if it was an upload nothing else uses.</summary>
    public static async Task RemoveAsync(AppDbContext db, MediaService media, string ownerId, string key, CancellationToken ct)
    {
        var removed = await db.ScenarioMedia.Where(m => m.ScenarioId == ownerId && m.Key == key).Select(m => m.AssetId).ToListAsync(ct);
        await db.ScenarioMedia.Where(m => m.ScenarioId == ownerId && m.Key == key).ExecuteDeleteAsync(ct);
        await media.DeleteUnusedUploadsAsync(removed, ct);
    }

    /// <summary>What's in each place now: the file, whether someone uploaded it, and its size.</summary>
    public static async Task<Dictionary<string, (Guid AssetId, bool Uploaded, long SizeBytes)>> PlacesAsync(AppDbContext db, string ownerId, CancellationToken ct) =>
        (await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == ownerId)
            .Join(db.MediaAssets, m => m.AssetId, a => a.Id, (m, a) => new { m.Key, a.Id, a.Provider, a.SizeBytes })
            .ToListAsync(ct))
        .ToDictionary(x => x.Key, x => (x.Id, x.Provider == MediaService.Upload, x.SizeBytes));

    /// <summary>The size limits, and this host's allowance and how much of it they've used.</summary>
    public static async Task<MediaLimits> LimitsAsync(AppDbContext db, AppUser user, MediaOptions o, CancellationToken ct) =>
        new(MaxImageBytes, LimitFor(MediaKind.Video, o), MaxAudioBytes, user.IsAdmin ? null : o.UploadQuotaMb * MB, await UsedAsync(db, user, ct));

    private static async Task<long> UsedAsync(AppDbContext db, AppUser user, CancellationToken ct) =>
        await db.MediaAssets.Where(a => a.OwnerUserId == user.Id && a.Provider == MediaService.Upload).SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;

    /// <summary>
    /// Refuses an upload that would take a host past their allowance. The file it replaces doesn't count, when nothing
    /// else shares it (that one goes once the new one is in). The admin has no limit.
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
