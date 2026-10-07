using Daftar.Domain.Security;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class RoleConfig : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Roles");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedOnAdd();
        b.Property(r => r.Name).IsRequired();
        b.Property(r => r.Permissions).IsRequired();
        b.HasIndex(r => r.Name).IsUnique();

        AuditColumns.Map(b);
    }
}

public sealed class UserConfig : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(u => u.Id);
        b.Property(u => u.Id).ValueGeneratedOnAdd();
        b.Property(u => u.Username).IsRequired();
        b.Property(u => u.FullName).IsRequired();
        b.Property(u => u.PasswordHash).IsRequired();
        b.Property(u => u.IsActive).HasDefaultValue(true);
        b.Property(u => u.MustChangePassword).HasDefaultValue(false);

        // Username is unique case-insensitively (NOCASE).
        b.HasIndex(u => u.Username).IsUnique();

        b.HasOne(u => u.Role)
            .WithMany()
            .HasForeignKey(u => u.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        AuditColumns.Map(b);
    }
}

public sealed class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedOnAdd();
        b.Property(a => a.Action).IsRequired();
        b.Property(a => a.EntityName).IsRequired();
        b.Property(a => a.EntityId).IsRequired();

        // UserId is deliberately not an FK: users are never deleted.
        b.HasIndex(a => new { a.EntityName, a.EntityId });
        b.HasIndex(a => a.At);
        b.HasIndex(a => a.UserId);
    }
}