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

internal sealed class FiscalDocumentConfiguration : IEntityTypeConfiguration<FiscalDocument>
{
    public void Configure(EntityTypeBuilder<FiscalDocument> builder)
    {
        builder.ToTable("fis_Documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Kind).HasConversion<int>().IsRequired();
        builder.Property(d => d.Reference).HasMaxLength(40).IsRequired();
        builder.Property(d => d.OriginalReference).HasMaxLength(40);
        builder.Property(d => d.Country).HasConversion<int>().IsRequired();
        builder.Property(d => d.PreviousHash).HasMaxLength(128);
        builder.Property(d => d.Hash).HasMaxLength(128);
        builder.Property(d => d.QrPayload).HasMaxLength(2000);
        builder.Property(d => d.Total).HasPrecision(18, 2);
        builder.Property(d => d.State).HasConversion<int>().IsRequired();
        builder.Property(d => d.StateReason).HasMaxLength(2000);

        // one document per sale and per return; one document per place in a till's chain
        builder.HasIndex(d => new { d.Kind, d.SourceId }).IsUnique();
        builder.HasIndex(d => new { d.TillId, d.Number }).IsUnique().HasFilter("Number IS NOT NULL");
        builder.HasIndex(d => new { d.State, d.CreatedAt });
    }
}
