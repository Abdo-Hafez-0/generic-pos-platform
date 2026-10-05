using CashManagement.Domain.Entities;
using CashManagement.Domain.Enums;
using CashManagement.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CashManagement.Infrastructure.Persistence.Configurations;

/// <summary>Cash session aggregate root. Table: cash_Sessions</summary>
internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable("cash_Sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasConversion(id => id.Value, v => new CashSessionId(v)).IsRequired();

        builder.Property(s => s.DrawerCode).HasMaxLength(CashSession.MaxDrawerCodeLength).IsRequired();
        builder.Property(s => s.OpenedBy).HasMaxLength(CashSession.MaxPersonLength).IsRequired();
        builder.Property(s => s.OpeningFloat).HasConversion(a => a.Value, v => new CashAmount(v)).HasColumnType("TEXT").IsRequired();
        builder.Property(s => s.Status).HasConversion<int>().IsRequired();
        builder.Property(s => s.OpenedAt).IsRequired();
        builder.Property(s => s.Notes).HasMaxLength(CashSession.MaxNotesLength * 2 + 1);

        builder.Property(s => s.ClosedAt);
        builder.Property(s => s.ClosedBy).HasMaxLength(CashSession.MaxPersonLength);
        builder.Property(s => s.CountedAmount).HasConversion(a => a == null ? (decimal?)null : a.Value.Value, v => v == null ? null : new CashAmount(v.Value)).HasColumnType("TEXT");
        builder.Property(s => s.ExpectedAmount).HasConversion(a => a == null ? (decimal?)null : a.Value.Value, v => v == null ? null : new CashAmount(v.Value)).HasColumnType("TEXT");
        builder.Property(s => s.Variance).HasColumnType("TEXT");

        builder.HasIndex(s => s.OpenedAt);
        builder.HasIndex(s => new { s.DrawerCode, s.Status });

        // Only one OPEN session per drawer (Status 1 = Open): a second concurrent open fails at the database as well.
        builder.HasIndex(s => s.DrawerCode).IsUnique().HasFilter($"\"Status\" = {(int)CashSessionStatus.Open}").HasDatabaseName("UX_cash_Sessions_OpenDrawer");

        builder.HasMany(s => s.Movements).WithOne().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(s => s.Balance);
    }
}

/// <summary>Table: cash_Movements. ReferenceType + ReferenceId are plain values (no foreign key, no other module's type).</summary>
internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> builder)
    {
        builder.ToTable("cash_Movements");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasConversion(id => id.Value, v => new CashMovementId(v)).IsRequired();
        builder.Property(m => m.SessionId).HasConversion(id => id.Value, v => new CashSessionId(v)).IsRequired();

        builder.Property(m => m.Kind).HasConversion<int>().IsRequired();
        builder.Property(m => m.Amount).HasColumnType("TEXT").IsRequired();
        builder.Property(m => m.Reason).HasMaxLength(200);
        builder.Property(m => m.ReferenceType).HasMaxLength(50);
        builder.Property(m => m.ReferenceId);
        builder.Property(m => m.RecordedBy).HasMaxLength(CashSession.MaxPersonLength);
        builder.Property(m => m.RecordedAt).IsRequired();

        builder.HasIndex(m => m.SessionId);
        builder.HasIndex(m => new { m.ReferenceType, m.ReferenceId });

        builder.Ignore(m => m.SignedAmount);
    }
}
