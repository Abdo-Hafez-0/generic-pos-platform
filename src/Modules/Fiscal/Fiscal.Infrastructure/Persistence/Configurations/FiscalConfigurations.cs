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
        // MISS-11c: the branch address in the authority's fields (the lengths of Egypt's seller.branchAddress)
        builder.Property(s => s.Governorate).HasMaxLength(100);
        builder.Property(s => s.City).HasMaxLength(100);
        builder.Property(s => s.Street).HasMaxLength(200);
        builder.Property(s => s.BuildingNumber).HasMaxLength(100);
        builder.Property(s => s.PostalCode).HasMaxLength(30);
        builder.Property(s => s.Floor).HasMaxLength(100);
        builder.Property(s => s.Room).HasMaxLength(100);
        builder.Property(s => s.Landmark).HasMaxLength(500);
        builder.Property(s => s.AdditionalInformation).HasMaxLength(500);
        builder.Property(s => s.UpdatedBy).HasMaxLength(100);
        builder.Ignore(s => s.IsOn);
        builder.Ignore(s => s.Address);
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
        builder.Property(t => t.DeviceSerial).HasMaxLength(100);   // MISS-11c
        builder.Property(t => t.Environment).HasConversion<int>().IsRequired();   // MISS-11c-3: the registration (no secrets)
        builder.Property(t => t.ClientId).HasMaxLength(100);
        builder.Property(t => t.PosOsVersion).HasMaxLength(50);
        builder.Property(t => t.PosModelFramework).HasMaxLength(10);
        builder.Property(t => t.RegisteredBy).HasMaxLength(100);
        builder.Ignore(t => t.EnvironmentLocked);
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
        builder.Property(d => d.AuthorityReference).HasMaxLength(200);
        builder.Ignore(d => d.IsOutstanding);
        builder.Ignore(d => d.NeedsAttention);

        // one document per sale and per return; one document per place in a till's chain
        // MISS-11c-4: one live document per sale or return and per place in the chain - a replaced (corrected) one stays beside its copy
        builder.HasIndex(d => new { d.Kind, d.SourceId }).IsUnique().HasFilter("State <> 7");
        builder.HasIndex(d => new { d.TillId, d.Number }).IsUnique().HasFilter("Number IS NOT NULL AND State <> 7");
        builder.HasIndex(d => new { d.State, d.CreatedAt });
    }
}

internal sealed class FiscalProductCodeConfiguration : IEntityTypeConfiguration<FiscalProductCode>
{
    public void Configure(EntityTypeBuilder<FiscalProductCode> builder)
    {
        builder.ToTable("fis_ProductCodes");
        builder.HasKey(c => c.ProductId);
        builder.Property(c => c.ProductId).ValueGeneratedNever();
        builder.Property(c => c.Type).HasConversion<int>().IsRequired();
        builder.Property(c => c.Code).HasMaxLength(100).IsRequired();
        builder.Property(c => c.UpdatedBy).HasMaxLength(100);
    }
}

internal sealed class FiscalUnitCodeConfiguration : IEntityTypeConfiguration<FiscalUnitCode>
{
    public void Configure(EntityTypeBuilder<FiscalUnitCode> builder)
    {
        builder.ToTable("fis_UnitCodes");
        builder.HasKey(c => c.UnitId);
        builder.Property(c => c.UnitId).ValueGeneratedNever();
        builder.Property(c => c.Code).HasMaxLength(30).IsRequired();
    }
}

internal sealed class FiscalTaxCodeConfiguration : IEntityTypeConfiguration<FiscalTaxCode>
{
    public void Configure(EntityTypeBuilder<FiscalTaxCode> builder)
    {
        builder.ToTable("fis_TaxCodes");
        builder.HasKey(c => c.TaxRateId);
        builder.Property(c => c.TaxRateId).ValueGeneratedNever();
        builder.Property(c => c.EgyptTaxType).HasMaxLength(30);
        builder.Property(c => c.EgyptSubType).HasMaxLength(50);
        builder.Property(c => c.SaudiCategory).HasMaxLength(2);
        builder.Property(c => c.SaudiExemptionCode).HasMaxLength(50);
        builder.Property(c => c.SaudiExemptionReason).HasMaxLength(500);
    }
}
