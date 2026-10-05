using AdminPortal.Application;
using BackupServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cloud.Infrastructure.Persistence;

/// <summary>Persistence form of a license (the application's <c>LicenseRecord</c> is mapped to and from it).</summary>
internal sealed class LicenseEntity
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";
    public string ActivationKeyHash { get; set; } = "";
    public string ProductId { get; set; } = "";
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public string ModulesJson { get; set; } = "[]";
    public string FeaturesJson { get; set; } = "[]";
    public Guid? InstallationId { get; set; }
    public int Status { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? LastIssuedAt { get; set; }
    public long RowVersion { get; set; }
}

/// <summary>Persistence form of a catalogued update package (bytes live in the package file store).</summary>
internal sealed class PackageEntity
{
    public Guid PackageId { get; set; }
    public int PackageType { get; set; }
    public string TargetId { get; set; } = "";
    public string Version { get; set; } = "";
    public string TargetFramework { get; set; } = "";
    public string MinimumHostVersion { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string Publisher { get; set; } = "";
    public int Status { get; set; }
    public string? ReleaseNotes { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string PublishedBy { get; set; } = "";
    public DateTimeOffset? WithdrawnAt { get; set; }
}

internal sealed class BackupTokenEntity
{
    public Guid LicenseId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The Stage 9 SERVER database. It is entirely separate from the desktop's SQLite database: the desktop never opens it
/// and the server never opens the desktop's. Table prefixes: lic_ (licenses), upd_ (packages), bak_ (backups), adm_ (administration).
/// </summary>
public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    internal DbSet<LicenseEntity> Licenses => Set<LicenseEntity>();
    internal DbSet<PackageEntity> Packages => Set<PackageEntity>();
    internal DbSet<BackupTokenEntity> BackupTokens => Set<BackupTokenEntity>();
    public DbSet<BackupRecord> Backups => Set<BackupRecord>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<RegisteredModule> Modules => Set<RegisteredModule>();
    public DbSet<AdminAuditEntry> AuditLog => Set<AdminAuditEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order or compare DateTimeOffset text; a sortable UTC-ordered number can.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LicenseEntity>(e =>
        {
            e.ToTable("lic_Licenses");
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerId).HasMaxLength(200).IsRequired();
            e.Property(x => x.ActivationKeyHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.ProductId).HasMaxLength(100).IsRequired();
            e.Property(x => x.ModulesJson).IsRequired();
            e.Property(x => x.FeaturesJson).IsRequired();
            e.HasIndex(x => x.ActivationKeyHash).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.InstallationId);
        });

        b.Entity<PackageEntity>(e =>
        {
            e.ToTable("upd_Packages");
            e.HasKey(x => x.PackageId);
            e.Property(x => x.TargetId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Version).HasMaxLength(50).IsRequired();
            e.Property(x => x.TargetFramework).HasMaxLength(100).IsRequired();
            e.Property(x => x.MinimumHostVersion).HasMaxLength(50).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.EnvelopeJson).IsRequired();
            e.Property(x => x.KeyId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Publisher).HasMaxLength(200).IsRequired();
            e.Property(x => x.PublishedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TargetId, x.Version, x.TargetFramework }).IsUnique();
            e.HasIndex(x => x.Status);
        });

        b.Entity<BackupTokenEntity>(e =>
        {
            e.ToTable("bak_AccessTokens");
            e.HasKey(x => x.LicenseId);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<BackupRecord>(e =>
        {
            e.ToTable("bak_Backups");
            e.HasKey(x => x.BackupId);
            e.Property(x => x.CustomerId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.Label).HasMaxLength(BackupService.MaxLabelLength);
            e.Property(x => x.ClientVersion).HasMaxLength(100);
            e.HasIndex(x => new { x.LicenseId, x.CreatedAt });
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("adm_Customers");
            e.HasKey(x => x.Id);
            // NOCASE makes the unique name index (and equality checks) case-insensitive.
            e.Property(x => x.Name).HasMaxLength(200).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<RegisteredModule>(e =>
        {
            e.ToTable("adm_Modules");
            e.HasKey(x => x.ModuleId);
            e.Property(x => x.ModuleId).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        });

        b.Entity<AdminAuditEntry>(e =>
        {
            e.ToTable("adm_AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }
}
