using Bardie.Harness.Auth.Ports;

namespace Kithara.Features.Auth;

/// <summary>AUTH-INVITE: deny control while invitee must complete first bind.</summary>
public static class BindingCompletionGate
{
    public const string MustCompleteBindingRequired = "must_complete_binding";

    public static IResult? DenyIfRequired(AuthUserRecord principal)
    {
        if (!principal.MustCompleteBinding)
        {
            return null;
        }

        return Results.Json(
            new { error = MustCompleteBindingRequired },
            statusCode: StatusCodes.Status403Forbidden);
    }
}
