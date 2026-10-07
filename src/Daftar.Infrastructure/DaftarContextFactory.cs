using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Daftar.Infrastructure;

/// <summary>
/// Used only by the EF Core design-time tools (dotnet ef migrations). The real application
/// registers the context through <see cref="DaftarServiceCollectionExtensions"/>.
/// </summary>
public sealed class DaftarContextFactory : IDesignTimeDbContextFactory<DaftarContext>
{
    public DaftarContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DaftarContext>()
            .UseSqlite("Data Source=daftar-design.db")
            .Options;

        return new DaftarContext(options);
    }
}