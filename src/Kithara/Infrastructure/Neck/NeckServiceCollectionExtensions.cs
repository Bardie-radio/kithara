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

/// <summary>Disposes encoder sessions on host shutdown.</summary>
internal sealed class NeckEncoderHostedService : IHostedService
{
    private readonly StrunaEncoderSupervisor _encoder;

    public NeckEncoderHostedService(StrunaEncoderSupervisor encoder)
    {
        _encoder = encoder;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _encoder.DisposeAsync().ConfigureAwait(false);
}
