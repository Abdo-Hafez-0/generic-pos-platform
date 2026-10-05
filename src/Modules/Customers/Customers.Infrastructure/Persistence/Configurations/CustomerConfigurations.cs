using Customers.Domain.Entities;
using Customers.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Customers.Infrastructure.Persistence.Configurations;

/// <summary>Customer aggregate root. Table: cus_Customers</summary>
internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("cus_Customers");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasConversion(id => id.Value, v => new CustomerId(v)).IsRequired();

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

        builder.HasMany(c => c.Addresses).WithOne().HasForeignKey("CustomerId").OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(c => c.Contacts).WithOne().HasForeignKey("CustomerId").OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(c => c.DomainEvents);
    }
}

/// <summary>Table: cus_CustomerAddresses</summary>
internal sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> builder)
    {
        builder.ToTable("cus_CustomerAddresses");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasConversion(id => id.Value, v => new CustomerAddressId(v)).IsRequired();
        builder.Property(a => a.CustomerId).HasConversion(id => id.Value, v => new CustomerId(v)).IsRequired();

        builder.Property(a => a.Type).HasConversion<int>().IsRequired();
        builder.Property(a => a.Line1).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Line2).HasMaxLength(200);
        builder.Property(a => a.City).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Region).HasMaxLength(100);
        builder.Property(a => a.PostalCode).HasMaxLength(30);
        builder.Property(a => a.Country).HasMaxLength(100).IsRequired();

        builder.HasIndex(a => a.CustomerId);
    }
}

/// <summary>Table: cus_CustomerContacts</summary>
internal sealed class CustomerContactConfiguration : IEntityTypeConfiguration<CustomerContact>
{
    public void Configure(EntityTypeBuilder<CustomerContact> builder)
    {
        builder.ToTable("cus_CustomerContacts");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasConversion(id => id.Value, v => new CustomerContactId(v)).IsRequired();
        builder.Property(c => c.CustomerId).HasConversion(id => id.Value, v => new CustomerId(v)).IsRequired();

        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(254);
        builder.Property(c => c.Phone).HasMaxLength(40);
        builder.Property(c => c.Role).HasMaxLength(100);

        builder.HasIndex(c => c.CustomerId);
    }
}
