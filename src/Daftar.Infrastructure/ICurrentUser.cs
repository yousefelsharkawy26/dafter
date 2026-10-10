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

public sealed class CurrentUser : ICurrentUser
{
    public int? UserId { get; private set; }

    public void SignIn(int userId) => UserId = userId;

    public void SignOut() => UserId = null;
}