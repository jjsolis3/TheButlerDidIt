using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<ThemeEntity> Themes => Set<ThemeEntity>();
    public DbSet<ScenarioEntity> Scenarios => Set<ScenarioEntity>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<PlayerNote> PlayerNotes => Set<PlayerNote>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Documents are stored as jsonb rather than text: Postgres validates the
        // JSON and lets you query inside it, e.g.
        //   SELECT state->>'phase' FROM "Parties";
        b.Entity<ThemeEntity>().Property(t => t.Document).HasColumnType("jsonb");
        b.Entity<ScenarioEntity>().Property(s => s.Document).HasColumnType("jsonb");
        b.Entity<ScenarioEntity>().Property(s => s.ContentRating).HasConversion<string>().HasMaxLength(20);
        b.Entity<ScenarioEntity>().Property(s => s.Source).HasConversion<string>().HasMaxLength(20);

        b.Entity<Party>(p =>
        {
            p.Property(x => x.State).HasColumnType("jsonb");
            p.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            p.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            p.Property(x => x.ContentLevel).HasConversion<string>().HasMaxLength(20);
            p.HasIndex(x => x.Code).IsUnique();
            p.HasIndex(x => x.HostUserId);
            p.HasIndex(x => x.NextDueAt);
            p.HasMany(x => x.Seats).WithOne(s => s.Party).HasForeignKey(s => s.PartyId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Seat>().HasIndex(s => s.TokenHash).IsUnique();
        b.Entity<MediaAsset>().HasIndex(m => m.ContentHash).IsUnique();
        b.Entity<MediaAsset>().Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
    }
}
