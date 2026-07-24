using Bardie.Orchestrator.Auth;
using Bardie.Orchestrator.Auth.Catalog;
using Bardie.Orchestrator.Auth.Ports;
using Bardie.Module.Channel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bardie.Orchestrator.Auth.Tests;

public class AuthOrchestratorScaffoldTests
{
    [Fact]
    public void AddAuthModuleOrchestrator_registers_catalog_and_module_channel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IAuthPersistence, FakeAuthPersistence>();
        services.AddAuthModuleOrchestrator(options =>
        {
            options.TlsDataPath = Path.Combine(Path.GetTempPath(), "auth-orch-mtls-" + Guid.NewGuid().ToString("N"));
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IAuthModuleCatalog>());
        Assert.NotNull(provider.GetRequiredService<AuthModuleOrchestrator>());
        Assert.NotNull(provider.GetRequiredService<Bardie.Module.Channel.Certificates.IModuleCertificateStore>());
    }

    private sealed class FakeAuthPersistence : IAuthPersistence
    {
        public Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<AuthBindingRecord?> FindBindingBySubjectAsync(
            string providerSlug,
            string externalSubject,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthBindingRecord?>(null);

        public Task<Guid> EnsureUserWithBindingAsync(
            EnsureUserBindingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<AuthUserRecord?> FindUserByBindingSubjectAsync(
            string providerSlug,
            string externalSubject,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthUserRecord?>(null);

        public Task<AuthUserRecord?> FindUserByIdAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthUserRecord?>(null);
    }
}
