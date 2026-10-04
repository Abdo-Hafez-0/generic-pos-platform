using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sales.Domain.Entities;
using Sales.Domain.ValueObjects;

namespace Sales.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for SalesTransaction.
/// Table: sal_SalesTransactions
/// </summary>
internal sealed class SalesTransactionConfiguration : IEntityTypeConfiguration<SalesTransaction>
{
    public void Configure(EntityTypeBuilder<SalesTransaction> builder)
    {
        builder.ToTable("sal_SalesTransactions");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).IsRequired();

        builder.Property(t => t.SaleId)
            .HasConversion(id => id.Value, value => new SaleId(value))
            .IsRequired();

        builder.Property(t => t.GrandTotal)
            .HasConversion(
                m => m.Amount,
                value => new Money(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(t => t.TaxTotal)
            .HasConversion(
                m => m.Amount,
                value => new Money(value))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(t => t.TransactedAt).IsRequired();

        builder.Property(t => t.Reference)
            .HasMaxLength(100);

        // One transaction per sale
        builder.HasIndex(t => t.SaleId).IsUnique();
        builder.HasIndex(t => t.TransactedAt);
    }
}
