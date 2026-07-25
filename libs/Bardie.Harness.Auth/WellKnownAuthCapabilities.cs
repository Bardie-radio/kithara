namespace Bardie.Harness.Auth;

/// <summary>
/// Bardie host conventions for auth-module <c>RegisterRequest.capabilities</c>.
/// Mesh contract treats capabilities as open strings; only the Auth Harness
/// (and host wrappers) gate RPCs on these values. ModuleChannel does not interpret them.
/// </summary>
public static class WellKnownAuthCapabilities
{
    /// <summary>
    /// Host may expose self-service binding create/update (<c>UpdateUserBinding</c> + discovery <c>bind_form</c>).
    /// </summary>
    public const string UpdateBinding = "updateBinding";

    /// <summary>Reserved — open signup via <c>bind_form</c> → <c>UpdateUserBinding</c> ceremony <c>bind</c> without invite.</summary>
    public const string SelfRegister = "selfRegister";

    /// <summary>Reserved — password-reset ceremony via <c>bind_form</c>.</summary>
    public const string PasswordReset = "passwordReset";

    public static bool HasCapability(IEnumerable<string> capabilities, string capability) =>
        capabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));
}
