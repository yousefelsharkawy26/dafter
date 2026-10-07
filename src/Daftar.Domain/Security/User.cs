using Daftar.Domain.Shared;

namespace Daftar.Domain.Security;

public sealed class User : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;

    /// <summary>Hashed, never plain. Format is decided by the Infrastructure password hasher.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public int RoleId { get; set; }
    public Role? Role { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public void RecordLogin() => LastLoginAt = DateTime.UtcNow;

    public bool Can(string code) => IsActive && Role is not null && Role.Has(code);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Username))
            throw new DomainException("Username is required.");
        if (string.IsNullOrWhiteSpace(PasswordHash))
            throw new DomainException("PasswordHash is required; passwords are never stored plain.");
    }
}