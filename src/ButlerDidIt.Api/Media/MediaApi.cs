using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game.Engine;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace ButlerDidIt.Api.Media;

public static class MediaApi
{
    public const long MaxPhotoBytes = 10 * 1024 * 1024;

    public static void MapMediaApi(this IEndpointRouteBuilder app)
    {
        // Generated and uploaded files. Asset ids are random GUIDs, so URLs can't be guessed.
        // They are cached forever by browsers because an asset never changes once created.
        app.MapGet("/media/assets/{id:guid}", async (Guid id, AppDbContext db, MediaStore store, HttpContext http, CancellationToken ct) =>
        {
            var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (asset is null || store.Resolve(asset.Path) is not { } file) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(file, asset.ContentType, enableRangeProcessing: true);
        });

        // ---- Host: status of voice/art preparation for a party's mystery, and a button to (re)start it.
        app.MapGet("/api/parties/{code}/media", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null || party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.NotFound();
            var job = await db.MediaJobs.AsNoTracking().Where(j => j.ScenarioId == party.ScenarioId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            var ready = await db.ScenarioMedia.CountAsync(m => m.ScenarioId == party.ScenarioId, ct);
            return Results.Ok(new { Ready = ready, Job = job is null ? null : new MediaJobView(job.Id, job.Status, job.Total, job.Done, job.Failed, job.Error) });
        }).RequireAuthorization(AuthPolicies.Host);

        app.MapPost("/api/parties/{code}/media", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db,
            ButlerDidIt.Ai.Media.MediaGateway media, TimeProvider clock, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null || party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.NotFound();
            if (!await media.VoicesConfiguredAsync(ct) && !await media.ImagesConfiguredAsync(ct))
                return Results.Problem("No Voice or Illustrator AI is set up yet. An admin can add one under Admin → AI.", statusCode: 400);
            var job = await MediaWorker.EnqueueAsync(db, party.ScenarioId, party.HostUserId, clock, ct);
            return Results.Ok(new MediaJobView(job.Id, job.Status, job.Total, job.Done, job.Failed, job.Error));
        }).RequireAuthorization(AuthPolicies.Host);

        // ---- Guest: costume selfie. Authenticated by the seat token (X-Seat-Token header).
        app.MapPost("/api/seat/photo", async (HttpRequest request, ClaimsPrincipal user, PartyService parties, MediaService mediaService, CancellationToken ct) =>
        {
            if (user.SeatId() is not { } seatId || user.PartyId() is not { } partyId) return Results.Unauthorized();
            if (!request.HasFormContentType) return Results.Problem("Send the photo as a form upload.", statusCode: 400);
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("photo");
            if (file is null || file.Length == 0) return Results.Problem("Choose a photo.", statusCode: 400);
            if (file.Length > MaxPhotoBytes) return Results.Problem("That photo is too large (10 MB max).", statusCode: 400);

            await using var stream = file.OpenReadStream();
            byte[] jpeg;
            try
            {
                jpeg = PhotoProcessing.ToSafeJpeg(stream, maxSide: 640);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }

            var assetId = await mediaService.SaveUploadAsync(MediaKind.Photo, jpeg, "image/jpeg", "jpg", partyId, ct);
            var url = MediaStore.Url(assetId);
            string? previous = null;
            await parties.ExecuteAsync(partyId, (s, now) =>
            {
                previous = s.State.Players.FirstOrDefault(p => p.SeatId == seatId)?.PhotoUrl;
                return new SetPlayerPhoto(now, seatId, url);
            }, ct: ct);
            // A retake replaces the old selfie; don't keep the old one around.
            if (MediaStore.AssetIdFromUrl(previous) is { } old) await mediaService.DeleteUploadsAsync([old], ct);
            return Results.Ok(new { PhotoUrl = url });
        }).RequireAuthorization(AuthPolicies.Seat).DisableAntiforgery();

        app.MapDelete("/api/seat/photo", async (ClaimsPrincipal user, PartyService parties, MediaService mediaService, CancellationToken ct) =>
        {
            if (user.SeatId() is not { } seatId || user.PartyId() is not { } partyId) return Results.Unauthorized();
            string? previous = null;
            await parties.ExecuteAsync(partyId, (s, now) =>
            {
                previous = s.State.Players.FirstOrDefault(p => p.SeatId == seatId)?.PhotoUrl;
                return new SetPlayerPhoto(now, seatId, null);
            }, ct: ct);
            // "Remove" means gone from the server too, not just hidden.
            if (MediaStore.AssetIdFromUrl(previous) is { } old) await mediaService.DeleteUploadsAsync([old], ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.Seat);
    }
}

/// <summary>
/// Turns an uploaded photo into a small, safe JPEG. Decoding and re-encoding
/// drops all metadata: phone photos often carry the GPS location where they were
/// taken, and we don't want to publish anyone's address. The image is also
/// rotated upright (phones store "rotate me" as metadata) and shrunk so it loads
/// fast on the TV.
/// </summary>
public static class PhotoProcessing
{
    public static byte[] ToSafeJpeg(Stream input, int maxSide)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        using var codec = SKCodec.Create(ms) ?? throw new InvalidDataException("That doesn't look like a photo. Use a JPEG or PNG.");
        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("That photo couldn't be read.");
        using var upright = Orient(decoded, codec.EncodedOrigin);

        var scale = Math.Min(1.0, (double)maxSide / Math.Max(upright.Width, upright.Height));
        var info = new SKImageInfo(Math.Max(1, (int)(upright.Width * scale)), Math.Max(1, (int)(upright.Height * scale)));
        using var resized = upright.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidDataException("That photo couldn't be resized.");
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return bitmap.Copy();
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, bitmap.Width / 2f, bitmap.Height / 2f); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, result.Height); canvas.RotateDegrees(270); break;
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, bitmap.Width / 2f, 0); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, bitmap.Height / 2f); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1, 0, bitmap.Height / 2f); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(0, result.Height); canvas.RotateDegrees(270); canvas.Scale(1, -1, 0, bitmap.Height / 2f); break;
        }
        using var source = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return result;
    }
}
