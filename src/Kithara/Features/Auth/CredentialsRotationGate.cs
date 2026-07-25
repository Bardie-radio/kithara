using Bardie.Harness.Auth.Ports;

namespace Kithara.Features.Auth;

/// <summary>AUTH-ROT-002: deny control while <see cref="AuthUserRecord.MustRotateCredentials"/>.</summary>
public static class CredentialsRotationGate
{
    public static IResult? DenyIfRequired(AuthUserRecord principal)
    {
        if (!principal.MustRotateCredentials)
        {
            return null;
        }

        return Results.Json(
            new { error = AuthEndpoints.CredentialsRotationRequired },
            statusCode: StatusCodes.Status403Forbidden);
    }
}
