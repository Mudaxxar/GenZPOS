using System.ComponentModel.DataAnnotations;

namespace GenZPOS.Data.Entities;

/// <summary>A person who can log in to GenZ-POS. One role per user.</summary>
public class AppUser
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string UserName { get; set; } = "";

    [MaxLength(100)]
    public string? FullName { get; set; }

    /// <summary>ASP.NET Core Identity password hash (PBKDF2). Never the password itself.</summary>
    [Required, MaxLength(200)]
    public string PasswordHash { get; set; } = "";

    public int RoleId { get; set; }
    public AppRole? Role { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Set for a new user / after a password reset: the next login must choose a new password.</summary>
    public bool MustChangePassword { get; set; }

    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>A named set of rights (see <c>Services/Permissions.cs</c>).</summary>
public class AppRole
{
    public const int AdminId = 1;
    public const int ManagerId = 2;
    public const int CashierId = 3;

    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = "";

    [MaxLength(200)]
    public string? Description { get; set; }

    /// <summary>The built-in Admin role: has every right, can't be edited or deleted.</summary>
    public bool IsAdmin { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission
{
    public int RoleId { get; set; }

    [Required, MaxLength(50)]
    public string Permission { get; set; } = "";
}
