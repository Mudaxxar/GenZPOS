using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>Login, logout and password changes. Passwords are hashed with ASP.NET Core Identity's PBKDF2 hasher.</summary>
public class AuthService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    public const string DefaultAdminUserName = "admin";
    public const string DefaultAdminPassword = "admin";
    public const int MinPasswordLength = 6;
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(5);

    private static readonly PasswordHasher<AppUser> Hasher = new();

    public static string HashPassword(AppUser user, string password) => Hasher.HashPassword(user, password);

    public static void CheckNewPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new BusinessException($"Password must be at least {MinPasswordLength} characters.");
    }

    /// <summary>
    /// First start (no users yet): creates "admin" / "admin" in the Admin role, who must choose a new password
    /// at the first login. Returns true if it was created.
    /// </summary>
    public async Task<bool> EnsureAdminAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Users.AnyAsync())
            return false;

        var admin = new AppUser
        {
            UserName = DefaultAdminUserName,
            FullName = "Owner",
            RoleId = AppRole.AdminId,
            MustChangePassword = true,
            CreatedAt = DateTime.Now,
        };
        admin.PasswordHash = HashPassword(admin, DefaultAdminPassword);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        AppLog.Info("Created the first admin user (admin / admin, must change password)");
        return true;
    }

    /// <summary>True until someone has logged in once: the login screen then shows the first-time password hint.</summary>
    public async Task<bool> IsFirstRunAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return !await db.Users.AnyAsync(u => u.LastLoginAt != null);
    }

    public async Task LoginAsync(string? userName, string? password)
    {
        userName = userName?.Trim();
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
            throw new BusinessException("Enter your user name and password.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName);
        const string wrong = "Wrong user name or password.";
        if (user is null)
            throw new BusinessException(wrong);

        var now = DateTime.Now;
        if (user.LockedUntil > now)
            throw new BusinessException(
                $"Too many wrong passwords. Try again after {user.LockedUntil:HH:mm}, or ask the owner to reset your password.");

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailedLogins)
            {
                user.LockedUntil = now + LockoutTime;
                user.FailedLogins = 0;
                AppLog.Warn($"User \"{user.UserName}\" locked for {LockoutTime.TotalMinutes:0} minutes after {MaxFailedLogins} wrong passwords");
            }
            await db.SaveChangesAsync();
            throw new BusinessException(wrong);
        }

        if (!user.IsActive)
            throw new BusinessException("This user is switched off. Ask the owner (Admin).");

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = HashPassword(user, password);
        user.FailedLogins = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync();

        currentUser.Set(await BuildSessionAsync(db, user.Id));
        AppLog.Info($"User \"{user.UserName}\" logged in");
    }

    public void Logout()
    {
        if (currentUser.Session is { } s)
            AppLog.Info($"User \"{s.UserName}\" logged out");
        currentUser.Set(null);
    }

    /// <summary>Re-reads the logged-in user's role and rights (after the owner edits roles or users).</summary>
    public async Task RefreshAsync()
    {
        if (currentUser.Session is not { } s) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var active = await db.Users.AnyAsync(u => u.Id == s.UserId && u.IsActive);
        currentUser.Set(active ? await BuildSessionAsync(db, s.UserId) : null);
    }

    public async Task ChangePasswordAsync(string? currentPassword, string? newPassword, string? confirmPassword)
    {
        var session = currentUser.Session ?? throw new BusinessException("Please log in again.");
        CheckNewPassword(newPassword);
        if (newPassword != confirmPassword)
            throw new BusinessException("The new passwords don't match.");
        if (newPassword == currentPassword)
            throw new BusinessException("Choose a password different from the current one.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId)
                   ?? throw new BusinessException("This user no longer exists.");
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
            throw new BusinessException("The current password is wrong.");

        user.PasswordHash = HashPassword(user, newPassword!);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
        currentUser.Set(session with { MustChangePassword = false });
        AppLog.Info($"User \"{user.UserName}\" changed their password");
    }

    private static async Task<UserSession> BuildSessionAsync(AppDbContext db, int userId)
    {
        var u = await db.Users.AsNoTracking()
            .Include(x => x.Role!).ThenInclude(r => r.Permissions)
            .FirstAsync(x => x.Id == userId);
        var role = u.Role!;
        var perms = role.Permissions.Select(p => p.Permission).ToHashSet();
        return new UserSession(u.Id, u.UserName, u.FullName, role.Id, role.Name, role.IsAdmin, perms, u.MustChangePassword);
    }
}
