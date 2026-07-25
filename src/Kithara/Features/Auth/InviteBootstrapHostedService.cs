using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kithara.Features.Auth;

/// <summary>
/// When no durable users exist, invents DEFAULT_ADMIN with a host OTP invite (AUTH-INVITE).
/// OTP plaintext is logged to the Kithara container log only — never on public HTTP.
/// </summary>
public sealed class InviteBootstrapHostedService : BackgroundService
{
    private readonly AuthModuleHarness _harness;
    private readonly ILogger<InviteBootstrapHostedService> _logger;

    public InviteBootstrapHostedService(
        AuthModuleHarness harness,
        ILogger<InviteBootstrapHostedService> logger)
    {
        _harness = harness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested && attempt < 30)
        {
            attempt++;
            try
            {
                if (await _harness.Persistence.HasAnyDurableUsersAsync(stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogDebug("Durable users exist; invite bootstrap skipped.");
                    return;
                }

                var result = await _harness.TryBootstrapInviteAsync(
                        InviteOtp.Generate,
                        InviteOtp.Hash,
                        stoppingToken)
                    .ConfigureAwait(false);
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
                _logger.LogWarning(ex, "Invite bootstrap attempt {Attempt} failed.", attempt);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogWarning("Invite bootstrap gave up after {Attempts} attempts.", attempt);
    }

    private void LogWelcome(InviteBootstrapResult result)
    {
        _logger.LogWarning(
            "BOOTSTRAP ADMIN (AUTH-INVITE): username={Username} id={UserId}. Registration OTP (log only — claim then bind_form): {Otp}",
            result.Username,
            result.UserId,
            result.RegistrationPassword);
    }
}
