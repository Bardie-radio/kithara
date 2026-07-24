using Kithara.Infrastructure.Neck;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Kithara.Infrastructure.Observability;

public static class OpenTelemetryExtensions
{
    public const string ServiceName = "bardie.kithara";

    /// <summary>
    /// Registers OTLP traces/metrics with <c>service.name=bardie.kithara</c>.
    /// Safe when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is unset (exporter no-ops / buffers until configured).
    /// </summary>
    public static WebApplicationBuilder AddKitharaOpenTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ServiceName,
                serviceVersion: typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing => tracing
                .AddSource(NeckActivity.Source.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddGrpcClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        return builder;
    }
}
