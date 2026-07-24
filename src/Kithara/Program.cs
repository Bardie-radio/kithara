using System.Threading.RateLimiting;
using Bardie.Orchestrator.Auth;
using Bardie.Module.Channel;
using Bardie.Module.Channel.Certificates;
using Bardie.Module.Channel.Hosting;
using Bardie.Orchestrator.Source;
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
builder.Services.AddAuthModuleOrchestrator(registerModuleChannel: false);
builder.Services.AddSourceModuleOrchestrator(registerModuleChannel: false);

builder.Services.AddKitharaDb(builder.Configuration);
builder.Services.AddKitharaBlobStorage(builder.Configuration);
builder.Services.AddKitharaLibrary();
builder.Services.AddKitharaNeck(builder.Configuration);
builder.Services.AddKitharaSearch(builder.Configuration);
builder.Services.AddModuleRegistry(builder.Configuration);
builder.Services.AddKitharaAuthAuthentication(builder.Configuration);
builder.Services.AddHostedService<SeedAdminBootstrapHostedService>();

// GUEST-XCHG-001: guest-code exchange is unauthenticated — bound by IP + Struna id.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("guest-exchange", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var strunaId = httpContext.Request.RouteValues.TryGetValue("id", out var id)
            ? id?.ToString() ?? string.Empty
            : string.Empty;
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{ip}:{strunaId}",
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
