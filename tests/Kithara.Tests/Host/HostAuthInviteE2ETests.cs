using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Ports;
using Kithara.Features.Auth;
using Kithara.Infrastructure.Persistence;
using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kithara.Tests.Host;

[CollectionDefinition("HostE2E", DisableParallelization = true)]
public sealed class HostE2ECollection : ICollectionFixture<HostE2EFixture>;

/// <summary>META-QA-001 — host WebApplicationFactory: discovery, invite claim→bind, /me, guest, rotate gate.</summary>
[Collection("HostE2E")]
public sealed class HostAuthInviteE2ETests
{
    private readonly HostE2EFixture _fx;

    public HostAuthInviteE2ETests(HostE2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task Discovery_lists_fake_bes_with_bind_form()
    {
        var response = await _fx.Client.GetAsync("/api/auth/discovery");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = doc.RootElement.GetProperty("providers");
        Assert.True(providers.GetArrayLength() >= 1);
        var first = providers[0];
        Assert.Equal("bes", first.GetProperty("id").GetString());
        Assert.True(first.TryGetProperty("bind_form", out var bindForm));
        Assert.NotEqual(JsonValueKind.Null, bindForm.ValueKind);
    }

    [Fact]
    public async Task Invite_claim_bind_authenticate_me_round_trip()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var harness = _fx.Factory.Services.GetRequiredService<AuthModuleHarness>();
        var (userId, username, otp) = await harness.CreateInviteAsync(
            $"admin-{suffix}",
            InviteOtp.Generate,
            InviteOtp.Hash);

        var claim = await _fx.Client.PostAsJsonAsync("/api/auth/claim", new
        {
            username,
            registration_password = otp,
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, claim.StatusCode);
        using var claimDoc = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        var claimToken = claimDoc.RootElement.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(claimToken));
        Assert.True(claimDoc.RootElement.GetProperty("must_complete_binding").GetBoolean());

        using (var claimClient = _fx.Factory.CreateClient())
        {
            claimClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", claimToken);
            var denied = await claimClient.PostAsJsonAsync("/api/streams", new
            {
                slug = $"blocked-{suffix}",
                title = "Blocked",
            });
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var claimClient = _fx.Factory.CreateClient())
        {
            claimClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", claimToken);
            var bind = await claimClient.PostAsJsonAsync("/api/auth/bindings/bes", new
            {
                ceremony = "bind",
                payload = new Dictionary<string, string>
                {
                    ["password"] = "password123",
                },
            });
            Assert.Equal(System.Net.HttpStatusCode.OK, bind.StatusCode);
            using var bindDoc = JsonDocument.Parse(await bind.Content.ReadAsStringAsync());
            Assert.False(bindDoc.RootElement.GetProperty("must_rotate_credentials").GetBoolean());
            Assert.False(bindDoc.RootElement.GetProperty("must_complete_binding").GetBoolean());
        }

        var user = await harness.Persistence.FindUserByIdAsync(userId);
        Assert.NotNull(user);
        Assert.False(user!.MustCompleteBinding);
        Assert.False(user.MustRotateCredentials);

        var login = await _fx.Client.PostAsJsonAsync("/api/auth/authenticate", new
        {
            provider_id = "bes",
            payload = new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = "password123",
            },
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
        using var loginDoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var access = loginDoc.RootElement.GetProperty("access_token").GetString();
        Assert.False(loginDoc.RootElement.GetProperty("must_rotate_credentials").GetBoolean());

        using var authed = _fx.Factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var me = await authed.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
        using var meDoc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(username, meDoc.RootElement.GetProperty("username").GetString());
        Assert.Equal(userId.ToString("D"), meDoc.RootElement.GetProperty("user_id").GetString());
        Assert.False(meDoc.RootElement.GetProperty("must_rotate_credentials").GetBoolean());
        Assert.False(meDoc.RootElement.GetProperty("must_complete_binding").GetBoolean());
    }

    [Fact]
    public async Task Guest_exchange_returns_tokens()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var harness = _fx.Factory.Services.GetRequiredService<AuthModuleHarness>();
        var ownerId = await harness.Persistence.CreateDurableUserAsync(mustRotateCredentials: false);
        await harness.Persistence.EnsureUserWithBindingAsync(new EnsureUserBindingRequest(
            "bes",
            $"owner-{suffix}",
            "{}",
            false,
            Roles: ["admin"],
            UserId: ownerId));

        var strunaId = Guid.NewGuid();
        var guestCode = "GUESTCODE1";
        var slug = $"g-{suffix}";
        await using (var db = await _fx.Factory.Services
                         .GetRequiredService<IDbContextFactory<KitharaDbContext>>()
                         .CreateDbContextAsync())
        {
            db.Strunas.Add(new Struna
            {
                Id = strunaId,
                Slug = slug,
                Title = "Guest Room",
                OwnerUserId = ownerId,
                GuestCode = guestCode,
                PlaybackAccess = PlaybackAccess.Public,
                ControlAccess = ControlAccess.Protected,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var exchange = await _fx.Client.PostAsJsonAsync(
            $"/api/streams/{strunaId:D}/guest/exchange",
            new { guest_code = guestCode });
        Assert.Equal(System.Net.HttpStatusCode.OK, exchange.StatusCode);
        using var doc = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task MustRotate_gate_blocks_stream_create()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var harness = _fx.Factory.Services.GetRequiredService<AuthModuleHarness>();
        var (userId, username, otp) = await harness.CreateInviteAsync(
            $"rotator-{suffix}",
            InviteOtp.Generate,
            InviteOtp.Hash);

        var claim = await _fx.Client.PostAsJsonAsync("/api/auth/claim", new
        {
            username,
            registration_password = otp,
        });
        claim.EnsureSuccessStatusCode();
        using var claimDoc = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        var claimToken = claimDoc.RootElement.GetProperty("access_token").GetString()!;

        using (var claimClient = _fx.Factory.CreateClient())
        {
            claimClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", claimToken);
            (await claimClient.PostAsJsonAsync("/api/auth/bindings/bes", new
            {
                ceremony = "bind",
                payload = new Dictionary<string, string> { ["password"] = "password123" },
            })).EnsureSuccessStatusCode();
        }

        var login = await _fx.Client.PostAsJsonAsync("/api/auth/authenticate", new
        {
            provider_id = "bes",
            payload = new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = "password123",
            },
        });
        login.EnsureSuccessStatusCode();
        using var loginDoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var access = loginDoc.RootElement.GetProperty("access_token").GetString();

        await harness.Persistence.SetMustRotateAsync(userId, true);

        using var authed = _fx.Factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var create = await authed.PostAsJsonAsync("/api/streams", new
        {
            slug = $"rotate-{suffix}",
            title = "Rotate Block",
        });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, create.StatusCode);
        var body = await create.Content.ReadAsStringAsync();
        Assert.Contains(AuthEndpoints.CredentialsRotationRequired, body, StringComparison.Ordinal);
    }
}
