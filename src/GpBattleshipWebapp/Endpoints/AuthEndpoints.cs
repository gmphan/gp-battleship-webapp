using System.Security.Claims;
using GpBattleshipWebapp.Auth;
using GpBattleshipWebapp.Data.Repositories;
using Microsoft.AspNetCore.Authentication;

namespace GpBattleshipWebapp.Endpoints;

/// <summary>
/// Authentication endpoints:
///   POST /api/auth/login    — verify credentials, set auth cookie
///   POST /api/auth/logout   — clear auth cookie
///   GET  /api/auth/me       — the current signed-in admin (401 if not signed in)
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();
        group.MapGet("/me", MeAsync).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        IAdminRepository admins,
        ILogger<AuthEndpointsMarker> logger,
        CancellationToken ct)
    {
        var username = (request.Username ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return Results.BadRequest(new { error = "Username and password are required." });

        var user = await admins.ValidateCredentialsAsync(username, password, ct);
        if (user is null)
        {
            // Same generic message whether the user is missing or the password is wrong.
            return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
        }

        // Record last login for DB admins (best-effort; config admins are stateless).
        if (!user.IsConfig)
            await admins.TouchLastLoginAsync(user.Username, ct);

        var identity = new ClaimsIdentity(AuthConstants.Scheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        identity.AddClaim(new Claim(AuthConstants.UsernameClaim, user.Username));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));            // role-based authorization
        identity.AddClaim(new Claim(AuthConstants.RoleClaim, user.Role));
        identity.AddClaim(new Claim(AuthConstants.SourceClaim, user.IsConfig ? "config" : "db"));

        await http.SignInAsync(AuthConstants.Scheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12),
            });

        logger.LogInformation("Admin '{User}' ({Role}) logged in.", user.Username, user.Role);
        return Results.Ok(new
        {
            username = user.Username,
            displayName = user.DisplayName,
            role = user.Role,
            source = user.IsConfig ? "config" : "db",
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(AuthConstants.Scheme);
        return Results.NoContent();
    }

    private static IResult MeAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            authenticated = true,
            username = http.User.FindFirstValue(AuthConstants.UsernameClaim) ?? http.User.Identity!.Name,
            role = http.User.FindFirstValue(ClaimTypes.Role),
            source = http.User.FindFirstValue(AuthConstants.SourceClaim),
        });
    }

    public sealed record LoginRequest(string? Username, string? Password);

    /// <summary>Marker type used only to obtain an <see cref="ILogger"/> for this class.</summary>
    public sealed class AuthEndpointsMarker { }
}
