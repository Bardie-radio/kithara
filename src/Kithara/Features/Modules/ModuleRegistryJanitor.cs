using Bardie.Harness.Auth.Catalog;
using Bardie.Harness.Source.Catalog;
using Microsoft.Extensions.Options;

namespace Kithara.Features.Modules;

/// <summary>Expires stale Module Registry entries and drops matching harness catalog rows.</summary>
public sealed class ModuleRegistryJanitor : BackgroundService
{
    private readonly InMemoryModuleRegistry _registry;
    private readonly IAuthModuleCatalog _authCatalog;
    private readonly ISourceModuleCatalog _sourceCatalog;
    private readonly ILogger<ModuleRegistryJanitor> _logger;
    private readonly TimeSpan _interval;

    public ModuleRegistryJanitor(
        InMemoryModuleRegistry registry,
        IAuthModuleCatalog authCatalog,
        ISourceModuleCatalog sourceCatalog,
        IOptions<ModuleRegistryOptions> options,
        ILogger<ModuleRegistryJanitor> logger)
    {
        _registry = registry;
        _authCatalog = authCatalog;
        _sourceCatalog = sourceCatalog;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.NextHeartbeatAfterSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var removed = _registry.RemoveExpired(now);
                _authCatalog.RemoveExpired(now);
                _sourceCatalog.RemoveExpired(now);

                foreach (var module in removed)
                {
                    _authCatalog.Remove(module.Slug);
                    _sourceCatalog.Remove(module.Slug);
                    _logger.LogInformation(
                        "Module {Slug} expired from registry (last heartbeat {Last})",
                        module.Slug,
                        module.LastHeartbeatAt);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Module registry janitor failed");
            }

            await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
