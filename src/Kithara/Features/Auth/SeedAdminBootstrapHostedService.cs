using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kithara.Features.Auth;

/// <summary>
/// When the user DB is empty, waits for an auth module advertising <c>seedAdmin</c> and bootstraps an admin.
/// Welcome text is logged to the Kithara container log only — never on public HTTP.
/// </summary>
public sealed class SeedAdminBootstrapHostedService : BackgroundService
{
    private readonly AuthModuleHarness _harness;
    private readonly ILogger<SeedAdminBootstrapHostedService> _logger;

    public SeedAdminBootstrapHostedService(
        AuthModuleHarness harness,
        ILogger<SeedAdminBootstrapHostedService> logger)
    {
        _harness = harness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the registry a moment for Compose modules to Register.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested && attempt < 60)
        {
            attempt++;
            try
            {
                if (await _harness.Persistence.HasAnyUsersAsync(stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogDebug("User DB is not empty; seedAdmin bootstrap skipped.");
                    return;
                }

                var result = await _harness.TrySeedAdminAsync(stoppingToken).ConfigureAwait(false);
                if (result is { Created: true })
                {
                    LogWelcome(result);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "seedAdmin bootstrap attempt {Attempt} failed.", attempt);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogWarning("seedAdmin bootstrap gave up after {Attempts} attempts (no capable module or empty DB still).", attempt);
    }

    private void LogWelcome(SeedAdminResult result)
    {
        // Single conspicuous log line — operators scrape container logs for one-time credentials.
        _logger.LogWarning(
            "BOOTSTRAP ADMIN (seedAdmin): user={Subject} id={UserId}. Credentials (log only — rotate on first login): {Welcome}",
            result.ExternalSubject,
            result.UserId,
            result.WelcomeLogText);
    }
}
