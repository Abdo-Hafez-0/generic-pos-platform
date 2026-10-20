using Fiscal.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fiscal.Infrastructure.Persistence.Configurations;

internal sealed class FiscalSettingsConfiguration : IEntityTypeConfiguration<FiscalSettings>
{
    public void Configure(EntityTypeBuilder<FiscalSettings> builder)
    {
        builder.ToTable("fis_Settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Country).HasConversion<int>().IsRequired();
        builder.Property(s => s.TaxNumber).HasMaxLength(20);
        builder.Property(s => s.BranchCode).HasMaxLength(20);
        builder.Property(s => s.ActivityCode).HasMaxLength(10);
        builder.Property(s => s.SellerNameArabic).HasMaxLength(200);
        builder.Property(s => s.AddressArabic).HasMaxLength(400);
        builder.Property(s => s.UpdatedBy).HasMaxLength(100);
        builder.Ignore(s => s.IsOn);
    }
}

internal sealed class FiscalTillConfiguration : IEntityTypeConfiguration<FiscalTill>
{
    public void Configure(EntityTypeBuilder<FiscalTill> builder)
    {
        builder.ToTable("fis_Tills");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.LastDocumentHash).HasMaxLength(128);
        builder.Property(t => t.Registration).HasConversion<int>().IsRequired();
        // the chain's counter must not be taken twice: a concurrent update fails instead of reusing a number
        builder.Property(t => t.Counter).IsConcurrencyToken();
        builder.Ignore(t => t.HasIssued);
    }
}
