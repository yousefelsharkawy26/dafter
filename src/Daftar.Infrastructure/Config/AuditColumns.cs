using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

/// <summary>
/// Maps the +A audit columns. CreatedAt/CreatedBy are stamped by the SaveChanges interceptor.
/// Every column except Id, BusinessDate and the documented exceptions.
/// </summary>
public static class AuditColumns
{
    public static void Map<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder)
        where TEntity : class, Domain.Shared.IAuditable
    {
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.CreatedBy).IsRequired();
        builder.Property(e => e.UpdatedAt);
        builder.Property(e => e.UpdatedBy);
    }
}