using System.Security.Claims;
using System.Text.Json;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Escape;

/// <param name="Mine">The host's own room, which they can edit (or the admin, any room in the database).</param>
/// <param name="BuiltIn">A hand-written room from content/escape: read-only, but anyone can make their own copy.</param>
public sealed record EditableRoom(string Id, bool CanEdit, bool BuiltIn, JsonElement Document);
public sealed record RoomDocumentRequest(JsonElement Document);

/// <param name="Edition">The room's edition after saving. It goes up when the change alters how the room plays,
/// which starts fresh leaderboards; a change to the story's words keeps them.</param>
public sealed record SavedRoom(bool Valid, IReadOnlyList<string> Errors, int Edition, bool NewEdition);

/// <summary>
/// The escape room editor (#113), the escape rooms' version of the mystery editor.
///
/// <list type="bullet">
/// <item>A host edits their own rooms: the ones the AI wrote for them, and their copies.</item>
/// <item>Any host can make their own copy of any room, a built-in one included, to change a riddle or put the
/// family's names in. Built-in rooms are never edited in place: they're read from content/ at every start.</item>
/// <item>A room is saved only when <see cref="EscapeRoomValidator"/> proves it can still be escaped, at every length and
/// difficulty, over its full set of puzzle sets. Checking as you type uses a handful of puzzle sets, to stay quick.</item>
/// </list>
/// </summary>
public static class EscapeEditorEndpoints
{
    /// <summary>How many puzzle sets the check as you type plays (saving plays <see cref="EscapeRoomValidator.SeedsChecked"/>).</summary>
    public const int QuickSeeds = 12;

