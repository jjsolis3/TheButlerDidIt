using ButlerDidIt.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

/// <summary>
/// Whether anyone may create a host account, or only people with an invite (#102). The server's configuration
/// (Auth:AllowRegistration) is where it starts; the admin can switch it on the admin hub, which is stored in
/// SiteSettings and wins. It's read from the database every time, so a switch reaches every server at once. That's
/// cheap: only the sign-in page and sign-ups ask.
/// </summary>
public static class SignUps
{
    public static async Task<bool> OpenAsync(AppDbContext db, AuthOptions options, CancellationToken ct) =>
        await SwitchAsync(db, ct) ?? options.AllowRegistration;

    /// <summary>The admin's choice, or null when they haven't made one (the configuration decides).</summary>
    public static Task<bool?> SwitchAsync(AppDbContext db, CancellationToken ct) =>
        db.SiteSettings.AsNoTracking().Where(s => s.Id == SiteSettingsEntity.SingleId).Select(s => s.AllowRegistration).FirstOrDefaultAsync(ct);
}
