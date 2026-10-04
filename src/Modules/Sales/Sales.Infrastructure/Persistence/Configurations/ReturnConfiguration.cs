using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the Return aggregate.
/// Table: sal_Returns
/// </summary>
internal sealed class ReturnConfiguration : IEntityTypeConfiguration<Return>
{
    public void Configure(EntityTypeBuilder<Return> builder)
    {
        builder.ToTable("sal_Returns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasConversion(id => id.Value, value => new ReturnId(value))
            .IsRequired();

        builder.Property(r => r.OriginalSaleId)
            .HasConversion(id => id.Value, value => new SaleId(value))
            .IsRequired();

        builder.Property(r => r.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(r => r.Notes)
            .HasMaxLength(1000);

        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();
        builder.Property(r => r.ProcessedAt);

        builder.HasIndex(r => r.OriginalSaleId);
        builder.HasIndex(r => r.Status);

        // Items owned by this Return
        builder.HasMany(r => r.Items)
            .WithOne()
            .HasForeignKey(i => i.ReturnId)
            .HasPrincipalKey(r => r.Id)
            .OnDelete(DeleteBehavior.Cascade);

        // Ignore computed properties
        builder.Ignore(r => r.TotalRefundAmount);
    }
}
