using Daftar.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daftar.Domain.Tests;

/// <summary>
/// Minimal host: one SQLite file, the audit interceptor registered, no Avalonia.
/// </summary>
public sealed class ServiceProviderFixture : IDisposable
{
    private ServiceProviderFixture(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; }

    public static async Task<ServiceProviderFixture> CreateAsync(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddDaftarPersistence(databasePath);
        return new ServiceProviderFixture(services.BuildServiceProvider());
    }

    public void Dispose() => (Services as IDisposable)?.Dispose();
}

public static class ScopeExtensions
{
    /// <summary>One-shot context from a fresh scope. The caller owns the scope.</summary>
    public static DaftarContext GetRequiredContext(this IServiceProvider services)
        => services.GetService(typeof(DaftarContext)) as DaftarContext
           ?? throw new InvalidOperationException("DaftarContext is not registered.");
}