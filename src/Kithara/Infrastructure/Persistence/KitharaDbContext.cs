using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Infrastructure.Persistence;

public sealed class KitharaDbContext : DbContext
{
    public KitharaDbContext(DbContextOptions<KitharaDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserAuthBinding> UserAuthBindings => Set<UserAuthBinding>();
    public DbSet<Struna> Strunas => Set<Struna>();
    public DbSet<StrunaControlGrant> StrunaControlGrants => Set<StrunaControlGrant>();
    public DbSet<Tune> Tunes => Set<Tune>();
    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();
    public DbSet<SearchResultCacheEntry> SearchResultCacheEntries => Set<SearchResultCacheEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ManagedByModuleSlug).HasMaxLength(64);
            entity.Property(x => x.MustRotateCredentials).IsRequired();
            entity.Property(x => x.Username).HasMaxLength(128);
            entity.Property(x => x.InvitePasswordHash).HasMaxLength(256);
            entity.Property(x => x.MustCompleteBinding).IsRequired();
            entity.Property(x => x.InviteRolesJson).HasMaxLength(512);
            entity.HasIndex(x => x.Kind);
            entity.HasIndex(x => x.Username)
                .IsUnique()
                .HasFilter("\"Username\" IS NOT NULL");
        });

        modelBuilder.Entity<UserAuthBinding>(entity =>
        {
            entity.ToTable("user_auth_bindings");
            entity.HasKey(x => new { x.UserId, x.ProviderSlug });
            entity.Property(x => x.ProviderSlug).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ExternalSubject).HasMaxLength(256);
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.HasIndex(x => new { x.ProviderSlug, x.ExternalSubject })
                .IsUnique()
                .HasFilter("\"ExternalSubject\" IS NOT NULL");
            entity.HasOne(x => x.User)
                .WithMany(x => x.AuthBindings)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Struna>(entity =>
        {
            entity.ToTable("strunas");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ListenToken).HasMaxLength(128);
            entity.Property(x => x.GuestCode).HasMaxLength(32);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasOne(x => x.Owner)
                .WithMany()
                .HasForeignKey(x => x.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StrunaControlGrant>(entity =>
        {
            entity.ToTable("struna_control_grants");
            entity.HasKey(x => new { x.StrunaId, x.UserId });
            entity.HasOne(x => x.Struna)
                .WithMany(x => x.ControlGrants)
                .HasForeignKey(x => x.StrunaId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tune>(entity =>
        {
            entity.ToTable("tunes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ModuleSlug).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ExternalId).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(512);
            entity.Property(x => x.Artist).HasMaxLength(512);
            entity.Property(x => x.ArtworkUrl).HasMaxLength(1024);
            entity.Property(x => x.StorageKey).HasMaxLength(512);
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.HasIndex(x => new { x.ModuleSlug, x.ExternalId }).IsUnique();
            entity.HasOne(x => x.CreatedBy)
                .WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<QueueEntry>(entity =>
        {
            entity.ToTable("queue_entries");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.StrunaId, x.Position }).IsUnique();
            entity.HasOne(x => x.Struna)
                .WithMany(x => x.QueueEntries)
                .HasForeignKey(x => x.StrunaId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Tune)
                .WithMany(x => x.QueueEntries)
                .HasForeignKey(x => x.TuneId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SearchResultCacheEntry>(entity =>
        {
            entity.ToTable("search_result_cache");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ModuleSlug).HasMaxLength(64).IsRequired();
            entity.Property(x => x.QueryKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ResultsJson).IsRequired();
            entity.HasIndex(x => new { x.PrincipalUserId, x.ModuleSlug, x.QueryKey });
            entity.HasOne(x => x.Principal)
                .WithMany()
                .HasForeignKey(x => x.PrincipalUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
