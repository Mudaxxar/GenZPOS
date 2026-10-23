using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

public class UserRow
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string? FullName { get; set; }
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class RoleRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsAdmin { get; set; }
    public int UserCount { get; set; }
    public List<string> Permissions { get; set; } = [];
}

/// <summary>Users &amp; Roles screen. Every call needs the "Users.Manage" right.</summary>
public class UserService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser, AuthService auth)
{
    public async Task<List<UserRow>> GetUsersAsync()
    {
        currentUser.Demand(Permissions.UsersManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.Now;
        return await db.Users.AsNoTracking()
            .OrderBy(u => u.UserName)
            .Select(u => new UserRow
            {
                Id = u.Id, UserName = u.UserName, FullName = u.FullName, RoleId = u.RoleId, RoleName = u.Role!.Name,
                IsActive = u.IsActive, MustChangePassword = u.MustChangePassword,
                IsLocked = u.LockedUntil != null && u.LockedUntil > now, LastLoginAt = u.LastLoginAt,
            })
            .ToListAsync();
    }

    public async Task<List<RoleRow>> GetRolesAsync()
    {
        currentUser.Demand(Permissions.UsersManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Id).ToListAsync();
        var counts = await db.Users.GroupBy(u => u.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        return roles.Select(r => new RoleRow
        {
            Id = r.Id, Name = r.Name, Description = r.Description, IsAdmin = r.IsAdmin,
            UserCount = counts.GetValueOrDefault(r.Id),
            Permissions = r.Permissions.Select(p => p.Permission).ToList(),
        }).ToList();
    }

    /// <summary>
    /// Adds or edits a user. <paramref name="password"/> is required for a new user; for an existing user it resets
    /// the password when filled in. A new / reset password must be changed at the next login when
    /// <paramref name="mustChangePassword"/> is set.
    /// </summary>
    public async Task<int> SaveUserAsync(UserRow model, string? password, bool mustChangePassword)
    {
        currentUser.Demand(Permissions.UsersManage);
        var userName = model.UserName.Trim();
        if (userName.Length == 0)
            throw new BusinessException("Enter a user name.");
        if (userName.Contains(' '))
            throw new BusinessException("User name can't contain spaces.");
        var isNew = model.Id == 0;
        if (isNew || !string.IsNullOrEmpty(password))
            AuthService.CheckNewPassword(password);

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        if (!await db.Roles.AnyAsync(r => r.Id == model.RoleId))
            throw new BusinessException("Select a role.");
        if (await db.Users.AnyAsync(u => u.UserName == userName && u.Id != model.Id))
            throw new BusinessException($"User name \"{userName}\" is already taken.");

        AppUser user;
        if (isNew)
        {
            user = new AppUser { CreatedAt = DateTime.Now };
            db.Users.Add(user);
        }
        else
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == model.Id)
                   ?? throw new BusinessException("This user no longer exists.");
        }

        var self = currentUser.Session?.UserId == user.Id;
        if (self && !model.IsActive)
            throw new BusinessException("You can't switch off your own user.");

        user.UserName = userName;
        user.FullName = string.IsNullOrWhiteSpace(model.FullName) ? null : model.FullName.Trim();
        user.RoleId = model.RoleId;
        user.IsActive = model.IsActive;
        if (!string.IsNullOrEmpty(password))
        {
            user.PasswordHash = AuthService.HashPassword(user, password);
            user.MustChangePassword = mustChangePassword;
            user.FailedLogins = 0;
            user.LockedUntil = null;
        }
        await db.SaveChangesAsync();
        await EnsureActiveAdminAsync(db);
        await tx.CommitAsync();

        AppLog.Info($"User \"{userName}\" {(isNew ? "added" : "changed")} by \"{currentUser.Session?.UserName}\"");
        if (self) await auth.RefreshAsync();
        return user.Id;
    }

    public async Task UnlockAsync(int userId)
    {
        currentUser.Demand(Permissions.UsersManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockedUntil, (DateTime?)null).SetProperty(u => u.FailedLogins, 0));
    }

    public async Task DeleteUserAsync(int userId)
    {
        currentUser.Demand(Permissions.UsersManage);
        if (currentUser.Session?.UserId == userId)
            throw new BusinessException("You can't delete your own user.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var deleted = await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();
        if (deleted == 0)
            throw new BusinessException("User not found. It may already have been deleted.");
        await EnsureActiveAdminAsync(db);
        await tx.CommitAsync();
    }

    public async Task<int> SaveRoleAsync(RoleRow model)
    {
        currentUser.Demand(Permissions.UsersManage);
        var name = model.Name.Trim();
        if (name.Length == 0)
            throw new BusinessException("Enter a role name.");
        var perms = model.Permissions.Intersect(Permissions.All).Distinct().ToList();

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != model.Id))
            throw new BusinessException($"A role named \"{name}\" already exists.");

        AppRole role;
        if (model.Id == 0)
        {
            role = new AppRole();
            db.Roles.Add(role);
        }
        else
        {
            role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == model.Id)
                   ?? throw new BusinessException("This role no longer exists.");
            if (role.IsAdmin)
                throw new BusinessException("The Admin role always has every right and can't be changed.");
        }

        role.Name = name;
        role.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        role.Permissions.RemoveAll(p => !perms.Contains(p.Permission));
        foreach (var p in perms.Where(p => role.Permissions.All(x => x.Permission != p)))
            role.Permissions.Add(new RolePermission { Permission = p });
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        AppLog.Info($"Role \"{name}\" saved by \"{currentUser.Session?.UserName}\": {string.Join(", ", perms)}");
        if (currentUser.Session?.RoleId == role.Id) await auth.RefreshAsync();
        return role.Id;
    }

    public async Task DeleteRoleAsync(int roleId)
    {
        currentUser.Demand(Permissions.UsersManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId)
                   ?? throw new BusinessException("Role not found. It may already have been deleted.");
        if (role.IsAdmin)
            throw new BusinessException("The Admin role can't be deleted.");
        var users = await db.Users.CountAsync(u => u.RoleId == roleId);
        if (users > 0)
            throw new BusinessException($"{users} user(s) have this role. Give them another role first.");
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
    }

    /// <summary>Nobody could manage users any more without an active Admin: refuse the change.</summary>
    private static async Task EnsureActiveAdminAsync(AppDbContext db)
    {
        if (!await db.Users.AnyAsync(u => u.IsActive && u.Role!.IsAdmin))
            throw new BusinessException("At least one active user must keep the Admin role.");
    }
}
