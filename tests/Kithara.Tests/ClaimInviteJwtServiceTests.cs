using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Bardie.Harness.Auth.Ports;
using Kithara.Features.Auth;
using Kithara.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kithara.Tests;

public class ClaimInviteJwtServiceTests
{
    [Fact]
    public async Task TryClaim_mints_bind_only_tokens_and_refresh_stops_after_complete()
    {
        await using var harness = await CreateHarnessAsync();
        const string otp = "REG-OTP-VALUE";
        await harness.Persistence.CreateInvitedUserAsync(new CreateInvitedUserRequest(
            "admin",
            InviteOtp.Hash(otp),
            ["admin"]));

        var minted = await harness.Claims.TryClaimAsync("admin", otp);
        Assert.NotNull(minted);
        var (access, refresh, _) = minted!.Value;

        var handler = new JwtSecurityTokenHandler();
        var accessJwt = handler.ReadJwtToken(access);
        Assert.Equal(ClaimInviteJwtService.ProviderClaimValue, accessJwt.Payload["bardie_provider"]?.ToString());
        Assert.Equal("true", accessJwt.Payload[ClaimInviteJwtService.BindOnlyClaim]?.ToString());
        Assert.DoesNotContain(accessJwt.Claims, c => c.Type == ClaimTypes.Role);

        var reminted = await harness.Claims.TryRefreshAsync(refresh);
        Assert.NotNull(reminted);

        var user = await harness.Persistence.FindUserByUsernameAsync("admin");
        Assert.NotNull(user);
        await harness.Persistence.CompleteInviteAsync(user!.UserId);

        Assert.Null(await harness.Claims.TryRefreshAsync(refresh));
        Assert.Null(await harness.Claims.TryRefreshAsync(reminted!.Value.RefreshToken));
    }

    [Fact]
    public async Task TryClaim_rejects_wrong_otp_and_completed_users()
    {
        await using var harness = await CreateHarnessAsync();
        const string otp = "good";
        var userId = await harness.Persistence.CreateInvitedUserAsync(new CreateInvitedUserRequest(
            "alice",
            InviteOtp.Hash(otp),
            ["user"]));

        Assert.Null(await harness.Claims.TryClaimAsync("alice", "bad"));

        await harness.Persistence.CompleteInviteAsync(userId);
        Assert.Null(await harness.Claims.TryClaimAsync("alice", otp));
    }

    private static async Task<ClaimHarness> CreateHarnessAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "kithara-claim-" + Guid.NewGuid().ToString("N") + ".db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<KitharaDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<IAuthPersistence, EfAuthPersistence>();
        services.Configure<GuestJwtOptions>(o =>
        {
            o.Issuer = "bardie.kithara.guest";
            o.Audience = "bardie.kithara";
            o.AccessTokenMinutes = 15;
            o.SigningKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        });
        services.AddSingleton<GuestJwtSigningKeyStore>();
        services.AddSingleton<ClaimInviteJwtService>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<KitharaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new ClaimHarness(provider, dbPath);
    }

    private sealed class ClaimHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _dbPath;

        public ClaimHarness(ServiceProvider provider, string dbPath)
        {
            _provider = provider;
            _dbPath = dbPath;
            Persistence = provider.GetRequiredService<IAuthPersistence>();
            Claims = provider.GetRequiredService<ClaimInviteJwtService>();
        }

        public IAuthPersistence Persistence { get; }

        public ClaimInviteJwtService Claims { get; }

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
