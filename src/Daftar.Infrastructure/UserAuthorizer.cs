using Daftar.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Daftar.Infrastructure;

/// <summary>
/// Answers "may the signed-in user do this?" from the database rather than from UI state.
///
/// <para>
/// <c>User.Can</c> existed and was never called: permission codes were modelled, editable in the
/// settings screen, and consulted nowhere. Every page was registered unconditionally and user
/// creation had no gate, so a cashier could open Settings, add an account, give it the Admin role
/// and sign in as one. Enforcing here rather than in the ViewModels is the point -- a UI check
/// hides a button, a service check refuses the write.
/// </para>
/// </summary>
public interface IUserAuthorizer
{
    /// <summary>True when the signed-in user holds the permission.</summary>
    Task<bool> CanAsync(string permissionCode, CancellationToken ct = default);

    /// <summary>Throws <see cref="Domain.Shared.DomainException"/> unless permitted.</summary>
    Task RequireAsync(string permissionCode, CancellationToken ct = default);

    /// <summary>
    /// The deepest line discount the signed-in user may apply, as a percentage. Null means
    /// unrestricted (no role, or a role with no ceiling configured).
    /// </summary>
    Task<decimal?> MaxDiscountPercentAsync(CancellationToken ct = default);
}

public sealed class UserAuthorizer(ICurrentUser currentUser, DaftarContext context) : IUserAuthorizer
{
    public async Task<bool> CanAsync(string permissionCode, CancellationToken ct = default)
    {
        var role = await CurrentRoleAsync(ct);

        if (role is null)
            return false;

        // An empty code is a presence check: "is anyone signed in with a usable role?"
        return string.IsNullOrWhiteSpace(permissionCode) || role.Has(permissionCode);
    }

    public async Task RequireAsync(string permissionCode, CancellationToken ct = default)
    {
        if (!await CanAsync(permissionCode, ct))
            throw new Domain.Shared.DomainException(
                $"ليست عندك صلاحية '{permissionCode}' للعملية دي.");
    }

    public async Task<decimal?> MaxDiscountPercentAsync(CancellationToken ct = default)
    {
        var role = await CurrentRoleAsync(ct);
        return role?.MaxDiscountPercent;
    }

    /// <summary>
    /// The role behind the signed-in user, or null when nobody is signed in. An inactive account
    /// resolves to null rather than to its permissions: disabling a user must take effect
    /// immediately, not at their next sign-in.
    /// </summary>
    private async Task<Role?> CurrentRoleAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return null;

        return await context.Users
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.Role!)
            .FirstOrDefaultAsync(ct);
    }
}