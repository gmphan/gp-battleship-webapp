using Microsoft.AspNetCore.Authentication.Cookies;

namespace GpBattleshipWebapp.Auth;

/// <summary>
/// Cookie-based authentication for the admin console + management API.
///
/// - API requests (/api/*) return 401/403 instead of redirecting, so the JS can react.
/// - The admin page itself is gated client-side (index.html shows a login overlay when
///   /api/auth/me returns 401); player pages stay public so QR scaners keep working.
/// The same cookie authorizes the SignalR hub.
/// </summary>
public static class AuthSetup
{
    public static IServiceCollection AddBattleshipAuth(this IServiceCollection services)
    {
        services
            .AddAuthentication(AuthConstants.Scheme)
            .AddCookie(AuthConstants.Scheme, options =>
            {
                options.Cookie.Name = AuthConstants.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Site is HTTPS via Cloudflare, but allow http on the LAN too.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
                options.LoginPath = AuthConstants.LoginPath;

                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }
                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        }
                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }
}
