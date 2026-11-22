using GpBattleshipWebapp.Data;
using GpBattleshipWebapp.Endpoints;
using GpBattleshipWebapp.Hubs;
using GpBattleshipWebapp.Injectors;
using Microsoft.AspNetCore.HttpOverrides;
using QRCoder;

var builder = WebApplication.CreateBuilder(args);

// Behind Cloudflare Tunnel / reverse proxy: honour X-Forwarded-* so generated
// URLs and HTTPS detection are correct.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    // Trust the tunnel/proxy network (Cloudflare connects from the local container network).
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Discover and run all IInjector implementations (DbInjector, AuthInjector, SignalRInjector).
builder.Services.InjectServicesFromAssemblies(builder.Configuration);

// Public base URL used to build the QR join link, e.g. https://battleship.example.com
// Configure via appsettings or the PUBLIC_BASE_URL environment variable.
var publicBaseUrl = builder.Configuration["PUBLIC_BASE_URL"] ?? "";
builder.Services.AddSingleton(new PublicUrlOptions(publicBaseUrl));

var app = builder.Build();

// Startup: apply EF migrations (creates the schema on first run).
await StartupTasks.InitializeAsync(app);

app.UseForwardedHeaders();

// Enforce HTTPS. Behind Cloudflare the request arrives as HTTP with
// X-Forwarded-Proto=https, so proxied requests are unaffected but direct hits are protected.
app.UseHttpsRedirection();

// Authentication/authorization middleware (cookie auth via AuthInjector).
app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
// Serve mutable app assets (HTML/JS/CSS) with no-cache + revalidation so a new
// deploy is picked up immediately instead of being served stale from the browser
// or an intermediate cache (e.g. Cloudflare). Unchanged files still return fast 304s.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        var ext = ctx.File.Name is { } name && name.LastIndexOf('.') is var dot && dot >= 0
            ? name[(dot + 1)..].ToLowerInvariant()
            : "";
        if (ext is "html" or "js" or "css" or "json")
        {
            headers.CacheControl = "no-cache, must-revalidate";
            headers["CDN-Cache-Control"] = "no-store";
            headers["Cloudflare-CDN-Cache-Control"] = "no-store";
        }
        else
        {
            headers.CacheControl = "public, max-age=86400";
        }
    }
});

app.MapGet("/", () => Results.Redirect("/index.html"));

// Generate a QR PNG that encodes the shared join URL for a given room.
app.MapGet("/api/qr/{code}", (string code, HttpRequest request, PublicUrlOptions opts) =>
{
    code = (code ?? "").Trim().ToUpperInvariant();
    if (code.Length == 0 || code.Length > 12)
        return Results.BadRequest("Invalid room code.");

    var baseUrl = !string.IsNullOrWhiteSpace(opts.PublicBaseUrl)
        ? opts.PublicBaseUrl.TrimEnd('/')
        : $"{request.Scheme}://{request.Host}";

    var joinUrl = $"{baseUrl}/play.html?room={Uri.EscapeDataString(code)}";

    using var generator = new QRCodeGenerator();
    using var data = generator.CreateQrCode(joinUrl, QRCodeGenerator.ECCLevel.Q);
    var png = new PngByteQRCode(data).GetGraphic(10);

    return Results.File(png, "image/png");
});

app.MapGet("/api/config", (PublicUrlOptions opts) =>
    Results.Ok(new { publicBaseUrl = opts.PublicBaseUrl }));

// Auth (login/logout/me) + SuperAdmin-only admin management.
app.MapAuthEndpoints();
app.MapAdminEndpoints();

// The hub supports BOTH anonymous players (QR scan) and authenticated hosts.
app.MapHub<BattleshipHub>("/hub/battleship");

app.Run();

public sealed record PublicUrlOptions(string PublicBaseUrl);
