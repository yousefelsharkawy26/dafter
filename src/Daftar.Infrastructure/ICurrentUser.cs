namespace Daftar.Infrastructure;

/// <summary>Who is acting. Null means system (seeding, background work, a failed login).</summary>
public interface ICurrentUser
{
    int? UserId { get; }
}

public sealed class CurrentUser : ICurrentUser
{
    public int? UserId { get; set; }
}