using Audit.Domain.Entities;
using Audit.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Audit.Infrastructure.Persistence.Configurations;

/// <summary>Audit entry aggregate root. Table: aud_AuditEntries. ActorId and EntityId are plain values (no foreign keys).</summary>
internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("aud_AuditEntries");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasConversion(id => id.Value, v => new AuditEntryId(v)).IsRequired();

        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.Module).HasMaxLength(AuditEntry.MaxModuleLength).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(AuditEntry.MaxActionLength).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(AuditEntry.MaxEntityTypeLength);
        builder.Property(e => e.EntityId).HasMaxLength(AuditEntry.MaxEntityIdLength);
        builder.Property(e => e.ActorId);
        builder.Property(e => e.ActorName).HasMaxLength(AuditEntry.MaxActorNameLength);
        builder.Property(e => e.Summary).HasMaxLength(AuditEntry.MaxSummaryLength);
        builder.Property(e => e.Details).HasMaxLength(AuditEntry.MaxDetailsLength);

        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.Module, e.Action });
        builder.HasIndex(e => new { e.EntityType, e.EntityId });
        builder.HasIndex(e => e.ActorId);
    }
}
