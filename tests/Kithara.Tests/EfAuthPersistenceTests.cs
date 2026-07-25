using Bardie.Harness.Auth.Ports;
using Kithara.Features.Auth;
using Kithara.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kithara.Tests;

public class EfAuthPersistenceTests
{
    [Fact]
    public async Task EnsureUserWithBinding_explicit_user_rejects_subject_owned_by_another()
    {
        await using var harness = await CreateHarnessAsync();
        var alice = await harness.Persistence.CreateDurableUserAsync(false);
        var bob = await harness.Persistence.CreateDurableUserAsync(false);

        await harness.Persistence.EnsureUserWithBindingAsync(new EnsureUserBindingRequest(
            "bes",
            "alice",
            "{}",
            false,
            UserId: alice));

        var ex = await Assert.ThrowsAsync<AuthBindingConflictException>(() =>
            harness.Persistence.EnsureUserWithBindingAsync(new EnsureUserBindingRequest(
                "bes",
                "alice",
                "{}",
                false,
                UserId: bob)));

        Assert.Contains("already bound", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await harness.Persistence.FindBindingByUserAsync(bob, "bes"));
    }

    [Fact]
    public async Task DeleteUnboundDurableUsers_clears_orphans_but_keeps_bound()
    {
        await using var harness = await CreateHarnessAsync();
        var orphan = await harness.Persistence.CreateDurableUserAsync(true);
        var bound = await harness.Persistence.CreateDurableUserAsync(false);
        await harness.Persistence.EnsureUserWithBindingAsync(new EnsureUserBindingRequest(
            "bes",
            "admin",
            "{}",
            true,
            UserId: bound));

        var removed = await harness.Persistence.DeleteUnboundDurableUsersAsync();
        Assert.Equal(1, removed);
        Assert.Null(await harness.Persistence.FindUserByIdAsync(orphan));
        Assert.NotNull(await harness.Persistence.FindUserByIdAsync(bound));
        Assert.True(await harness.Persistence.HasAnyAuthBindingsAsync());
    }

    [Fact]
    public async Task DeleteUser_removes_cascaded_bindings()
    {
        await using var harness = await CreateHarnessAsync();
        var userId = await harness.Persistence.CreateDurableUserAsync(false);
        await harness.Persistence.EnsureUserWithBindingAsync(new EnsureUserBindingRequest(
            "bes",
            "bob",
            "{}",
            false,
            UserId: userId));

        await harness.Persistence.DeleteUserAsync(userId);

        Assert.Null(await harness.Persistence.FindUserByIdAsync(userId));
        Assert.False(await harness.Persistence.HasAnyAuthBindingsAsync());
    }

    [Fact]
    public async Task CreateInvitedUser_and_complete_invite_clears_pending()
    {
        await using var harness = await CreateHarnessAsync();
        var otp = "one-time-secret";
        var userId = await harness.Persistence.CreateInvitedUserAsync(new CreateInvitedUserRequest(
            "admin",
            InviteOtp.Hash(otp),
            ["admin"]));

        Assert.True(await harness.Persistence.HasAnyDurableUsersAsync());
        var pending = await harness.Persistence.FindUserByUsernameAsync("ADMIN");
        Assert.NotNull(pending);
        Assert.True(pending!.MustCompleteBinding);
        Assert.Equal("admin", pending.Username);
        Assert.Contains("admin", pending.InviteRoles!);
        Assert.True(InviteOtp.Verify(pending.InvitePasswordHash, otp));

        await harness.Persistence.EnsureUserWithBindingAsync(new EnsureUserBindingRequest(
            "bes",
            "admin",
            "{}",
            false,
            Roles: ["admin"],
            UserId: userId));
        await harness.Persistence.CompleteInviteAsync(userId);
        var done = await harness.Persistence.FindUserByIdAsync(userId);
        Assert.NotNull(done);
        Assert.False(done!.MustCompleteBinding);
        Assert.Null(done.InvitePasswordHash);
        Assert.False(done.MustRotateCredentials);
        Assert.Equal(0, await harness.Persistence.DeleteUnboundDurableUsersAsync());
    }

    [Fact]
    public async Task CreateInvitedUser_rejects_duplicate_username()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.Persistence.CreateInvitedUserAsync(new CreateInvitedUserRequest(
            "alice",
            InviteOtp.Hash("x"),
            ["user"]));

        await Assert.ThrowsAsync<AuthUsernameConflictException>(() =>
            harness.Persistence.CreateInvitedUserAsync(new CreateInvitedUserRequest(
                "Alice",
                InviteOtp.Hash("y"),
                ["user"])));
    }

    [Fact]
    public async Task EnsureUserWithBinding_rejects_subject_matching_another_username()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.Persistence.CreateInvitedUserAsync(new CreateInvitedUserRequest(
            "admin",
            InviteOtp.Hash("otp"),
            ["admin"]));
        var alice = await harness.Persistence.CreateInvitedUserAsync(new CreateInvitedUserRequest(
            "alice",
            InviteOtp.Hash("otp2"),
            ["user"]));

        var ex = await Assert.ThrowsAsync<AuthBindingConflictException>(() =>
            harness.Persistence.EnsureUserWithBindingAsync(new EnsureUserBindingRequest(
                "bes",
                "admin",
                "{}",
                false,
                UserId: alice)));

        Assert.Contains("username", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await harness.Persistence.FindBindingByUserAsync(alice, "bes"));
    }

    private static async Task<PersistenceHarness> CreateHarnessAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "kithara-auth-" + Guid.NewGuid().ToString("N") + ".db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<KitharaDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<IAuthPersistence, EfAuthPersistence>();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<KitharaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new PersistenceHarness(provider, dbPath);
    }

    private sealed class PersistenceHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _dbPath;

        public PersistenceHarness(ServiceProvider provider, string dbPath)
        {
            _provider = provider;
            _dbPath = dbPath;
            Persistence = provider.GetRequiredService<IAuthPersistence>();
        }

        public IAuthPersistence Persistence { get; }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
    }
}
