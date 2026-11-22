using GpBattleshipWebapp.Models;

namespace GpBattleshipWebapp.Data.Repositories;

/// <summary>
/// Admin account store. Combines the config-based admins (from appsettings /
/// variables.env) with the database-backed admins into one merged view.
/// Config admins are authoritative: they always exist, cannot be deleted, and their
/// role/password come from configuration.
/// </summary>
public interface IAdminRepository : IGenericRepository<AdminUser>
{
    /// <summary>
    /// Validate credentials. Returns a merged admin (Id == Guid.Empty / IsConfig for
    /// config admins) if valid & active, else null.
    /// </summary>
    Task<AdminUser?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Look up an admin (config or DB) by username (case-insensitive).</summary>
    Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>All admins (config first, then DB), for the management UI.</summary>
    Task<IReadOnlyList<AdminUser>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Create a DB-backed admin. Throws <see cref="AdminConflictException"/> if the
    /// username already exists (in config or DB).
    /// </summary>
    Task<AdminUser> CreateAsync(string username, string password, string role, string? displayName = null, CancellationToken ct = default);

    /// <summary>Record a successful login timestamp (DB admins only; config admins are stateless).</summary>
    Task TouchLastLoginAsync(string username, CancellationToken ct = default);
}

public class AdminConflictException : Exception
{
    public AdminConflictException(string message) : base(message) { }
}
