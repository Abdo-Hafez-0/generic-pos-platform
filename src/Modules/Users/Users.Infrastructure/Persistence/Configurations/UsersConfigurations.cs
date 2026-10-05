using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Users.Domain.Entities;
using Users.Domain.ValueObjects;

namespace Users.Infrastructure.Persistence.Configurations;

/// <summary>User aggregate root. Table: usr_Users</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("usr_Users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasConversion(id => id.Value, v => new UserId(v)).IsRequired();

        builder.Property(u => u.Username).HasMaxLength(User.MaxUsernameLength).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(User.MaxDisplayNameLength).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(User.MaxEmailLength);
        builder.Property(u => u.Status).HasConversion<int>().IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.UpdatedAt);

        builder.HasIndex(u => u.Username).IsUnique();
        builder.HasIndex(u => u.Status);

        builder.HasMany(u => u.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Role aggregate root. Table: usr_Roles</summary>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("usr_Roles");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasConversion(id => id.Value, v => new RoleId(v)).IsRequired();

        builder.Property(r => r.Name).HasMaxLength(Role.MaxNameLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(Role.MaxDescriptionLength);
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasIndex(r => r.Name).IsUnique();

        builder.HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Table: usr_RolePermissions (composite key RoleId + Permission)</summary>
internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("usr_RolePermissions");

        builder.Property(p => p.RoleId).HasConversion(id => id.Value, v => new RoleId(v)).IsRequired();
        builder.Property(p => p.Permission).HasMaxLength(PermissionCode.MaxLength).IsRequired();
        builder.HasKey(p => new { p.RoleId, p.Permission });
    }
}

/// <summary>Table: usr_UserRoles (composite key UserId + RoleId). Both ends are in this module, so real foreign keys are allowed.</summary>
internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("usr_UserRoles");

        builder.Property(r => r.UserId).HasConversion(id => id.Value, v => new UserId(v)).IsRequired();
        builder.Property(r => r.RoleId).HasConversion(id => id.Value, v => new RoleId(v)).IsRequired();
        builder.HasKey(r => new { r.UserId, r.RoleId });

        builder.HasIndex(r => r.RoleId);
        builder.HasOne<Role>().WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Table: usr_UserCredentials (one row per user that has a password). Holds only a HASH, never a password.</summary>
internal sealed class UserCredentialConfiguration : IEntityTypeConfiguration<UserCredential>
{
    public void Configure(EntityTypeBuilder<UserCredential> builder)
    {
        builder.ToTable("usr_UserCredentials");

        builder.HasKey(c => c.UserId);
        builder.Property(c => c.UserId).HasConversion(id => id.Value, v => new UserId(v)).IsRequired();
        builder.Property(c => c.PasswordHash).HasMaxLength(UserCredential.MaxHashLength).IsRequired();
        builder.Property(c => c.PasswordChangedAt).IsRequired();
        builder.Property(c => c.MustChangePassword).IsRequired();
        builder.Property(c => c.FailedAttempts).IsRequired();
        builder.Property(c => c.LockedUntil);
        builder.Property(c => c.LastFailedAt);
        builder.Property(c => c.LastSignInAt);

        builder.HasOne<User>().WithOne().HasForeignKey<UserCredential>(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
