using System.Threading.RateLimiting;
using Bardie.Harness.Auth;
using Bardie.Logos.Channel;
using Bardie.Logos.Channel.Certificates;
using Bardie.Logos.Channel.Hosting;
using Bardie.Harness.Source;
using Kithara.Features.Auth;
using Kithara.Features.Library;
using Kithara.Features.Modules;
using Kithara.Features.Search;
using Kithara.Features.Streams;
using Kithara.Features.Streaming;
using Kithara.Infrastructure.Neck;
using Kithara.Infrastructure.Observability;
using Kithara.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables();

builder.WebHost.ConfigureKestrel(options => options.ConfigureBardieModuleListeners());

builder.AddKitharaOpenTelemetry();

builder.Services.AddModuleChannel(builder.Configuration);
builder.Services.AddAuthModuleHarness(registerModuleChannel: false);
builder.Services.AddSourceModuleHarness(registerModuleChannel: false);

builder.Services.AddKitharaDb(builder.Configuration);
builder.Services.AddKitharaBlobStorage(builder.Configuration);
builder.Services.AddKitharaLibrary();
builder.Services.AddKitharaNeck(builder.Configuration);
builder.Services.AddKitharaSearch(builder.Configuration);
builder.Services.AddKitharaAuthAuthentication(builder.Configuration);
builder.Services.AddModuleRegistry(builder.Configuration);
builder.Services.AddHostedService<InviteBootstrapHostedService>();
builder.Services.AddSingleton<GuestExchangeLockout>();
builder.Services.AddSingleton<InviteClaimLockout>();
builder.Services.AddSingleton<ClaimInviteJwtService>();

// GUEST-XCHG-001: guest-code exchange is unauthenticated — bound by IP + Struna id.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("guest-exchange", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // Partition by Struna id (GUID route) or slug (by-slug route).
        var strunaKey = httpContext.Request.RouteValues.TryGetValue("id", out var id)
            ? id?.ToString() ?? string.Empty
            : httpContext.Request.RouteValues.TryGetValue("slug", out var slug)
                ? slug?.ToString() ?? string.Empty
                : string.Empty;
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{ip}:{strunaKey}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    options.AddPolicy("invite-claim", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadyHealthCheck>("database", tags: ["ready"])
    .AddCheck<ModuleTlsHealthCheck>("module-tls", tags: ["ready"])
    .AddCheck("grpc-listener", () => HealthCheckResult.Healthy("gRPC listener configured on :5000"), tags: ["ready"]);

var app = builder.Build();

var certificateStore = app.Services.GetRequiredService<IModuleCertificateStore>();
await certificateStore.EnsureLoadedAsync().ConfigureAwait(false);

// Ensure guest signing key material exists at boot (used by POST …/guest/exchange).
_ = app.Services.GetRequiredService<GuestJwtSigningKeyStore>().GetSigningKey();

await app.MigrateKitharaDatabaseAsync().ConfigureAwait(false);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapKitharaHealthEndpoints();
app.MapAuthEndpoints();
app.MapSearchEndpoints();
app.MapStrunaEndpoints();
app.MapStreamEndpoints();
app.MapModuleRegistry();

app.Run();

public partial class Program;
