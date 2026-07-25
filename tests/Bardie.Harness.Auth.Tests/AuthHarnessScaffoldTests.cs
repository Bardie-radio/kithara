using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Catalog;
using Bardie.Harness.Auth.Ports;
using Bardie.Module.Channel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bardie.Harness.Auth.Tests;

public class AuthHarnessScaffoldTests
{
    [Fact]
    public void AddAuthModuleHarness_registers_catalog_and_module_channel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IAuthPersistence, FakeAuthPersistence>();
        services.AddAuthModuleHarness(options =>
        {
            options.TlsDataPath = Path.Combine(Path.GetTempPath(), "auth-harness-mtls-" + Guid.NewGuid().ToString("N"));
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IAuthModuleCatalog>());
        Assert.NotNull(provider.GetRequiredService<AuthModuleHarness>());
        Assert.NotNull(provider.GetRequiredService<Bardie.Module.Channel.Certificates.IModuleCertificateStore>());
    }

    private sealed class FakeAuthPersistence : IAuthPersistence
    {
        public Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<Guid> CreateDurableUserAsync(
            bool mustRotateCredentials,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<AuthBindingRecord?> FindBindingBySubjectAsync(
            string providerSlug,
            string externalSubject,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthBindingRecord?>(null);

        public Task<AuthBindingRecord?> FindBindingByUserAsync(
            Guid userId,
            string providerSlug,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthBindingRecord?>(null);

        public Task<Guid> EnsureUserWithBindingAsync(
            EnsureUserBindingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.UserId ?? Guid.NewGuid());

        public Task<AuthUserRecord?> FindUserByBindingSubjectAsync(
            string providerSlug,
            string externalSubject,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthUserRecord?>(null);

        public Task<AuthUserRecord?> FindUserByIdAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthUserRecord?>(null);

        public Task SetMustRotateAsync(
            Guid userId,
            bool mustRotateCredentials,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
