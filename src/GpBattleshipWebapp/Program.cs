using GpBattleshipWebapp.Hubs;
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

builder.Services.AddSignalR();

// Public base URL used to build the QR join link, e.g. https://battleship.example.com
// Configure via appsettings or the PUBLIC_BASE_URL environment variable.
var publicBaseUrl = builder.Configuration["PUBLIC_BASE_URL"] ?? "";
builder.Services.AddSingleton(new PublicUrlOptions(publicBaseUrl));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/index.html"));

// Generate a QR PNG that encodes the shared join URL for a given room.
app.MapGet("/api/qr/{code}", (string code, HttpRequest request, PublicUrlOptions opts) =>
{
    code = (code ?? "").Trim().ToUpperInvariant();
    if (code.Length == 0 || code.Length > 12)
    {
        return Results.BadRequest("Invalid room code.");
    }

    // Prefer the configured public URL (correct behind the tunnel); otherwise
    // fall back to the request's own scheme/host.
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
{
    // Expose the public base URL so the admin page can display the join link.
    return Results.Ok(new { publicBaseUrl = opts.PublicBaseUrl });
});

app.MapHub<BattleshipHub>("/hub/battleship");

app.Run();

public sealed record PublicUrlOptions(string PublicBaseUrl);

