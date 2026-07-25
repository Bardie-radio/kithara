namespace Kithara.Infrastructure.Neck;

public static class NeckServiceCollectionExtensions
{
    public static IServiceCollection AddKitharaNeck(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<NeckOptions>(options =>
        {
            configuration.GetSection(NeckOptions.SectionName).Bind(options);
            var path = configuration["BARDIE_STRUNA_FIFO_PATH"];
            if (!string.IsNullOrWhiteSpace(path))
            {
                options.StrunaFifoRoot = path.Trim();
            }

            var ffmpegRoot = configuration["BARDIE_FFMPEG_ROOT"];
            if (!string.IsNullOrWhiteSpace(ffmpegRoot))
            {
                options.FfmpegRootPath = ffmpegRoot.Trim();
            }
        });

        services.AddSingleton<StrunaEncoderSupervisor>();
        services.AddSingleton<Neck>();
        services.AddHostedService<NeckEncoderHostedService>();
        return services;
    }
}

/// <summary>
/// Rehydrates encode-alive sessions from DB Strunas on startup; disposes encoders on shutdown.
/// </summary>
internal sealed class NeckEncoderHostedService(
    Neck neck,
    StrunaEncoderSupervisor encoder,
    ILogger<NeckEncoderHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await neck.RehydrateAliveStrunasAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Do not fail host boot — individual Struna failures are logged inside Rehydrate.
            logger.LogError(ex, "Encode-alive rehydration failed");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await encoder.DisposeAsync().ConfigureAwait(false);
}
