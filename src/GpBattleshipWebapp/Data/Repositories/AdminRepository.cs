using GpBattleshipWebapp.Auth;
using GpBattleshipWebapp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GpBattleshipWebapp.Data.Repositories;

/// <summary>
/// Merges config-based admins (appsettings / variables.env, authoritative & read-only)
/// with DB-backed admins (managed at runtime by a SuperAdmin).
///
/// DB admins store PBKDF2 hashes. Config admins store plaintext only in configuration
/// (never in DB); their password is verified directly against config.
/// </summary>
public class AdminRepository : GenericRepository<AdminUser>, IAdminRepository
{
    private readonly IReadOnlyList<ConfigAdminOptions> _configAdmins;
    private readonly ILogger<AdminRepository> _log;
    private readonly HashSet<string> _configUsernames;

    public AdminRepository(AppDbContext context, IOptions<AuthOptions> auth, ILogger<AdminRepository> log)
        : base(context)
    {
        _log = log;
        _configAdmins = auth.Value.EffectiveConfigAdmins();
        _configUsernames = _configAdmins.Select(a => Normalize(a.Username!)).ToHashSet();
    }

    private static string Normalize(string u) => (u ?? string.Empty).Trim().ToLowerInvariant();

    private ConfigAdminOptions? FindConfigAdmin(string username)
    {
        var n = Normalize(username);
        return _configAdmins.FirstOrDefault(a => Normalize(a.Username!) == n);
    }

    private static AdminUser ConfigAdminToModel(ConfigAdminOptions a) => new()
    {
        Id = Guid.Empty,
        Username = Normalize(a.Username!),
        DisplayName = a.Username!,
        Role = AdminRoles.IsValid(a.Role) ? a.Role! : AdminRoles.Admin,
        IsActive = true,
        IsConfig = true,
        PasswordHash = string.Empty, // verified against config, never stored
    };

    public async Task<AdminUser?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return null;

        // 1) Config admin wins (authoritative).
        var cfg = FindConfigAdmin(username);
        if (cfg != null)
        {
            return string.Equals(cfg.Password, password, StringComparison.Ordinal)
                ? ConfigAdminToModel(cfg)
                : null;
        }

        // 2) DB admin.
        var user = await GetByUsernameAsync(username, ct);
        if (user is null || !user.IsActive) return null;

        return PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }

    public Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        var cfg = FindConfigAdmin(username);
        if (cfg != null) return Task.FromResult<AdminUser?>(ConfigAdminToModel(cfg));

        var n = Normalize(username);
        return DbSet.AsNoTracking().FirstOrDefaultAsync(u => u.Username == n, ct);
    }

    public async Task<IReadOnlyList<AdminUser>> ListAllAsync(CancellationToken ct = default)
    {
        var list = new List<AdminUser>();
        list.AddRange(_configAdmins.Select(ConfigAdminToModel));

        var dbAdmins = await DbSet.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
        list.AddRange(dbAdmins);
        return list;
    }

    public async Task<AdminUser> CreateAsync(string username, string password, string role, string? displayName = null, CancellationToken ct = default)
    {
        var n = Normalize(username);
        if (n.Length is < 3 or > 64)
            throw new ArgumentException("Username must be 3–64 characters.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.");
        if (!AdminRoles.IsValid(role))
            throw new ArgumentException("Invalid role.");

        // Cannot collide with a config admin (they're authoritative & always present).
        if (_configUsernames.Contains(n))
            throw new AdminConflictException("That username is reserved by configuration.");

        if (await DbSet.AnyAsync(u => u.Username == n, ct))
            throw new AdminConflictException("That username is already taken.");

        var user = new AdminUser
        {
            Username = n,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        await AddAsync(user, ct);
        await SaveChangesAsync(ct);
        _log.LogInformation("Created DB admin '{User}' with role {Role}.", n, role);
        return user;
    }

    public async Task TouchLastLoginAsync(string username, CancellationToken ct = default)
    {
        // Config admins are stateless; only DB rows record a login timestamp.
        var n = Normalize(username);
        var user = await DbSet.FirstOrDefaultAsync(u => u.Username == n, ct);
        if (user is null) return;
        user.LastLoginUtc = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }
}
