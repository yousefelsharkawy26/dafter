namespace Daftar.Infrastructure;

/// <summary>Who is acting. Null means system (seeding, background work, a failed login).</summary>
public interface ICurrentUser
{
    int? UserId { get; }

    /// <summary>
    /// Clears the signed-in identity. Called on sign-out: the singleton outlives the session, and
    /// without this every write between sign-out and the next sign-in was stamped with the
    /// previous user's id -- which is exactly where an audit trail is supposed to be trusted.
    /// </summary>
    void SignOut();
}

/// <summary>
/// The signed-in identity for the current logical operation.
/// </summary>
/// <remarks>
/// A singleton with a plain field is wrong here: EF contexts are scoped and are created per
/// operation, so any code that reaches the identity through a scope gets whatever value happened
/// to be there. <see cref="AsyncLocal{T}"/> flows with the async call context instead, so a posting
/// operation and the audit interceptor that stamps it always agree on who is acting, even when
/// several operations are in flight.
/// </remarks>
public sealed class CurrentUser : ICurrentUser
{
    public int? UserId
    {
        get => CurrentUserIdentity.Value?.UserId;
        set => CurrentUserIdentity.Value = value is { } id ? new CurrentUserState(id) : null;
    }

    public void SignIn(int userId) => UserId = userId;

    public void SignOut() => UserId = null;
}

/// <summary>The signed-in id for the current async flow.</summary>
internal static class CurrentUserIdentity
{
    private static readonly AsyncLocal<CurrentUserState?> _current = new();

    internal static CurrentUserState? Value
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

internal sealed record CurrentUserState(int UserId);