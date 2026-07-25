namespace Bardie.Harness.Auth.Ports;

/// <summary>
/// Raised when attaching a binding would steal another user's <c>(provider, external_subject)</c>.
/// </summary>
public sealed class AuthBindingConflictException : InvalidOperationException
{
    public AuthBindingConflictException(string message)
        : base(message)
    {
    }
}
