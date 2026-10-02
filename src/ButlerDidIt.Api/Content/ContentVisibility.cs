using ButlerDidIt.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Content;

/// <summary>
/// What's on the shelves, for both games. The admin can share their own room or mystery with every host
/// (a <c>Shared</c> flag on its row) and take a built-in one off the shelf (a <see cref="HiddenContentEntity"/> row).
/// Improving a built-in room or mystery means both: edit a copy, share it, hide the original.
/// </summary>
public static class ContentVisibility
{
    /// <summary>The ids of the built-in rooms or hand-written mysteries taken off the shelf.</summary>
    public static async Task<HashSet<string>> HiddenAsync(AppDbContext db, GameKind kind, CancellationToken ct) =>
        (await db.HiddenContent.AsNoTracking().Where(h => h.Kind == kind).Select(h => h.ContentId).ToListAsync(ct)).ToHashSet();

    public static Task<bool> IsHiddenAsync(AppDbContext db, GameKind kind, string id, CancellationToken ct) =>
        db.HiddenContent.AnyAsync(h => h.Kind == kind && h.ContentId == id, ct);

    /// <summary>Takes a built-in room or mystery off the shelf, or puts it back. Doing it twice is harmless.</summary>
    public static async Task SetHiddenAsync(AppDbContext db, GameKind kind, string id, bool hidden, DateTimeOffset now, CancellationToken ct)
    {
        if (!hidden)
        {
            await db.HiddenContent.Where(h => h.Kind == kind && h.ContentId == id).ExecuteDeleteAsync(ct);
            return;
        }
        if (await IsHiddenAsync(db, kind, id, ct)) return;
        db.HiddenContent.Add(new HiddenContentEntity { Kind = kind, ContentId = id, HiddenAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Hidden at the same moment from another tab: it's hidden either way.
        }
    }

    /// <summary>Whether this signed-in user is the admin. Read from the database, so a change takes effect at once.</summary>
    public static async Task<bool> IsAdminAsync(AppDbContext db, string? userId, CancellationToken ct) =>
        userId is not null && await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.IsAdmin).FirstOrDefaultAsync(ct);
}
