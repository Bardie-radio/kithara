using System.Security.Claims;
using Kithara.Features.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Kithara.Tests;

public class BindOnlyGateTests
{
    [Theory]
    [InlineData("/api/auth/me", true)]
    [InlineData("/api/auth/bindings/bes", true)]
    [InlineData("/api/auth/bindings/bes/extra", true)]
    [InlineData("/api/auth/register", false)]
    [InlineData("/api/search", false)]
    [InlineData("/api/search/quick", false)]
    [InlineData("/api/streams", false)]
    [InlineData("/api/streams/listen", false)]
    public void IsAllowedPath_matches_bindings_and_me_only(string path, bool allowed)
    {
        Assert.Equal(allowed, BindOnlyGate.IsAllowedPath(new PathString(path)));
    }

    [Fact]
    public void IsBindOnlyToken_true_for_claim_and_bind_only_claim()
    {
        var byProvider = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("bardie_provider", ClaimInviteJwtService.ProviderClaimValue),
        ]));
        Assert.True(BindOnlyGate.IsBindOnlyToken(byProvider));

        var byFlag = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimInviteJwtService.BindOnlyClaim, "true"),
            new Claim("bardie_provider", "bes"),
        ]));
        Assert.True(BindOnlyGate.IsBindOnlyToken(byFlag));

        var login = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("bardie_provider", "bes"),
        ]));
        Assert.False(BindOnlyGate.IsBindOnlyToken(login));
    }
}
