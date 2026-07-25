namespace Bardie.Harness.Auth;

/// <summary>
/// Bardie host conventions for auth-module <c>RegisterRequest.capabilities</c>.
/// Mesh contract treats capabilities as open strings; only the Auth Harness
/// (and host wrappers) gate RPCs on these values. ModuleChannel does not interpret them.
/// </summary>
public static class WellKnownAuthCapabilities
{
    /// <summary>Host may call <c>SeedAdminBinding</c> when the user DB is empty.</summary>
    public const string SeedAdmin = "seedAdmin";

    /// <summary>Reserved — open signup via <c>bind_form</c> → <c>UpdateUserBinding</c> without operator seed.</summary>
    public const string SelfRegister = "selfRegister";

    /// <summary>Reserved — password-reset ceremony via <c>bind_form</c>.</summary>
    public const string PasswordReset = "passwordReset";
}
