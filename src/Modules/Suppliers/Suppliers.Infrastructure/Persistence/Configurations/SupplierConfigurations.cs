using Suppliers.Domain.Entities;
using Suppliers.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Suppliers.Infrastructure.Persistence.Configurations;

/// <summary>Supplier aggregate root. Table: sup_Suppliers</summary>
internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("sup_Suppliers");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasConversion(id => id.Value, v => new SupplierId(v)).IsRequired();

        builder.Property(c => c.Code).HasMaxLength(30).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(254);
        builder.Property(c => c.Phone).HasMaxLength(40);
        builder.Property(c => c.Notes).HasMaxLength(1000);
        builder.Property(c => c.Status).HasConversion<int>().IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasIndex(c => c.Code).IsUnique();
        builder.HasIndex(c => c.Name);
        builder.HasIndex(c => c.Status);

        builder.HasMany(c => c.Addresses).WithOne().HasForeignKey("SupplierId").OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(c => c.Contacts).WithOne().HasForeignKey("SupplierId").OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(c => c.DomainEvents);
    }
}

/// <summary>Table: sup_SupplierAddresses</summary>
internal sealed class SupplierAddressConfiguration : IEntityTypeConfiguration<SupplierAddress>
{
    public void Configure(EntityTypeBuilder<SupplierAddress> builder)
    {
        builder.ToTable("sup_SupplierAddresses");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasConversion(id => id.Value, v => new SupplierAddressId(v)).IsRequired();
        builder.Property(a => a.SupplierId).HasConversion(id => id.Value, v => new SupplierId(v)).IsRequired();

        builder.Property(a => a.Type).HasConversion<int>().IsRequired();
        builder.Property(a => a.Line1).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Line2).HasMaxLength(200);
        builder.Property(a => a.City).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Region).HasMaxLength(100);
        builder.Property(a => a.PostalCode).HasMaxLength(30);
        builder.Property(a => a.Country).HasMaxLength(100).IsRequired();

        builder.HasIndex(a => a.SupplierId);
    }
}

/// <summary>Table: sup_SupplierContacts</summary>
internal sealed class SupplierContactConfiguration : IEntityTypeConfiguration<SupplierContact>
{
    public void Configure(EntityTypeBuilder<SupplierContact> builder)
    {
        builder.ToTable("sup_SupplierContacts");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasConversion(id => id.Value, v => new SupplierContactId(v)).IsRequired();
        builder.Property(c => c.SupplierId).HasConversion(id => id.Value, v => new SupplierId(v)).IsRequired();

        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(254);
        builder.Property(c => c.Phone).HasMaxLength(40);
        builder.Property(c => c.Role).HasMaxLength(100);

        builder.HasIndex(c => c.SupplierId);
    }
}
