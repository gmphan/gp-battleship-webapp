using GpBattleshipWebapp.Data.Repositories;
using GpBattleshipWebapp.Models;

namespace GpBattleshipWebapp.Endpoints;

/// <summary>
/// SuperAdmin-only endpoints to manage admin accounts.
///   GET    /api/admins         — list (config + DB)
///   POST   /api/admins         — create a DB admin
///   PUT    /api/admins/{id}    — update a DB admin's role / active state
///   DELETE /api/admins/{id}    — delete a DB admin
/// Config admins (from variables.env) are read-only and cannot be modified/deleted.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admins")
            .RequireAuthorization(policy => policy.RequireRole(AdminRoles.SuperAdmin));

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(IAdminRepository admins, CancellationToken ct)
    {
        var list = await admins.ListAllAsync(ct);
        return Results.Ok(list.Select(ToDto));
    }

    private static async Task<IResult> CreateAsync(CreateAdminRequest req, IAdminRepository admins, CancellationToken ct)
    {
        try
        {
            var role = AdminRoles.IsValid(req.Role) ? req.Role! : AdminRoles.Admin;
            var created = await admins.CreateAsync(
                req.Username ?? string.Empty, req.Password ?? string.Empty, role, req.DisplayName, ct);
            return Results.Ok(ToDto(created));
        }
        catch (AdminConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateAdminRequest req, IAdminRepository admins, CancellationToken ct)
    {
        var user = await admins.GetByIdAsync(id, ct);
        if (user is null) return Results.NotFound(new { error = "Admin not found." });

        if (req.Role is not null)
        {
            if (!AdminRoles.IsValid(req.Role)) return Results.BadRequest(new { error = "Invalid role." });
            user.Role = req.Role;
        }
        if (req.IsActive.HasValue) user.IsActive = req.IsActive.Value;
        if (req.DisplayName is not null) user.DisplayName = req.DisplayName.Trim();

        admins.Update(user);
        await admins.SaveChangesAsync(ct);
        return Results.Ok(ToDto(user));
    }

    private static async Task<IResult> DeleteAsync(Guid id, IAdminRepository admins, CancellationToken ct)
    {
        var user = await admins.GetByIdAsync(id, ct);
        if (user is null) return Results.NotFound(new { error = "Admin not found." });

        admins.Remove(user);
        await admins.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static AdminDto ToDto(AdminUser u) => new(
        Id: u.IsConfig ? (Guid?)null : u.Id,
        Username: u.Username,
        DisplayName: u.DisplayName,
        Role: u.Role,
        IsActive: u.IsActive,
        IsConfig: u.IsConfig,
        CreatedAtUtc: u.IsConfig ? null : u.CreatedAtUtc,
        LastLoginUtc: u.IsConfig ? null : u.LastLoginUtc);

    public sealed record AdminDto(
        Guid? Id, string Username, string DisplayName, string Role, bool IsActive,
        bool IsConfig, DateTime? CreatedAtUtc, DateTime? LastLoginUtc);

    public sealed record CreateAdminRequest(string? Username, string? Password, string? Role, string? DisplayName);
    public sealed record UpdateAdminRequest(string? Role, bool? IsActive, string? DisplayName);
}
