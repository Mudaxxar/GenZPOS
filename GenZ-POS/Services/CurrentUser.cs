using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>Who is logged in, with the rights of their role (read once at login).</summary>
public sealed record UserSession(
    int UserId, string UserName, string? FullName, int RoleId, string RoleName, bool IsAdmin,
    IReadOnlySet<string> Permissions, bool MustChangePassword)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? UserName : FullName;

    public bool Has(string permission) => IsAdmin || Permissions.Contains(permission);
}

/// <summary>
/// The logged-in user for this app window (singleton). Pages use it to hide buttons; services call
/// <see cref="Demand"/> so a right is enforced even if a screen forgets to hide something.
/// </summary>
public sealed class CurrentUser
{
    public UserSession? Session { get; private set; }

    public bool IsSignedIn => Session is not null;

    /// <summary>Raised on login, logout and when the user's rights are reloaded.</summary>
    public event Action? Changed;

    public bool Has(string permission) => Session?.Has(permission) == true;

    /// <summary>Throws a <see cref="BusinessException"/> (shown as a normal message) if the user lacks the right.</summary>
    public void Demand(string permission)
    {
        if (Session is null)
            throw new BusinessException("Please log in again.");
        if (!Session.Has(permission))
            throw new BusinessException(
                $"You don't have the right \"{Permissions.Describe(permission)}\". Ask the owner (Admin) to give it to your role.");
    }

    internal void Set(UserSession? session)
    {
        Session = session;
        Changed?.Invoke();
    }

    /// <summary>An always-allowed user for tests and background jobs (never used by the UI).</summary>
    public static CurrentUser ForSystem()
    {
        var user = new CurrentUser();
        user.Set(new UserSession(0, "system", "System", AppRole.AdminId, "Admin", IsAdmin: true,
            new HashSet<string>(), MustChangePassword: false));
        return user;
    }
}
