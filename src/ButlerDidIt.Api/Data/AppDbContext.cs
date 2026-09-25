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
    public DbSet<AiProviderEntity> AiProviders => Set<AiProviderEntity>();
    public DbSet<AiRoleEntity> AiRoles => Set<AiRoleEntity>();
    public DbSet<AiModelPriceEntity> AiModelPrices => Set<AiModelPriceEntity>();
    public DbSet<AiUsageEntity> AiUsage => Set<AiUsageEntity>();
    public DbSet<GenerationJobEntity> GenerationJobs => Set<GenerationJobEntity>();

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
        b.Entity<ScenarioEntity>().HasIndex(s => s.OwnerUserId);

        b.Entity<AiProviderEntity>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<AiRoleEntity>(e =>
        {
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            // A provider in use by a role can't be deleted by accident.
            e.HasOne(x => x.Provider).WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AiModelPriceEntity>(e =>
        {
            e.Property(x => x.InputPerMillion).HasPrecision(12, 4);
            e.Property(x => x.OutputPerMillion).HasPrecision(12, 4);
        });
        b.Entity<AiUsageEntity>(e =>
        {
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProviderKind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CostUsd).HasPrecision(14, 6);
            // Budget checks sum one host's spend this month, so index exactly that.
            e.HasIndex(x => new { x.HostUserId, x.At });
            e.HasIndex(x => x.At);
        });
        b.Entity<GenerationJobEntity>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Request).HasColumnType("jsonb");
            e.Property(x => x.Warnings).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => x.HostUserId);
        });
    }
}
