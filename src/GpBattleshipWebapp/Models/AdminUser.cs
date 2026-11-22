namespace GpBattleshipWebapp.Models;

/// <summary>Role constants for admin accounts.</summary>
public static class AdminRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";

    public static bool IsValid(string? role) =>
        role == SuperAdmin || role == Admin;
}

/// <summary>
/// A runtime-managed admin account stored in the database.
///
/// Config-based admins (from appsettings / variables.env via Auth:InitialAdmin and
/// Auth:SeedAdmins) are NOT stored here — they are resolved from configuration and
/// always exist. This entity represents admins that a SuperAdmin creates at runtime.
/// </summary>
public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Login handle (unique, case-insensitive). Stored lower-case.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Friendly name shown in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash encoded as "iterations.saltB64.hashB64". See PasswordHasher.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>"SuperAdmin" or "Admin".</summary>
    public string Role { get; set; } = AdminRoles.Admin;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginUtc { get; set; }

    /// <summary>
    /// True when this admin comes from configuration (appsettings / variables.env)
    /// rather than the database. Config admins are read-only (can't be edited/deleted)
    /// and have no persisted Id. Not mapped to a column.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsConfig { get; set; }
}
