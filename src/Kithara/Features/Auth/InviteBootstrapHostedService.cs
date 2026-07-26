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
        // Banner stays Warning so it survives Production filters and stands out in compose logs.
        // Intentional OTP plaintext — claim then bind_form; never log join secrets.
        _logger.LogWarning(
            """

            ======================================================================
              KITHARA AUTH-INVITE — BOOTSTRAP ADMIN
            ----------------------------------------------------------------------
              username:          {Username}
              user id:           {UserId}
              Registration OTP:  {Otp}
            ----------------------------------------------------------------------
              Next: open any user-aware client and claim user → enter username and
                                 OTP → complete bind to any auth provider.
              OTP is log-only (never on public HTTP). Rotate ops access if leaked.
            ======================================================================
            """,
            result.Username,
            result.UserId,
            result.RegistrationPassword);
    }
}
