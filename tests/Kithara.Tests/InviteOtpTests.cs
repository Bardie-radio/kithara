using Kithara.Features.Auth;
using Xunit;

namespace Kithara.Tests;

public class InviteOtpTests
{
    [Fact]
    public void Generate_returns_url_safe_nonempty()
    {
        var otp = InviteOtp.Generate();
        Assert.False(string.IsNullOrWhiteSpace(otp));
        Assert.DoesNotContain('+', otp);
        Assert.DoesNotContain('/', otp);
        Assert.DoesNotContain('=', otp);
    }

    [Fact]
    public void Hash_verify_round_trip()
    {
        var otp = InviteOtp.Generate();
        var hash = InviteOtp.Hash(otp);
        Assert.True(InviteOtp.Verify(hash, otp));
        Assert.False(InviteOtp.Verify(hash, otp + "x"));
        Assert.False(InviteOtp.Verify(null, otp));
        Assert.False(InviteOtp.Verify(hash, ""));
    }

    [Fact]
    public void Hash_is_not_plaintext()
    {
        var otp = "test-otp-value";
        var hash = InviteOtp.Hash(otp);
        Assert.DoesNotContain(otp, hash);
    }
}
