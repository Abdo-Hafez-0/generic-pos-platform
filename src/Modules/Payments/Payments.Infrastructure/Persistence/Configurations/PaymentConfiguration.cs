using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain.Entities;
using Payments.Domain.ValueObjects;

namespace Payments.Infrastructure.Persistence.Configurations;

/// <summary>Payment aggregate root. Table: pay_Payments. ReferenceType + ReferenceId are plain values (no FK, no other module's type).</summary>
internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("pay_Payments");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasConversion(id => id.Value, v => new PaymentId(v)).IsRequired();

        builder.Property(p => p.ReferenceType).HasMaxLength(50).IsRequired();
        builder.Property(p => p.ReferenceId).IsRequired();

        builder.Property(p => p.Amount)
            .HasConversion(m => m.Amount, v => new Money(v))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.Method).HasConversion<int>().IsRequired();
        builder.Property(p => p.MethodDetail).HasMaxLength(100);
        builder.Property(p => p.TenderedAmount).HasColumnType("TEXT");
        builder.Property(p => p.RecordedBy).HasMaxLength(100);
        builder.Property(p => p.Status).HasConversion<int>().IsRequired();
        builder.Property(p => p.RecordedAt).IsRequired();
        builder.Property(p => p.VoidedAt);
        builder.Property(p => p.VoidReason).HasMaxLength(500);

        builder.HasIndex(p => new { p.ReferenceType, p.ReferenceId });
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.RecordedAt);

        builder.Ignore(p => p.ChangeDue);
    }
}
