using GpBattleshipWebapp.Models;

namespace GpBattleshipWebapp.Auth;

/// <summary>A config-based admin (from appsettings / variables.env).</summary>
public class ConfigAdminOptions
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Role { get; set; }
}

/// <summary>
/// Binds the "Auth" configuration section:
///
///   Auth:InitialAdmin:Username   (default "SuperAdmin")
///   Auth:InitialAdmin:Password    (required to seed a SuperAdmin)
///   Auth:InitialAdmin:Role        (default "SuperAdmin")
///   Auth:SeedAdmins:0:Username    (optional extra config admins)
///   Auth:SeedAdmins:0:Password
///   Auth:SeedAdmins:0:Role
///
/// Env form (variables.env): Auth__InitialAdmin__Password=..., etc.
/// </summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    public ConfigAdminOptions? InitialAdmin { get; set; }
    public List<ConfigAdminOptions> SeedAdmins { get; set; } = new();

    /// <summary>
    /// All valid, fully-specified config admins (initial admin + seed list), with
    /// defaulted usernames/roles. Config admins are authoritative over DB admins.
    /// </summary>
    public IReadOnlyList<ConfigAdminOptions> EffectiveConfigAdmins()
    {
        var list = new List<ConfigAdminOptions>();

        if (InitialAdmin is { } ia && !string.IsNullOrWhiteSpace(ia.Password))
        {
            list.Add(new ConfigAdminOptions
            {
                Username = string.IsNullOrWhiteSpace(ia.Username) ? "SuperAdmin" : ia.Username!.Trim(),
                Password = ia.Password,
                Role = AdminRoles.IsValid(ia.Role) ? ia.Role : AdminRoles.SuperAdmin,
            });
        }

        foreach (var sa in SeedAdmins)
        {
            if (sa is null || string.IsNullOrWhiteSpace(sa.Username) || string.IsNullOrWhiteSpace(sa.Password))
                continue;
            list.Add(new ConfigAdminOptions
            {
                Username = sa.Username!.Trim(),
                Password = sa.Password,
                Role = AdminRoles.IsValid(sa.Role) ? sa.Role : AdminRoles.Admin,
            });
        }

        return list;
    }
}