    /// <summary>
    /// How rooms are written for the editor and saved from it: as the room files look, without the read-only
    /// properties a room works out for itself (its host, playable lengths, scene objects) and without nulls.
    /// </summary>
    private static readonly JsonSerializerOptions Tidy = new(GameJson.Options)
    {
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonElement ForEditor(EscapeRoom room) => JsonSerializer.SerializeToElement(room, Tidy);

    private const int MaxDocumentLength = 200_000;
    private const int MaxStages = 6;
    private const int MaxPuzzles = 30;

    public static void MapEscapeEditorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/escape-rooms").RequireAuthorization(AuthPolicies.Host);

        // The whole room, answers included. The editor shows them only after a "Spoilers!" warning.
        group.MapGet("/{id}/document", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, EscapeCatalog catalog, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (catalog.Find(id) is { } builtIn) return Results.Ok(new EditableRoom(id, CanEdit: false, BuiltIn: true, ForEditor(builtIn)));
            var row = await db.EscapeRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row is null || !CanEdit(user, row)) return Results.NotFound();
            return Results.Ok(new EditableRoom(id, CanEdit: true, BuiltIn: false, ForEditor(GameJson.Deserialize<EscapeRoom>(row.Document))));
        });

        // Live feedback while typing. Nothing is saved.
        group.MapPost("/validate", (RoomDocumentRequest req) =>
        {
            var (_, errors) = Check(req.Document, QuickSeeds);
            return new ValidationResult(errors.Count == 0, errors);
        });

        group.MapPut("/{id}/document", async (string id, RoomDocumentRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            EscapeCatalog catalog, MediaService media, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (catalog.Find(id) is not null)
                return Results.Problem("Built-in rooms can't be edited. Make your own copy and edit that.", statusCode: 403);
            var row = await db.EscapeRooms.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row is null || !CanEdit(user, row)) return Results.NotFound();
            if (await BusyPartyAsync(db, id, ct) is { } code)
                return Results.Problem($"Party {code} is using this room right now. Finish or delete that party first, or edit a copy.", statusCode: 409);

            // The full check: every puzzle set, where typing only tried a handful.
            var (room, errors) = Check(req.Document, EscapeRoomValidator.SeedsChecked);
            if (room is null || errors.Count > 0) return Refused(errors);
            if (room.Id != id) return Refused(["The room's id can't be changed. Make a copy instead."]);

            var old = GameJson.Deserialize<EscapeRoom>(row.Document);
            // The server decides the edition: a change to how the room plays starts fresh leaderboards.
            var newEdition = PlayChanged(old, room);
            room = room with { Edition = newEdition ? old.Edition + 1 : old.Edition };

            row.Title = room.Title;
            row.ContentRating = room.ContentRating;
            row.Document = JsonSerializer.Serialize(room, Tidy);
            row.UpdatedAt = clock.GetUtcNow();

            // Pictures the AI painted from words that changed are now wrong; forget just those. The next game with the
            // AI on paints the new ones, and anything unchanged keeps its picture. The host's own uploads stay, unless
            // their stage is gone.
            var stale = StaleArt(old, room);
            var slots = EscapeMediaEndpoints.Slots(room).Select(s => s.Key).ToHashSet();
            var jobId = EscapeMedia.JobId(id);
            var art = await db.ScenarioMedia.Where(m => m.ScenarioId == jobId)
                .Select(m => new { m.Key, m.AssetId, Uploaded = db.MediaAssets.Any(a => a.Id == m.AssetId && a.Provider == MediaService.Upload) })
                .ToListAsync(ct);
            var gone = art.Where(m => !slots.Contains(m.Key) || (stale.Contains(m.Key) && !m.Uploaded)).ToList();
            var goneKeys = gone.Select(m => m.Key).ToList();
            await db.ScenarioMedia.Where(m => m.ScenarioId == jobId && goneKeys.Contains(m.Key)).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await media.DeleteUnusedUploadsAsync(gone.Where(m => m.Uploaded).Select(m => m.AssetId), ct);
            catalog.Forget(id);
            catalog.ForgetArt(id);
            return Results.Ok(new SavedRoom(true, [], room.Edition, newEdition));
        });

        group.MapPost("/{id}/duplicate", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            EscapeCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            EscapeRoom source;
            if (catalog.Find(id) is { } builtIn) source = builtIn;
            else if (await db.EscapeRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } row && CanEdit(user, row)) source = GameJson.Deserialize<EscapeRoom>(row.Document);
            else return Results.NotFound();

            // A record, so `with` copies every setting, including ones added later; only the id, title and edition change.
            var title = $"{source.Title} (copy)";
            var copy = source with
            {
                Id = ScenarioEditorEndpoints.NewId(source.Title, "room"),
                Title = title.Length <= 200 ? title : title[..200],
                Edition = 1, // a new room: its own leaderboards from the start
            };
            var now = clock.GetUtcNow();
            db.EscapeRooms.Add(new EscapeRoomEntity
            {
                Id = copy.Id, OwnerUserId = user.Id, Title = copy.Title, ContentRating = copy.ContentRating,
                Document = JsonSerializer.Serialize(copy, Tidy), CreatedAt = now, UpdatedAt = now, CopiedFrom = id,
            });
            var copyId = copy.Id;
            // The copy starts with the original's pictures (the files are shared, not duplicated).
            var (from, to) = (EscapeMedia.JobId(id), EscapeMedia.JobId(copyId));
            var art = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == from).ToListAsync(ct);
            db.ScenarioMedia.AddRange(art.Select(m => new ScenarioMediaEntity { ScenarioId = to, Key = m.Key, AssetId = m.AssetId }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { Id = copyId });
        });
    }

    /// <summary>A save that didn't pass: the first problem as the message (what the page shows), and the whole list.</summary>
    private static IResult Refused(List<string> errors) => Results.Problem(
        errors.Count == 1 ? errors[0] : $"{errors[0]} (and {errors.Count - 1} more)",
        statusCode: 400, extensions: new Dictionary<string, object?> { ["errors"] = errors });

    // Unknown and someone else's look the same (404), so nobody can probe for other hosts' rooms.
    internal static bool CanEdit(AppUser user, EscapeRoomEntity row) => row.OwnerUserId == user.Id || user.IsAdmin;

    private static Task<string?> BusyPartyAsync(AppDbContext db, string id, CancellationToken ct) =>
        db.Parties.AsNoTracking().Where(p => p.ScenarioId == id && p.Kind == GameKind.EscapeRoom && p.Status != PartyStatus.Finished)
            .Select(p => p.Code).FirstOrDefaultAsync(ct);

    /// <summary>Parses and checks a room. Every problem becomes a readable message rather than a crash.</summary>
    internal static (EscapeRoom? Room, List<string> Errors) Check(JsonElement document, int seeds)
    {
        var raw = document.GetRawText();
        if (raw.Length > MaxDocumentLength) return (null, [$"The room is too big ({raw.Length:N0} characters; at most {MaxDocumentLength:N0})."]);
        EscapeRoom room;
        try
        {
            room = GameJson.Deserialize<EscapeRoom>(raw);
            if (room is null) return (null, ["The room is empty."]);
        }
        catch (JsonException ex)
        {
            // e.g. "JSON deserialization for type 'EscapeRoom' was missing required properties, including: 'intro'."
            return (null, [$"The room couldn't be read: {ex.Message}"]);
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(room.Title)) errors.Add("Give the room a title.");
        if (room.Stages.Count > MaxStages) errors.Add($"A room can have at most {MaxStages} stages.");
        if (room.Puzzles.Count > MaxPuzzles) errors.Add($"A room can have at most {MaxPuzzles} puzzles.");
        if (errors.Count > 0) return (room, errors);
        try
        {
            errors.AddRange(EscapeRoomValidator.Validate(room, seeds));
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            // A section sent as null, or a template the generators can't fill: say so instead of failing the request.
            errors.Add($"Part of the room is missing or can't be built ({ex.GetType().Name}). Check the JSON tab.");
        }
        return (room, errors);
    }

    /// <summary>
    /// Whether a change alters how the room plays, rather than only the words of its story: anything but the title,
    /// synopsis, intro, endings, look, sound, seasons, game master and the stages' names and descriptions.
    /// </summary>
    internal static bool PlayChanged(EscapeRoom before, EscapeRoom after) => PlayOnly(before) != PlayOnly(after);

    private static string PlayOnly(EscapeRoom room) => GameJson.Serialize(room with
    {
        Title = "", Synopsis = "", Intro = "", EscapedText = "", FailedText = "", ArtStyle = "", Theme = "",
        Soundscape = Soundscape.Drone, Seasons = [], GameMaster = null, ContentRating = default, Edition = 0,
        Stages = room.Stages.Select(s => s with { Title = "", Description = "", Soundscape = null }).ToList(),
    });

    /// <summary>The picture keys whose prompt changed or whose stage went: their pictures no longer match the room.</summary>
    internal static HashSet<string> StaleArt(EscapeRoom before, EscapeRoom after)
    {
        var now = EscapeMediaPlan.For(after).ToDictionary(i => i.Key);
        return EscapeMediaPlan.For(before).Where(i => !now.TryGetValue(i.Key, out var n) || n != i).Select(i => i.Key).ToHashSet();
    }
}
